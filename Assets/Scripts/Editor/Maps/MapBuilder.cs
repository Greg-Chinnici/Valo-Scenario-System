using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Rendering.Universal.ShaderGUI;
using UnityEngine;
using UnityEngine.Rendering;
using ValorantReplay.Playback;
using ValoReplay.Maps;

namespace ValoReplay.EditorTools.Maps
{
    /// <summary>
    /// Turns MapExports/&lt;Map&gt; (written by Tools/MapExport/export_map.py) into Unity assets under Assets/Maps/&lt;Map&gt;:
    /// textures, one mesh asset holding every unique mesh, URP materials and &lt;Map&gt;.prefab, already in replay space.
    /// Registers the prefab in Assets/Maps/Resources/MapRegistry.asset.
    /// </summary>
    public static class MapBuilder
    {
        public const string ExportRoot = "MapExports";
        public const string AssetRoot = "Assets/Maps";
        private const string RegistryPath = AssetRoot + "/Resources/" + MapRegistry.ResourcePath + ".asset";

        /// <summary>Textures the current build uses as normal maps / packed mask maps (read by MapTexturePostprocessor).</summary>
        internal static readonly HashSet<string> NormalMaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal static readonly HashSet<string> MaskMaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        [MenuItem("ValoReplay/Maps/Build All Exported Maps")]
        private static void BuildAllMenu()
        {
            var maps = ExportedMaps();
            if (maps.Count == 0) { EditorUtility.DisplayDialog("No map exports", $"Nothing in {Path.GetFullPath(ExportRoot)}. Run Tools/MapExport/export_all.ps1 first.", "OK"); return; }
            if (!EditorUtility.DisplayDialog("Build maps", $"Build {maps.Count} maps ({string.Join(", ", maps)})? This imports thousands of textures and can take a while.", "Build", "Cancel")) return;
            foreach (var m in maps) Build(m);
        }

        [MenuItem("ValoReplay/Maps/Build One Map...")]
        private static void BuildOneMenu()
        {
            string dir = EditorUtility.OpenFolderPanel("Map export folder", Path.GetFullPath(ExportRoot), "");
            if (string.IsNullOrEmpty(dir)) return;
            Build(Path.GetFileName(dir.TrimEnd('/', '\\')));
        }

        // ------------------------------------------------------------------------------------------ style
        // Grey (the default) builds maps in near-greys: colour textures and tints keep only a trace of their colour and
        // a little less contrast, so the maps read as a neutral backdrop rather than the game's own art. Shapes, normal
        // maps and lighting are unchanged. Off copies the exported textures as they are. Rebuild a map to apply.
        private const string GreyPref = "ValoReplay.Maps.GreyTextures";
        private const string GreyMenu = "ValoReplay/Maps/Grey Textures";
        private const float Saturation = 0.12f;     // share of the original colour kept
        private const float Contrast = 0.85f;       // detail kept around each texture's mean brightness
        private static readonly Color GreyTint = new Color(1f, 1.01f, 1.04f);   // the same cool grey as the viewer's plain mode

        internal static bool Grey
        {
            get => EditorPrefs.GetBool(GreyPref, true);
            set => EditorPrefs.SetBool(GreyPref, value);
        }

        private static string StyleTag => (Grey ? $"grey s{Saturation} c{Contrast}" : "original") + (LowPoly ? $" low{LowPolyTextureSize}" : "");

        // Low poly (on by default) draws every mesh with about a third of its triangles, flat shaded, without normal
        // maps and with smaller textures. Colliders keep the full meshes, so sight lines, shots and movement are the
        // same either way. Rebuild a map to apply.
        private const string LowPolyPref = "ValoReplay.Maps.LowPoly";
        private const string LowPolyMenu = "ValoReplay/Maps/Low Poly";
        private const float LowPolyRatio = 0.3f;        // share of each mesh's triangles kept
        private const int LowPolyMinTriangles = 48;     // smaller meshes keep all theirs (they are still flat shaded)
        internal const int LowPolyTextureSize = 256;
        internal const int TextureSize = 1024;

        internal static bool LowPoly
        {
            get => EditorPrefs.GetBool(LowPolyPref, true);
            set => EditorPrefs.SetBool(LowPolyPref, value);
        }

        [MenuItem(LowPolyMenu, false, 101)]
        private static void ToggleLowPoly()
        {
            LowPoly = !LowPoly;
            Debug.Log($"[MapBuilder] Low poly {(LowPoly ? "on" : "off")}. Rebuild maps (ValoReplay > Maps > Build) to apply.");
        }

        [MenuItem(LowPolyMenu, true)]
        private static bool ToggleLowPolyValidate()
        {
            Menu.SetChecked(LowPolyMenu, LowPoly);
            return true;
        }

        [MenuItem(GreyMenu, false, 100)]
        private static void ToggleGrey()
        {
            Grey = !Grey;
            Debug.Log($"[MapBuilder] Grey textures {(Grey ? "on" : "off")}. Rebuild maps (ValoReplay > Maps > Build) to apply.");
        }

        [MenuItem(GreyMenu, true)]
        private static bool ToggleGreyValidate()
        {
            Menu.SetChecked(GreyMenu, Grey);
            return true;
        }

        private static Color Desaturate(Color c)
        {
            float l = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
            return new Color(Mathf.Lerp(l, c.r, Saturation) * GreyTint.r,
                             Mathf.Lerp(l, c.g, Saturation) * GreyTint.g,
                             Mathf.Lerp(l, c.b, Saturation) * GreyTint.b, c.a);
        }

        /// <summary>A colour texture into the build: copied as is, or greyed (alpha kept, so cut-outs keep their holes).</summary>
        private static void WriteColour(string from, string to, bool restyle)
        {
            if (!File.Exists(from)) return;
            if (!Grey)
            {
                if (restyle) File.Copy(from, to, true); else CopyIfChanged(from, to);
                return;
            }
            if (!restyle && File.Exists(to) && File.GetLastWriteTimeUtc(to) >= File.GetLastWriteTimeUtc(from)) return;

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            try
            {
                if (!tex.LoadImage(File.ReadAllBytes(from)))
                {
                    Debug.LogWarning($"[MapBuilder] Could not read {from}; copied without greying.");
                    File.Copy(from, to, true);
                    return;
                }
                var px = tex.GetPixels32();
                double sum = 0;
                for (int i = 0; i < px.Length; i++) sum += 0.2126 * px[i].r + 0.7152 * px[i].g + 0.0722 * px[i].b;
                float mean = px.Length > 0 ? (float)(sum / px.Length) : 128f;
                for (int i = 0; i < px.Length; i++)
                {
                    var c = px[i];
                    float l = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
                    float g = mean + (l - mean) * Contrast;
                    px[i] = new Color32(Byte((g + (c.r - l) * Saturation) * GreyTint.r),
                                        Byte((g + (c.g - l) * Saturation) * GreyTint.g),
                                        Byte((g + (c.b - l) * Saturation) * GreyTint.b), c.a);
                }
                tex.SetPixels32(px);
                File.WriteAllBytes(to, tex.EncodeToPNG());
            }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
        }

        private static byte Byte(float v) => (byte)Mathf.Clamp(Mathf.RoundToInt(v), 0, 255);

        public static List<string> ExportedMaps() =>
            Directory.Exists(ExportRoot)
                ? Directory.GetDirectories(ExportRoot).Where(d => File.Exists(Path.Combine(d, "map.json"))).Select(Path.GetFileName).OrderBy(n => n).ToList()
                : new List<string>();

        public static void Build(string mapName)
        {
            string src = Path.Combine(ExportRoot, mapName);
            string dst = $"{AssetRoot}/{mapName}";
            var levelPath = MapCatalog.All.FirstOrDefault(m => m.DisplayName.Equals(mapName, StringComparison.OrdinalIgnoreCase))?.MapUrl;
            if (levelPath == null) Debug.LogWarning($"[MapBuilder] '{mapName}' is not in MapCatalog; name the export folder after the map's display name.");

            try
            {
                EditorUtility.DisplayProgressBar($"Building {mapName}", "Reading export", 0f);
                var manifest = JsonUtility.FromJson<MapManifest>(File.ReadAllText(Path.Combine(src, "map.json")));
                var raw = MeshesBin.Read(Path.Combine(src, "meshes.bin"));

                Directory.CreateDirectory(dst + "/Textures");
                Directory.CreateDirectory(dst + "/Materials");
                var textures = ImportTextures(manifest, src, dst, mapName);
                var materials = BuildMaterials(manifest, textures, dst, mapName);
                var meshes = BuildMeshes(raw, dst, mapName);
                var render = LowPoly ? LowPolyMeshes(meshes, dst, mapName) : meshes;
                var prefab = BuildPrefab(manifest, raw, meshes, render, materials, dst, mapName);
                Register(mapName, levelPath, prefab);
                Debug.Log($"[MapBuilder] {mapName}: {meshes.Length} meshes, {materials.Length} materials, {manifest.objects.Count} objects -> {AssetDatabase.GetAssetPath(prefab)}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                NormalMaps.Clear();
                MaskMaps.Clear();
            }
        }

        // ------------------------------------------------------------------------------------------ textures
        private sealed class MapTextures
        {
            public readonly Dictionary<string, Texture2D> ByFile = new Dictionary<string, Texture2D>();
            public readonly Dictionary<string, Texture2D> MaskByMra = new Dictionary<string, Texture2D>();
        }

        private static MapTextures ImportTextures(MapManifest manifest, string src, string dst, string mapName)
        {
            var colour = new HashSet<string>();
            var normals = new HashSet<string>();
            var mras = new HashSet<string>();
            foreach (var m in manifest.materials)
            {
                Add(colour, DiffuseFile(m));
                Add(normals, NormalFile(m));
                Add(mras, MraFile(m));
            }
            NormalMaps.Clear();
            MaskMaps.Clear();
            foreach (var n in normals) NormalMaps.Add(n);

            // the style the existing textures were written in, kept beside them (dot files are ignored by Unity)
            string styleFile = $"{dst}/Textures/.style";
            bool restyle = !File.Exists(styleFile) ? Grey : File.ReadAllText(styleFile).Trim() != StyleTag;

            AssetDatabase.StartAssetEditing();
            try
            {
                int i = 0, total = colour.Count + normals.Count + mras.Count;
                foreach (var file in colour)
                {
                    if (i++ % 20 == 0) EditorUtility.DisplayProgressBar($"Building {mapName}", $"{(Grey ? "Greying" : "Copying")} textures ({i}/{total})", 0.1f + 0.3f * i / Math.Max(1, total));
                    if (normals.Contains(file)) continue;     // a normal map is never greyed
                    WriteColour(Path.Combine(src, "textures", file), $"{dst}/Textures/{file}", restyle);
                }
                foreach (var file in normals)
                {
                    if (i++ % 20 == 0) EditorUtility.DisplayProgressBar($"Building {mapName}", $"Copying textures ({i}/{total})", 0.1f + 0.3f * i / Math.Max(1, total));
                    CopyIfChanged(Path.Combine(src, "textures", file), $"{dst}/Textures/{file}");
                }
                File.WriteAllText(styleFile, StyleTag);
                foreach (var file in mras)
                {
                    if (i++ % 10 == 0) EditorUtility.DisplayProgressBar($"Building {mapName}", $"Converting mask maps ({i}/{total})", 0.1f + 0.3f * i / Math.Max(1, total));
                    string mask = MaskName(file);
                    MaskMaps.Add(mask);
                    ConvertMra(Path.Combine(src, "textures", file), $"{dst}/Textures/{mask}");
                }
            }
            finally { AssetDatabase.StopAssetEditing(); }
            EditorUtility.DisplayProgressBar($"Building {mapName}", "Importing textures", 0.4f);
            AssetDatabase.Refresh();

            var result = new MapTextures();
            foreach (var file in colour.Concat(normals))
                result.ByFile[file] = AssetDatabase.LoadAssetAtPath<Texture2D>($"{dst}/Textures/{file}");
            foreach (var file in mras)
                result.MaskByMra[file] = AssetDatabase.LoadAssetAtPath<Texture2D>($"{dst}/Textures/{MaskName(file)}");
            return result;
        }

        private static void Add(HashSet<string> set, string file) { if (!string.IsNullOrEmpty(file)) set.Add(file); }

        private static string MaskName(string mraFile) => Path.GetFileNameWithoutExtension(mraFile) + "_MS.png";

        private static void CopyIfChanged(string from, string to)
        {
            if (!File.Exists(from)) return;
            if (File.Exists(to) && new FileInfo(to).Length == new FileInfo(from).Length) return;
            File.Copy(from, to, true);
        }

        /// <summary>Unreal MRA (R metallic, G roughness, B AO) -> URP mask (R metallic, G occlusion, A smoothness).</summary>
        private static void ConvertMra(string from, string to)
        {
            if (!File.Exists(from) || File.Exists(to)) return;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            if (!tex.LoadImage(File.ReadAllBytes(from))) { UnityEngine.Object.DestroyImmediate(tex); return; }
            var px = tex.GetPixels32();
            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];
                px[i] = new Color32(c.r, c.b, c.b, (byte)(255 - c.g));
            }
            tex.SetPixels32(px);
            File.WriteAllBytes(to, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }

        // ------------------------------------------------------------------------------------------ materials
        private static string DiffuseFile(MaterialEntry m) => m.Texture("Diffuse", "Diffuse A", "Albedo", "Albedo_DF", "Base Color", "Diffuse Low");
        private static string NormalFile(MaterialEntry m) => m.Texture("Normal", "Texture A Normal", "Normal_NM");
        private static string MraFile(MaterialEntry m) => m.Texture("MRA", "MRA A", "MRA_MRA");

        private enum Surface { Opaque, Cutout, Transparent }

        private static Material[] BuildMaterials(MapManifest manifest, MapTextures textures, string dst, string mapName)
        {
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            var unlit = Shader.Find("Universal Render Pipeline/Unlit");
            var result = new Material[manifest.materials.Count];
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < result.Length; i++)
            {
                if (i % 25 == 0) EditorUtility.DisplayProgressBar($"Building {mapName}", $"Materials ({i}/{result.Length})", 0.5f + 0.1f * i / result.Length);
                var e = manifest.materials[i];
                string name = Sanitize(string.IsNullOrEmpty(e.name) ? "Default" : e.name);
                while (!usedNames.Add(name)) name += "_";
                string path = $"{dst}/Materials/{name}.mat";

                bool isUnlit = e.shader.IndexOf("Unlit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               e.shader.IndexOf("Flame", StringComparison.OrdinalIgnoreCase) >= 0;
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null)
                {
                    mat = new Material(isUnlit ? unlit : lit);
                    AssetDatabase.CreateAsset(mat, path);
                }
                else mat.shader = isUnlit ? unlit : lit;
                Configure(mat, e, textures, isUnlit);
                EditorUtility.SetDirty(mat);
                result[i] = mat;
            }
            AssetDatabase.SaveAssets();
            return result;
        }

        private static void Configure(Material mat, MaterialEntry e, MapTextures textures, bool isUnlit)
        {
            textures.ByFile.TryGetValue(DiffuseFile(e) ?? "", out var diffuse);
            textures.ByFile.TryGetValue(NormalFile(e) ?? "", out var normal);
            if (LowPoly) normal = null;      // flat faces: surface bumps would bring the detail back
            textures.MaskByMra.TryGetValue(MraFile(e) ?? "", out var mask);

            // BLENDMODE from the importer: 1 = masked (decals, papers, foliage cards). 0 and 2 both appear on opaque
            // surfaces, so translucency is taken from the material name / shader instead.
            float blend = e.ParamFloat("BLENDMODE", 2f);
            var surface = SeeThrough(e) ? Surface.Transparent : Mathf.Approximately(blend, 1f) ? Surface.Cutout : Surface.Opaque;
            bool twoSided = e.ParamFloat("TWOSIDED", 0f) > 0.5f || surface == Surface.Cutout;

            var tint = ToColor(e.Param("Tint"), Color.white);
            var baseColor = diffuse != null ? tint : ToColor(e.Param("DiffuseColor"), Color.gray) * tint;
            if (surface == Surface.Transparent) baseColor.a = Mathf.Min(baseColor.a, 0.35f);
            if (Grey) baseColor = Desaturate(baseColor);

            mat.SetTexture("_BaseMap", diffuse);
            mat.SetColor("_BaseColor", baseColor);
            mat.SetFloat("_Surface", surface == Surface.Transparent ? 1f : 0f);
            mat.SetFloat("_Blend", 0f);
            mat.SetFloat("_AlphaClip", surface == Surface.Cutout ? 1f : 0f);
            mat.SetFloat("_Cutoff", 0.4f);
            mat.SetFloat("_Cull", twoSided ? (float)CullMode.Off : (float)CullMode.Back);

            if (!isUnlit)
            {
                mat.SetTexture("_BumpMap", normal);
                mat.SetFloat("_BumpScale", 1f);
                mat.SetTexture("_MetallicGlossMap", mask);
                mat.SetTexture("_OcclusionMap", mask);
                mat.SetFloat("_OcclusionStrength", mask != null ? 1f : 0f);
                mat.SetFloat("_Smoothness", mask != null ? 1f : 1f - e.ParamFloat("Roughness", 0.8f));
                mat.SetFloat("_Metallic", mask != null ? 1f : e.ParamFloat("Metallic", 0f));
                mat.SetFloat("_SmoothnessTextureChannel", 0f);   // metallic map alpha
                BaseShaderGUI.SetMaterialKeywords(mat, LitGUI.SetMaterialKeywords);
            }
            else BaseShaderGUI.SetMaterialKeywords(mat);
        }

        private static readonly string[] SolidGlass = { "opaque", "shard", "stylized", "hourglass", "trim", "palette" };

        /// <summary>
        /// Drawn see-through, and left out of collision (sight, shots and movement pass): glass panes, water surfaces,
        /// decals and flames. Names alone over-match, so these stay solid: radianite crystals ("GlassShardGlow"), opaque
        /// or stylized (fake interior) glass, glass trims, and floors, towers or props with "Water" in the name (blend
        /// floors such as HerringBoneWater, WaterTower, WaterFountain).
        /// </summary>
        private static bool SeeThrough(MaterialEntry e)
        {
            string name = (e.name ?? "").ToLowerInvariant();
            string shader = (e.shader ?? "").ToLowerInvariant();
            if (name.Contains("flame") || shader.Contains("flame")) return true;
            if (shader.Contains("opacity")) return true;                                    // decals, puddles, cracks
            if (name.Contains("waterfall")) return true;
            if ((shader.Contains("water") && !shader.Contains("blend")) || name.StartsWith("water_")) return true;
            if (name.Contains("glass") || shader.Contains("glass"))
                return !SolidGlass.Any(name.Contains);
            return false;
        }

        private static Color ToColor(float[] v, Color fallback) =>
            v == null || v.Length < 3 ? fallback : new Color(v[0], v[1], v[2], v.Length > 3 ? v[3] : 1f);

        private static string Sanitize(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Length > 80 ? s.Substring(0, 80) : s;
        }

        // ------------------------------------------------------------------------------------------ meshes
        private static Mesh[] BuildMeshes(List<RawMesh> raw, string dst, string mapName)
        {
            string path = $"{dst}/{mapName}_Meshes.asset";
            AssetDatabase.DeleteAsset(path);
            // one .asset file: the first mesh is the main object, the rest are sub-assets
            var result = new Mesh[raw.Count];
            for (int m = 0; m < raw.Count; m++)
            {
                if (m % 50 == 0) EditorUtility.DisplayProgressBar($"Building {mapName}", $"Meshes ({m}/{raw.Count})", 0.6f + 0.2f * m / raw.Count);
                result[m] = ToMesh(raw[m]);
                if (m == 0) AssetDatabase.CreateAsset(result[m], path);
                else AssetDatabase.AddObjectToAsset(result[m], path);
            }
            AssetDatabase.SaveAssets();
            return result;
        }

        /// <summary>Simplified, flat-shaded copies of the meshes for drawing (saved with them).</summary>
        private static Mesh[] LowPolyMeshes(Mesh[] meshes, string dst, string mapName)
        {
            string path = $"{dst}/{mapName}_Meshes.asset";
            var result = new Mesh[meshes.Length];
            long before = 0, after = 0;
            for (int m = 0; m < meshes.Length; m++)
            {
                if (m % 25 == 0) EditorUtility.DisplayProgressBar($"Building {mapName}", $"Low poly meshes ({m}/{meshes.Length})", 0.7f + 0.1f * m / meshes.Length);
                for (int s = 0; s < meshes[m].subMeshCount; s++) before += meshes[m].GetIndexCount(s) / 3;
                result[m] = MeshSimplifier.Simplify(meshes[m], LowPolyRatio, LowPolyMinTriangles);
                for (int s = 0; s < result[m].subMeshCount; s++) after += result[m].GetIndexCount(s) / 3;
                AssetDatabase.AddObjectToAsset(result[m], path);
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[MapBuilder] {mapName}: low poly {after:N0} of {before:N0} triangles ({100.0 * after / Math.Max(1, before):F0}%)");
            return result;
        }

        private static Mesh ToMesh(RawMesh r)
        {
            int n = r.VertexCount;
            var pos = new Vector3[n];
            var nrm = new Vector3[n];
            var uv0 = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                MapSpace.Point(r.Positions[i * 3], r.Positions[i * 3 + 1], r.Positions[i * 3 + 2], out float x, out float y, out float z);
                pos[i] = new Vector3(x, y, z);
                MapSpace.Point(r.Normals[i * 3], r.Normals[i * 3 + 1], r.Normals[i * 3 + 2], out x, out y, out z);
                nrm[i] = new Vector3(x, y, z);
                uv0[i] = new Vector2(r.Uv0[i * 2], r.Uv0[i * 2 + 1]);
            }
            var mesh = new Mesh { name = r.Name, indexFormat = n > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.SetVertices(pos);
            mesh.SetNormals(nrm);
            mesh.SetUVs(0, uv0);
            if (r.Uv1 != null)
            {
                var uv1 = new Vector2[n];
                for (int i = 0; i < n; i++) uv1[i] = new Vector2(r.Uv1[i * 2], r.Uv1[i * 2 + 1]);
                mesh.SetUVs(1, uv1);
            }
            if (r.Colors != null)
            {
                var c = new Color32[n];
                for (int i = 0; i < n; i++) c[i] = new Color32(r.Colors[i * 4], r.Colors[i * 4 + 1], r.Colors[i * 4 + 2], r.Colors[i * 4 + 3]);
                mesh.SetColors(c);
            }
            mesh.subMeshCount = r.Submeshes.Count;
            for (int s = 0; s < r.Submeshes.Count; s++)
            {
                var src = r.Submeshes[s].Indices;
                var idx = new int[src.Length];
                for (int t = 0; t < src.Length; t += 3)      // the axis change is a reflection: flip winding
                {
                    idx[t] = (int)src[t];
                    idx[t + 1] = (int)src[t + 2];
                    idx[t + 2] = (int)src[t + 1];
                }
                mesh.SetTriangles(idx, s, false);
            }
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return mesh;
        }

        // ------------------------------------------------------------------------------------------ prefab
        /// <summary><paramref name="meshes"/> collide; <paramref name="render"/> (the same, or their low poly copies) are drawn.</summary>
        private static GameObject BuildPrefab(MapManifest manifest, List<RawMesh> raw, Mesh[] meshes, Mesh[] render, Material[] materials, string dst, string mapName)
        {
            var root = new GameObject(mapName);
            string meshPath = $"{dst}/{mapName}_Meshes.asset";
            var solidParts = new Dictionary<(int, ulong), Mesh>();
            try
            {
                var groups = new Dictionary<string, Transform>();
                for (int i = 0; i < manifest.objects.Count; i++)
                {
                    if (i % 200 == 0) EditorUtility.DisplayProgressBar($"Building {mapName}", $"Placing objects ({i}/{manifest.objects.Count})", 0.8f + 0.2f * i / manifest.objects.Count);
                    var o = manifest.objects[i];
                    if (o.mesh < 0 || o.mesh >= meshes.Length) continue;
                    string group = string.IsNullOrEmpty(o.collection) ? "Ungrouped" : o.collection;
                    bool vista = group.IndexOf("Vista", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!groups.TryGetValue(group, out var parent))
                    {
                        parent = new GameObject(group).transform;
                        parent.SetParent(root.transform, false);
                        groups[group] = parent;
                    }

                    var go = new GameObject(o.name);
                    go.transform.SetParent(parent, false);
                    ApplyMatrix(go.transform, MapSpace.Matrix(o.matrix));
                    go.AddComponent<MeshFilter>().sharedMesh = render[o.mesh];
                    var mr = go.AddComponent<MeshRenderer>();
                    var subs = raw[o.mesh].Submeshes;
                    var mats = new Material[subs.Count];
                    ulong solid = 0;        // bit s: submesh s is opaque
                    for (int s = 0; s < subs.Count; s++)
                    {
                        int slot = subs[s].Slot;
                        int mi = o.materials != null && o.materials.Length > 0 ? o.materials[Mathf.Clamp(slot, 0, o.materials.Length - 1)] : -1;
                        mats[s] = mi >= 0 && mi < materials.Length ? materials[mi] : null;
                        bool transparent = mats[s] != null && mats[s].GetFloat("_Surface") > 0.5f;
                        if (!transparent && s < 64) solid |= 1UL << s;
                    }
                    mr.sharedMaterials = mats;

                    var flags = StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic;
                    if (vista) mr.shadowCastingMode = ShadowCastingMode.Off;   // far scenery: no shadows, no collision
                    else
                    {
                        flags |= StaticEditorFlags.NavigationStatic;
                        // glass and water are seen and shot through, so only the opaque parts collide: a window frame
                        // or a floor with a puddle decal still blocks sight, bullets and movement
                        var collision = CollisionMesh(o.mesh, meshes[o.mesh], subs.Count, solid, solidParts, meshPath);
                        if (collision != null) go.AddComponent<MeshCollider>().sharedMesh = collision;
                    }
                    GameObjectUtility.SetStaticEditorFlags(go, flags);
                }
                if (solidParts.Count > 0) AssetDatabase.SaveAssets();
                string path = $"{dst}/{mapName}.prefab";
                return PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        /// <summary>The mesh an object collides with: the whole mesh when every part is opaque, none when no part is,
        /// and otherwise a copy holding only the opaque parts (made once per mesh and set of parts, saved with the
        /// map's meshes).</summary>
        private static Mesh CollisionMesh(int index, Mesh mesh, int submeshes, ulong solid, Dictionary<(int, ulong), Mesh> cache, string assetPath)
        {
            ulong all = submeshes >= 64 ? ulong.MaxValue : (1UL << submeshes) - 1;
            if (solid == 0) return null;
            if (solid == all || submeshes > 64) return mesh;
            if (cache.TryGetValue((index, solid), out var part)) return part;

            var triangles = new List<int>();
            for (int s = 0; s < submeshes && s < 64; s++)
                if ((solid & (1UL << s)) != 0) triangles.AddRange(mesh.GetTriangles(s));
            part = new Mesh { name = mesh.name + "_Solid", indexFormat = mesh.indexFormat };
            part.SetVertices(mesh.vertices);
            part.SetTriangles(triangles, 0, true);
            AssetDatabase.AddObjectToAsset(part, assetPath);
            cache[(index, solid)] = part;
            return part;
        }

        /// <summary>Row-major Unity-space matrix -> local TRS (negative determinant becomes a negative X scale).</summary>
        private static void ApplyMatrix(Transform t, float[] u)
        {
            var c0 = new Vector3(u[0], u[4], u[8]);
            var c1 = new Vector3(u[1], u[5], u[9]);
            var c2 = new Vector3(u[2], u[6], u[10]);
            float det = Vector3.Dot(Vector3.Cross(c0, c1), c2);
            var scale = new Vector3(c0.magnitude * (det < 0 ? -1f : 1f), c1.magnitude, c2.magnitude);
            if (Mathf.Abs(scale.x) < 1e-8f || scale.y < 1e-8f || scale.z < 1e-8f) { t.localScale = Vector3.zero; return; }
            var fwd = c2 / scale.z;
            var up = c1 / scale.y;
            t.localPosition = new Vector3(u[3], u[7], u[11]);
            t.localRotation = Quaternion.LookRotation(fwd, up);
            t.localScale = scale;
        }

        // ------------------------------------------------------------------------------------------ registry
        private static void Register(string mapName, string levelPath, GameObject prefab)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RegistryPath));
            var reg = AssetDatabase.LoadAssetAtPath<MapRegistry>(RegistryPath);
            if (reg == null)
            {
                reg = ScriptableObject.CreateInstance<MapRegistry>();
                AssetDatabase.CreateAsset(reg, RegistryPath);
            }
            reg.maps.RemoveAll(e => e.displayName == mapName || e.prefab == null);
            reg.maps.Add(new MapRegistry.Entry { displayName = mapName, levelPath = levelPath ?? "", prefab = prefab });
            reg.maps.Sort((a, b) => string.CompareOrdinal(a.displayName, b.displayName));
            EditorUtility.SetDirty(reg);
            AssetDatabase.SaveAssets();
        }
    }

    /// <summary>Import settings for map textures: normal maps (DirectX green), linear mask maps, capped size.</summary>
    internal sealed class MapTexturePostprocessor : AssetPostprocessor
    {
        private void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(MapBuilder.AssetRoot + "/", StringComparison.Ordinal) || !assetPath.Contains("/Textures/")) return;
            var ti = (TextureImporter)assetImporter;
            string file = Path.GetFileName(assetPath);
            // colour textures are rewritten when the style changes, so they are reimported at the low poly size
            bool colour = !MapBuilder.NormalMaps.Contains(file) && !MapBuilder.MaskMaps.Contains(file) &&
                          !file.EndsWith("_NM.png", StringComparison.OrdinalIgnoreCase) && !file.EndsWith("_MS.png", StringComparison.OrdinalIgnoreCase);
            ti.maxTextureSize = colour && MapBuilder.LowPoly ? MapBuilder.LowPolyTextureSize : MapBuilder.TextureSize;
            ti.mipmapEnabled = true;
            ti.textureCompression = TextureImporterCompression.Compressed;
            ti.anisoLevel = 4;
            if (MapBuilder.NormalMaps.Contains(file) || file.EndsWith("_NM.png", StringComparison.OrdinalIgnoreCase))
            {
                ti.textureType = TextureImporterType.NormalMap;
                ti.flipGreenChannel = true;     // Unreal normal maps are DirectX-style
            }
            else if (MapBuilder.MaskMaps.Contains(file) || file.EndsWith("_MS.png", StringComparison.OrdinalIgnoreCase))
            {
                ti.sRGBTexture = false;
            }
        }
    }
}
