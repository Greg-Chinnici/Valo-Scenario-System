// Measurements.cs - a player's shooting measured from a recording, with the same definitions RoundSim counts its own
// agents by, so recorded and simulated numbers compare directly:
//   - accuracy: hits on enemies per bullet, for guns whose bullets are single projectiles the simulation models
//     (no shotguns, no ability guns);
//   - hit regions: head and leg share of those hits (every hit, not only the killing blow);
//   - notices: an enemy comes into view (line of sight on the SimMap, inside the field of view, within sight range)
//     after at least NoticeGap out of it, checked every PerceptionInterval like the simulation's perception;
//   - reaction: from a notice to the first shot at that enemy (aimed within AimedShotAngle of it), if that is the
//     player's next shot and comes within ReactionWindow;
//   - aim error: horizontal angle between the view and the enemy at the notice (crosshair placement).
using System;
using System.Collections.Generic;
using System.Numerics;
using ValorantReplay.Playback;

namespace ValorantReplay.Sim
{
    public static class Measurements
    {
        public const float NoticeGap = 1f;
        public const float ReactionWindow = 1.5f;
        public const float AimedShotAngle = 10f;

        /// <summary>Guns whose bullets count for accuracy: shotguns (pellets) and ability guns (Neon's and Chamber's ults,
        /// Chamber's Headhunter) are left out on both sides.</summary>
        public static bool CountsForAccuracy(string weaponClass)
        {
            if (string.IsNullOrEmpty(weaponClass)) return false;
            return weaponClass.IndexOf("Shotgun", StringComparison.OrdinalIgnoreCase) < 0 &&
                   weaponClass.IndexOf("Gun_", StringComparison.Ordinal) < 0;
        }

        /// <summary>Adds the shooting of every player between from and to (replay seconds) to into, indexed by player.
        /// Bullets, hits and notices are counted where they start in [from, to); a reaction may land up to ReactionWindow
        /// later.</summary>
        public static void AddShooting(VrpReplay r, SimMap map, float from, float to, IDictionary<int, PlayerTally> into, SimOptions options = null)
        {
            options = options ?? new SimOptions();
            int n = r.Players.Count;
            var guns = new HashSet<string>();
            var shots = new List<Shot>();
            foreach (var s in r.Shots)
            {
                if (s.Time >= to + ReactionWindow) break;
                if (s.Time < from || s.Player >= n) continue;
                shots.Add(s);
                if (CountsForAccuracy(s.Weapon)) guns.Add(s.Weapon);
                if (s.Time < to && CountsForAccuracy(s.Weapon)) Metrics.Get(into, s.Player).Bullets += Math.Max(1, (int)s.Projectiles);
            }
            foreach (var d in r.Damage)
            {
                if (d.Time < from || d.Time >= to || d.Attacker >= n || d.Victim >= n || !guns.Contains(d.Weapon)) continue;
                if (r.Players[d.Attacker].Team == r.Players[d.Victim].Team) continue;
                var t = Metrics.Get(into, d.Attacker);
                t.Hits++;
                if (d.Region == HitRegion.Head) t.HeadHits++;
                else if (d.Region == HitRegion.Legs) t.LegHits++;
            }
            AddNotices(r, map, from, to, shots, into, options);
        }

        static void AddNotices(VrpReplay r, SimMap map, float from, float to, List<Shot> shots, IDictionary<int, PlayerTally> into, SimOptions options)
        {
            int n = r.Players.Count;
            var seen = new bool[n, n];
            var lastSeen = new float[n, n];
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) lastSeen[i, j] = float.NegativeInfinity;
            var frames = new Frame[n];
            var eyes = new Vector3[n];
            float cosFov = (float)Math.Cos(options.FovHalfAngle * Math.PI / 180.0);
            float step = options.PerceptionInterval;
            for (float t = from; t < to; t += step)
            {
                for (int p = 0; p < n; p++)
                {
                    frames[p] = r.Sample(p, t);
                    eyes[p] = SimSpace.FromReplay(r, frames[p]) + new Vector3(0f, frames[p].Crouching ? 0.35f : 0.65f, 0f);
                }
                for (int a = 0; a < n; a++)
                    for (int b = 0; b < n; b++)
                    {
                        if (a == b || r.Players[a].Team == r.Players[b].Team || r.Players[a].Team > 1 || r.Players[b].Team > 1) continue;
                        bool now = false;
                        float yawTo = 0f;
                        if (frames[a].Alive && frames[b].Alive)
                        {
                            var to3 = eyes[b] - eyes[a];
                            float d = to3.Length();
                            var flat = new Vector3(to3.X, 0f, to3.Z);
                            float len = flat.Length();
                            yawTo = SimSpace.YawOf(flat);
                            bool fov = len < 3f || Vector3.Dot(SimSpace.Forward(frames[a].Yaw), flat / len) >= cosFov;
                            now = d <= options.MaxSightRange && fov && map.Grid.LineOfSight(eyes[a], eyes[b]);
                        }
                        if (now && !seen[a, b] && t - lastSeen[a, b] >= NoticeGap)
                        {
                            var tally = Metrics.Get(into, a);
                            tally.Notices++;
                            tally.AimErrorSum += Math.Abs(DeltaYaw(frames[a].Yaw, yawTo));
                            float reaction = ReactionTo(r, shots, a, b, t);
                            if (reaction >= 0f) { tally.Reactions++; tally.ReactionSum += reaction; }
                        }
                        if (now) lastSeen[a, b] = t;
                        seen[a, b] = now;
                    }
            }
        }

        /// <summary>Seconds from t to a's next shot if it is aimed at b and within ReactionWindow, else -1.</summary>
        static float ReactionTo(VrpReplay r, List<Shot> shots, int a, int b, float t)
        {
            foreach (var s in shots)
            {
                if (s.Player != a || s.Time <= t) continue;
                if (s.Time > t + ReactionWindow || s.Directions.Count == 0) return -1f;
                var dir = DirectionFromReplay(r, s.Directions[0]);
                var target = SimSpace.FromReplay(r, r.Sample(b, s.Time));
                var toTarget = target + new Vector3(0f, 0.65f, 0f) - SimSpace.FromReplay(r, s.X, s.Y, s.Z);
                if (toTarget.LengthSquared() < 1e-6f || dir.LengthSquared() < 1e-6f) return -1f;
                float cos = Vector3.Dot(Vector3.Normalize(dir), Vector3.Normalize(toTarget));
                return cos >= (float)Math.Cos(AimedShotAngle * Math.PI / 180.0) ? s.Time - t : -1f;
            }
            return -1f;
        }

        /// <summary>A direction in the replay's space (Shot.Directions) in simulation (Unity) space.</summary>
        public static Vector3 DirectionFromReplay(VrpReplay r, (float X, float Y, float Z) d) =>
            r.Space == CoordinateSpace.Unity ? new Vector3(d.X, d.Y, d.Z) : new Vector3(d.Y, d.Z, d.X);

        /// <summary>Signed degrees from yaw a to yaw b, in (-180, 180].</summary>
        public static float DeltaYaw(float a, float b)
        {
            float d = (b - a) % 360f;
            if (d > 180f) d -= 360f;
            else if (d <= -180f) d += 360f;
            return d;
        }
    }
}
