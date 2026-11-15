// SimBatch.cs - running many simulated rounds (in parallel, deterministically) and tallying them (BatchResult).
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ValorantReplay.Playback;

namespace ValorantReplay.Sim
{
    public sealed class BatchResult
    {
        public int Rounds;
        public int AttackerWins;
        public int Planted;
        public readonly Dictionary<RoundEndReason, int> Reasons = new Dictionary<RoundEndReason, int>();
        public readonly Dictionary<int, PlayerTally> Players = new Dictionary<int, PlayerTally>();
        double durationSum, divergedSum;

        public float MeanDuration => Rounds > 0 ? (float)(durationSum / Rounds) : 0f;
        public float MeanDiverged => Rounds > 0 ? (float)(divergedSum / Rounds) : 0f;
        public float AttackerWinRate => Rounds > 0 ? AttackerWins / (float)Rounds : 0f;
        /// <summary>Half-width of the 95% (Wilson) confidence interval of AttackerWinRate: the Monte Carlo error from
        /// running a finite number of rounds, not how right the simulation is.</summary>
        public float AttackerWinHalfWidth => WilsonHalfWidth(AttackerWins, Rounds);

        public void Add(RoundResult r)
        {
            Rounds++;
            if (r.AttackersWon) AttackerWins++;
            if (r.Planted) Planted++;
            Reasons.TryGetValue(r.Reason, out int c);
            Reasons[r.Reason] = c + 1;
            durationSum += r.Duration;
            divergedSum += r.Diverged;
            Metrics.AddRound(r.Kills, r.Players, Players);
            Metrics.AddShooting(r.Shooting, Players);
        }

        public static float WilsonHalfWidth(int successes, int n, float z = 1.96f)
        {
            if (n <= 0) return 0.5f;
            double p = successes / (double)n, z2 = z * z;
            return (float)(z * Math.Sqrt(p * (1 - p) / n + z2 / (4.0 * n * n)) / (1 + z2 / n));
        }

        /// <summary>Sums the tallies of a set of agents (e.g. everyone using one preset).</summary>
        public PlayerTally Combined(IEnumerable<int> agents)
        {
            var t = new PlayerTally();
            foreach (int a in agents) if (Players.TryGetValue(a, out var p)) t.Add(p);
            return t;
        }
    }

    public static class SimBatch
    {
        /// <summary>Runs rounds simulations (create(i) builds the i-th; give each its own seed) and tallies them. threads
        /// above 1 runs them in parallel (0 = all cores); the result does not depend on it, since every run has its own
        /// seed and results are tallied in run order. create must then be safe to call from several threads.</summary>
        public static BatchResult Run(Func<int, RoundSim> create, int rounds, int threads = 1)
        {
            var b = new BatchResult();
            foreach (var r in RunAll(create, 0, rounds, threads)) b.Add(r);
            return b;
        }

        /// <summary>Runs blocks of simulations until the attacker win rate's 95% confidence interval is at most
        /// halfWidth either side (after at least minRounds), or maxRounds have run. Deterministic like Run.</summary>
        public static BatchResult RunUntil(Func<int, RoundSim> create, float halfWidth, int maxRounds, int minRounds = 100,
            int block = 50, int threads = 1)
        {
            var b = new BatchResult();
            while (b.Rounds < maxRounds && (b.Rounds < minRounds || b.AttackerWinHalfWidth > halfWidth))
                foreach (var r in RunAll(create, b.Rounds, Math.Min(block, maxRounds - b.Rounds), threads)) b.Add(r);
            return b;
        }

        /// <summary>Results of runs first .. first + count - 1, in order.</summary>
        public static RoundResult[] RunAll(Func<int, RoundSim> create, int first, int count, int threads = 1)
        {
            var results = new RoundResult[count];
            if (threads == 1 || count < 2)
                for (int i = 0; i < count; i++) results[i] = create(first + i).RunToEnd();
            else
                Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = threads <= 0 ? Environment.ProcessorCount : threads },
                    i => results[i] = create(first + i).RunToEnd());
            return results;
        }
    }
}
