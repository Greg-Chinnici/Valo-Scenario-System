// Tempo.cs - how a round is paced, with one sampler for recorded and simulated rounds so both are measured the same way:
//   - execute: the first moment ExecuteCount living attackers stand on the same site (SimMap.InSite), before a plant;
//   - plant: when the spike went down;
//   - movement: mean horizontal speed of living players per side, in BinSeconds bins of round time, before the plant.
// Everything is sampled every Interval seconds from the moment the measurement starts (the barrier drop for calibration).
// TempoModel is the learned side: when attacks go in on a map.
using System;
using System.Collections.Generic;
using System.Numerics;
using ValorantReplay.Playback;

namespace ValorantReplay.Sim
{
    public sealed class RoundTempo
    {
        public const float Interval = 0.5f;
        public const float BinSeconds = 10f;
        public const int Bins = 10;
        public const int ExecuteCount = 2;

        public float ExecuteTime = -1f;     // seconds from the start; -1 = never
        public float PlantTime = -1f;
        public readonly double[] AttackerSpeed = new double[Bins], DefenderSpeed = new double[Bins];
        public readonly int[] AttackerSamples = new int[Bins], DefenderSamples = new int[Bins];

        public float AttackerSpeedAt(int bin) => AttackerSamples[bin] > 0 ? (float)(AttackerSpeed[bin] / AttackerSamples[bin]) : float.NaN;
        public float DefenderSpeedAt(int bin) => DefenderSamples[bin] > 0 ? (float)(DefenderSpeed[bin] / DefenderSamples[bin]) : float.NaN;

        public void Add(RoundTempo o)
        {
            for (int b = 0; b < Bins; b++)
            {
                AttackerSpeed[b] += o.AttackerSpeed[b]; AttackerSamples[b] += o.AttackerSamples[b];
                DefenderSpeed[b] += o.DefenderSpeed[b]; DefenderSamples[b] += o.DefenderSamples[b];
            }
        }

        /// <summary>One sample at time (seconds from the start): positions now and Interval earlier (alive both times).</summary>
        public void Sample(float time, SimMap map, bool planted, IList<TempoPlayer> players)
        {
            if (planted)
            {
                if (PlantTime < 0f) PlantTime = time;
                return;
            }
            int bin = (int)(time / BinSeconds);
            foreach (var p in players)
            {
                if (!p.Alive || !p.WasAlive || bin >= Bins) continue;
                double speed = SimSpace.FlatDistance(p.Position, p.Previous) / Interval;
                if (speed > 8.0) continue;   // a teleport or a respawn, not movement
                if (p.Attacking) { AttackerSpeed[bin] += speed; AttackerSamples[bin]++; }
                else { DefenderSpeed[bin] += speed; DefenderSamples[bin]++; }
            }
            if (ExecuteTime >= 0f) return;
            foreach (var site in map.Sites)
            {
                int on = 0;
                foreach (var p in players) if (p.Alive && p.Attacking && map.InSite(site, p.Position)) on++;
                if (on >= ExecuteCount) { ExecuteTime = time; return; }
            }
        }

        /// <summary>The recorded round ri from replay time from until to.</summary>
        public static RoundTempo Recorded(VrpReplay r, int ri, SimMap map, float from, float to)
        {
            var rd = r.Rounds[ri];
            var tempo = new RoundTempo();
            var players = new List<TempoPlayer>();
            for (float t = from + Interval; t < to; t += Interval)
            {
                players.Clear();
                for (int p = 0; p < r.Players.Count; p++)
                {
                    if (r.Players[p].Team > 1) continue;
                    var f = r.Sample(p, t);
                    var g = r.Sample(p, t - Interval);
                    players.Add(new TempoPlayer
                    {
                        Attacking = r.Players[p].Team == rd.AttackingTeam, Alive = f.Alive, WasAlive = g.Alive,
                        Position = SimSpace.FromReplay(r, f), Previous = SimSpace.FromReplay(r, g),
                    });
                }
                tempo.Sample(t - from, map, rd.PlantTime >= 0f && rd.PlantTime <= t, players);
            }
            return tempo;
        }
    }

    /// <summary>When attacks happen on a map, learned from replays: plant times after the barrier drop. An attacking
    /// director that has one executes at a recorded plant time minus ExecuteLead (the walk in and the plant), drawn per
    /// round, instead of at a fixed time.</summary>
    public sealed class TempoModel
    {
        /// <summary>Seconds between committing to a site and the plant. Swept on the corpus (10, 15, 20 s): 15 s fits the
        /// time rounds take best; win prediction barely moves with it.</summary>
        public static float ExecuteLead = 15f;
        /// <summary>Plant times below which a model has no opinion.</summary>
        public const int MinPlants = 4;

        public readonly List<float> PlantTimes = new List<float>();

        public void AddReplay(VrpReplay r)
        {
            foreach (var rd in r.Rounds)
                if (rd.BarrierTime >= 0f && rd.AttackingTeam <= 1 && rd.PlantTime >= rd.BarrierTime) PlantTimes.Add(rd.PlantTime - rd.BarrierTime);
        }

        public static TempoModel FromReplays(IEnumerable<VrpReplay> replays)
        {
            var m = new TempoModel();
            foreach (var r in replays) m.AddReplay(r);
            return m;
        }

        public bool Known => PlantTimes.Count >= MinPlants;

        /// <summary>Seconds after the barrier drop to execute: a recorded plant time minus ExecuteLead, never before 0.</summary>
        public float DrawExecute(Random rng) => Math.Max(0f, PlantTimes[rng.Next(PlantTimes.Count)] - ExecuteLead);
    }

    public struct TempoPlayer
    {
        public bool Attacking, Alive, WasAlive;
        public Vector3 Position, Previous;
    }
}
