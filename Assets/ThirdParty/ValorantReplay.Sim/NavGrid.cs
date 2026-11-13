// NavGrid.cs - walkable space rebuilt from where players stood, for pathing and line of sight. No map geometry is
// needed: every grounded sample of a living player marks its cell as floor, so coverage grows with every replay of
// the map. A column can hold several floors (heaven above a site, bridges). Units are Unity space (metres, Y-up);
// node heights are the replay's player location (capsule centre), ActorHeight above the floor.
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace ValorantReplay.Sim
{
    public sealed class NavGrid
    {
        /// <summary>Height of the replay's player location (capsule centre) above the floor it stands on.</summary>
        public const float ActorHeight = 0.9f;

        public struct Node
        {
            public int X, Z;        // cell
            public float Y;         // player location height (floor + ActorHeight)
            public int Samples;     // player samples behind this node; 0 = filled hole
        }

        public float CellSize { get; }
        public int MinX { get; }
        public int MinZ { get; }
        public int Width { get; }
        public int Depth { get; }
        public int Count => nodes.Length;
        public IReadOnlyList<Node> Nodes => nodes;

        /// <summary>Line of sight: a run of unwalkable cells shorter than this along the ray does not block. Walls show up
        /// as bands at least a player's width wide (nobody stands inside them); thinner gaps are sampling holes.</summary>
        public float MinBlockerWidth = 0.75f;
        /// <summary>Line of sight: how far below a floor the ray counts as inside it (the slab under heaven).</summary>
        public float SlabThickness = 0.4f;

        readonly Node[] nodes;
        readonly int[] colStart, colCount, colNodes;   // dense column -> node indices, sorted by height
        readonly int[] linkStart, links;               // directed adjacency (CSR)
        readonly ThreadLocal<PathFinder> finders;

        public NavGrid(float cellSize, IEnumerable<Node> source, float maxClimb = 1.0f, float maxDrop = 4f)
        {
            CellSize = cellSize;
            var list = new List<Node>(source);
            if (list.Count == 0) throw new ArgumentException("no walkable nodes");
            int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
            foreach (var n in list)
            {
                minX = Math.Min(minX, n.X); maxX = Math.Max(maxX, n.X);
                minZ = Math.Min(minZ, n.Z); maxZ = Math.Max(maxZ, n.Z);
            }
            MinX = minX; MinZ = minZ; Width = maxX - minX + 1; Depth = maxZ - minZ + 1;
            list.Sort((a, b) =>
            {
                int c = ColumnIndex(a.X, a.Z).CompareTo(ColumnIndex(b.X, b.Z));
                return c != 0 ? c : a.Y.CompareTo(b.Y);
            });
            nodes = list.ToArray();
            colStart = new int[Width * Depth];
            colCount = new int[Width * Depth];
            colNodes = new int[nodes.Length];
            for (int i = 0; i < nodes.Length; i++)
            {
                int c = ColumnIndex(nodes[i].X, nodes[i].Z);
                if (colCount[c]++ == 0) colStart[c] = i;
                colNodes[i] = i;
            }

            var adj = new List<int>();
            linkStart = new int[nodes.Length + 1];
            for (int i = 0; i < nodes.Length; i++)
            {
                linkStart[i] = adj.Count;
                var a = nodes[i];
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        // no corner cutting: a diagonal step needs both orthogonal cells open at about this height
                        if (dx != 0 && dz != 0 && (!HasFloorNear(a.X + dx, a.Z, a.Y, maxClimb) || !HasFloorNear(a.X, a.Z + dz, a.Y, maxClimb))) continue;
                        int c = Column(a.X + dx, a.Z + dz);
                        if (c < 0) continue;
                        for (int k = colStart[c], e = k + colCount[c]; k < e; k++)
                        {
                            float dy = nodes[colNodes[k]].Y - a.Y;
                            if (dy <= maxClimb && -dy <= maxDrop) adj.Add(colNodes[k]);
                        }
                    }
            }
            linkStart[nodes.Length] = adj.Count;
            links = adj.ToArray();
            finders = new ThreadLocal<PathFinder>(() => new PathFinder(this));
        }

        int ColumnIndex(int x, int z) => (z - MinZ) * Width + (x - MinX);

        int Column(int x, int z)
        {
            if (x < MinX || z < MinZ || x >= MinX + Width || z >= MinZ + Depth) return -1;
            int c = ColumnIndex(x, z);
            return colCount[c] > 0 ? c : -1;
        }

        bool HasFloorNear(int x, int z, float y, float tolerance)
        {
            int c = Column(x, z);
            if (c < 0) return false;
            for (int k = colStart[c], e = k + colCount[c]; k < e; k++)
                if (Math.Abs(nodes[colNodes[k]].Y - y) <= tolerance) return true;
            return false;
        }

        public int CellOf(float v) => (int)Math.Floor(v / CellSize);

        public Vector3 Position(int node) => new Vector3((nodes[node].X + 0.5f) * CellSize, nodes[node].Y, (nodes[node].Z + 0.5f) * CellSize);

        public ReadOnlySpan<int> Links(int node) => new ReadOnlySpan<int>(links, linkStart[node], linkStart[node + 1] - linkStart[node]);

        /// <summary>Nodes in the column under (x, z), lowest first.</summary>
        public IEnumerable<int> NodesAt(float x, float z)
        {
            int c = Column(CellOf(x), CellOf(z));
            if (c < 0) yield break;
            for (int k = colStart[c], e = k + colCount[c]; k < e; k++) yield return colNodes[k];
        }

        /// <summary>The node closest to p (height counts double, so a floor above does not win), or -1 if none is within
        /// maxDistance horizontally.</summary>
        public int Nearest(Vector3 p, float maxDistance = 3f)
        {
            int cx = CellOf(p.X), cz = CellOf(p.Z), r = (int)Math.Ceiling(maxDistance / CellSize);
            int best = -1;
            float bestD = float.MaxValue;
            for (int ring = 0; ring <= r; ring++)
            {
                for (int dz = -ring; dz <= ring; dz++)
                    for (int dx = -ring; dx <= ring; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring) continue;
                        int c = Column(cx + dx, cz + dz);
                        if (c < 0) continue;
                        for (int k = colStart[c], e = k + colCount[c]; k < e; k++)
                        {
                            var q = Position(colNodes[k]);
                            float hx = q.X - p.X, hz = q.Z - p.Z, dy = (q.Y - p.Y) * 2f;
                            float d = hx * hx + hz * hz + dy * dy;
                            if (d < bestD) { bestD = d; best = colNodes[k]; }
                        }
                    }
                // a hit on this ring can still lose to the next one by at most one cell diagonal
                if (best >= 0 && Math.Sqrt(bestD) < (ring - 1) * CellSize) break;
            }
            return best;
        }

        /// <summary>Approximate line of sight between two points (e.g. eye positions). The ray is blocked by a long enough
        /// run of columns with no floor at or below it, or by passing through the slab under a floor.</summary>
        public bool LineOfSight(Vector3 from, Vector3 to)
        {
            float dx = to.X - from.X, dz = to.Z - from.Z;
            float len = (float)Math.Sqrt(dx * dx + dz * dz);
            // fine enough horizontally to see wall bands, and vertically that no sample can skip over a floor slab
            int steps = Math.Max(1, (int)Math.Ceiling(Math.Max(len / (CellSize * 0.5f), Math.Abs(to.Y - from.Y) / (SlabThickness * 0.5f))));
            float step = len / steps, blocked = 0f;
            for (int i = 1; i < steps; i++)
            {
                var p = Vector3.Lerp(from, to, i / (float)steps);
                var k = Classify(p);
                if (k == Cell.Slab) return false;          // through a floor: always blocks
                if (k == Cell.Open) { blocked = 0f; continue; }
                blocked += step;
                if (blocked >= MinBlockerWidth) return false;
            }
            return true;
        }

        enum Cell : byte { Open, Solid, Slab }

        Cell Classify(Vector3 p)
        {
            int c = Column(CellOf(p.X), CellOf(p.Z));
            if (c < 0) return Cell.Solid;
            bool floorBelow = false;
            for (int k = colStart[c], e = k + colCount[c]; k < e; k++)
            {
                float surface = nodes[colNodes[k]].Y - ActorHeight;
                if (p.Y < surface && p.Y > surface - SlabThickness) return Cell.Slab;
                if (surface <= p.Y + 0.05f) floorBelow = true;
            }
            return floorBelow ? Cell.Open : Cell.Solid;
        }

        /// <summary>A* from start to goal (node indices). Fills path with the nodes after start, ending at goal.</summary>
        public bool FindPath(int start, int goal, List<int> path) => finders.Value.Find(start, goal, path);

        /// <summary>Walking distance between two nodes, or +inf if there is no path.</summary>
        public float PathLength(int start, int goal)
        {
            var path = new List<int>();
            if (!FindPath(start, goal, path)) return float.PositiveInfinity;
            float d = 0f;
            var prev = Position(start);
            foreach (var n in path) { var q = Position(n); d += Vector3.Distance(prev, q); prev = q; }
            return d;
        }

        /// <summary>Per-thread A* scratch (stamps instead of clearing), so one grid can serve parallel simulations.</summary>
        sealed class PathFinder
        {
            readonly NavGrid g;
            readonly float[] cost;
            readonly int[] from, seen;
            readonly bool[] closed;
            int stamp;
            readonly MinHeap open = new MinHeap();

            public PathFinder(NavGrid grid)
            {
                g = grid;
                cost = new float[grid.Count];
                from = new int[grid.Count];
                seen = new int[grid.Count];
                closed = new bool[grid.Count];
            }

            public bool Find(int start, int goal, List<int> path)
            {
                path.Clear();
                if (start < 0 || goal < 0) return false;
                if (start == goal) return true;
                if (++stamp == int.MaxValue) { Array.Clear(seen, 0, seen.Length); stamp = 1; }
                open.Clear();
                var target = g.Position(goal);
                Visit(start, -1, 0f);
                open.Push(Vector3.Distance(g.Position(start), target), start);
                while (open.Count > 0)
                {
                    int n = open.Pop();
                    if (closed[n]) continue;
                    if (n == goal)
                    {
                        for (int k = goal; k != start; k = from[k]) path.Add(k);
                        path.Reverse();
                        return true;
                    }
                    closed[n] = true;
                    var pn = g.Position(n);
                    foreach (int m in g.Links(n))
                    {
                        float c = cost[n] + Vector3.Distance(pn, g.Position(m));
                        if (seen[m] == stamp && (closed[m] || c >= cost[m])) continue;
                        Visit(m, n, c);
                        open.Push(c + Vector3.Distance(g.Position(m), target), m);
                    }
                }
                return false;
            }

            void Visit(int n, int parent, float c)
            {
                seen[n] = stamp; closed[n] = false; cost[n] = c; from[n] = parent;
            }
        }

        sealed class MinHeap
        {
            float[] keys = new float[256];
            int[] vals = new int[256];
            public int Count { get; private set; }

            public void Clear() => Count = 0;

            public void Push(float key, int val)
            {
                if (Count == keys.Length) { Array.Resize(ref keys, Count * 2); Array.Resize(ref vals, Count * 2); }
                int i = Count++;
                while (i > 0)
                {
                    int p = (i - 1) / 2;
                    if (keys[p] <= key) break;
                    keys[i] = keys[p]; vals[i] = vals[p]; i = p;
                }
                keys[i] = key; vals[i] = val;
            }

            public int Pop()
            {
                int top = vals[0];
                float key = keys[--Count];
                int val = vals[Count], i = 0;
                while (true)
                {
                    int c = 2 * i + 1;
                    if (c >= Count) break;
                    if (c + 1 < Count && keys[c + 1] < keys[c]) c++;
                    if (keys[c] >= key) break;
                    keys[i] = keys[c]; vals[i] = vals[c]; i = c;
                }
                keys[i] = key; vals[i] = val;
                return top;
            }
        }

        // ---------------------------------------------------------------- building from samples

        /// <summary>Collects player samples (from any number of replays of one map) and turns them into a NavGrid.</summary>
        public sealed class Builder
        {
            public float CellSize = 0.5f;
            /// <summary>Samples in one column further apart than this vertically are separate floors.</summary>
            public float FloorGap = 1.5f;
            public int MinSamples = 1;
            /// <summary>Passes that fill an empty cell when most of its neighbours are floor at a similar height.</summary>
            public int FillHolePasses = 1;
            public float MaxClimb = 1.0f, MaxDrop = 4f;
            const float BinHeight = 0.25f;

            struct Bin { public int N; public double Sum; }
            readonly Dictionary<(int, int), Dictionary<int, Bin>> bins = new Dictionary<(int, int), Dictionary<int, Bin>>();
            public long Samples { get; private set; }

            public void Add(Vector3 p)
            {
                var key = ((int)Math.Floor(p.X / CellSize), (int)Math.Floor(p.Z / CellSize));
                if (!bins.TryGetValue(key, out var col)) bins[key] = col = new Dictionary<int, Bin>();
                int b = (int)Math.Round(p.Y / BinHeight);
                col.TryGetValue(b, out var bin);
                col[b] = new Bin { N = bin.N + 1, Sum = bin.Sum + p.Y };
                Samples++;
            }

            /// <summary>Marks the cells between two consecutive samples of one player (start included, end not).</summary>
            public void AddSegment(Vector3 a, Vector3 b)
            {
                float d = (float)Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Z - a.Z) * (b.Z - a.Z));
                int steps = Math.Max(1, (int)Math.Ceiling(d / (CellSize * 0.5f)));
                for (int i = 0; i < steps; i++) Add(Vector3.Lerp(a, b, i / (float)steps));
            }

            public NavGrid Build()
            {
                var floors = new Dictionary<(int, int), List<Node>>();
                foreach (var kv in bins)
                {
                    var keys = new List<int>(kv.Value.Keys);
                    keys.Sort();
                    var list = new List<Node>();
                    int start = 0;
                    for (int i = 1; i <= keys.Count; i++)
                    {
                        if (i < keys.Count && (keys[i] - keys[i - 1]) * BinHeight <= FloorGap) continue;
                        long n = 0;
                        double sum = 0;
                        for (int k = start; k < i; k++) { var bin = kv.Value[keys[k]]; n += bin.N; sum += bin.Sum; }
                        if (n >= MinSamples)
                            list.Add(new Node { X = kv.Key.Item1, Z = kv.Key.Item2, Y = (float)(sum / n), Samples = (int)Math.Min(n, int.MaxValue) });
                        start = i;
                    }
                    if (list.Count > 0) floors[kv.Key] = list;
                }

                for (int pass = 0; pass < FillHolePasses; pass++)
                {
                    var add = new Dictionary<(int, int), Node>();
                    foreach (var kv in floors)
                        for (int dz = -1; dz <= 1; dz++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                var cell = (kv.Key.Item1 + dx, kv.Key.Item2 + dz);
                                if (floors.ContainsKey(cell) || add.ContainsKey(cell)) continue;
                                if (TryFill(floors, cell, out var node)) add[cell] = node;
                            }
                    foreach (var kv in add) floors[kv.Key] = new List<Node> { kv.Value };
                }

                var all = new List<Node>();
                foreach (var l in floors.Values) all.AddRange(l);
                return new NavGrid(CellSize, all, MaxClimb, MaxDrop);
            }

            // an empty cell with at least 5 of 8 neighbours on one floor (within half a metre) is floor too
            static bool TryFill(Dictionary<(int, int), List<Node>> floors, (int, int) cell, out Node node)
            {
                var ys = new List<float>();
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                        if ((dx != 0 || dz != 0) && floors.TryGetValue((cell.Item1 + dx, cell.Item2 + dz), out var l))
                            foreach (var n in l) ys.Add(n.Y);
                node = default;
                foreach (float y in ys)
                {
                    int near = 0;
                    float sum = 0f;
                    foreach (float o in ys) if (Math.Abs(o - y) <= 0.5f) { near++; sum += o; }
                    if (near < 5) continue;
                    node = new Node { X = cell.Item1, Z = cell.Item2, Y = sum / near, Samples = 0 };
                    return true;
                }
                return false;
            }
        }
    }
}
