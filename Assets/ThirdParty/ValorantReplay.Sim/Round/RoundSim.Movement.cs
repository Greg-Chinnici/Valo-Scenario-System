// RoundSim.Movement.cs - where exactly an agent goes and how: its spot in the formation, pathing (corners,
// exposure), spacing from teammates, retreating, hold spots, and where its eyes wander.
using System;
using System.Collections.Generic;
using System.Numerics;

namespace ValorantReplay.Sim
{
    public sealed partial class RoundSim
    {
        // nudge away from teammates that are too close, staying on walkable cells; the spike carrier holds its line and
        // the others make room
        void KeepSpacing(SimAgent a, float dt)
        {
            if (SpikeState == SpikeStatus.Carried && SpikeCarrier == a.Index) return;
            var push = Vector3.Zero;
            foreach (var m in agents)
            {
                if (m == a || !m.Alive || m.Team != a.Team) continue;
                var away = a.Position - m.Position;
                away.Y = 0f;
                float d = away.Length(), spacing = Math.Max(Options.Spacing, SpreadOf(a) * 0.6f);
                if (d >= spacing) continue;
                if (d < 1e-3f)
                {
                    // exactly on top of each other: split by index
                    float ang = (a.Index * 2.399f) % 6.2832f;
                    away = new Vector3((float)Math.Cos(ang), 0f, (float)Math.Sin(ang));
                    d = 0f;
                }
                else away /= d;
                push += away * (spacing - d);
            }
            // a moving agent is never pushed back along its own path (that would freeze the marching order)
            if (a.Moving)
            {
                var fwd = a.Forward;
                float back = Vector3.Dot(push, fwd);
                if (back < 0f) push -= fwd * back;
            }
            if (push.LengthSquared() < 1e-6f) return;
            float max = Options.WalkSpeed * dt;
            if (push.Length() > max) push = Vector3.Normalize(push) * max;
            var p = a.Position + push;
            foreach (int n in Map.Grid.NodesAt(p.X, p.Z))
                if (Math.Abs(Map.Grid.Nodes[n].Y - a.Position.Y) < 1f) { a.Position = p; return; }
        }

        // each agent's own spot around the team objective (Profile.Spread away, a fixed direction per agent), so a team
        // does not walk single file into one point. The defender closest to a planted spike goes to the spike itself.
        Vector3 OwnSpot(SimAgent a, Vector3 objective)
        {
            if (SpikeState == SpikeStatus.Planted && !a.Attacking)
            {
                bool closest = true;
                float mine = Vector3.Distance(a.Position, SpikePosition);
                foreach (var m in agents)
                    if (m != a && m.Alive && !m.Attacking && m.Mode == AgentMode.Ai && Vector3.Distance(m.Position, SpikePosition) < mine) { closest = false; break; }
                if (closest) return objective;
            }
            if (SpikeState == SpikeStatus.Dropped && a.Attacking && NearestAiAttackerToSpike() == a.Index) return objective;
            // a defender holding a learned spot holds exactly there
            if (!a.Attacking && SpikeState != SpikeStatus.Planted && a.HoldSite != null && a.HoldSite == a.AssignedSite && Vector3.Distance(objective, a.HoldSpot) < 0.01f) return objective;
            // the spike carrier executing on its site goes to the plant spots themselves, not a spot around them that can
            // lie outside the site
            if (SpikeState == SpikeStatus.Carried && SpikeCarrier == a.Index && directors[a.Team].Executing) return objective;
            double ang = a.Index * 2.399;   // golden angle: neighbours by index end up far apart
            float spread = SpreadOf(a);
            var p = objective + new Vector3((float)Math.Cos(ang), 0f, (float)Math.Sin(ang)) * spread;
            // aggressive players take the front of the formation (towards the enemy's side), passive ones the back
            var front = a.Attacking ? objective - Map.AttackerSpawn : Map.AttackerSpawn - objective;
            front.Y = 0f;
            if (front.LengthSquared() > 1f) p += Vector3.Normalize(front) * ((a.Profile.Aggression - 0.5f) * spread);
            int n = Map.Grid.Nearest(p, 3f);
            if (n < 0) return objective;
            var q = Map.Grid.Position(n);
            return Math.Abs(q.Y - objective.Y) < 2f ? q : objective;
        }

        // Profile.Spread is fitted to the spacing before the plant; around a planted spike teams play tighter
        float SpreadOf(SimAgent a) => a.Profile.Spread * (SpikeState == SpikeStatus.Planted ? Options.PostPlantSpread : 1f);

        Vector3 Retreat(SimAgent a, Vector3 threat)
        {
            float mine = Vector3.Distance(a.Position, threat);
            SimAgent best = null;
            float bd = float.MaxValue;
            foreach (var m in agents)
            {
                if (!m.Alive || m.Team != a.Team || m == a || Vector3.Distance(m.Position, threat) <= mine + 3f) continue;
                float d = Vector3.Distance(m.Position, a.Position);
                if (d < bd) { bd = d; best = m; }
            }
            if (best != null) return best.Position;
            return a.Attacking ? Map.AttackerSpawn : Map.DefenderSpawn;
        }

        void MoveTowards(SimAgent a, Vector3 goal, float dt)
        {
            var g = Map.Grid;
            bool goalChanged = float.IsNaN(a.PathGoal.X) || Vector3.Distance(goal, a.PathGoal) > 2f;
            if (goalChanged || (a.PathIndex >= a.Path.Count && SimSpace.FlatDistance(a.Position, goal) > 1.5f && Time >= a.NextDecision - 0.01f))
            {
                a.PathGoal = goal;
                a.PathIndex = 0;
                var exposure = a.Mode == AgentMode.Ai ? Exposure(a) : null;
                Func<int, float> cost = exposure;
                // wide corners when there is time; orders and a ticking clock take the direct line
                float k = Options.CornerCost * (1f - a.Urgency);
                bool carrying = SpikeState == SpikeStatus.Carried && SpikeCarrier == a.Index && directors[a.Team].Executing;
                if (a.Mode == AgentMode.Ai && k > 0f && a.Order.Type == OrderType.None && !carrying)
                {
                    var clear = g.Clearance;
                    float margin = Options.CornerMargin;
                    cost = exposure == null ? (Func<int, float>)(n => k * Math.Max(0f, 1f - clear[n] / margin))
                        : n => k * Math.Max(0f, 1f - clear[n] / margin) + exposure(n);
                }
                if (!g.FindPath(g.Nearest(a.Position), g.Nearest(goal, 6f), a.Path, cost)) a.Path.Clear();
            }
            float speed = a.Walking ? Options.WalkSpeed : Options.RunSpeed;
            if (a.Action == AgentAction.Advance || a.Action == AgentAction.Peek) speed *= a.Profile.Pace;
            float budget = speed * dt;
            var start = a.Position;
            while (budget > 1e-4f && a.PathIndex < a.Path.Count)
            {
                var target = g.Position(a.Path[a.PathIndex]);
                var delta = target - a.Position;
                float dist = delta.Length();
                if (dist <= budget) { a.Position = target; budget -= dist; a.PathIndex++; }
                else { a.Position += delta / dist * budget; budget = 0f; }
            }
            var moved = a.Position - start;
            a.Moving = moved.X * moved.X + moved.Z * moved.Z > 1e-4f;
            // while moving: at a believed threat (players walking towards a sound look at it); else an angle players check
            // here within MovingCone of the way it walks; else where it walks
            if (a.Moving && !(a.Target >= 0 && sees[a.Index, a.Target]) && a.WatchesThreat && SimSpace.FlatDistance(a.ThreatPosition, a.Position) > 1f)
                a.Yaw = SimSpace.YawOf(a.ThreatPosition - a.Position) + a.LookOffset;
            else if (a.Moving && !(a.Target >= 0 && sees[a.Index, a.Target]))
                a.Yaw = (Map.Look != null && Map.Look.TryLook(a.Position, a.Attacking, moved, a.LookQuantile, out float learned) ? learned : SimSpace.YawOf(moved))
                    + a.LookOffset;
        }

        /// <summary>A defender's hold spot on its assigned site, from Map.Holds (drawn once per site, clear of teammates'
        /// spots); false without a hold model or spots for the site.</summary>
        internal bool HoldSpotFor(SimAgent a, out Vector3 spot)
        {
            spot = default;
            if (Map.Holds == null || a.Attacking || string.IsNullOrEmpty(a.AssignedSite)) return false;
            if (a.HoldSite != a.AssignedSite)
            {
                var taken = new List<Vector3>();
                foreach (var m in agents) if (m != a && m.Team == a.Team && m.Alive && m.HoldSite == a.AssignedSite) taken.Add(m.HoldSpot);
                if (!Map.Holds.Draw(a.AssignedSite, lookRng[a.Index], taken, out var s)) return false;
                a.HoldSite = a.AssignedSite;
                a.HoldSpot = s;
            }
            spot = a.HoldSpot;
            return true;
        }

        /// <summary>Wandering eyes (GlanceRate): out of a fight, now and then look GlanceMin..GlanceMax deg off where the
        /// agent means to look, for 0.3-1 s; game sense keeps the eyes on a believed threat. Applied after Think set the
        /// yaw, as an offset (taken off again when Think left the yaw alone).</summary>
        void Glance(SimAgent a)
        {
            if (a.GlanceApplied != 0f && a.Yaw == a.YawWithGlance) a.Yaw -= a.GlanceApplied;
            a.GlanceApplied = 0f;
            bool busy = (a.Target >= 0 && sees[a.Index, a.Target]) || a.Action == AgentAction.Plant || a.Action == AgentAction.Defuse;
            float rate = Options.GlanceRate * 2f * (1f - a.Profile.Skill) * a.Profile.GlanceWander;
            if (rate <= 0f || busy) { a.GlanceUntil = -1f; return; }
            var rng = lookRng[a.Index];
            if (Time >= a.GlanceUntil && Time >= a.NextGlance)
            {
                a.NextGlance = Time + (float)(-Math.Log(1.0 - rng.NextDouble()) / rate);
                if (!(a.WatchesThreat && rng.NextDouble() < a.Profile.Skill))
                {
                    a.GlanceOffset = (rng.NextDouble() < 0.5 ? -1f : 1f) * Profile.Lerp(Options.GlanceMin, Options.GlanceMax, (float)rng.NextDouble());
                    a.GlanceUntil = Time + Profile.Lerp(0.3f, 1f, (float)rng.NextDouble());
                }
            }
            if (Time >= a.GlanceUntil) return;
            a.Yaw += a.GlanceOffset;
            a.GlanceApplied = a.GlanceOffset;
            a.YawWithGlance = a.Yaw;
        }

        /// <summary>Extra path cost per metre for agent a (RouteCare): how exposed each node is to the enemies a believes in,
        /// scaled by its game sense; null when it does not care or believes in no one.</summary>
        Func<int, float> Exposure(SimAgent a)
        {
            float care = Options.RouteCare * a.Profile.Skill;
            if (care <= 0f || Options.MaxRouteThreats <= 0) return null;
            var threats = new List<(Vector3 eye, float weight)>();
            foreach (var e in agents)
            {
                if (!e.Alive || e.Team == a.Team || !Believed(a, e.Index, out var sight, out float c) || c < 0.2f) continue;
                if (SimSpace.FlatDistance(sight.Position, a.Position) > Options.ExposureRange * 2f) continue;
                threats.Add((sight.Position + new Vector3(0f, 0.65f, 0f), c));
            }
            if (threats.Count == 0) return null;
            if (threats.Count > Options.MaxRouteThreats)
            {
                threats.Sort((x, y) => y.weight.CompareTo(x.weight));
                threats.RemoveRange(Options.MaxRouteThreats, threats.Count - Options.MaxRouteThreats);
            }
            var g = Map.Grid;
            var known = new Dictionary<int, float>();
            return n =>
            {
                if (known.TryGetValue(n, out float v)) return v;
                var p = g.Position(n);
                var eye = p + new Vector3(0f, 0.65f, 0f);
                v = 0f;
                foreach (var (te, w) in threats)
                {
                    float d = SimSpace.FlatDistance(te, p);
                    if (d < Options.ExposureRange && g.LineOfSight(te, eye)) v += w * (1f - d / Options.ExposureRange);
                }
                v *= care;
                known[n] = v;
                return v;
            };
        }
    }
}
