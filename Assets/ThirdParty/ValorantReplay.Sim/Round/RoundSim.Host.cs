// RoundSim.Host.cs - the API a host (Unity, a commander UI) drives the round with: taking over an agent and moving,
// shooting, planting and defusing for it (External), and giving AI agents orders (Command).
using System;
using System.Collections.Generic;
using System.Numerics;

namespace ValorantReplay.Sim
{
    public sealed partial class RoundSim
    {
        /// <summary>Hands an agent to the user. Everyone else keeps following the recording until they notice.</summary>
        public void TakeControl(int agent)
        {
            var a = agents[agent];
            if (a.Mode == AgentMode.Replay && a.DivergedAt < 0f) a.DivergedAt = Time;
            a.Mode = AgentMode.External;
            a.Action = AgentAction.None;
            a.Order = default;
            a.Path.Clear();
            if (SpikeCarrier == agent) spikeFromRecording = false;
        }

        /// <summary>Moves an External agent (call once per Step).</summary>
        public void SetExternalState(int agent, Vector3 position, float yaw, bool walking = false, bool crouching = false)
        {
            var a = agents[agent];
            if (a.Mode != AgentMode.External || !a.Alive) return;
            a.Moving = SimSpace.FlatDistance(position, a.Position) > 0.02f;
            a.Position = position;
            a.Yaw = yaw;
            a.Walking = walking;
            a.Crouching = crouching;
            if (a.Moving && (a.Action == AgentAction.Plant || a.Action == AgentAction.Defuse)) { a.Action = AgentAction.None; a.TaskProgress = 0f; }
        }

        /// <summary>An External agent fires one shot from its eye along direction. Returns the agent hit, or -1. The hit
        /// region comes from where the ray passes the target (head sphere, body and legs as one capsule).</summary>
        public int ExternalFire(int agent, Vector3 direction)
        {
            var a = agents[agent];
            if (a.Mode != AgentMode.External || !a.Alive || Time < a.NextShotAt || direction.LengthSquared() < 1e-6f) return -1;
            a.NextShotAt = Time + a.Weapon.FireInterval;
            a.LastShot = Time;
            Gunshot(a);
            var dir = Vector3.Normalize(direction);
            int hit = -1;
            float best = float.MaxValue;
            bool head = false;
            foreach (var t in agents)
            {
                if (!t.Alive || t.Team == a.Team) continue;
                var toHead = t.Eye + new Vector3(0f, 0.1f, 0f) - a.Eye;
                float along = Vector3.Dot(toHead, dir);
                if (along <= 0f || along >= best) continue;
                var closest = a.Eye + dir * along;
                bool isHead = Vector3.Distance(closest, t.Eye + new Vector3(0f, 0.1f, 0f)) < 0.17f;
                float flat = SimSpace.FlatDistance(closest, t.Position);
                bool isBody = flat < 0.38f && closest.Y > t.Position.Y - NavGrid.ActorHeight && closest.Y < t.Eye.Y - 0.1f;
                if ((!isHead && !isBody) || !Clear(a.Eye, closest)) continue;
                best = along; hit = t.Index; head = isHead;
            }
            if (hit < 0) return -1;
            var target = agents[hit];
            float d = Vector3.Distance(a.Eye, target.Eye);
            float raw = head ? a.Weapon.Head : a.Weapon.Body;
            Damage(target, a.Weapon.DamageAt(d, raw), a);
            return hit;
        }

        /// <summary>An External agent fired, with what it hit decided outside the simulation (e.g. Unity raycasts against the
        /// real map and hitboxes): enemies in range hear the shot. Report each bullet that hit someone with ExternalHit.</summary>
        public void ExternalGunshot(int agent)
        {
            var a = agents[agent];
            if (a.Mode != AgentMode.External || !a.Alive) return;
            a.LastShot = Time;
            Gunshot(a);
        }

        /// <summary>A bullet of an External agent hit an enemy for damage (before armor), as decided outside the simulation.
        /// Returns whether it killed. Teammates, the dead and non-External shooters are ignored.</summary>
        public bool ExternalHit(int agent, int target, float damage)
        {
            var a = agents[agent];
            if (a.Mode != AgentMode.External || !a.Alive || target < 0 || target >= agents.Count || damage <= 0f) return false;
            var t = agents[target];
            if (!t.Alive || t.Team == a.Team) return false;
            a.Target = target;          // the victim knows who shot it (See: shotAtBy)
            a.LastShot = Time;
            Damage(t, damage, a);
            return !t.Alive;
        }

        /// <summary>An External agent lets go of the plant / defuse key.</summary>
        public void CancelTask(int agent)
        {
            var a = agents[agent];
            if (a.Mode != AgentMode.External || (a.Action != AgentAction.Plant && a.Action != AgentAction.Defuse)) return;
            a.Action = AgentAction.None;
            a.TaskProgress = 0f;
        }

        /// <summary>An External attacker with the spike starts planting (it continues while the agent stands still).</summary>
        public bool BeginPlant(int agent)
        {
            var a = agents[agent];
            if (a.Mode != AgentMode.External || SpikeState != SpikeStatus.Carried || SpikeCarrier != agent || !Map.InSite(Map.NearestSite(a.Position), a.Position)) return false;
            a.Action = AgentAction.Plant;
            a.TaskProgress = 0f;
            Event(SimEventType.PlantStarted, agent);
            return true;
        }

        /// <summary>An External defender next to the planted spike starts defusing.</summary>
        public bool BeginDefuse(int agent)
        {
            var a = agents[agent];
            if (a.Mode != AgentMode.External || a.Attacking || SpikeState != SpikeStatus.Planted || Vector3.Distance(a.Position, SpikePosition) > 1.5f) return false;
            a.Action = AgentAction.Defuse;
            a.TaskProgress = 0f;
            Event(SimEventType.DefuseStarted, agent);
            return true;
        }

        /// <summary>Orders snap to the walkable cell nearest the target within this many metres.</summary>
        public const float OrderSnap = 6f;
        /// <summary>An order to go somewhere is done this close to the spot (or in its grid cell): teammates keep their
        /// spacing, so two sent to one spot cannot both stand on it.</summary>
        public const float OrderArrival = 1.5f;

        /// <summary>Gives an agent an order (see OrderType), replacing any it has; an agent on the recording leaves it,
        /// one planting or defusing for another reason stops. Plant: Target is where on a site (off a site = the nearest
        /// site's centre). Defuse and PickUp go to the spike, whatever Target says. False when the order cannot be carried
        /// out from here: the agent is dead or External, it is not the side or spike state the order needs, or there is
        /// nowhere walkable near the target.</summary>
        public bool Command(int agent, OrderType type, Vector3 target = default, float yaw = 0f)
        {
            if (agent < 0 || agent >= agents.Count || type == OrderType.None) return false;
            var a = agents[agent];
            if (!a.Alive || a.Mode == AgentMode.External || Over) return false;
            switch (type)
            {
                case OrderType.Plant:
                {
                    if (!a.Attacking || SpikeState != SpikeStatus.Carried || SpikeCarrier != agent) return false;
                    var site = Map.NearestSite(target);
                    if (site == null) return false;
                    if (!Map.InSite(site, target)) target = site.Center;
                    int n = Map.Grid.Nearest(target, OrderSnap);
                    if (n >= 0 && Map.InSite(site, Map.Grid.Position(n))) target = Map.Grid.Position(n);
                    break;
                }
                case OrderType.Defuse:
                    if (a.Attacking || SpikeState != SpikeStatus.Planted) return false;
                    target = SpikePosition;
                    break;
                case OrderType.PickUp:
                    if (!a.Attacking || SpikeState != SpikeStatus.Dropped) return false;
                    target = SpikePosition;
                    break;
                default:
                {
                    int n = Map.Grid.Nearest(target, OrderSnap);
                    if (n < 0) return false;
                    target = Map.Grid.Position(n);
                    break;
                }
            }
            if (a.Mode == AgentMode.Replay) Diverge(a);
            bool sameTask = (a.Action == AgentAction.Plant && type == OrderType.Plant) || (a.Action == AgentAction.Defuse && type == OrderType.Defuse);
            if ((a.Action == AgentAction.Plant || a.Action == AgentAction.Defuse) && !sameTask) { a.Action = AgentAction.None; a.TaskProgress = 0f; }
            a.Order = new AgentOrder { Type = type, Target = target, Yaw = yaw };
            a.WatchesThreat = false;
            a.Path.Clear();
            a.PathGoal = new Vector3(float.NaN);
            return true;
        }

        /// <summary>Ends an agent's order without it being done: the brain decides again from the next tick (a plant or
        /// defuse it started carries on).</summary>
        public void ClearOrder(int agent)
        {
            if (agent < 0 || agent >= agents.Count || agents[agent].Order.Type == OrderType.None) return;
            var a = agents[agent];
            a.Order = default;
            a.WantsDecision = true;
        }

        void EndOrder(SimAgent a, bool done)
        {
            a.Order = default;
            a.WantsDecision = true;
            a.PathGoal = new Vector3(float.NaN);
            Event(SimEventType.OrderDone, a.Index, done ? 1 : 0);
        }
    }
}
