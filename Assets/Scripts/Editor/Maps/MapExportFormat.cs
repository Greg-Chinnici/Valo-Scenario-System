using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ValoReplay.EditorTools.Maps
{
    // Data written by Tools/MapExport/export_map.py (map.json + meshes.bin). No UnityEngine types here, so the
    // format and the axis conversion can be checked outside Unity.

    [Serializable]
    public sealed class MapManifest
    {
        public string source;
        public string space;
        public List<MaterialEntry> materials = new List<MaterialEntry>();
        public List<string> meshes = new List<string>();
        public List<ObjectEntry> objects = new List<ObjectEntry>();
    }

    [Serializable]
    public sealed class MaterialEntry
    {
        public string name;
        public string shader;
        public List<TextureEntry> textures = new List<TextureEntry>();
        public List<ParamEntry> @params = new List<ParamEntry>();

        public string Texture(params string[] inputs)
        {
            foreach (var want in inputs)
                foreach (var t in textures)
                    if (t.input.Equals(want, StringComparison.OrdinalIgnoreCase)) return t.file;
            return null;
        }

        public float[] Param(string name)
        {
            foreach (var p in @params) if (p.name == name) return p.value;
            return null;
        }

        public float ParamFloat(string name, float fallback)
        {
            var v = Param(name);
            return v != null && v.Length > 0 ? v[0] : fallback;
        }
    }

    [Serializable] public sealed class TextureEntry { public string input; public string file; }
    [Serializable] public sealed class ParamEntry { public string name; public float[] value; }

    [Serializable]
    public sealed class ObjectEntry
    {
        public string name;
        public int mesh;
        public string collection;
        public float[] matrix;     // Blender world matrix, row-major
        public int[] materials;    // material index per slot
    }

    public sealed class RawMesh
    {
        public string Name;
        public float[] Positions, Normals, Uv0, Uv1;   // Blender local space
        public byte[] Colors;
        public List<(int Slot, uint[] Indices)> Submeshes = new List<(int, uint[])>();
        public int VertexCount => Positions.Length / 3;
    }

    public static class MeshesBin
    {
        public static List<RawMesh> Read(string path)
        {
            var result = new List<RawMesh>();
            using (var r = new BinaryReader(File.OpenRead(path)))
            {
                if (Encoding.ASCII.GetString(r.ReadBytes(8)) != "VRMESH01") throw new InvalidDataException($"{path}: not a VRMESH01 file");
                int count = r.ReadInt32();
                for (int m = 0; m < count; m++)
                {
                    var mesh = new RawMesh { Name = Encoding.UTF8.GetString(r.ReadBytes(r.ReadInt32())) };
                    int n = r.ReadInt32(), flags = r.ReadInt32();
                    mesh.Positions = Floats(r, n * 3);
                    mesh.Normals = Floats(r, n * 3);
                    mesh.Uv0 = Floats(r, n * 2);
                    if ((flags & 1) != 0) mesh.Uv1 = Floats(r, n * 2);
                    if ((flags & 2) != 0) mesh.Colors = r.ReadBytes(n * 4);
                    int subs = r.ReadInt32();
                    for (int s = 0; s < subs; s++)
                    {
                        int slot = r.ReadInt32(), ic = r.ReadInt32();
                        var idx = new uint[ic];
                        Buffer.BlockCopy(r.ReadBytes(ic * 4), 0, idx, 0, ic * 4);
                        mesh.Submeshes.Add((slot, idx));
                    }
                    result.Add(mesh);
                }
            }
            return result;
        }

        private static float[] Floats(BinaryReader r, int count)
        {
            var a = new float[count];
            Buffer.BlockCopy(r.ReadBytes(count * 4), 0, a, 0, count * 4);
            return a;
        }
    }

    /// <summary>
    /// Blender (metres, Z-up, Y = -Unreal Y) to Unity/replay space (metres, Y-up, Z = Unreal X, X = Unreal Y):
    /// unity = C * blender with C = [[0,-1,0],[0,0,1],[1,0,0]]. C is a reflection (det -1), so triangle winding flips.
    /// </summary>
    public static class MapSpace
    {
        public static void Point(float bx, float by, float bz, out float x, out float y, out float z)
        {
            x = -by; y = bz; z = bx;
        }

        /// <summary>Blender world matrix (row-major 4x4) -> Unity-space matrix (row-major): C * M * C^T.</summary>
        public static float[] Matrix(float[] m)
        {
            // C maps blender axis (x, y, z) -> unity (-y, z, x); rows of C: unity x = -b.y, unity y = b.z, unity z = b.x
            int[] src = { 1, 2, 0 };        // unity row/col i reads blender row/col src[i]
            float[] sign = { -1f, 1f, 1f };
            var o = new float[16];
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++) o[i * 4 + j] = sign[i] * sign[j] * m[src[i] * 4 + src[j]];
                o[i * 4 + 3] = sign[i] * m[src[i] * 4 + 3];
            }
            o[15] = 1f;
            return o;
        }
    }
}
