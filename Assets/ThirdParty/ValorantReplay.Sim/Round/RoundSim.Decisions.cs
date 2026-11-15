// RoundSim.Decisions.cs - each AI agent's tick: carry out an order, or ask the brain (UtilityBrain) what to do
// given the director's objective and what it believes, and turn the choice into a goal, a pace and a look.
using System;
using System.Collections.Generic;
using System.Numerics;

namespace ValorantReplay.Sim
{
    public sealed partial class RoundSim
    {
        void Think(SimAgent a, float dt)
        {
            if (a.Action == AgentAction.Plant || a.Action == AgentAction.Defuse) { a.Moving = false; return; }
            if (a.Order.Type != OrderType.None && FollowOrder(a, dt)) return;
            if (a.WantsDecision || Time >= a.NextDecision) Decide(a);
            bool engaged = a.Target >= 0 && sees[a.Index, a.Target];
            if (engaged && a.Action != AgentAction.FallBack) { a.Moving = false; return; }   // stop and shoot
            if (a.Action == AgentAction.Hold || (a.Action == AgentAction.Peek && AnyVisible(a)))
            {
                a.Moving = false;
                a.Yaw = a.HoldYaw;
                KeepSpacing(a, dt);
                return;
            }
            MoveTowards(a, a.Goal, dt);
            KeepSpacing(a, dt);
        }

        /// <summary>Carries out an agent's order for this tick: there at a run, stopping to fight whoever it sees (the
        /// brain is not asked). False when the order just ended: the brain decides this tick.</summary>
        bool FollowOrder(SimAgent a, float dt)
        {
            var o = a.Order;
            bool possible = o.Type == OrderType.Plant ? SpikeState == SpikeStatus.Carried && SpikeCarrier == a.Index
                : o.Type == OrderType.Defuse ? SpikeState == SpikeStatus.Planted
                : o.Type == OrderType.PickUp ? SpikeState == SpikeStatus.Dropped
                : true;
            if (!possible) { EndOrder(a, false); return false; }
            if (o.Type == OrderType.Defuse || o.Type == OrderType.PickUp) o.Target = SpikePosition;
            a.Goal = o.Target;
            if (a.Target >= 0 && sees[a.Index, a.Target]) { a.Moving = false; return true; }   // stop and shoot, then carry on

            float d = SimSpace.FlatDistance(a.Position, o.Target);
            bool there = d <= Math.Max(OrderArrival, Map.Grid.CellSize) && Math.Abs(a.Position.Y - o.Target.Y) < 1.5f;
            switch (o.Type)
            {
                case OrderType.Move when there:
                    EndOrder(a, true);
                    return false;
                case OrderType.Hold when there:
                    a.Action = AgentAction.Hold;
                    a.Moving = false;
                    a.Yaw = o.Yaw;
                    KeepSpacing(a, dt);
                    return true;
                case OrderType.Plant when there && Map.InSite(Map.NearestSite(a.Position), a.Position):
                    a.Action = AgentAction.Plant;
                    a.TaskProgress = 0f;
                    a.Moving = false;
                    a.Path.Clear();
                    Event(SimEventType.PlantStarted, a.Index);
                    return true;
                case OrderType.Defuse when Vector3.Distance(a.Position, SpikePosition) <= 1.5f:
                    a.Action = AgentAction.Defuse;
                    a.TaskProgress = 0f;
                    a.Moving = false;
                    a.Path.Clear();
                    Event(SimEventType.DefuseStarted, a.Index);
                    return true;
            }

            a.Action = AgentAction.Advance;
            a.Walking = false;
            if ((o.Type == OrderType.Defuse || o.Type == OrderType.PickUp) && d <= Map.Grid.CellSize * 1.5f && d > 0.01f)
            {
                // the spike lies anywhere, not on a cell centre: the last steps go straight to it
                var to = o.Target - a.Position;
                to.Y = 0f;
                a.Position += to / d * Math.Min(d, Options.RunSpeed * dt);
                a.Yaw = SimSpace.YawOf(to);
                a.Moving = true;
                return true;
            }
            MoveTowards(a, o.Target, dt);
            if (!a.Moving && a.Path.Count == 0)
            {
                EndOrder(a, false);         // no way there
                return false;
            }
            KeepSpacing(a, dt);
            return true;
        }

        void Decide(SimAgent a)
        {
            a.WantsDecision = false;
            a.NextDecision = Time + a.Profile.DecisionInterval;
            var dir = directors[a.Team];
            var objective = dir.Objective(a, out float objRadius);
            var s = new Situation
            {
                Attacking = a.Attacking,
                SpikePlanted = SpikeState == SpikeStatus.Planted,
                CarriesSpike = SpikeState == SpikeStatus.Carried && SpikeCarrier == a.Index,
                Health = a.Durability,
                NearestThreat = float.PositiveInfinity,
                ObjectiveDistance = SimSpace.FlatDistance(a.Position, objective),
            };
            s.TimePressure = SpikeState == SpikeStatus.Planted ? Profile.Clamp01(1f - SpikeTimer / Options.SpikeDuration) : Profile.Clamp01(1f - Remaining / Options.RoundTime);
            s.Urgency = Urgency(a);
            a.Urgency = s.Urgency;
            s.AtObjective = s.ObjectiveDistance <= objRadius;

            Vector3 threatPos = default;
            float bestPriority = 0f;
            foreach (var e in agents)
            {
                if (!e.Alive || e.Team == a.Team) continue;
                if (sees[a.Index, e.Index]) s.EnemiesVisible++;
                if (!Believed(a, e.Index, out var sight, out float c) || c < 0.15f) continue;
                float d = Vector3.Distance(a.Position, sight.Position);
                if (c > 0.3f && d < 20f) s.EnemiesNear++;
                float priority = c * Math.Max(0.05f, 1f - d / 50f);
                if (priority > bestPriority) { bestPriority = priority; s.NearestThreat = d; s.ThreatCertainty = c; threatPos = sight.Position; }
            }

            float teamDist = 0f;
            int mates = 0;
            foreach (var m in agents)
            {
                if (!m.Alive || m.Team != a.Team || m == a) continue;
                if (Vector3.Distance(m.Position, a.Position) <= 15f) s.AlliesNear++;
                teamDist += SimSpace.FlatDistance(m.Position, objective);
                mates++;
            }
            s.AheadOfTeam = mates > 0 ? teamDist / mates - s.ObjectiveDistance : 0f;

            var death = lastDeath[a.Team];
            Vector3 tradePos = death.Position;
            if (Time - death.Time <= Options.TradeWindow + 2f && Vector3.Distance(a.Position, death.Position) <= a.Profile.TradeRange)
            {
                s.TradeAvailable = true;
                if (death.Killer >= 0 && agents[death.Killer].Alive && Believed(a, death.Killer, out var ks, out float kc) && kc > 0.3f) tradePos = ks.Position;
            }

            UtilityBrain.Score(s, a.Profile, a.Scores);
            a.Action = UtilityBrain.Pick(a.Scores, a.Action, a.Profile, decideRng[a.Index]);
            switch (a.Action)
            {
                case AgentAction.Advance: a.Goal = OwnSpot(a, objective); break;
                case AgentAction.Peek:
                {
                    var to = threatPos - a.Position;
                    float d = to.Length();
                    a.Goal = d > Options.PeekDistance ? a.Position + to / d * Options.PeekDistance : threatPos;
                    break;
                }
                case AgentAction.FallBack: a.Goal = Retreat(a, threatPos); break;
                case AgentAction.Trade: a.Goal = tradePos; break;
                default: a.Goal = a.Position; break;
            }
            // passive players walk (silently) near known enemies; aggressive ones (or anyone short of time) run, unless their
            // game sense says to keep quiet while there is time
            bool approaching = a.Action == AgentAction.Peek || a.Action == AgentAction.Advance;
            a.Walking = (UtilityBrain.EffectiveAggression(s, a.Profile) < 0.5f && (a.Action == AgentAction.Peek || (a.Action == AgentAction.Advance && s.NearestThreat < 25f)))
                || (approaching && s.NearestThreat < Options.StealthRange && a.Profile.Skill * (1f - s.Urgency) > Options.StealthSense);
            // where to look while holding: at a believed threat; else an angle real players check here (Map.Look); else the
            // objective or the attackers' way in. The angle and this player's placement error are drawn per decision.
            var lookRandom = lookRng[a.Index];
            a.LookQuantile = (float)lookRandom.NextDouble();
            a.WatchesThreat = s.ThreatCertainty > 0.15f;
            a.ThreatPosition = threatPos;
            a.LookOffset = SimMath.Gaussian(lookRandom) * a.Profile.LookSpread;
            if (s.ThreatCertainty <= 0.15f && Map.Look != null && Map.Look.TryLook(a.Position, a.Attacking, null, a.LookQuantile, out float learned))
                a.HoldYaw = learned + a.LookOffset;
            else
            {
                var watch = s.ThreatCertainty > 0.15f ? threatPos : a.Attacking ? objective : (SpikeState == SpikeStatus.Planted ? SpikePosition : Map.AttackerSpawn);
                var look = watch - a.Position;
                if (look.X * look.X + look.Z * look.Z > 0.01f) a.HoldYaw = SimSpace.YawOf(look) + a.LookOffset;
            }
        }

        /// <summary>0..1: how close the time left is to the time this agent needs for its side's objective.</summary>
        float Urgency(SimAgent a)
        {
            float left, need;
            if (a.Attacking && SpikeState != SpikeStatus.Planted)
            {
                var site = Map.SiteNamed(directors[a.Team].TargetSite) ?? Map.NearestSite(a.Position);
                if (site == null) return 0f;
                left = Remaining;
                need = SimSpace.FlatDistance(a.Position, site.Center) / Options.RunSpeed + Options.PlantDuration;
            }
            else if (!a.Attacking && SpikeState == SpikeStatus.Planted)
            {
                left = SpikeTimer;
                need = SimSpace.FlatDistance(a.Position, SpikePosition) / Options.RunSpeed + Options.DefuseDuration;
                if (left < need) return 0f;   // no defuse possible any more: the director has it save
                return Options.DefenderUrgency * Profile.Clamp01(1f - (left - need - Options.UrgencyMargin) / Options.UrgencyWindow);
            }
            else return 0f;
            return Profile.Clamp01(1f - (left - need - Options.UrgencyMargin) / Options.UrgencyWindow);
        }
    }
}
