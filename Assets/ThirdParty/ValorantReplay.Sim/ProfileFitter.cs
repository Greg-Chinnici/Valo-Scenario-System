// ProfileFitter.cs - a Profile for each player of a replay, from what they did in it: how often they took the opening
// duel (aggression), how close they stayed to teammates and how often their deaths were traded (cohesion), headshot
// rate and K/D (skill), agent role and spacing (play style). Each knob is scored against measured averages, so 0.5 is
// a typical player; the raw stats are kept so the population can be re-measured as more replays come in.
// Execution is also measured directly (Measurements: accuracy, hit regions, reaction, aim error) and converted to simulation
// parameters with transfer constants found by simulation (vrpsim tune), so a fitted agent shoots like the player did.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ValorantReplay.Playback;

namespace ValorantReplay.Sim
{
    public sealed class FittedProfile
    {
        public int Player;
        public string Name = "", Agent = "", Role = "";
        public Profile Profile;
        /// <summary>Kill stats over whole rounds, shooting stats (Measurements) from the barrier drop to the decision.</summary>
        public PlayerTally Tally = new PlayerTally();
        public float HeadshotRate;
        public float KillDeath;
        /// <summary>Average distance (m) to the nearest living teammate during combat, before the plant.</summary>
        public float TeammateDistance;
    }

    /// <summary>Everything ProfileFitter measures from a replay, per round, so fits over any set of rounds (e.g. all but
    /// one) only add them up.</summary>
    public sealed class ReplayMeasures
    {
        public VrpReplay Replay;
        public RoundMeasures[] Rounds;
    }

    public sealed class RoundMeasures
    {
        public readonly Dictionary<int, PlayerTally> Tallies = new Dictionary<int, PlayerTally>();
        public int[] KillHeadshots;
        public double[] DistSum;
        public int[] DistCount;
    }

    public static class ProfileFitter
    {
        /// <summary>One entry per player, indexed like replay.Players. includeRound (0-based round index) limits the fit to
        /// some rounds, e.g. all but the one being predicted, so a player's result in that round does not leak into the
        /// profile that predicts it. map gives line of sight for the reaction measurements (default: built from this
        /// replay alone), options the perception they use (match the simulation's), transfer how measured execution
        /// becomes parameters (default: ProfileFitter.Transfer; use the one tuned for the perception).</summary>
        public static FittedProfile[] Fit(VrpReplay replay, Func<int, bool> includeRound = null, SimMap map = null,
            SimOptions options = null, ExecutionTransfer transfer = null) =>
            Fit(Measure(replay, map, options), includeRound, transfer);

        /// <summary>Measures every round of a replay once (the expensive part: line of sight over the whole match), with
        /// the perception of options (field of view, sight range).</summary>
        public static ReplayMeasures Measure(VrpReplay replay, SimMap map = null, SimOptions options = null)
        {
            map = map ?? SimMap.FromReplays(new[] { replay });
            int n = replay.Players.Count;
            var m = new ReplayMeasures { Replay = replay, Rounds = new RoundMeasures[replay.Rounds.Count] };
            for (int ri = 0; ri < replay.Rounds.Count; ri++)
            {
                var round = replay.Rounds[ri];
                var rm = m.Rounds[ri] = new RoundMeasures { KillHeadshots = new int[n], DistSum = new double[n], DistCount = new int[n] };
                float start = round.BarrierTime >= 0f ? round.BarrierTime : round.StartTime;
                var kills = new List<KillRecord>();
                foreach (var k in replay.Kills)
                {
                    if (k.Time < round.StartTime || k.Time >= round.EndTime || k.Victim >= n) continue;
                    bool player = k.Killer < n;
                    kills.Add(new KillRecord
                    {
                        Time = k.Time, Killer = player ? k.Killer : -1, Victim = k.Victim,
                        KillerTeam = player ? replay.Players[k.Killer].Team : -1, VictimTeam = replay.Players[k.Victim].Team,
                    });
                    if (player && k.Headshot && k.Killer != k.Victim) rm.KillHeadshots[k.Killer]++;
                }
                var players = new List<int>();
                for (int p = 0; p < n; p++) if (replay.Sample(p, start).Alive) players.Add(p);
                Metrics.AddRound(kills, players, rm.Tallies);

                if (round.BarrierTime < 0f) continue;
                Measurements.AddShooting(replay, map, round.BarrierTime, Calibration.DecidedAt(replay, ri), rm.Tallies, options);
                float end = round.PlantTime >= 0f ? round.PlantTime : round.EndTime;
                for (float t = round.BarrierTime + 5f; t < end; t += 1f)
                    for (int p = 0; p < n; p++)
                    {
                        var f = replay.Sample(p, t);
                        if (!f.Alive) continue;
                        var pos = SimSpace.FromReplay(replay, f);
                        float best = float.MaxValue;
                        for (int q = 0; q < n; q++)
                        {
                            if (q == p || replay.Players[q].Team != replay.Players[p].Team) continue;
                            var g = replay.Sample(q, t);
                            if (g.Alive) best = Math.Min(best, SimSpace.FlatDistance(pos, SimSpace.FromReplay(replay, g)));
                        }
                        if (best < float.MaxValue) { rm.DistSum[p] += best; rm.DistCount[p]++; }
                    }
            }
            return m;
        }

        public static FittedProfile[] Fit(ReplayMeasures m, Func<int, bool> includeRound = null, ExecutionTransfer transfer = null)
        {
            var replay = m.Replay;
            int n = replay.Players.Count;
            var tallies = new PlayerTally[n];
            var headshots = new int[n];
            var distSum = new double[n];
            var distCount = new int[n];
            for (int p = 0; p < n; p++) tallies[p] = new PlayerTally();
            for (int ri = 0; ri < m.Rounds.Length; ri++)
            {
                if (includeRound != null && !includeRound(ri)) continue;
                var rm = m.Rounds[ri];
                foreach (var kv in rm.Tallies) if (kv.Key < n) tallies[kv.Key].Add(kv.Value);
                for (int p = 0; p < n; p++)
                {
                    headshots[p] += rm.KillHeadshots[p];
                    distSum[p] += rm.DistSum[p];
                    distCount[p] += rm.DistCount[p];
                }
            }

            var result = new FittedProfile[n];
            for (int p = 0; p < n; p++)
            {
                var pl = replay.Players[p];
                var tally = tallies[p];
                var fp = new FittedProfile
                {
                    Player = p, Name = pl.DisplayName, Agent = pl.AgentName ?? "", Role = pl.AgentRole ?? "", Tally = tally,
                    HeadshotRate = tally.Kills > 0 ? headshots[p] / (float)tally.Kills : HeadshotMean,
                    KillDeath = tally.Deaths > 0 ? tally.Kills / (float)tally.Deaths : tally.Kills,
                    TeammateDistance = distCount[p] > 0 ? (float)(distSum[p] / distCount[p]) : TeammateMean,
                };
                fp.Profile = ToProfile(fp, transfer);
                result[p] = fp;
            }
            return result;
        }

        // Population the knobs are scored against: 170 player-matches from 17 replays (patches 13.06-13.09, mixed
        // queues, Nov 2026). Mean and standard deviation of each stat; replays mark only the killing blow as a headshot,
        // hence the high headshot rate. Re-measure as the replay collection grows.
        public static float HeadshotMean = 0.54f, HeadshotSd = 0.23f;
        public static float KillDeathMean = 1.18f, KillDeathSd = 0.8f;     // K/D clipped at 4
        public static float OpeningMean = 0.20f, OpeningSd = 0.17f;
        public static float TradedMean = 0.12f, TradedSd = 0.15f;
        public static float TeammateMean = 18.3f, TeammateSd = 6.9f;       // metres
        /// <summary>Rounds at which a player's own stats count half: fewer rounds pull the knobs towards 0.5.</summary>
        public static float ShrinkRounds = 8f;

        // Measured execution (Measurements' definitions), same corpus, from `vrpsim tune`: population values, and how much
        // of their own data a player needs before it counts as much as the population (shrinkage). Shooting is measured
        // from the barrier drop to the decision. Reaction and aim error depend on the perception (what counts as a
        // notice), so their population values live in ExecutionTransfer.
        public static float AccuracyMean = 0.194f, AccuracyPrior = 200f;    // bullets
        public static float HeadHitMean = 0.189f, LegHitMean = 0.082f, HitPrior = 40f;   // hits
        public static float ReactionPrior = 8f, AimErrorPrior = 20f;        // reactions; notices
        /// <summary>How measured execution becomes simulation parameters when Fit is given none: tuned for the
        /// simulation's default perception. Set it (or pass one) to match the SimOptions the profiles will play with.</summary>
        public static ExecutionTransfer Transfer = ExecutionTransfer.Headless;

        /// <summary>Profile.LookSpread that gives an aim error at notices of aimError, with Transfer.</summary>
        public static float PlacementFor(float aimError) => Transfer.PlacementFor(aimError);

        /// <summary>Stats -> knobs: each knob is 0.5 + 0.2 standard deviations from the population above (so +-2.5 sd spans
        /// 0..1), shrunk towards 0.5 for players with few rounds. Execution: measured values shrunk towards the population
        /// by their sample size, then converted with the transfer constants.</summary>
        public static Profile ToProfile(FittedProfile f, ExecutionTransfer transfer = null)
        {
            var x = transfer ?? Transfer;
            float trust = f.Tally.Rounds / (f.Tally.Rounds + ShrinkRounds);
            float Z(float v, float mean, float sd) => (v - mean) / sd;
            float Knob(float z) => 0.5f + 0.2f * z * trust;
            float skill = Knob(0.5f * Z(f.HeadshotRate, HeadshotMean, HeadshotSd) + 0.5f * Z(Math.Min(f.KillDeath, 4f), KillDeathMean, KillDeathSd));
            float aggression = Knob(Z(f.Tally.OpeningRate, OpeningMean, OpeningSd));
            float cohesion = Knob(-0.7f * Z(f.TeammateDistance, TeammateMean, TeammateSd) + 0.3f * Z(f.Tally.TradedRate, TradedMean, TradedSd));
            PlayStyle style;
            if (f.TeammateDistance > TeammateMean + TeammateSd) style = PlayStyle.Lurker;
            else if (f.Role == "Duelist") style = PlayStyle.Entry;
            else if (f.Role == "Sentinel") style = PlayStyle.Anchor;
            else if (f.Role == "Initiator" || f.Role == "Controller") style = PlayStyle.Support;
            else style = PlayStyle.Balanced;
            var t = f.Tally;
            return new Profile(skill, aggression, cohesion, style)
            {
                Accuracy = Clamp(Shrink(t.Hits, t.Bullets, AccuracyMean, AccuracyPrior) / x.Accuracy, 0.02f, 0.95f),
                Headshots = Shrink(t.HeadHits, t.Hits, HeadHitMean, HitPrior),
                Legshots = Shrink(t.LegHits, t.Hits, LegHitMean, HitPrior),
                Reaction = Clamp(Shrink((float)t.ReactionSum, t.Reactions, x.ReactionMean, ReactionPrior) - x.ReactionOffset, 0.08f, 1.2f),
                Placement = x.PlacementFor(Shrink((float)t.AimErrorSum, t.Notices, x.AimErrorMean, AimErrorPrior)),
            };
        }

        /// <summary>sum / count pulled towards mean as if prior more samples at the mean had been seen.</summary>
        public static float Shrink(float sum, float count, float mean, float prior) => (sum + prior * mean) / (count + prior);

        static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    }

    /// <summary>How measured execution turns into simulation parameters under one perception setup. A measured accuracy
    /// is not BaseAccuracy (distance and movement lower what an agent lands), a measured reaction includes turning, and
    /// aim error depends on where agents look; `vrpsim tune` finds these by simulating the corpus until simulated and
    /// recorded population values agree. Re-tune after changing the gunfight, how agents look, or the perception.</summary>
    public sealed class ExecutionTransfer
    {
        /// <summary>Population reaction (seconds) and aim error at a notice (degrees) measured with this perception: what a
        /// player with few notices is shrunk towards.</summary>
        public float ReactionMean, AimErrorMean;
        /// <summary>Simulated accuracy per unit of BaseAccuracy.</summary>
        public float Accuracy;
        /// <summary>Simulated reaction minus ReactionTime, seconds (turning, stopping, waiting for the next shot).</summary>
        public float ReactionOffset;
        /// <summary>Aim error at a notice with LookSpread 0 (Map.Look and watching believed threats), and per degree of
        /// LookSpread (a line across players).</summary>
        public float AimErrorBase, AimErrorSlope;

        /// <summary>Profile.LookSpread that gives an aim error at notices of aimError (0 at or below AimErrorBase).</summary>
        public float PlacementFor(float aimError) => Math.Max(0f, (aimError - AimErrorBase) / AimErrorSlope);

        public ExecutionTransfer Clone() => (ExecutionTransfer)MemberwiseClone();

        public override string ToString() => $"accuracy {Accuracy:0.000}, reaction offset {ReactionOffset:0.000} s, aim error {AimErrorBase:0.0} + {AimErrorSlope:0.00} per degree of spread " +
            $"(population reaction {ReactionMean:0.000} s, aim error {AimErrorMean:0.0} deg)";

        /// <summary>SimOptions' default perception (field of view 104°, sight 80 m, footsteps 25 m, gunshots 60 m) and the
        /// NavGrid's line of sight. 17 replays, learned behaviour held out, converged in 3 steps.</summary>
        public static ExecutionTransfer Headless => new ExecutionTransfer
        {
            ReactionMean = 0.465f, AimErrorMean = 12.34f, Accuracy = 0.723f, ReactionOffset = 0.074f, AimErrorBase = 9.0f, AimErrorSlope = 0.4f,
        };

        /// <summary>ValoReplay round play's perception (field of view 103°, sight 120 m, footsteps 40 m, gunshots 100 m:
        /// its KnowledgeSettings) with the NavGrid's line of sight; Unity raycasts the real map instead, which this does not
        /// capture. `vrpsim tune --perception valoreplay`, 17 replays, converged in 3 steps: close to Headless (the
        /// longer ranges mostly add far notices that rarely end in a shot).</summary>
        public static ExecutionTransfer ValoReplay => new ExecutionTransfer
        {
            ReactionMean = 0.466f, AimErrorMean = 12.29f, Accuracy = 0.724f, ReactionOffset = 0.080f, AimErrorBase = 7.7f, AimErrorSlope = 0.46f,
        };
    }

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
