// PlanContext.cs - what a team plan sees of the round and can do in it: read the round, ask what the team believes,
// find points along walking paths, and have the team decide again. Plans hand out objectives through it; they never
// move agents themselves.
using System;
using System.Collections.Generic;
using System.Numerics;

namespace ValorantReplay.Sim
{
    public sealed class PlanContext
    {
        readonly Dictionary<(Vector3, Vector3, float), Vector3> along = new Dictionary<(Vector3, Vector3, float), Vector3>();

        internal PlanContext(RoundSim sim, int team, Random random)
        {
            Sim = sim;
            Team = team;
            Random = random;
        }

        public RoundSim Sim { get; }
        public int Team { get; }
        public SimMap Map => Sim.Map;
        public SimOptions Options => Sim.Options;
        /// <summary>Round time left before the plant.</summary>
        public float Remaining => Sim.Remaining;
        /// <summary>The round's own random stream (SimRandom). Draw from it in Begin only: a draw mid-round would shift
        /// every later round-level draw, and two variants of a round would no longer share their dice.</summary>
        public Random Random { get; }
        /// <summary>The other team's director (its plan, whether it executes).</summary>
        public TeamDirector Enemy => Sim.Director(1 - Team);

        /// <summary>This team's agents in index order, the dead included.</summary>
        public IEnumerable<SimAgent> Members
        {
            get { foreach (var a in Sim.Agents) if (a.Team == Team) yield return a; }
        }

        public bool CarriesSpike(SimAgent a) => Sim.SpikeState == SpikeStatus.Carried && Sim.SpikeCarrier == a.Index;

        /// <summary>Living enemies anyone on the team believes (certainty > 0.4) are within radius of p.</summary>
        public int BelievedEnemiesNear(Vector3 p, float radius) => Sim.BelievedEnemiesNear(Team, p, radius);

        /// <summary>Enemies believed to be committing to site (see RoundSim.BelievedEnemiesCommitting).</summary>
        public int BelievedEnemiesCommitting(SimMap.Site site) => Sim.BelievedEnemiesCommitting(Team, site);

        /// <summary>A defender's hold spot on its assigned site, from Map.Holds; false without one.</summary>
        public bool HoldSpot(SimAgent a, out Vector3 spot) => Sim.HoldSpotFor(a, out spot);

        /// <summary>The site after name in the map's order, wrapping round (the first site when name is none).</summary>
        public SimMap.Site SiteAfter(string name)
        {
            var sites = Map.Sites;
            return sites[(sites.FindIndex(s => s.Name == name) + 1) % sites.Count];
        }

        /// <summary>Every agent on the team decides again on its next tick instead of at its next decision time, e.g.
        /// because its objective just moved.</summary>
        public void Redecide()
        {
            foreach (var a in Members) a.WantsDecision = true;
        }

        public void Redecide(SimAgent a) => a.WantsDecision = true;

        /// <summary>The point fraction of the way along the walking path from -> to (the straight line between them where
        /// there is no path).</summary>
        public Vector3 PointAlong(Vector3 from, Vector3 to, float fraction)
        {
            var key = (from, to, fraction);
            if (along.TryGetValue(key, out var p)) return p;
            var g = Map.Grid;
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
            along[key] = p;
            return p;
        }
    }
}
