// RoundSim.Perception.cs - who sees and hears whom (every PerceptionInterval), what that does to beliefs and
// callouts, and the belief queries decisions and the directors read.
using System;
using System.Collections.Generic;
using System.Numerics;

namespace ValorantReplay.Sim
{
    public sealed partial class RoundSim
    {
        void Perceive()
        {
            int n = agents.Count;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    var a = agents[i];
                    var b = agents[j];
                    if (!a.Alive || !b.Alive || a.Team == b.Team) { sees[i, j] = sees[j, i] = false; continue; }
                    float d = Vector3.Distance(a.Position, b.Position);
                    bool los = d <= Options.MaxSightRange && Clear(a.Eye, b.Eye);
                    See(a, b, los, d);
                    See(b, a, los, d);
                }
            foreach (var a in agents)
            {
                if (!a.Alive) continue;
                foreach (var b in agents)
                {
                    if (!b.Alive || b.Team == a.Team || !b.Moving || b.Walking || sees[a.Index, b.Index]) continue;
                    float d = Vector3.Distance(a.Position, b.Position);
                    if (d <= Options.FootstepRange && Time - a.LastHeard[b.Index] >= 0.5f) Hear(a, b, d, Options.FootstepRange, 0.6f, true);
                }
            }
        }

        void See(SimAgent a, SimAgent b, bool los, float d)
        {
            bool shotAtBy = b.Target == a.Index && Time - b.LastShot < 0.5f;
            bool view = los && (d < 3f || InFov(a, b.Position));
            bool noticed = view || (los && shotAtBy);
            notices.Look(a, b, view, Time);
            bool was = sees[a.Index, b.Index];
            sees[a.Index, b.Index] = noticed;
            onlyShotAt[a.Index, b.Index] = noticed && !view;
            if (!noticed) return;
            var s = new Sighting { Enemy = b.Index, Position = b.Position, Time = Time, Certainty = 1f, Cue = Cue.Seen };
            a.Beliefs.Observe(s, Time, a.Profile.MemoryHalfLife);
            if (!was || Time - a.LastCallout[b.Index] >= Options.CalloutRefresh * a.Profile.CalloutRefreshScale)
            {
                a.LastCallout[b.Index] = Time;
                Callout(a, s);
            }
            if (!was) a.WantsDecision = true;
            if (a.Mode == AgentMode.Replay && b.Mode != AgentMode.Replay) Diverge(a);
        }

        void Callout(SimAgent a, Sighting s)
        {
            bool made = boards[a.Team].Callout(s, a.Index, a.Profile, Time, commsRng[a.Index]);
            Log(new SimEvent { Type = made ? SimEventType.CalloutMade : SimEventType.CalloutDropped, Agent = a.Index, Other = s.Enemy, Position = s.Position });
        }

        // forcesDivergence: whether hearing a sound the recording does not have takes a replay agent off it. Footsteps do;
        // gunshots only within footstep range (a fight across the map does not change what a player does next).
        void Hear(SimAgent a, SimAgent b, float d, float range, float certainty, bool forcesDivergence)
        {
            a.LastHeard[b.Index] = Time;
            float err = a.Profile.HearingError * (d / range);
            var senses = sensesRng[a.Index];
            var off = new Vector3((float)(senses.NextDouble() * 2 - 1) * err, 0f, (float)(senses.NextDouble() * 2 - 1) * err);
            var s = new Sighting { Enemy = b.Index, Position = b.Position + off, Time = Time, Certainty = certainty * (1f - 0.5f * d / range), Cue = Cue.Heard };
            if (a.Beliefs.Observe(s, Time, a.Profile.MemoryHalfLife) && Time - a.LastCallout[b.Index] >= Options.CalloutRefresh * a.Profile.CalloutRefreshScale)
            {
                a.LastCallout[b.Index] = Time;
                Callout(a, s);
            }
            if (forcesDivergence && a.Mode == AgentMode.Replay && b.Mode != AgentMode.Replay) Diverge(a);
        }

        void Gunshot(SimAgent shooter)
        {
            foreach (var e in agents)
            {
                if (!e.Alive || e.Team == shooter.Team || sees[e.Index, shooter.Index]) continue;
                float d = Vector3.Distance(e.Position, shooter.Position);
                if (d <= Options.GunshotRange && Time - e.LastHeard[shooter.Index] >= 0.25f) Hear(e, shooter, d, Options.GunshotRange, 0.85f, d <= Options.FootstepRange);
            }
        }

        bool InFov(SimAgent a, Vector3 p)
        {
            var to = p - a.Position;
            to.Y = 0f;
            float len = to.Length();
            if (len < 1e-3f) return true;
            float cos = Vector3.Dot(a.Forward, to / len);
            return cos >= (float)Math.Cos(Options.FovHalfAngle * Math.PI / 180.0);
        }

        /// <summary>The better of the agent's own memory and its team board for one enemy.</summary>
        internal bool Believed(SimAgent a, int enemy, out Sighting s, out float certainty)
        {
            if (BeliefSource != null && BeliefSourceMode == BeliefSourceMode.Replace)
            {
                if (!BeliefSource.Believes(a.Team, enemy, out var at, out certainty) || certainty <= 0f) { s = default; certainty = 0f; return false; }
                certainty = Math.Min(1f, certainty);
                s = new Sighting { Enemy = enemy, Position = at, Time = Time, Certainty = certainty, Cue = Cue.Callout };
                return true;
            }
            // own beliefs, then callouts weighted by how much this agent trusts whoever made them, then an outside source
            float hl = a.Profile.MemoryHalfLife;
            bool own = a.Beliefs.TryGet(enemy, out var mine);
            bool team = boards[a.Team].Board.TryGet(enemy, out var shared);
            float cm = own ? mine.CertaintyAt(Time, hl) : 0f;
            float ct = team ? Math.Min(1f, shared.CertaintyAt(Time, hl) * trust.Scale(a, shared.Caller)) : 0f;
            s = cm >= ct ? mine : shared;
            certainty = Math.Max(cm, ct);
            if (BeliefSource != null && BeliefSource.Believes(a.Team, enemy, out var p, out float cx))
            {
                cx = Math.Min(1f, cx * trust.Scale(a, -1));
                if (cx > certainty)
                {
                    s = new Sighting { Enemy = enemy, Position = p, Time = Time, Certainty = cx, Cue = Cue.Callout, Caller = -1 };
                    certainty = cx;
                }
            }
            if (certainty <= 0f) { s = default; certainty = 0f; return false; }
            return true;
        }

        /// <summary>Living enemies of team that anyone on it believes (certainty > 0.4) are within radius of p.</summary>
        internal int BelievedEnemiesNear(int team, Vector3 p, float radius)
        {
            int count = 0;
            foreach (var e in agents)
            {
                if (!e.Alive || e.Team == team) continue;
                foreach (var a in agents)
                {
                    if (a.Team != team || !a.Alive) continue;
                    if (Believed(a, e.Index, out var s, out float c) && c > 0.4f && SimSpace.FlatDistance(s.Position, p) <= radius) { count++; break; }
                }
            }
            return count;
        }

        /// <summary>Enemies of team believed (certainty > 0.4) to be committing to site: nearest to it of all sites and
        /// past 40% of the way from the attacker spawn.</summary>
        internal int BelievedEnemiesCommitting(int team, SimMap.Site site)
        {
            float reach = 0.6f * SimSpace.FlatDistance(Map.AttackerSpawn, site.Center);
            int count = 0;
            foreach (var e in agents)
            {
                if (!e.Alive || e.Team == team) continue;
                foreach (var a in agents)
                {
                    if (a.Team != team || !a.Alive || !Believed(a, e.Index, out var s, out float c) || c <= 0.4f) continue;
                    if (Map.NearestSite(s.Position) == site && SimSpace.FlatDistance(s.Position, site.Center) <= reach) { count++; break; }
                }
            }
            return count;
        }

        internal int NearestAiAttackerToSpike()
        {
            int best = -1;
            float bd = float.MaxValue;
            // someone ordered to pick it up is the one going for it
            foreach (var a in agents) if (a.Alive && a.Attacking && a.Mode == AgentMode.Ai && a.Order.Type == OrderType.PickUp) return a.Index;
            foreach (var a in agents)
            {
                if (!a.Alive || !a.Attacking || a.Mode != AgentMode.Ai) continue;
                float d = Vector3.Distance(a.Position, SpikePosition);
                if (d < bd) { bd = d; best = a.Index; }
            }
            return best;
        }

        bool AnyVisible(SimAgent a)
        {
            for (int j = 0; j < agents.Count; j++) if (sees[a.Index, j]) return true;
            return false;
        }
    }
}
