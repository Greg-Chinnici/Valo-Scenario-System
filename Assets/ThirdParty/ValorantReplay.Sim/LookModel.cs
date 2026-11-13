// LookModel.cs - where players look, learned from replays: for every 3 m patch of a map, by side and by whether the
// player is moving, a histogram of view directions over combat time. Real players check the angles enemies come from
// rather than looking where they walk; agents with a LookModel do the same. A direction is drawn from the histogram
// (neighbouring patches count half) at a quantile the agent keeps between decisions, so it checks a stable angle for a
// while and different angles over a round, in proportion to how often real players checked them. Moving agents only
// consider directions within MovingCone of where they walk.
using System;
using System.Collections.Generic;
using System.Numerics;
using ValorantReplay.Playback;

namespace ValorantReplay.Sim
{
    public sealed class LookModel
    {
        public const int Bins = 16;
        public const float CellSize = 3f;
        public const float LevelHeight = 3f;
        /// <summary>Below this speed (m/s) a recorded player counts as still.</summary>
        public const float StillSpeed = 1f;
        /// <summary>Degrees either side of the walking direction a moving agent looks within.</summary>
        public const float MovingCone = 90f;
        /// <summary>Samples (weighted, neighbours count half) below which a patch has no opinion.</summary>
        public const float MinSamples = 12f;
        public const float SampleInterval = 0.25f;

        readonly Dictionary<(int, int, int, int), float[]> cells = new Dictionary<(int, int, int, int), float[]>();
        public int Samples { get; private set; }

        static int Kind(bool attacking, bool moving) => (attacking ? 2 : 0) + (moving ? 1 : 0);

        static (int, int, int) Cell(Vector3 p) =>
            ((int)Math.Floor(p.X / CellSize), (int)Math.Floor(p.Z / CellSize), (int)Math.Floor(p.Y / LevelHeight));

        public void Add(Vector3 position, float yaw, bool attacking, bool moving)
        {
            var (x, z, y) = Cell(position);
            var key = (x, z, y, Kind(attacking, moving));
            if (!cells.TryGetValue(key, out var h)) cells[key] = h = new float[Bins];
            h[Bin(yaw)]++;
            Samples++;
        }

        /// <summary>Adds the view direction of every living player every SampleInterval of combat (barrier drop to round
        /// end), by side and movement.</summary>
        public void AddReplay(VrpReplay r)
        {
            foreach (var round in r.Rounds)
            {
                if (round.BarrierTime < 0f || round.AttackingTeam > 1) continue;
                for (int p = 0; p < r.Players.Count; p++)
                {
                    int team = r.Players[p].Team;
                    if (team > 1) continue;
                    for (float t = round.BarrierTime; t < round.EndTime; t += SampleInterval)
                    {
                        var f = r.Sample(p, t);
                        if (!f.Alive || (f.Flags & FrameFlags.OffMap) != 0) continue;
                        var g = r.Sample(p, t + SampleInterval);
                        var pos = SimSpace.FromReplay(r, f);
                        bool moving = g.Alive && SimSpace.FlatDistance(pos, SimSpace.FromReplay(r, g)) / SampleInterval >= StillSpeed;
                        Add(pos, f.Yaw, team == round.AttackingTeam, moving);
                    }
                }
            }
        }

        public static LookModel FromReplays(IEnumerable<VrpReplay> replays)
        {
            var m = new LookModel();
            foreach (var r in replays) m.AddReplay(r);
            return m;
        }

        /// <summary>The direction at quantile u (0..1) of where players looked here. heading (a walking direction) limits
        /// it to MovingCone either side. False when the patch has too few samples.</summary>
        public bool TryLook(Vector3 position, bool attacking, Vector3? heading, float u, out float yaw)
        {
            yaw = 0f;
            var h = new float[Bins];
            var (x, z, y) = Cell(position);
            int kind = Kind(attacking, heading.HasValue);
            float total = 0f;
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!cells.TryGetValue((x + dx, z + dz, y, kind), out var c)) continue;
                    float w = dx == 0 && dz == 0 ? 1f : 0.5f;
                    for (int b = 0; b < Bins; b++) { h[b] += c[b] * w; total += c[b] * w; }
                }
            if (total < MinSamples) return false;

            // bins in angle order, starting opposite the heading (so the allowed cone is contiguous) or at bin 0
            int first = 0;
            if (heading.HasValue)
            {
                float hy = SimSpace.YawOf(heading.Value);
                first = Bin(hy + 180f);
                for (int b = 0; b < Bins; b++)
                    if (Math.Abs(Measurements.DeltaYaw(hy, Centre(b))) > MovingCone) h[b] = 0f;
                total = 0f;
                foreach (float v in h) total += v;
                if (total < MinSamples * 0.5f) return false;
            }
            float target = Math.Max(0f, Math.Min(0.9999f, u)) * total, run = 0f;
            int pick = first;
            for (int i = 0; i < Bins; i++)
            {
                int b = (first + i) % Bins;
                run += h[b];
                if (run > target) { pick = b; break; }
            }
            // refine within the picked bin and its neighbours (weighted circular mean)
            double sx = 0, sz = 0;
            for (int k = -1; k <= 1; k++)
            {
                int b = (pick + k + Bins) % Bins;
                double r = Centre(b) * Math.PI / 180.0;
                sx += h[b] * Math.Sin(r);
                sz += h[b] * Math.Cos(r);
            }
            yaw = (float)(Math.Atan2(sx, sz) * 180.0 / Math.PI);
            return true;
        }

        public static int Bin(float yaw)
        {
            float y = (yaw % 360f + 360f) % 360f;
            return Math.Min(Bins - 1, (int)(y / (360f / Bins)));
        }

        public static float Centre(int bin) => (bin + 0.5f) * (360f / Bins);
    }
}
