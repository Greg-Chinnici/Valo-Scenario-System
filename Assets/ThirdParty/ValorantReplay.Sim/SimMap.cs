// SimMap.cs - everything the simulation knows about a map: the NavGrid, plant sites and spawns. Built from .vrp
// files alone (more replays of the map = better coverage), or from ASCII art for tests.
using System;
using System.Collections.Generic;
using System.Numerics;
using ValorantReplay.Playback;

namespace ValorantReplay.Sim
{
    /// <summary>Replay positions -> simulation space (Unity: metres, Y-up), whatever space the .vrp was written in.</summary>
    public static class SimSpace
    {
        public static Vector3 FromReplay(VrpReplay replay, float x, float y, float z)
        {
            if (replay.Space == CoordinateSpace.Unity) return new Vector3(x, y, z);
            VrpReplay.ToUnity(x, y, z, out float ux, out float uy, out float uz);
            return new Vector3(ux, uy, uz);
        }

        public static Vector3 FromReplay(VrpReplay replay, in Frame f) => FromReplay(replay, f.X, f.Y, f.Z);

        /// <summary>Horizontal unit vector for a yaw in degrees. Frame.Yaw works for both replay spaces: the Unreal->Unity
        /// axis swap (x = ueY, z = ueX) turns Unreal yaw into Unity's Y euler.</summary>
        public static Vector3 Forward(float yawDegrees)
        {
            double r = yawDegrees * Math.PI / 180.0;
            return new Vector3((float)Math.Sin(r), 0f, (float)Math.Cos(r));
        }

        public static float YawOf(Vector3 dir) => (float)(Math.Atan2(dir.X, dir.Z) * 180.0 / Math.PI);

        public static float FlatDistance(Vector3 a, Vector3 b)
        {
            float dx = a.X - b.X, dz = a.Z - b.Z;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }
    }

    public sealed class SimMap
    {
        public sealed class Site
        {
            public string Name = "";
            public Vector3 Center;
            public float Radius;
            public int Plants;      // recorded plants it was built from (0 for ASCII maps)
        }

        public NavGrid Grid;
        public string Map = "";
        public List<Site> Sites = new List<Site>();
        public Vector3 AttackerSpawn, DefenderSpawn;
        public int Replays;
        /// <summary>Where players look on this map (null = agents look where they walk, or at their objective).</summary>
        public LookModel Look;
        /// <summary>When attacks go in on this map (null = the attack plan's fixed timing).</summary>
        public TempoModel Tempo;

        /// <summary>The same map (grid, sites, spawns shared) with other learned behaviour, e.g. learned without the
        /// replay being predicted.</summary>
        public SimMap WithLearned(LookModel look, TempoModel tempo) => new SimMap
        {
            Grid = Grid, Map = Map, Sites = Sites, AttackerSpawn = AttackerSpawn, DefenderSpawn = DefenderSpawn, Replays = Replays,
            Look = look, Tempo = tempo,
        };

        public Site SiteNamed(string name) => Sites.Find(s => s.Name == name);

        public Site NearestSite(Vector3 p)
        {
            Site best = null;
            float bd = float.MaxValue;
            foreach (var s in Sites)
            {
                float d = SimSpace.FlatDistance(s.Center, p);
                if (d < bd) { bd = d; best = s; }
            }
            return best;
        }

        public bool InSite(Site s, Vector3 p) => s != null && SimSpace.FlatDistance(s.Center, p) <= s.Radius && Math.Abs(s.Center.Y - p.Y) < 3f;

        /// <summary>Builds a map from replays of the same map. Walkable space comes from every grounded player sample,
        /// sites from recorded plants (grouped by site letter), spawns from positions at each barrier drop, the look model
        /// from where players looked in combat, the tempo model from when they planted.</summary>
        public static SimMap FromReplays(IEnumerable<VrpReplay> replays, NavGrid.Builder builder = null)
        {
            builder = builder ?? new NavGrid.Builder();
            var map = new SimMap { Look = new LookModel(), Tempo = new TempoModel() };
            var plants = new Dictionary<string, List<Vector3>>();
            Vector3 atk = Vector3.Zero, def = Vector3.Zero;
            int na = 0, nd = 0;
            foreach (var r in replays)
            {
                if (map.Replays == 0) map.Map = r.Map;
                else if (r.Map != map.Map) throw new ArgumentException($"{r.MatchId} is on {r.Map}, not {map.Map}");
                map.Replays++;
                AddSamples(builder, r);
                map.Look.AddReplay(r);
                map.Tempo.AddReplay(r);
                foreach (var round in r.Rounds)
                {
                    if (round.PlantTime >= 0f && round.Site != "")
                    {
                        if (!plants.TryGetValue(round.Site, out var l)) plants[round.Site] = l = new List<Vector3>();
                        l.Add(SimSpace.FromReplay(r, round.PlantX, round.PlantY, round.PlantZ));
                    }
                    if (round.BarrierTime < 0f || round.AttackingTeam == 255) continue;
                    for (int p = 0; p < r.Players.Count; p++)
                    {
                        var f = r.Sample(p, round.BarrierTime);
                        if (!f.Alive) continue;
                        var pos = SimSpace.FromReplay(r, f);
                        if (r.Players[p].Team == round.AttackingTeam) { atk += pos; na++; } else { def += pos; nd++; }
                    }
                }
            }
            if (map.Replays == 0) throw new ArgumentException("no replays");
            map.Grid = builder.Build();
            if (na > 0) map.AttackerSpawn = atk / na;
            if (nd > 0) map.DefenderSpawn = def / nd;
            foreach (var kv in plants)
            {
                var c = Vector3.Zero;
                foreach (var p in kv.Value) c += p;
                c /= kv.Value.Count;
                float spread = 0f;
                foreach (var p in kv.Value) spread += Vector3.DistanceSquared(p, c);
                spread = (float)Math.Sqrt(spread / kv.Value.Count);
                map.Sites.Add(new Site { Name = kv.Key, Center = c, Radius = Math.Max(4f, 1.5f * spread + 2f), Plants = kv.Value.Count });
            }
            map.Sites.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return map;
        }

        /// <summary>Feeds every grounded, living, on-map sample of a replay into the builder, with the cells between
        /// consecutive samples (players move ~0.3 m per 20 Hz tick, so trails come out continuous).</summary>
        public static void AddSamples(NavGrid.Builder builder, VrpReplay r)
        {
            const FrameFlags skip = FrameFlags.OffMap | FrameFlags.Airborne;
            for (int p = 0; p < r.Players.Count; p++)
            {
                bool havePrev = false;
                Vector3 prev = default;
                for (int t = 0; t < r.TickCount; t++)
                {
                    var f = r.GetFrame(t, p);
                    if (!f.Alive || (f.Flags & FrameFlags.HasData) == 0 || (f.Flags & skip) != 0) { havePrev = false; continue; }
                    var pos = SimSpace.FromReplay(r, f);
                    if (havePrev && (f.Flags & FrameFlags.Snapped) == 0 && Vector3.Distance(prev, pos) < 1.5f) builder.AddSegment(prev, pos);
                    else builder.Add(pos);
                    prev = pos;
                    havePrev = true;
                }
            }
        }

        /// <summary>A flat test map from ASCII rows: '#' or ' ' is solid, anything else floor. Upper-case letters mark
        /// site cells (site = their centroid), 'a' / 'd' attacker / defender spawn cells. Row r is z = r, column c is x = c
        /// (times cellSize).</summary>
        public static SimMap FromAscii(string[] rows, float cellSize = 1f)
        {
            var nodes = new List<NavGrid.Node>();
            var sites = new SortedDictionary<char, List<Vector3>>();
            var atk = new List<Vector3>();
            var def = new List<Vector3>();
            for (int z = 0; z < rows.Length; z++)
            {
                if (rows[z].Length != rows[0].Length) throw new ArgumentException($"row {z} has {rows[z].Length} columns, row 0 has {rows[0].Length}");
                for (int x = 0; x < rows[z].Length; x++)
                {
                    char ch = rows[z][x];
                    if (ch == '#' || ch == ' ') continue;
                    nodes.Add(new NavGrid.Node { X = x, Z = z, Y = NavGrid.ActorHeight, Samples = 1 });
                    var c = new Vector3((x + 0.5f) * cellSize, NavGrid.ActorHeight, (z + 0.5f) * cellSize);
                    if (ch >= 'A' && ch <= 'Z')
                    {
                        if (!sites.TryGetValue(ch, out var l)) sites[ch] = l = new List<Vector3>();
                        l.Add(c);
                    }
                    else if (ch == 'a') atk.Add(c);
                    else if (ch == 'd') def.Add(c);
                }
            }
            var map = new SimMap { Grid = new NavGrid(cellSize, nodes), Map = "ascii" };
            foreach (var kv in sites)
            {
                var c = Vector3.Zero;
                foreach (var p in kv.Value) c += p;
                c /= kv.Value.Count;
                float radius = 0f;
                foreach (var p in kv.Value) radius = Math.Max(radius, Vector3.Distance(p, c));
                map.Sites.Add(new Site { Name = kv.Key.ToString(), Center = c, Radius = radius + cellSize });
            }
            map.AttackerSpawn = Mean(atk);
            map.DefenderSpawn = Mean(def);
            return map;
        }

        static Vector3 Mean(List<Vector3> l)
        {
            if (l.Count == 0) return Vector3.Zero;
            var s = Vector3.Zero;
            foreach (var p in l) s += p;
            return s / l.Count;
        }
    }
}
