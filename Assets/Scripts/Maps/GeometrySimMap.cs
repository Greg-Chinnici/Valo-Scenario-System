using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using UnityEngine;
using ValorantReplay.Playback;
using ValorantReplay.Sim;
using SVector3 = System.Numerics.Vector3;

namespace ValoReplay.Maps
{
    /// <summary>
    /// A <see cref="SimMap"/> from map geometry instead of replays, so the round AI can play a map nobody has recorded.
    /// Map colliders are sampled on a grid with downward raycasts (every floor of a column, backfaces included); a point
    /// is walkable where a standing capsule fits on it. Walkable space not reachable from either spawn (roofs, wall tops,
    /// the outside of the map) is dropped.
    ///
    /// Sites and spawns come from marker GameObjects anywhere under the given roots: "Site A" / "Site B" / "Site C"
    /// (radius from a trigger SphereCollider on the marker, else <see cref="Settings.DefaultSiteRadius"/>),
    /// "Attacker Spawn" and "Defender Spawn". Markers should sit on the floor and carry no solid collider. A known
    /// Valorant map can fall back to its MapCatalog callouts ("A Site", "Attacker Side Spawn").
    ///
    /// The map carries no learned behaviour (Look, Tempo are null: TeamDirector would execute at a fixed time). Attach
    /// what round play would learn with <see cref="SimMap.WithLearned"/>; self-play does (SelfPlaySetup).
    /// </summary>
    public static class GeometrySimMap
    {
        public sealed class Settings
        {
            public float CellSize = 0.5f;
            /// <summary>Standing player capsule (Valorant's is about this size).</summary>
            public float CapsuleHeight = 2f, CapsuleRadius = 0.35f;
            /// <summary>The capsule is tested this far above the floor, so curbs, slopes and stair noses do not block it.</summary>
            public float StepOffset = 0.3f;
            /// <summary>Steeper surfaces are not floor.</summary>
            public float MaxSlope = 50f;
            public float MaxClimb = 1.0f, MaxDrop = 4f;
            public float DefaultSiteRadius = 6f;
            /// <summary>Drop nodes that cannot be walked to from either spawn.</summary>
            public bool ReachableOnly = true;
            /// <summary>Colliders whose name contains one of these are ignored (spawn barriers drop at the round start).</summary>
            public string[] IgnoreColliderNames = { "Barrier" };
            public int MaxHitsPerColumn = 64;
        }

        public sealed class Report
        {
            public Bounds Bounds;
            public int Columns, Hits, FloorHits, DownFacingHits, SteepHits, CapsuleBlocked, Samples;
            public int RawNodes, Nodes;
            public int IgnoredColliders;
            public int AttackerReach, DefenderReach;
            public bool SpawnsConnected;
            public double Seconds;
            public string SiteSource = "", SpawnSource = "";
            /// <summary>The grid before dropping unreachable nodes (for diagnostics).</summary>
            public NavGrid RawGrid;
            public readonly List<string> Warnings = new List<string>();

            public override string ToString() =>
                $"geometry grid: {Nodes} nodes ({RawNodes} before reachability), {Columns} columns, {Hits} ray hits " +
                $"({FloorHits} floor, {DownFacingHits} down-facing, {SteepHits} steep), {CapsuleBlocked} floors without room for a capsule, " +
                $"{IgnoredColliders} colliders ignored, {Seconds:0.0}s; sites from {SiteSource}, spawns from {SpawnSource}" +
                (Warnings.Count > 0 ? "\n  " + string.Join("\n  ", Warnings) : "");
        }

        static readonly Regex SiteName = new Regex(@"^Site ([A-Z])$", RegexOptions.IgnoreCase);

        /// <summary>
        /// Builds the map. <paramref name="isMap"/> says which colliders are map geometry; <paramref name="markerRoots"/>
        /// are searched for site / spawn markers; <paramref name="levelPath"/> (optional) picks MapCatalog callouts for
        /// whatever the markers do not give.
        /// </summary>
        public static SimMap Build(Func<Collider, bool> isMap, IEnumerable<Transform> markerRoots, string levelPath, Settings settings, out Report report)
        {
            settings ??= new Settings();
            report = new Report();
            var sw = Stopwatch.StartNew();
            Func<Collider, bool> solid = c => c != null && !c.isTrigger && c.enabled && isMap(c) && !Ignored(c, settings);

            var colliders = new List<Collider>();
            foreach (var c in UnityEngine.Object.FindObjectsByType<Collider>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (c.isTrigger || !isMap(c)) continue;
                if (Ignored(c, settings)) { report.IgnoredColliders++; continue; }
                colliders.Add(c);
            }
            if (colliders.Count == 0) throw new InvalidOperationException("no map colliders to sample");
            var bounds = colliders[0].bounds;
            foreach (var c in colliders) bounds.Encapsulate(c.bounds);
            report.Bounds = bounds;
            Physics.SyncTransforms();

            var builder = new NavGrid.Builder
            {
                CellSize = settings.CellSize,
                FillHolePasses = 0,     // a hole one cell wide is a wall here, not a sampling gap
                MaxClimb = settings.MaxClimb,
                MaxDrop = settings.MaxDrop,
            };
            Sample(builder, solid, bounds, settings, report);
            if (builder.Samples == 0) throw new InvalidOperationException("no floor where a player fits");
            var raw = builder.Build();
            report.RawGrid = raw;
            report.RawNodes = raw.Count;

            var map = new SimMap { Grid = raw, Map = levelPath ?? "" };
            var markers = FindMarkers(markerRoots, settings);
            var info = MapCatalog.Find(levelPath);

            // spawns first: reachability is measured from them
            var spawnSources = new List<string>();
            map.AttackerSpawn = Locate(raw, markers.AttackerSpawn, info, "Attacker Side", "Spawn", "attacker spawn", report, spawnSources);
            map.DefenderSpawn = Locate(raw, markers.DefenderSpawn, info, "Defender Side", "Spawn", "defender spawn", report, spawnSources);
            report.SpawnSource = string.Join(", ", spawnSources);

            // can the teams meet? If not, name the colliders around the smaller side's edge (barriers, closed doors)
            var fromAtk = Reachable(raw, new[] { map.AttackerSpawn });
            var fromDef = Reachable(raw, new[] { map.DefenderSpawn });
            int atkNode = raw.Nearest(map.AttackerSpawn, 8f), defNode = raw.Nearest(map.DefenderSpawn, 8f);
            report.AttackerReach = fromAtk.Count;
            report.DefenderReach = fromDef.Count;
            report.SpawnsConnected = defNode >= 0 && atkNode >= 0 && fromAtk.Contains(defNode) && fromDef.Contains(atkNode);
            if (!report.SpawnsConnected)
            {
                report.Warnings.Add($"the spawns are not connected both ways (attacker spawn reaches {fromAtk.Count} nodes, defender spawn {fromDef.Count})");
                foreach (var b in EdgeBlockers(raw, fromAtk.Count <= fromDef.Count ? fromAtk : fromDef, solid, settings))
                    report.Warnings.Add($"  edge blocked by {b.Key} ({b.Value} cells)");
            }

            if (settings.ReachableOnly)
            {
                var keep = new HashSet<int>(fromAtk);
                keep.UnionWith(fromDef);
                if (keep.Count == 0) throw new InvalidOperationException("neither spawn is on the walkable grid");
                var nodes = new List<NavGrid.Node>(keep.Count);
                foreach (int n in keep) nodes.Add(raw.Nodes[n]);
                map.Grid = new NavGrid(settings.CellSize, nodes, settings.MaxClimb, settings.MaxDrop);
            }
            report.Nodes = map.Grid.Count;
            // snap the spawns onto the final grid
            map.AttackerSpawn = Snap(map.Grid, map.AttackerSpawn) ?? map.AttackerSpawn;
            map.DefenderSpawn = Snap(map.Grid, map.DefenderSpawn) ?? map.DefenderSpawn;

            var siteSources = new HashSet<string>();
            foreach (var kv in markers.Sites)
            {
                var c = Snap(map.Grid, kv.Value.pos);
                if (c == null) { report.Warnings.Add($"Site {kv.Key} marker is not on the walkable grid"); continue; }
                map.Sites.Add(new SimMap.Site { Name = kv.Key, Center = c.Value, Radius = kv.Value.radius });
                siteSources.Add("markers");
            }
            if (info != null)
                foreach (var co in info.Callouts)
                {
                    if (co.Region != "Site" || co.SuperRegion.Length != 1 || map.SiteNamed(co.SuperRegion) != null) continue;
                    var c = Snap(map.Grid, Callout(co));
                    if (c == null) { report.Warnings.Add($"{co.Name} callout is not on the walkable grid"); continue; }
                    map.Sites.Add(new SimMap.Site { Name = co.SuperRegion, Center = c.Value, Radius = settings.DefaultSiteRadius });
                    siteSources.Add("MapCatalog callouts");
                }
            map.Sites.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            report.SiteSource = siteSources.Count > 0 ? string.Join(" + ", siteSources) : "nothing";
            if (map.Sites.Count == 0) report.Warnings.Add("no sites: add \"Site A\" / \"Site B\" markers");

            report.Seconds = sw.Elapsed.TotalSeconds;
            return map;
        }

        /// <summary>
        /// Gives a MeshCollider to every mesh under the roots that is drawn with at least one opaque material but has no
        /// collider (vista scenery excluded). Maps built before MapBuilder gave such objects a collider of their opaque
        /// parts have none on any object with a transparent submesh, which on Ascent drops whole floors (MidFloor_0,
        /// AtkGround_1, MarketFloorStairs_0: a puddle or decal submesh) and cuts the attacker side off. A rebuilt map
        /// needs none of this. The caller destroys the returned colliders when done.
        /// </summary>
        public static List<Collider> AddMissingColliders(IEnumerable<Transform> roots)
        {
            var added = new List<Collider>();
            foreach (var root in roots)
            {
                if (root == null) continue;
                foreach (var mr in root.GetComponentsInChildren<MeshRenderer>())
                {
                    if (mr.GetComponent<Collider>() != null) continue;
                    var mf = mr.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null || InVista(mr.transform)) continue;
                    bool opaque = false;
                    foreach (var m in mr.sharedMaterials)
                        if (m != null && !(m.HasProperty("_Surface") && m.GetFloat("_Surface") > 0.5f)) { opaque = true; break; }
                    if (!opaque) continue;
                    var mc = mr.gameObject.AddComponent<MeshCollider>();
                    mc.sharedMesh = mf.sharedMesh;
                    added.Add(mc);
                }
            }
            Physics.SyncTransforms();
            return added;
        }

        static bool InVista(Transform t)
        {
            for (; t != null; t = t.parent)
                if (t.name.IndexOf("Vista", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        static bool Ignored(Collider c, Settings s)
        {
            foreach (var n in s.IgnoreColliderNames)
                if (c.name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // ------------------------------------------------------------------ sampling

        static void Sample(NavGrid.Builder builder, Func<Collider, bool> solid, Bounds bounds, Settings s, Report report)
        {
            float cell = s.CellSize, minFloorNormal = Mathf.Cos(s.MaxSlope * Mathf.Deg2Rad);
            int x0 = Mathf.FloorToInt(bounds.min.x / cell), x1 = Mathf.FloorToInt(bounds.max.x / cell);
            int z0 = Mathf.FloorToInt(bounds.min.z / cell), z1 = Mathf.FloorToInt(bounds.max.z / cell);
            float top = bounds.max.y + 1f, bottom = bounds.min.y - 1f;
            var hits = new RaycastHit[64];
            var overlaps = new Collider[32];
            bool backfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;     // map walls and floors can be single-sided
            try
            {
                for (int iz = z0; iz <= z1; iz++)
                    for (int ix = x0; ix <= x1; ix++)
                    {
                        report.Columns++;
                        float x = (ix + 0.5f) * cell, z = (iz + 0.5f) * cell, y = top;
                        for (int k = 0; k < s.MaxHitsPerColumn && y > bottom; k++)
                        {
                            if (!Nearest(new Vector3(x, y, z), Vector3.down, y - bottom, solid, hits, out var hit)) break;
                            report.Hits++;
                            y = hit.point.y - 0.01f;
                            // a down-facing hit is a face seen from behind: a single-sided floor modelled upside down
                            // (common, e.g. Ascent's attacker spawn) or the underside of a solid seen from inside it.
                            // Both count as floor here; the headroom ray below rejects the inside-a-solid case.
                            if (hit.normal.y <= -minFloorNormal) report.DownFacingHits++;
                            else if (hit.normal.y < minFloorNormal) { report.SteepHits++; continue; }
                            report.FloorHits++;
                            if (!Fits(x, hit.point.y, z, s, solid, hits, overlaps)) { report.CapsuleBlocked++; continue; }
                            float floor = hit.point.y;
                            builder.Add(new SVector3(x, floor + NavGrid.ActorHeight, z));
                            report.Samples++;
                        }
                    }
            }
            finally { Physics.queriesHitBackfaces = backfaces; }
        }

        /// <summary>Does a standing capsule fit on the floor at (x, floor, z)? Overlap tests only see a mesh collider's
        /// surface, so a capsule wholly inside a solid passes them: a ray up through the capsule's height catches that
        /// (from inside, it hits the solid's top from behind).</summary>
        public static bool Fits(float x, float floor, float z, Settings s, Func<Collider, bool> solid, RaycastHit[] hits, Collider[] overlaps)
        {
            var p0 = new Vector3(x, floor + s.StepOffset + s.CapsuleRadius, z);
            var p1 = new Vector3(x, floor + s.CapsuleHeight - s.CapsuleRadius, z);
            int n = Physics.OverlapCapsuleNonAlloc(p0, p1, s.CapsuleRadius, overlaps, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++) if (solid(overlaps[i])) return false;
            return !Nearest(new Vector3(x, floor + 0.05f, z), Vector3.up, s.CapsuleHeight - 0.05f, solid, hits, out _);
        }

        /// <summary>Closest hit on a collider that passes the filter.</summary>
        public static bool Nearest(Vector3 origin, Vector3 dir, float distance, Func<Collider, bool> filter, RaycastHit[] buffer, out RaycastHit hit)
        {
            int n = Physics.RaycastNonAlloc(origin, dir, buffer, distance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            int best = -1;
            for (int i = 0; i < n; i++)
                if (filter(buffer[i].collider) && (best < 0 || buffer[i].distance < buffer[best].distance)) best = i;
            hit = best >= 0 ? buffer[best] : default;
            return best >= 0;
        }

        /// <summary>Colliders that keep a capsule out of the cells just outside a region (by collider path, most first).</summary>
        public static List<KeyValuePair<string, int>> EdgeBlockers(NavGrid g, HashSet<int> region, Func<Collider, bool> solid, Settings s, int top = 12)
        {
            var counts = new Dictionary<string, int>();
            var overlaps = new Collider[32];
            var seen = new HashSet<(int, int)>();
            foreach (int i in region)
            {
                var n = g.Nodes[i];
                float floor = n.Y - NavGrid.ActorHeight;
                for (int d = 0; d < 4; d++)
                {
                    int x = n.X + (d == 0 ? 1 : d == 1 ? -1 : 0), z = n.Z + (d == 2 ? 1 : d == 3 ? -1 : 0);
                    bool inside = false;
                    foreach (int m in g.NodesAt((x + 0.5f) * g.CellSize, (z + 0.5f) * g.CellSize))
                        if (region.Contains(m) && Math.Abs(g.Nodes[m].Y - n.Y) < 2f) { inside = true; break; }
                    if (inside || !seen.Add((x, z))) continue;
                    var c = new Vector3((x + 0.5f) * g.CellSize, floor, (z + 0.5f) * g.CellSize);
                    int k = Physics.OverlapCapsuleNonAlloc(c + Vector3.up * (s.StepOffset + s.CapsuleRadius), c + Vector3.up * (s.CapsuleHeight - s.CapsuleRadius),
                        s.CapsuleRadius, overlaps, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                    for (int j = 0; j < k; j++)
                    {
                        if (!solid(overlaps[j])) continue;
                        var t = overlaps[j].transform;
                        string key = (t.parent != null ? t.parent.name + "/" : "") + t.name;
                        counts.TryGetValue(key, out int v);
                        counts[key] = v + 1;
                    }
                }
            }
            var list = new List<KeyValuePair<string, int>>(counts);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            if (list.Count > top) list.RemoveRange(top, list.Count - top);
            return list;
        }

        /// <summary>Nodes reachable (following the grid's directed links) from the nodes nearest to the given points.</summary>
        public static HashSet<int> Reachable(NavGrid g, IEnumerable<SVector3> from)
        {
            var seen = new HashSet<int>();
            var queue = new Queue<int>();
            foreach (var p in from)
            {
                int s = g.Nearest(p, 8f);
                if (s >= 0 && seen.Add(s)) queue.Enqueue(s);
            }
            while (queue.Count > 0)
            {
                int n = queue.Dequeue();
                foreach (int m in g.Links(n))
                    if (seen.Add(m)) queue.Enqueue(m);
            }
            return seen;
        }

        static SVector3? Snap(NavGrid g, SVector3 p)
        {
            int n = g.Nearest(p, 12f);
            return n >= 0 ? g.Position(n) : (SVector3?)null;
        }

        // ------------------------------------------------------------------ markers

        sealed class Markers
        {
            public readonly SortedDictionary<string, (SVector3 pos, float radius)> Sites = new SortedDictionary<string, (SVector3, float)>();
            public SVector3? AttackerSpawn, DefenderSpawn;
        }

        static Markers FindMarkers(IEnumerable<Transform> roots, Settings s)
        {
            var m = new Markers();
            if (roots == null) return m;
            foreach (var root in roots)
            {
                if (root == null) continue;
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    string name = t.name.Trim();
                    // a marker stands on the floor; the sim wants the capsule centre
                    var p = new SVector3(t.position.x, t.position.y + NavGrid.ActorHeight, t.position.z);
                    var site = SiteName.Match(name);
                    if (site.Success)
                    {
                        float r = s.DefaultSiteRadius;
                        var sphere = t.GetComponent<SphereCollider>();
                        if (sphere != null) r = sphere.radius * Mathf.Max(t.lossyScale.x, t.lossyScale.z);
                        m.Sites[site.Groups[1].Value.ToUpperInvariant()] = (p, r);
                    }
                    else if (name.Equals("Attacker Spawn", StringComparison.OrdinalIgnoreCase)) m.AttackerSpawn = p;
                    else if (name.Equals("Defender Spawn", StringComparison.OrdinalIgnoreCase)) m.DefenderSpawn = p;
                }
            }
            return m;
        }

        static SVector3 Locate(NavGrid g, SVector3? marker, MapInfo info, string super, string region, string what, Report report, List<string> sources)
        {
            if (marker != null)
            {
                sources.Add($"{what} marker");
                return Snap(g, marker.Value) ?? marker.Value;
            }
            if (info != null)
                foreach (var c in info.Callouts)
                    if (c.SuperRegion == super && c.Region == region)
                    {
                        sources.Add($"{what} callout");
                        var p = Callout(c);
                        var s = Snap(g, p);
                        if (s == null) report.Warnings.Add($"{c.Name} callout is not on the walkable grid");
                        return s ?? p;
                    }
            throw new InvalidOperationException($"no {what}: add an \"{(what.StartsWith("attacker") ? "Attacker Spawn" : "Defender Spawn")}\" marker");
        }

        /// <summary>A MapCatalog callout in sim space (its height is rough: Snap finds the floor).</summary>
        public static SVector3 Callout(Callout c)
        {
            VrpReplay.ToUnity(c.X, c.Y, c.Z, out float x, out float y, out float z);
            return new SVector3(x, y + NavGrid.ActorHeight, z);
        }
    }
}
