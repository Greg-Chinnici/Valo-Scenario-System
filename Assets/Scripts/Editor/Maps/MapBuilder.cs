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
                var prefab = BuildPrefab(manifest, raw, meshes, materials, dst, mapName);
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

            AssetDatabase.StartAssetEditing();
            try
            {
                int i = 0, total = colour.Count + normals.Count + mras.Count;
                foreach (var file in colour.Concat(normals))
                {
                    if (i++ % 20 == 0) EditorUtility.DisplayProgressBar($"Building {mapName}", $"Copying textures ({i}/{total})", 0.1f + 0.3f * i / Math.Max(1, total));
                    CopyIfChanged(Path.Combine(src, "textures", file), $"{dst}/Textures/{file}");
                }
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
            textures.MaskByMra.TryGetValue(MraFile(e) ?? "", out var mask);

            // BLENDMODE from the importer: 1 = masked (decals, papers, foliage cards). 0 and 2 both appear on opaque
            // surfaces, so translucency is taken from the material name / shader instead.
            float blend = e.ParamFloat("BLENDMODE", 2f);
            string lower = (e.name + " " + e.shader).ToLowerInvariant();
            var surface = lower.Contains("glass") || lower.Contains("opacity") || lower.Contains("water") || lower.Contains("flame")
                ? Surface.Transparent : Mathf.Approximately(blend, 1f) ? Surface.Cutout : Surface.Opaque;
            bool twoSided = e.ParamFloat("TWOSIDED", 0f) > 0.5f || surface == Surface.Cutout;

            var tint = ToColor(e.Param("Tint"), Color.white);
            var baseColor = diffuse != null ? tint : ToColor(e.Param("DiffuseColor"), Color.gray) * tint;
            if (surface == Surface.Transparent) baseColor.a = Mathf.Min(baseColor.a, 0.35f);

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
        private static GameObject BuildPrefab(MapManifest manifest, List<RawMesh> raw, Mesh[] meshes, Material[] materials, string dst, string mapName)
        {
            var root = new GameObject(mapName);
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
                    go.AddComponent<MeshFilter>().sharedMesh = meshes[o.mesh];
                    var mr = go.AddComponent<MeshRenderer>();
                    var subs = raw[o.mesh].Submeshes;
                    var mats = new Material[subs.Count];
                    bool anyTransparent = false;
                    for (int s = 0; s < subs.Count; s++)
                    {
                        int slot = subs[s].Slot;
                        int mi = o.materials != null && o.materials.Length > 0 ? o.materials[Mathf.Clamp(slot, 0, o.materials.Length - 1)] : -1;
                        mats[s] = mi >= 0 && mi < materials.Length ? materials[mi] : null;
                        if (mats[s] != null && mats[s].GetFloat("_Surface") > 0.5f) anyTransparent = true;
                    }
                    mr.sharedMaterials = mats;

                    var flags = StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic;
                    if (vista) mr.shadowCastingMode = ShadowCastingMode.Off;   // far scenery: no shadows, no collision
                    else
                    {
                        flags |= StaticEditorFlags.NavigationStatic;
                        if (!anyTransparent) go.AddComponent<MeshCollider>().sharedMesh = meshes[o.mesh];
                    }
                    GameObjectUtility.SetStaticEditorFlags(go, flags);
                }
                string path = $"{dst}/{mapName}.prefab";
                return PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
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
            ti.maxTextureSize = 1024;
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
