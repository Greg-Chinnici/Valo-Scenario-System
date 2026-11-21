// RoundSim.Decisions.cs - each AI agent's tick: carry out an order, or ask the brain (UtilityBrain) what to do
// given the director's objective and what it believes, and turn the choice into a goal, a pace and a look (and for a
// peek or a trade, the way it swings: PeekStyle).
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
            // a jiggle is one quick move out and back: it decides again once it is back
            bool jiggling = a.Action == AgentAction.Peek && a.Swing == PeekStyle.Jiggle;
            if (!jiggling && (a.WantsDecision || Time >= a.NextDecision)) Decide(a);
            if (a.Action == AgentAction.Peek && a.Swing == PeekStyle.Jiggle) { Jiggle(a, dt); return; }
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

        /// <summary>Longest a jiggle lasts, seconds: out and back is about a second at a run.</summary>
        const float JiggleTimeout = 2.5f;

        /// <summary>A jiggle peek: out towards the believed enemy until it sees someone or has stepped JiggleDistance, then
        /// straight back where it came from without stopping to fight; back there (or after JiggleTimeout, e.g. when a
        /// teammate now stands there), it holds and decides again.</summary>
        void Jiggle(SimAgent a, float dt)
        {
            if ((a.PeekReturning && SimSpace.FlatDistance(a.Position, a.PeekOrigin) <= 0.5f) || Time >= a.JiggleEnds)
            {
                a.Action = AgentAction.Hold;
                a.Swing = PeekStyle.None;
                a.PeekReturning = false;
                a.Moving = false;
                a.Yaw = a.HoldYaw;
                a.WantsDecision = true;
                return;
            }
            if (!a.PeekReturning && AnyVisible(a)) a.PeekReturning = true;
            MoveTowards(a, a.PeekReturning ? a.PeekOrigin : a.Goal, dt);
            if (!a.PeekReturning && !a.Moving && a.PathIndex >= a.Path.Count)
            {
                a.PeekReturning = true;     // as far out as it goes
                MoveTowards(a, a.PeekOrigin, dt);
            }
            if (a.PeekReturning && !a.Moving)
            {
                // the path ends on a grid node: the last steps go straight back to where it stood
                var to = a.PeekOrigin - a.Position;
                to.Y = 0f;
                float d = to.Length();
                if (d > 0.01f && d <= Map.Grid.CellSize * 1.5f)
                {
                    a.Position += to / d * Math.Min(d, Options.RunSpeed * dt);
                    a.Moving = true;
                }
            }
            KeepSpacing(a, dt);
        }

        /// <summary>Where a peek (or a trade, reach = all the way) in style goes towards target: a wide swing comes out
        /// WideOffset to the open side of the straight line, a jiggle only JiggleDistance along the way there, a slice
        /// straight at it.</summary>
        Vector3 PeekGoal(SimAgent a, Vector3 target, PeekStyle style, float reach)
        {
            var to = target - a.Position;
            float d = to.Length();
            if (d < 0.01f) return target;
            if (style == PeekStyle.Jiggle) return StepsTowards(a.Position, target, Options.JiggleDistance);
            var straight = d > reach ? a.Position + to / d * reach : target;
            if (style != PeekStyle.Wide) return straight;
            var dir = new Vector3(to.X, 0f, to.Z);
            if (dir.LengthSquared() < 1e-4f) return straight;
            dir = Vector3.Normalize(dir);
            var side = new Vector3(-dir.Z, 0f, dir.X) * Options.WideOffset;
            // swing out into the open: the side whose spot has more room round it
            var g = Map.Grid;
            int left = g.Nearest(straight + side, 2f), right = g.Nearest(straight - side, 2f);
            bool leftOk = left >= 0 && Math.Abs(g.Position(left).Y - straight.Y) < 1.5f, rightOk = right >= 0 && Math.Abs(g.Position(right).Y - straight.Y) < 1.5f;
            if (leftOk && (!rightOk || g.Clearance[left] >= g.Clearance[right])) return g.Position(left);
            if (rightOk) return g.Position(right);
            return straight;
        }

        readonly List<int> stepsPath = new List<int>();

        /// <summary>The point distance metres along the walking path from -> to (to itself when it is closer; the straight
        /// line when there is no path): a few steps the way one would really go, never through a wall.</summary>
        Vector3 StepsTowards(Vector3 from, Vector3 to, float distance)
        {
            var g = Map.Grid;
            int start = g.Nearest(from), end = g.Nearest(to, 6f);
            var dir = to - from;
            float straight = dir.Length();
            if (start < 0 || end < 0 || !g.FindPath(start, end, stepsPath)) return straight > distance ? from + dir / straight * distance : to;
            // the node nearest distance along (grid nodes are a cell apart)
            float run = 0f, best = distance;
            var prev = from;
            var pick = from;
            foreach (int n in stepsPath)
            {
                var p = g.Position(n);
                run += SimSpace.FlatDistance(prev, p);
                prev = p;
                if (Math.Abs(run - distance) < best) { best = Math.Abs(run - distance); pick = p; }
                if (run >= distance) break;
            }
            return pick;
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

            s.Economy = Economy(a.Team);
            Vector3 threatPos = default;
            int threatEnemy = -1;
            float bestPriority = 0f;
            foreach (var e in agents)
            {
                if (!e.Alive || e.Team == a.Team) continue;
                if (sees[a.Index, e.Index]) s.EnemiesVisible++;
                if (!Believed(a, e.Index, out var sight, out float c) || c < 0.15f) continue;
                float d = Vector3.Distance(a.Position, sight.Position);
                if (c > 0.3f && d < 20f) s.EnemiesNear++;
                float priority = c * Math.Max(0.05f, 1f - d / 50f);
                if (priority > bestPriority) { bestPriority = priority; s.NearestThreat = d; s.ThreatCertainty = c; threatPos = sight.Position; threatEnemy = e.Index; }
            }
            s.JustKilled = Time - a.LastKillTime <= Options.RepositionWindow;
            if (s.JustKilled && threatEnemy < 0) threatPos = a.LastKillPosition;   // where the trade will come from
            // loadouts are public at the start of the round: whether this distance suits its gun better than the threat's
            if (threatEnemy >= 0)
                s.RangeFit = Math.Max(-1f, Math.Min(1f, 2f * (a.Weapon.Effectiveness(s.NearestThreat) - agents[threatEnemy].Weapon.Effectiveness(s.NearestThreat))));

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
                if (death.Killer >= 0 && agents[death.Killer].Alive && Believed(a, death.Killer, out var ks, out float kc) && kc > 0.3f)
                {
                    tradePos = ks.Position;
                    s.KillerCertainty = kc;
                }
            }

            UtilityBrain.Score(s, a.Profile, a.Scores);
            var previous = a.Action;
            a.Action = UtilityBrain.Pick(a.Scores, a.Action, a.Profile, decideRng[a.Index]);
            // a peek or trade keeps the style it started with
            bool swinging = a.Action == AgentAction.Peek || a.Action == AgentAction.Trade;
            if (!swinging) { a.Swing = PeekStyle.None; a.PeekReturning = false; }
            else if (a.Action != previous || a.Swing == PeekStyle.None)
            {
                a.Swing = UtilityBrain.ChoosePeek(s, a.Profile, a.Action == AgentAction.Trade, decideRng[a.Index]);
                a.PeekOrigin = a.Position;
                a.PeekReturning = false;
                a.JiggleEnds = Time + JiggleTimeout;
            }
            switch (a.Action)
            {
                case AgentAction.Advance: a.Goal = OwnSpot(a, objective); break;
                case AgentAction.Peek: a.Goal = PeekGoal(a, threatPos, a.Swing, Options.PeekDistance); break;
                case AgentAction.FallBack: a.Goal = Retreat(a, threatPos); break;
                case AgentAction.Trade: a.Goal = PeekGoal(a, tradePos, a.Swing, float.MaxValue); break;
                default: a.Goal = a.Position; break;
            }
            // passive players walk (silently) near known enemies; aggressive ones (or anyone short of time) run, unless their
            // game sense says to keep quiet while there is time. A slice is walked, a wide swing or a jiggle run.
            bool approaching = a.Action == AgentAction.Peek || a.Action == AgentAction.Advance;
            if (swinging && a.Swing != PeekStyle.None) a.Walking = a.Swing == PeekStyle.Slice;
            else a.Walking = (UtilityBrain.EffectiveAggression(s, a.Profile) < 0.5f && (a.Action == AgentAction.Peek || (a.Action == AgentAction.Advance && s.NearestThreat < 25f)))
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
