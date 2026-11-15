// SimSpace.cs - simulation space (Unity: metres, Y-up) and the geometry helpers used across the simulation.
using System;
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
}
