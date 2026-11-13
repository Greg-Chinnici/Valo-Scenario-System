using System.Collections.Generic;
using UnityEngine;
using ValorantReplay.Playback;

namespace ValoReplay.Knowledge
{
    /// <summary>
    /// The map split into callout areas, for area beliefs ("Jett: 60% A Main"). Each point belongs to its nearest
    /// callout (2D). Learned from where players stood in the recording (map structure only, not who stood where):
    /// standable cells, which areas connect (players walked from one to the other) and how long that takes, plus
    /// probe points per area used to tell whether an area is in an ally's view.
    /// </summary>
    public sealed class AreaModel
    {
        public sealed class Area
        {
            public int Index;
            public string Name, SuperRegion;
            /// <summary>Mean of the area's standable cells (player capsule centre height).</summary>
            public Vector3 Center;
            public int CellCount;
            public readonly List<int> Neighbors = new List<int>();
            public readonly List<float> TravelTimes = new List<float>();
            /// <summary>Spread-out standing points (capsule centre height) checked for visibility.</summary>
            public Vector3[] Probes = new Vector3[0];
        }

        public struct Cell
        {
            public Vector3 Floor;
            public int Area;
        }

        public readonly Area[] Areas;
        public readonly Cell[] Cells;
        public readonly float CellSize;
        public readonly int AttackerSpawn = -1, DefenderSpawn = -1;
        /// <summary>Area names grouped by super region ("A", "B", "Mid", "Attacker Side", ...), in map order.</summary>
        public readonly List<string> SuperRegions = new List<string>();

        private readonly Vector2[] _callouts;

        private AreaModel(VrpReplay replay, MapInfo info, float cellSize, int probes, float moveSpeed)
        {
            CellSize = cellSize;
            int n = info.Callouts.Length;
            Areas = new Area[n];
            _callouts = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                var c = info.Callouts[i];
                VrpReplay.ToUnity(c.X, c.Y, c.Z, out float x, out _, out float z);
                _callouts[i] = new Vector2(x, z);
                Areas[i] = new Area { Index = i, Name = c.Name, SuperRegion = c.SuperRegion };
                if (!SuperRegions.Contains(c.SuperRegion)) SuperRegions.Add(c.SuperRegion);
                if (c.Region == "Spawn" && c.SuperRegion.StartsWith("Attacker")) AttackerSpawn = i;
                if (c.Region == "Spawn" && c.SuperRegion.StartsWith("Defender")) DefenderSpawn = i;
            }

            // standable cells: where anyone stood (not jumping), split by height so stacked floors stay apart
            var sums = new Dictionary<long, (double Y, int Count)>();
            var transitions = new Dictionary<long, int>();
            int players = replay.Players.Count;
            var last = new int[players];
            for (int p = 0; p < players; p++) last[p] = -1;
            for (int t = 0; t < replay.TickCount; t++)
                for (int p = 0; p < players; p++)
                {
                    var f = replay.GetFrame(t, p);
                    bool usable = f.Alive && (f.Flags & FrameFlags.HasData) != 0 && (f.Flags & FrameFlags.OffMap) == 0;
                    if (!usable || (f.Flags & FrameFlags.Snapped) != 0) { last[p] = usable ? AreaAt(replay.UnityPosition(f)) : -1; continue; }
                    var pos = replay.UnityPosition(f);
                    int area = AreaAt(pos);
                    if (last[p] >= 0 && last[p] != area)
                    {
                        long key = (long)Mathf.Min(last[p], area) * 4096 + Mathf.Max(last[p], area);
                        transitions.TryGetValue(key, out int count);
                        transitions[key] = count + 1;
                    }
                    last[p] = area;
                    if (f.Airborne) continue;
                    long cell = CellKey(pos);
                    sums.TryGetValue(cell, out var s);
                    sums[cell] = (s.Y + pos.y, s.Count + 1);
                }

            var cells = new List<Cell>();
            var centers = new Vector3[n];
            foreach (var kv in sums)
            {
                if (kv.Value.Count < 3) continue;
                Unpack(kv.Key, out int ix, out int iz);
                var floor = new Vector3((ix + 0.5f) * cellSize, (float)(kv.Value.Y / kv.Value.Count) - 1f, (iz + 0.5f) * cellSize);
                int area = AreaAt(floor);
                cells.Add(new Cell { Floor = floor, Area = area });
                centers[area] += floor + Vector3.up;
                Areas[area].CellCount++;
            }
            Cells = cells.ToArray();
            for (int i = 0; i < n; i++)
                Areas[i].Center = Areas[i].CellCount > 0 ? centers[i] / Areas[i].CellCount : new Vector3(_callouts[i].x, 0f, _callouts[i].y);

            foreach (var kv in transitions)
            {
                if (kv.Value < 2) continue;     // boundary flicker
                int a = (int)(kv.Key / 4096), b = (int)(kv.Key % 4096);
                float time = Mathf.Max(1f, Vector3.Distance(Areas[a].Center, Areas[b].Center) / Mathf.Max(0.5f, moveSpeed));
                Areas[a].Neighbors.Add(b); Areas[a].TravelTimes.Add(time);
                Areas[b].Neighbors.Add(a); Areas[b].TravelTimes.Add(time);
            }

            // probes: farthest-point sampling over each area's cells, starting next to its centre
            var byArea = new List<Vector3>[n];
            for (int i = 0; i < n; i++) byArea[i] = new List<Vector3>();
            foreach (var c in Cells) byArea[c.Area].Add(c.Floor + Vector3.up);
            for (int i = 0; i < n; i++) Areas[i].Probes = Spread(byArea[i], Areas[i].Center, probes);
        }

        /// <summary>Area model for a replay's map, or null when the map has no callouts.</summary>
        public static AreaModel Build(VrpReplay replay, KnowledgeSettings s)
        {
            var info = replay.MapInfo;
            if (info == null || info.Callouts == null || info.Callouts.Length == 0) return null;
            return new AreaModel(replay, info, Mathf.Max(0.5f, s.areaCellSize), Mathf.Max(1, s.areaProbes), s.enemyMoveSpeed);
        }

        /// <summary>Area of a point (nearest callout in 2D).</summary>
        public int AreaAt(Vector3 p)
        {
            int best = 0;
            float bd = float.MaxValue;
            for (int i = 0; i < _callouts.Length; i++)
            {
                float dx = _callouts[i].x - p.x, dz = _callouts[i].y - p.z, d = dx * dx + dz * dz;
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        public int SpawnOf(TeamRole role) => role == TeamRole.Attacking ? AttackerSpawn : role == TeamRole.Defending ? DefenderSpawn : -1;

        private long CellKey(Vector3 p)
        {
            int ix = Mathf.FloorToInt(p.x / CellSize), iz = Mathf.FloorToInt(p.z / CellSize), iy = Mathf.FloorToInt(p.y / 2.5f);
            return ((long)(ix + 32768) << 40) | ((long)(iz + 32768) << 20) | (long)(iy + 512);
        }

        private static void Unpack(long key, out int ix, out int iz)
        {
            ix = (int)(key >> 40) - 32768;
            iz = (int)((key >> 20) & 0xFFFFF) - 32768;
        }

        private static Vector3[] Spread(List<Vector3> points, Vector3 center, int count)
        {
            if (points.Count == 0) return new Vector3[0];
            var chosen = new List<Vector3>();
            var nearest = new float[points.Count];
            int first = 0;
            float bd = float.MaxValue;
            for (int i = 0; i < points.Count; i++)
            {
                float d = (points[i] - center).sqrMagnitude;
                if (d < bd) { bd = d; first = i; }
                nearest[i] = float.MaxValue;
            }
            int next = first;
            while (chosen.Count < count && next >= 0)
            {
                chosen.Add(points[next]);
                int far = -1;
                float fd = 0.5f;     // stop once every cell is within ~0.7 m of a probe
                for (int i = 0; i < points.Count; i++)
                {
                    nearest[i] = Mathf.Min(nearest[i], (points[i] - points[next]).sqrMagnitude);
                    if (nearest[i] > fd) { fd = nearest[i]; far = i; }
                }
                next = far;
            }
            return chosen.ToArray();
        }
    }
}
