using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using ValorantReplay.Playback;
using ValorantReplay.Sim;
using ValoReplay.Maps;
using SVector3 = System.Numerics.Vector3;

namespace ValoReplay.EditorTools.AI
{
    /// <summary>
    /// Geometry grid (<see cref="GeometrySimMap"/>) against the replay grid (<see cref="SimMap.FromReplays"/>) of the same
    /// map: how much of where players stood the geometry covers, why the rest is missing (probing the colliders at each
    /// missing cell), steps between neighbouring cells that players took but the geometry grid does not link (boxes,
    /// ledges, drops), and walking distances between spawns and sites on both.
    /// </summary>
    public static class GridComparison
    {
        public static string Compare(SimMap geo, GeometrySimMap.Report geoReport, SimMap rep, Func<Collider, bool> isMap, GeometrySimMap.Settings settings)
        {
            settings ??= new GeometrySimMap.Settings();
            var info = MapCatalog.Find(geo.Map != "" ? geo.Map : rep.Map);
            var sb = new StringBuilder();
            var g = geo.Grid;
            var r = rep.Grid;
            var raw = geoReport.RawGrid ?? g;
            if (Math.Abs(g.CellSize - r.CellSize) > 1e-4f) sb.AppendLine($"cell sizes differ ({g.CellSize} vs {r.CellSize}): comparing by position");

            int recorded = 0, filled = 0;
            for (int i = 0; i < r.Count; i++) if (r.Nodes[i].Samples > 0) recorded++; else filled++;
            sb.AppendLine($"Grid comparison on {MapCatalog.DisplayName(rep.Map)} ({rep.Replays} replay)");
            sb.AppendLine($"  replay grid    {r.Count} nodes ({recorded} stood on, {filled} hole-filled), {Area(r):0} m² of floor");
            sb.AppendLine($"  geometry grid  {g.Count} nodes ({geoReport.RawNodes} before dropping what neither spawn reaches), {Area(g):0} m²");

            // ---- replay nodes: covered or why not
            var dys = new List<float>();
            int exact = 0, near = 0;
            var missing = new Dictionary<string, List<SVector3>>();
            var hits = new RaycastHit[64];
            var overlaps = new Collider[32];
            Func<Collider, bool> solid = c => c != null && !c.isTrigger && isMap(c) && !settings.IgnoreColliderNames.Any(n => c.name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
            float minFloorNormal = Mathf.Cos(settings.MaxSlope * Mathf.Deg2Rad);
            // renderers of the map without a collider (vista scenery excluded: it is outside the playable space)
            var bare = UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None)
                .Where(m => m.GetComponent<Collider>() == null && m.transform.parent != null && m.transform.parent.name.IndexOf("Vista", StringComparison.OrdinalIgnoreCase) < 0)
                .Cast<Renderer>().ToList();
            var bareHits = new Dictionary<string, int>();
            bool backfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            try
            {
                for (int i = 0; i < r.Count; i++)
                {
                    if (r.Nodes[i].Samples == 0) continue;
                    var p = r.Position(i);
                    int m = Match(g, p, 0.75f);
                    if (m >= 0) { exact++; dys.Add(p.Y - g.Nodes[m].Y); continue; }
                    if (MatchNear(g, p, 0.75f)) { near++; continue; }
                    string why = Classify(p, raw, solid, settings, minFloorNormal, hits, overlaps, bare, bareHits);
                    if (!missing.TryGetValue(why, out var l)) missing[why] = l = new List<SVector3>();
                    l.Add(p);
                }
            }
            finally { Physics.queriesHitBackfaces = backfaces; }

            int miss = recorded - exact - near;
            sb.AppendLine($"  coverage       {Pct(exact, recorded)} of stood-on replay cells have a geometry node in the same cell (±0.75 m), {Pct(near, recorded)} one cell off, {Pct(miss, recorded)} missing");
            if (dys.Count > 0)
            {
                dys.Sort();
                sb.AppendLine($"  height offset  replay − geometry node: p10 {dys[(int)(0.1 * (dys.Count - 1))]:+0.00;-0.00}, median {dys[dys.Count / 2]:+0.00;-0.00}, p90 {dys[(int)(0.9 * (dys.Count - 1))]:+0.00;-0.00} m (0 = replay positions are ActorHeight above the collider floor)");
            }
            sb.AppendLine("  missing replay cells, by what the colliders say there:");
            foreach (var kv in missing.OrderByDescending(k => k.Value.Count))
                sb.AppendLine($"    {kv.Key,-44} {kv.Value.Count,6}  {Places(info, kv.Value)}");

            if (bareHits.Count > 0)
                sb.AppendLine("    rendered meshes without a collider under missing cells: " + string.Join(", ", bareHits.OrderByDescending(k => k.Value).Take(10).Select(k => $"{k.Key} {k.Value}")));

            // ---- geometry nodes nobody stood on
            int visited = 0;
            var unvisited = new List<SVector3>();
            for (int i = 0; i < g.Count; i++)
            {
                var p = g.Position(i);
                if (Match(r, p, 1f) >= 0 || MatchNear(r, p, 1f)) visited++;
                else unvisited.Add(p);
            }
            sb.AppendLine($"  geometry nodes within a cell of a replay node: {Pct(visited, g.Count)}; the other {unvisited.Count} ({unvisited.Count * g.CellSize * g.CellSize:0} m²) nobody stood on in this replay: {Places(info, unvisited, 8)}");
            var elevated = unvisited.Where(p => !r.NodesAt(p.X, p.Z).Any()).ToList();
            sb.AppendLine($"    of which in columns with no replay node at all (ledges, box tops, roofs, unplayed corners): {elevated.Count}");

            // ---- steps players took that the geometry grid does not link
            var linkMiss = new Dictionary<string, List<SVector3>>();
            int linksChecked = 0;
            for (int i = 0; i < r.Count; i++)
            {
                if (r.Nodes[i].Samples == 0) continue;
                var pi = r.Position(i);
                int gi = Match(g, pi, 0.75f);
                if (gi < 0) continue;
                foreach (int j in r.Links(i))
                {
                    if (r.Nodes[j].Samples == 0) continue;
                    var pj = r.Position(j);
                    int gj = Match(g, pj, 0.75f);
                    if (gj < 0) continue;
                    linksChecked++;
                    bool linked = false;
                    foreach (int k in g.Links(gi)) if (k == gj) { linked = true; break; }
                    if (linked) continue;
                    float dy = g.Nodes[gj].Y - g.Nodes[gi].Y;
                    bool diagonal = g.Nodes[gj].X != g.Nodes[gi].X && g.Nodes[gj].Z != g.Nodes[gi].Z;
                    string why = dy > settings.MaxClimb ? $"climb > {settings.MaxClimb:0.0} m (box / ledge)"
                        : -dy > settings.MaxDrop ? $"drop > {settings.MaxDrop:0} m"
                        : diagonal ? "diagonal, a side cell missing (corner / door frame)" : "other";
                    if (!linkMiss.TryGetValue(why, out var l)) linkMiss[why] = l = new List<SVector3>();
                    l.Add(pi);
                }
            }
            sb.AppendLine($"  replay steps (links between stood-on cells) with both ends on the geometry grid: {linksChecked}, not linked there: {linkMiss.Values.Sum(l => l.Count)}");
            foreach (var kv in linkMiss.OrderByDescending(k => k.Value.Count))
                sb.AppendLine($"    {kv.Key,-44} {kv.Value.Count,6}  {Places(info, kv.Value)}");

            // ---- one-way drops in the geometry grid
            int drops = 0;
            var dropPlaces = new List<SVector3>();
            for (int i = 0; i < g.Count; i++)
                foreach (int j in g.Links(i))
                    if (g.Nodes[i].Y - g.Nodes[j].Y > settings.MaxClimb) { drops++; dropPlaces.Add(g.Position(i)); }
            sb.AppendLine($"  one-way drop links in the geometry grid: {drops}  {Places(info, dropPlaces)}");

            // ---- walking distances
            sb.AppendLine("  walking distance (m), same endpoints (the replay's spawns = where players stand at barrier drop, its plant centres)");
            sb.AppendLine("                                  replay grid   geometry grid");
            void Dist(string label, SVector3 a, SVector3 b) => sb.AppendLine($"    {label,-28} {PathLen(r, a, b),10:0.0}   {PathLen(g, a, b),12:0.0}");
            Dist("attacker → defender spawn", rep.AttackerSpawn, rep.DefenderSpawn);
            foreach (var gs in geo.Sites)
            {
                var rc = rep.SiteNamed(gs.Name)?.Center ?? gs.Center;
                Dist($"attacker spawn → {gs.Name} site", rep.AttackerSpawn, rc);
                Dist($"defender spawn → {gs.Name} site", rep.DefenderSpawn, rc);
            }
            // where the replay grid's route leaves the geometry grid
            foreach (var gs in geo.Sites)
            {
                var rs = rep.SiteNamed(gs.Name);
                Breaks(sb, $"attacker spawn → {gs.Name}", r, rep.AttackerSpawn, rs?.Center ?? gs.Center, g, raw, info, solid, settings, minFloorNormal, hits, overlaps, bare);
            }
            sb.AppendLine("  sites / spawns, geometry (callouts or markers) vs replay (plants, barrier-drop positions):");
            sb.AppendLine($"    attacker spawn  {SVector3.Distance(geo.AttackerSpawn, rep.AttackerSpawn):0.0} m apart; defender spawn {SVector3.Distance(geo.DefenderSpawn, rep.DefenderSpawn):0.0} m apart");
            foreach (var gs in geo.Sites)
            {
                var rs = rep.SiteNamed(gs.Name);
                sb.AppendLine(rs == null ? $"    site {gs.Name}: no plant on it in the replay"
                    : $"    site {gs.Name}: centres {SVector3.Distance(gs.Center, rs.Center):0.0} m apart, radius {gs.Radius:0.0} vs {rs.Radius:0.0} ({rs.Plants} plants)");
            }
            return sb.ToString();
        }

        /// <summary>Walks the replay grid's shortest path and lists the stretches where it is not on the geometry grid.</summary>
        static void Breaks(StringBuilder sb, string label, NavGrid r, SVector3 from, SVector3 to, NavGrid g, NavGrid raw, MapInfo info,
            Func<Collider, bool> solid, GeometrySimMap.Settings s, float minFloorNormal, RaycastHit[] hits, Collider[] overlaps, List<Renderer> bare)
        {
            int a = r.Nearest(from, 8f), b = r.Nearest(to, 8f);
            var path = new List<int>();
            if (a < 0 || b < 0 || !r.FindPath(a, b, path)) { sb.AppendLine($"  replay route {label}: none"); return; }
            var stretches = new List<string>();
            int run = 0;
            SVector3 start = default;
            string why = "";
            var scratch = new Dictionary<string, int>();
            bool backfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            try
            {
                for (int i = 0; i <= path.Count; i++)
                {
                    bool on = i == path.Count || Match(g, r.Position(path[i]), 0.75f) >= 0 || MatchNear(g, r.Position(path[i]), 0.75f);
                    if (!on)
                    {
                        if (run++ == 0) { start = r.Position(path[i]); why = Classify(start, raw, solid, s, minFloorNormal, hits, overlaps, bare, scratch); }
                        continue;
                    }
                    if (run > 0) stretches.Add($"{run} cells at {SelfPlay.Place(info, start)} ({start.X:0.0}, {start.Y:0.0}, {start.Z:0.0}): {why}");
                    run = 0;
                }
            }
            finally { Physics.queriesHitBackfaces = backfaces; }
            sb.AppendLine($"  replay route {label} ({path.Count} cells): " + (stretches.Count == 0 ? "all on the geometry grid" : $"{stretches.Count} gaps"));
            foreach (var st in stretches.Take(8)) sb.AppendLine("    " + st);
        }

        static string Classify(SVector3 p, NavGrid raw, Func<Collider, bool> solid, GeometrySimMap.Settings s, float minFloorNormal, RaycastHit[] hits, Collider[] overlaps,
            List<Renderer> bare, Dictionary<string, int> bareHits)
        {
            if (Match(raw, p, 0.75f) >= 0) return "walkable but cut off from both spawns";
            // the collider surface under the replay position (it is ActorHeight above its floor)
            var origin = new Vector3(p.X, p.Y + 1.1f, p.Z);
            if (!GeometrySimMap.Nearest(origin, Vector3.down, 1.1f + NavGrid.ActorHeight + 0.75f, solid, hits, out var hit))
            {
                // a rendered floor without a collider? (MapBuilder skips the collider of any object with a transparent submesh)
                Renderer best = null;
                foreach (var b in bare)
                {
                    var bb = b.bounds;
                    if (p.X < bb.min.x || p.X > bb.max.x || p.Z < bb.min.z || p.Z > bb.max.z || bb.min.y > p.Y || bb.max.y < p.Y - 3f) continue;
                    if (best == null || bb.size.sqrMagnitude < best.bounds.size.sqrMagnitude) best = b;
                }
                if (best == null)
                {
                    bool below = GeometrySimMap.Nearest(origin, Vector3.down, 30f, solid, hits, out var far);
                    float drop = below ? p.Y - NavGrid.ActorHeight - far.point.y : float.PositiveInfinity;
                    return drop < 3f ? "air, floor 1-3 m below (jumps, ledges)" : drop < 30f ? "air, floor 3-30 m below (drops, gaps)" : "air, nothing below";
                }
                string key = (best.transform.parent != null ? best.transform.parent.name + "/" : "") + best.name;
                bareHits.TryGetValue(key, out int c);
                bareHits[key] = c + 1;
                return "rendered mesh under it has no collider";
            }
            if (hit.point.y > p.Y + 0.2f) return "under a surface (inside geometry / low ceiling)";
            if (hit.normal.y > -minFloorNormal && hit.normal.y < minFloorNormal) return "steep surface (ramp / stair side)";
            float floor = hit.point.y;
            if (!GeometrySimMap.Fits(p.X, floor, p.Z, s, solid, hits, overlaps))
            {
                // a ceiling within the capsule's height, or a wall within its radius
                bool roof = GeometrySimMap.Nearest(new Vector3(p.X, floor + 0.05f, p.Z), Vector3.up, s.CapsuleHeight, solid, hits, out _);
                return roof ? "capsule blocked: low ceiling / door frame" : "capsule blocked: wall within 0.35 m (tight gap / wall-hugging)";
            }
            return "floor fits a capsule but no node (column merged)";
        }

        /// <summary>The node in p's column within dy of its height, or -1.</summary>
        static int Match(NavGrid g, SVector3 p, float dy)
        {
            foreach (int n in g.NodesAt(p.X, p.Z))
                if (Math.Abs(g.Nodes[n].Y - p.Y) <= dy) return n;
            return -1;
        }

        static bool MatchNear(NavGrid g, SVector3 p, float dy)
        {
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                    if ((dx != 0 || dz != 0) && Match(g, p + new SVector3(dx * g.CellSize, 0f, dz * g.CellSize), dy) >= 0) return true;
            return false;
        }

        static float PathLen(NavGrid g, SVector3 a, SVector3 b)
        {
            int s = g.Nearest(a, 8f), t = g.Nearest(b, 8f);
            return s < 0 || t < 0 ? float.NaN : g.PathLength(s, t);
        }

        static float Area(NavGrid g) => g.Count * g.CellSize * g.CellSize;

        static string Pct(int k, int of) => of == 0 ? "-" : $"{100f * k / of:0.0}% ({k})";

        static string Places(MapInfo info, List<SVector3> points, int top = 5)
        {
            if (points.Count == 0) return "";
            return "[" + string.Join(", ", points.GroupBy(p => SelfPlay.Place(info, p)).OrderByDescending(x => x.Count()).Take(top).Select(x => $"{x.Key} {x.Count()}")) + "]";
        }
    }
}
