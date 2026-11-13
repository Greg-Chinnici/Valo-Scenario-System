// TeamDirector.cs - team-level strategy. Attackers pick a site and a plan (when to execute), and rotate once if they
// learn the site is stacked; defenders split across sites and retake after a plant. The director only hands each
// agent an objective; whether the agent goes there is the agent's own decision (UtilityBrain).
using System;
using System.Collections.Generic;
using System.Numerics;

namespace ValorantReplay.Sim
{
    public enum AttackPlan : byte
    {
        Default = 0,    // take space towards the site, execute when attacks on the map do (TempoModel), else mid-round
        Rush = 1,       // execute immediately
        Slow = 2,       // take space, execute late
    }

    public sealed class TeamDirector
    {
        public readonly int Team;
        public readonly bool Attacking;
        public AttackPlan Plan;
        public string TargetSite = "";
        public bool Executing;
        public int Rotations;
        /// <summary>Execute once this much round time is left.</summary>
        public float ExecuteAt;
        /// <summary>Believed defenders on the target site that make the attackers rotate (once, with time left).</summary>
        public int StackThreshold = 3;

        readonly Dictionary<string, Vector3> staging = new Dictionary<string, Vector3>();
        readonly Dictionary<string, Vector3> push = new Dictionary<string, Vector3>();

        public TeamDirector(int team, bool attacking)
        {
            Team = team;
            Attacking = attacking;
        }

        internal void Setup(RoundSim sim, AttackPlan plan, string targetSite, Random rng)
        {
            var sites = sim.Map.Sites;
            if (Attacking)
            {
                Plan = plan;
                ExecuteAt = plan == AttackPlan.Rush ? float.PositiveInfinity : plan == AttackPlan.Slow ? 30f : 55f;
                // the default plan goes in when attacks on this map really do (TempoModel), drawn per round
                if (plan == AttackPlan.Default && sim.Map.Tempo != null && sim.Map.Tempo.Known)
                    ExecuteAt = sim.Options.RoundTime - sim.Map.Tempo.DrawExecute(rng);
                TargetSite = targetSite ?? (sites.Count > 0 ? sites[rng.Next(sites.Count)].Name : "");
                Executing = sim.Remaining <= ExecuteAt;
                return;
            }
            // defenders: anchors first, then everyone else, dealt round-robin over the sites
            var defenders = new List<SimAgent>();
            foreach (var a in sim.Agents) if (a.Team == Team) defenders.Add(a);
            defenders.Sort((x, y) => (x.Profile.Style == PlayStyle.Anchor ? 0 : 1).CompareTo(y.Profile.Style == PlayStyle.Anchor ? 0 : 1) * 100 + x.Index.CompareTo(y.Index));
            for (int i = 0; i < defenders.Count; i++)
                defenders[i].AssignedSite = sites.Count > 0 ? sites[i % sites.Count].Name : "";
        }

        /// <summary>Defenders: believed attackers committing to one site that make the other sites' defenders rotate (once).</summary>
        public int RotateOnContact = 2;

        /// <summary>Defenders after the plant: regroup outside the site, retake together.</summary>
        public bool Retaking;
        Vector3 retakePoint;
        bool haveRetakePoint;

        internal void Update(RoundSim sim)
        {
            if (sim.SpikeState == SpikeStatus.Planted)
            {
                if (!Attacking) RetakeTiming(sim);
                return;
            }
            if (!Attacking) { DefenderRotation(sim); return; }
            if (!Executing && sim.Remaining <= ExecuteAt) Executing = true;
            if (Rotations > 0 || sim.Remaining < 25f || sim.Map.Sites.Count < 2) return;
            var site = sim.Map.SiteNamed(TargetSite);
            if (site == null || sim.BelievedEnemiesNear(Team, site.Center, site.Radius * 2f) < StackThreshold) return;
            string best = TargetSite;
            int fewest = int.MaxValue;
            foreach (var s in sim.Map.Sites)
            {
                if (s.Name == TargetSite) continue;
                int n = sim.BelievedEnemiesNear(Team, s.Center, s.Radius * 2f);
                if (n < fewest) { fewest = n; best = s.Name; }
            }
            TargetSite = best;
            Rotations++;
            foreach (var a in sim.Agents) if (a.Team == Team) a.WantsDecision = true;
        }

        // once callouts put RotateOnContact attackers on the way to one site and none to the others, everyone from the other sites
        // rotates in except one player per site (an anchor if there is one) who stays to watch for a fake
        void DefenderRotation(RoundSim sim)
        {
            var sites = sim.Map.Sites;
            if (Rotations > 0 || sites.Count < 2) return;
            SimMap.Site hit = null;
            foreach (var site in sites)
            {
                int n = sim.BelievedEnemiesCommitting(Team, site);
                if (n >= RotateOnContact) { if (hit != null) return; hit = site; }
                else if (n > 0) return;
            }
            if (hit == null) return;
            Rotations++;
            foreach (var site in sites)
            {
                if (site == hit) continue;
                var here = new List<SimAgent>();
                foreach (var a in sim.Agents) if (a.Team == Team && a.Alive && a.AssignedSite == site.Name) here.Add(a);
                var stay = here.Find(a => a.Profile.Style == PlayStyle.Anchor) ?? (here.Count > 0 ? here[0] : null);
                foreach (var a in here)
                {
                    if (a == stay) continue;
                    a.AssignedSite = hit.Name;
                    a.WantsDecision = true;
                }
            }
        }

        // go in once most of the living defenders are at the regroup point, or when waiting longer would leave too little
        // time to defuse
        void RetakeTiming(RoundSim sim)
        {
            if (Retaking) return;
            if (!haveRetakePoint)
            {
                retakePoint = Along(sim, new Dictionary<string, Vector3>(), "retake", sim.SpikePosition, sim.Map.DefenderSpawn, 0.45f);
                haveRetakePoint = true;
            }
            int alive = 0, ready = 0;
            foreach (var a in sim.Agents)
            {
                if (a.Team != Team || !a.Alive) continue;
                alive++;
                if (SimSpace.FlatDistance(a.Position, retakePoint) <= 8f) ready++;
            }
            if (ready >= Math.Max(1, alive - 1) || sim.SpikeTimer < sim.Options.DefuseDuration + 15f)
            {
                Retaking = true;
                foreach (var a in sim.Agents) if (a.Team == Team) a.WantsDecision = true;
            }
        }

        /// <summary>Where this agent should be now, and how close counts as there.</summary>
        internal Vector3 Objective(RoundSim sim, SimAgent a, out float radius)
        {
            radius = 3f;
            var map = sim.Map;
            if (sim.SpikeState == SpikeStatus.Planted)
            {
                // a defender who can no longer reach the spike and defuse in time saves instead of dying for nothing.
                // (Saving when outnumbered was tried: at 2 or 3 down it made detonations 48% / 30% vs 8% recorded.)
                if (!Attacking && sim.SpikeTimer < SimSpace.FlatDistance(a.Position, sim.SpikePosition) / sim.Options.RunSpeed + sim.Options.DefuseDuration)
                {
                    radius = 8f;
                    return map.DefenderSpawn;
                }
                if (Attacking || Retaking || !haveRetakePoint) return sim.SpikePosition;   // post-plant / retake
                radius = 8f;
                return retakePoint;
            }
            if (Attacking)
            {
                if (sim.SpikeState == SpikeStatus.Dropped && sim.NearestAiAttackerToSpike() == a.Index) { radius = 1f; return sim.SpikePosition; }
                string site = TargetSite;
                if (a.Profile.Style == PlayStyle.Lurker && !Executing && map.Sites.Count > 1)
                    site = map.Sites[(map.Sites.FindIndex(s => s.Name == TargetSite) + 1) % map.Sites.Count].Name;
                var s0 = map.SiteNamed(site);
                if (s0 == null) return map.DefenderSpawn;
                if (Executing && site == TargetSite) { radius = s0.Radius; return s0.Center; }
                radius = 4f;
                return Along(sim, staging, site, map.AttackerSpawn, s0.Center, 0.55f);
            }
            var mine = map.SiteNamed(a.AssignedSite);
            if (mine == null) return map.DefenderSpawn;
            // aggressive non-anchors take space towards the attackers until the attack commits
            var attackers = sim.Director(1 - Team);
            if (a.Profile.Style != PlayStyle.Anchor && a.Profile.Aggression > 0.7f && attackers != null && !attackers.Executing)
            {
                radius = 4f;
                return Along(sim, push, mine.Name, mine.Center, map.AttackerSpawn, 0.35f);
            }
            // where real defenders hold this site (Map.Holds), else anywhere on it
            if (sim.HoldSpotFor(a, out var spot)) { radius = 2.5f; return spot; }
            radius = mine.Radius;
            return mine.Center;
        }

        // the point a fraction of the way along the walking path from -> to (cached per site)
        static Vector3 Along(RoundSim sim, Dictionary<string, Vector3> cache, string key, Vector3 from, Vector3 to, float fraction)
        {
            if (cache.TryGetValue(key, out var p)) return p;
            var g = sim.Map.Grid;
            int a = g.Nearest(from, 8f), b = g.Nearest(to, 8f);
            var path = new List<int>();
            p = Vector3.Lerp(from, to, fraction);
            if (a >= 0 && b >= 0 && g.FindPath(a, b, path) && path.Count > 0)
            {
                float total = 0f;
                var prev = g.Position(a);
                foreach (int n in path) { total += Vector3.Distance(prev, g.Position(n)); prev = g.Position(n); }
                float want = total * fraction, run = 0f;
                prev = g.Position(a);
                foreach (int n in path)
                {
                    run += Vector3.Distance(prev, g.Position(n));
                    prev = g.Position(n);
                    if (run >= want) { p = prev; break; }
                }
            }
            cache[key] = p;
            return p;
        }
    }
}
