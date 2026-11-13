// HoldModel.cs - where defenders play, learned from replays: every SampleInterval from SetupTime after the barrier drop
// until the round's first kill or the plant, each living defender adds where it is to a CellSize patch, and patches belong
// to their nearest site. Real defenders play off their site, not on its centre: long angles, heaven, mid and info spots
// (27-33 m from the site centre 25 s in, against 17-19 m for agents around the centre), and they rarely stand still, so
// every position counts, not only standing ones. Agents with a HoldModel draw their spot from these patches, in proportion
// to how long real defenders were there, so fights start at the distances real ones do.
using System;
using System.Collections.Generic;
using System.Numerics;
using ValorantReplay.Playback;

namespace ValorantReplay.Sim
{
    public sealed class HoldModel
    {
        public const float CellSize = 2f;
        public const float SampleInterval = 0.5f;
        /// <summary>Seconds after the barrier drop before defenders count as set up.</summary>
        public const float SetupTime = 5f;
        /// <summary>Patches further than this from their nearest site are not anyone's hold.</summary>
        public const float MaxReach = 50f;
        /// <summary>Teammates holding one site keep at least this far apart.</summary>
        public const float MinSeparation = 5f;

        public sealed class Spot
        {
            public Vector3 Position;
            public float Weight;     // samples (seconds x 2)
        }

        readonly Dictionary<string, List<Spot>> sites = new Dictionary<string, List<Spot>>();
        public int Samples { get; private set; }

        public IReadOnlyList<Spot> SpotsOf(string site) => sites.TryGetValue(site ?? "", out var l) ? l : (IReadOnlyList<Spot>)Array.Empty<Spot>();
        public bool Known => Samples > 0;

        /// <summary>Learns from replays of one map whose sites are already known (SimMap.Sites).</summary>
        public static HoldModel FromReplays(IEnumerable<VrpReplay> replays, IList<SimMap.Site> siteList)
        {
            var m = new HoldModel();
            if (siteList == null || siteList.Count == 0) return m;
            var cells = new Dictionary<(int, int, int), (Vector3 sum, float n)>();
            foreach (var r in replays)
            {
                int n = r.Players.Count;
                for (int ri = 0; ri < r.Rounds.Count; ri++)
                {
                    var round = r.Rounds[ri];
                    if (round.BarrierTime < 0f || round.AttackingTeam > 1) continue;
                    float end = round.PlantTime >= 0f ? round.PlantTime : round.EndTime;
                    foreach (var k in r.Kills)
                        if (k.Time >= round.BarrierTime && k.Time < end) { end = k.Time; break; }
                    for (float t = round.BarrierTime + SetupTime; t < end; t += SampleInterval)
                        for (int p = 0; p < n; p++)
                        {
                            if (r.Players[p].Team == round.AttackingTeam) continue;
                            var f = r.Sample(p, t);
                            if (!f.Alive) continue;
                            var pos = SimSpace.FromReplay(r, f);
                            var key = ((int)Math.Floor(pos.X / CellSize), (int)Math.Floor(pos.Z / CellSize), (int)Math.Floor(pos.Y / 3f));
                            cells.TryGetValue(key, out var c);
                            cells[key] = (c.sum + pos, c.n + 1f);
                            m.Samples++;
                        }
                }
            }
            foreach (var c in cells.Values)
            {
                var pos = c.sum / c.n;
                SimMap.Site best = null;
                float bd = MaxReach;
                foreach (var s in siteList)
                {
                    float d = SimSpace.FlatDistance(s.Center, pos);
                    if (d <= bd) { bd = d; best = s; }
                }
                if (best == null) continue;
                if (!m.sites.TryGetValue(best.Name, out var l)) m.sites[best.Name] = l = new List<Spot>();
                l.Add(new Spot { Position = pos, Weight = c.n });
            }
            foreach (var l in m.sites.Values) l.Sort((a, b) => b.Weight.CompareTo(a.Weight));
            return m;
        }

        /// <summary>A spot on site drawn in proportion to how long defenders held there, at least MinSeparation from every
        /// position in taken (a few tries, then the best found); false when the site has no spots.</summary>
        public bool Draw(string site, Random rng, IList<Vector3> taken, out Vector3 spot)
        {
            spot = default;
            var l = SpotsOf(site);
            if (l.Count == 0) return false;
            float total = 0f;
            foreach (var s in l) total += s.Weight;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                double x = rng.NextDouble() * total;
                Spot pick = l[l.Count - 1];
                foreach (var s in l) { x -= s.Weight; if (x <= 0) { pick = s; break; } }
                spot = pick.Position;
                bool free = true;
                foreach (var q in taken) if (SimSpace.FlatDistance(q, spot) < MinSeparation) { free = false; break; }
                if (free) return true;
            }
            return true;
        }
    }
}
