using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ValoReplay.EditorTools.Maps
{
    /// <summary>
    /// Low-poly copies of map meshes: quadric edge collapse (Garland and Heckbert) down to a share of the triangles,
    /// then flat shading. Vertices are welded by position and UV first, so hard normals don't stop a collapse; an edge
    /// used by one triangle only (an open edge or a UV seam) is never collapsed, which keeps outlines and textures in
    /// place. A collapse moves an edge onto one of its two ends (that end keeps its UVs and colour), and is refused when
    /// it would flip a neighbouring triangle.
    /// </summary>
    internal static class MeshSimplifier
    {
        private struct Quadric
        {
            public double A, B, C, D, E, F, G, H, I, J;    // the upper triangle of the plane's outer product

            public static Quadric Plane(double a, double b, double c, double d) => new Quadric
            {
                A = a * a, B = a * b, C = a * c, D = a * d, E = b * b, F = b * c, G = b * d, H = c * c, I = c * d, J = d * d,
            };

            public static Quadric operator +(Quadric p, Quadric q) => new Quadric
            {
                A = p.A + q.A, B = p.B + q.B, C = p.C + q.C, D = p.D + q.D, E = p.E + q.E,
                F = p.F + q.F, G = p.G + q.G, H = p.H + q.H, I = p.I + q.I, J = p.J + q.J,
            };

            public double Error(Vector3 v)
            {
                double x = v.x, y = v.y, z = v.z;
                return A * x * x + 2 * B * x * y + 2 * C * x * z + 2 * D * x + E * y * y
                     + 2 * F * y * z + 2 * G * y + H * z * z + 2 * I * z + J;
            }
        }

        private struct Tri
        {
            public int V0, V1, V2, Sub;
            public double E0, E1, E2, Min;
            public bool Deleted, Dirty;
            public Vector3 N;

            public int this[int k]
            {
                get => k == 0 ? V0 : k == 1 ? V1 : V2;
                set { if (k == 0) V0 = value; else if (k == 1) V1 = value; else V2 = value; }
            }

            public double Err(int k) => k == 0 ? E0 : k == 1 ? E1 : E2;
        }

        private struct Vert
        {
            public Vector3 P;
            public Quadric Q;
            public int TStart, TCount, Attr;
            public bool Border;
        }

        private struct Ref { public int T, K; }

        private sealed class State
        {
            public Tri[] Tris;
            public int TriCount;
            public Vert[] Verts;
            public List<Ref> Refs = new List<Ref>();
        }

        /// <summary>A flat-shaded copy of <paramref name="mesh"/> with about <paramref name="ratio"/> of its triangles
        /// (meshes under <paramref name="minTriangles"/> keep all of theirs). Submeshes keep their order.</summary>
        public static Mesh Simplify(Mesh mesh, float ratio, int minTriangles)
        {
            var pos = mesh.vertices;
            var uv0 = mesh.uv;
            var uv1 = mesh.uv2;
            var col = mesh.colors32;
            bool hasUv0 = uv0 != null && uv0.Length == pos.Length;
            bool hasUv1 = uv1 != null && uv1.Length == pos.Length;
            bool hasCol = col != null && col.Length == pos.Length;

            // weld by position and UV
            var weld = new Dictionary<(long, long, long, long, long), int>();
            var remap = new int[pos.Length];
            var verts = new List<Vert>();
            for (int i = 0; i < pos.Length; i++)
            {
                var u = hasUv0 ? uv0[i] : Vector2.zero;
                var key = (Q(pos[i].x), Q(pos[i].y), Q(pos[i].z), Q(u.x), Q(u.y));
                if (!weld.TryGetValue(key, out int w))
                {
                    w = verts.Count;
                    weld[key] = w;
                    verts.Add(new Vert { P = pos[i], Attr = i });
                }
                remap[i] = w;
            }

            var tris = new List<Tri>();
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var idx = mesh.GetTriangles(s);
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    int a = remap[idx[t]], b = remap[idx[t + 1]], c = remap[idx[t + 2]];
                    if (a == b || b == c || a == c) continue;
                    tris.Add(new Tri { V0 = a, V1 = b, V2 = c, Sub = s });
                }
            }

            var st = new State { Tris = tris.ToArray(), TriCount = tris.Count, Verts = verts.ToArray() };
            if (st.TriCount >= minTriangles)
                Run(st, Math.Max(4, (int)(st.TriCount * Mathf.Clamp01(ratio))));

            // flat-shaded output: three vertices per triangle
            int live = 0;
            for (int i = 0; i < st.TriCount; i++) if (!st.Tris[i].Deleted) live++;
            int n = live * 3;
            var oPos = new Vector3[n];
            var oUv0 = hasUv0 ? new Vector2[n] : null;
            var oUv1 = hasUv1 ? new Vector2[n] : null;
            var oCol = hasCol ? new Color32[n] : null;
            var bySub = new List<int>[mesh.subMeshCount];
            for (int s = 0; s < bySub.Length; s++) bySub[s] = new List<int>();
            int o = 0;
            for (int i = 0; i < st.TriCount; i++)
            {
                var t = st.Tris[i];
                if (t.Deleted) continue;
                for (int k = 0; k < 3; k++, o++)
                {
                    var v = st.Verts[t[k]];
                    oPos[o] = v.P;
                    if (hasUv0) oUv0[o] = uv0[v.Attr];
                    if (hasUv1) oUv1[o] = uv1[v.Attr];
                    if (hasCol) oCol[o] = col[v.Attr];
                    bySub[t.Sub].Add(o);
                }
            }

            var result = new Mesh { name = mesh.name + "_Low", indexFormat = n > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            result.SetVertices(oPos);
            if (hasUv0) result.SetUVs(0, oUv0);
            if (hasUv1) result.SetUVs(1, oUv1);
            if (hasCol) result.SetColors(oCol);
            result.subMeshCount = bySub.Length;
            for (int s = 0; s < bySub.Length; s++) result.SetTriangles(bySub[s], s, false);
            result.RecalculateBounds();
            result.RecalculateNormals();     // no shared vertices: face normals
            return result;
        }

        private static long Q(float v) => (long)Math.Round(v * 10000.0);

        // ------------------------------------------------------------------------------------------ edge collapse
        /// <summary>Largest quadric error (sum of squared distances to the original faces, m²) a collapse may cost: about
        /// 4 cm of shape change. A mesh that can't reach its target within it stays above the target rather than
        /// losing its shape (without the cap, a door frame's posts were pulled into spikes).</summary>
        private const double MaxError = 0.0016;

        private static void Run(State st, int target)
        {
            int deleted = 0, quietPasses = 0;
            var dead0 = new List<bool>();
            var dead1 = new List<bool>();
            for (int iter = 0; iter < 100; iter++)
            {
                if (st.TriCount - deleted <= target) break;
                if (iter % 5 == 0) { Update(st, iter); deleted = 0; }
                for (int i = 0; i < st.TriCount; i++) st.Tris[i].Dirty = false;
                // the error allowed grows each pass, so the cheapest collapses go first, up to the cap
                double threshold = Math.Min(MaxError, 1e-9 * Math.Pow(iter + 3, 7));
                int before = deleted;

                for (int i = 0; i < st.TriCount && st.TriCount - deleted > target; i++)
                {
                    var t = st.Tris[i];
                    if (t.Deleted || t.Dirty || t.Min > threshold) continue;
                    for (int j = 0; j < 3; j++)
                    {
                        if (t.Err(j) > threshold) continue;
                        int i0 = t[j], i1 = t[(j + 1) % 3];
                        ref var v0 = ref st.Verts[i0];
                        ref var v1 = ref st.Verts[i1];
                        if (v0.Border || v1.Border) continue;

                        EdgeError(v0, v1, out bool toV1);
                        var p = toV1 ? v1.P : v0.P;
                        Resize(dead0, v0.TCount);
                        Resize(dead1, v1.TCount);
                        if (Flipped(st, p, i1, v0, dead0) || Flipped(st, p, i0, v1, dead1)) continue;

                        v0.P = p;
                        if (toV1) v0.Attr = v1.Attr;
                        v0.Q = v0.Q + v1.Q;
                        int start = st.Refs.Count;
                        Retarget(st, i0, v0, dead0, ref deleted);
                        Retarget(st, i0, v1, dead1, ref deleted);
                        int count = st.Refs.Count - start;
                        if (count <= v0.TCount)
                        {
                            for (int k = 0; k < count; k++) st.Refs[v0.TStart + k] = st.Refs[start + k];
                            st.Refs.RemoveRange(start, count);
                        }
                        else v0.TStart = start;
                        v0.TCount = count;
                        break;
                    }
                }
                // at the cap, stop once a pass (with fresh references) finds nothing more to collapse
                if (deleted != before) quietPasses = 0;
                else if (threshold >= MaxError && ++quietPasses >= 2) break;
            }
            Compact(st);
        }

        private static void Resize(List<bool> list, int n)
        {
            list.Clear();
            for (int i = 0; i < n; i++) list.Add(false);
        }

        private static double EdgeError(in Vert a, in Vert b, out bool toB)
        {
            var q = a.Q + b.Q;
            double ea = q.Error(a.P), eb = q.Error(b.P);
            toB = eb < ea;
            return Math.Min(ea, eb);
        }

        /// <summary>Would moving <paramref name="v"/> to <paramref name="p"/> flip or squash one of its triangles?
        /// Marks the triangles that the collapse removes (those also using <paramref name="other"/>).</summary>
        private static bool Flipped(State st, Vector3 p, int other, in Vert v, List<bool> dead)
        {
            for (int k = 0; k < v.TCount; k++)
            {
                var r = st.Refs[v.TStart + k];
                ref var t = ref st.Tris[r.T];
                if (t.Deleted) continue;
                int a = t[(r.K + 1) % 3], b = t[(r.K + 2) % 3];
                if (a == other || b == other) { dead[k] = true; continue; }
                var d1 = (st.Verts[a].P - p).normalized;
                var d2 = (st.Verts[b].P - p).normalized;
                if (Mathf.Abs(Vector3.Dot(d1, d2)) > 0.999f) return true;
                var n = Vector3.Cross(d1, d2).normalized;
                dead[k] = false;
                // against the triangle as it is now and as it was in the original mesh
                var now = Vector3.Cross(st.Verts[a].P - v.P, st.Verts[b].P - v.P).normalized;
                if (Vector3.Dot(n, now) < 0.2f || Vector3.Dot(n, t.N) < 0.2f) return true;
            }
            return false;
        }

        private static void Retarget(State st, int i0, in Vert v, List<bool> dead, ref int deleted)
        {
            for (int k = 0; k < v.TCount; k++)
            {
                var r = st.Refs[v.TStart + k];
                ref var t = ref st.Tris[r.T];
                if (t.Deleted) continue;
                if (dead[k]) { t.Deleted = true; deleted++; continue; }
                t[r.K] = i0;
                t.Dirty = true;
                Errors(st, ref t);
                st.Refs.Add(r);
            }
        }

        private static void Errors(State st, ref Tri t)
        {
            t.E0 = EdgeError(st.Verts[t.V0], st.Verts[t.V1], out _);
            t.E1 = EdgeError(st.Verts[t.V1], st.Verts[t.V2], out _);
            t.E2 = EdgeError(st.Verts[t.V2], st.Verts[t.V0], out _);
            t.Min = Math.Min(t.E0, Math.Min(t.E1, t.E2));
        }

        /// <summary>Drops deleted triangles and rebuilds the vertex-to-triangle references; on the first pass also
        /// finds open edges and sums each vertex's plane quadric.</summary>
        private static void Update(State st, int iter)
        {
            if (iter > 0)
            {
                int dst = 0;
                for (int i = 0; i < st.TriCount; i++) if (!st.Tris[i].Deleted) st.Tris[dst++] = st.Tris[i];
                st.TriCount = dst;
            }

            var verts = st.Verts;
            for (int i = 0; i < verts.Length; i++) { verts[i].TStart = 0; verts[i].TCount = 0; }
            for (int i = 0; i < st.TriCount; i++)
                for (int k = 0; k < 3; k++) verts[st.Tris[i][k]].TCount++;
            int start = 0;
            for (int i = 0; i < verts.Length; i++) { verts[i].TStart = start; start += verts[i].TCount; verts[i].TCount = 0; }
            st.Refs.Clear();
            for (int i = 0; i < start; i++) st.Refs.Add(default);
            for (int i = 0; i < st.TriCount; i++)
                for (int k = 0; k < 3; k++)
                {
                    ref var v = ref verts[st.Tris[i][k]];
                    st.Refs[v.TStart + v.TCount] = new Ref { T = i, K = k };
                    v.TCount++;
                }

            if (iter != 0) return;

            // an edge is open when only one triangle uses it: both its ends are then on a border
            var counts = new Dictionary<int, int>();
            for (int i = 0; i < verts.Length; i++)
            {
                counts.Clear();
                for (int k = 0; k < verts[i].TCount; k++)
                {
                    var t = st.Tris[st.Refs[verts[i].TStart + k].T];
                    for (int c = 0; c < 3; c++)
                    {
                        int id = t[c];
                        if (id == i) continue;
                        counts.TryGetValue(id, out int m);
                        counts[id] = m + 1;
                    }
                }
                foreach (var kv in counts)
                    if (kv.Value == 1) { verts[i].Border = true; verts[kv.Key].Border = true; }
            }

            for (int i = 0; i < verts.Length; i++) verts[i].Q = default;
            for (int i = 0; i < st.TriCount; i++)
            {
                ref var t = ref st.Tris[i];
                var p0 = verts[t.V0].P;
                var n = Vector3.Cross(verts[t.V1].P - p0, verts[t.V2].P - p0);
                n = n.sqrMagnitude > 1e-20f ? n.normalized : Vector3.zero;
                t.N = n;
                var plane = Quadric.Plane(n.x, n.y, n.z, -Vector3.Dot(n, p0));
                verts[t.V0].Q = verts[t.V0].Q + plane;
                verts[t.V1].Q = verts[t.V1].Q + plane;
                verts[t.V2].Q = verts[t.V2].Q + plane;
            }
            for (int i = 0; i < st.TriCount; i++) Errors(st, ref st.Tris[i]);
        }

        private static void Compact(State st)
        {
            int dst = 0;
            for (int i = 0; i < st.TriCount; i++) if (!st.Tris[i].Deleted) st.Tris[dst++] = st.Tris[i];
            st.TriCount = dst;
        }
    }
}
