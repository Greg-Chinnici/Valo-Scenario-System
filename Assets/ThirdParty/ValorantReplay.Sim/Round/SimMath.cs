// SimMath.cs - small numeric helpers shared by the simulation.
using System;

namespace ValorantReplay.Sim
{
    static class SimMath
    {
        /// <summary>A standard normal draw (Box-Muller).</summary>
        public static float Gaussian(Random r)
        {
            double u1 = 1.0 - r.NextDouble(), u2 = r.NextDouble();
            return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }

        /// <summary>Signed turn from one yaw to another, -180..180 degrees.</summary>
        public static float DeltaYaw(float from, float to)
        {
            float d = (to - from) % 360f;
            if (d > 180f) d -= 360f;
            if (d < -180f) d += 360f;
            return d;
        }

        public static float[] Filled(int n, float v)
        {
            var a = new float[n];
            for (int i = 0; i < n; i++) a[i] = v;
            return a;
        }
    }
}
