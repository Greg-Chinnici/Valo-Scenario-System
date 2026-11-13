// Calibration.cs - how well the simulation predicts what really happened. Every recorded round is restarted from
// snapshots (the barrier drop and moments after it) with every player as AI, simulated many times, and the recorded
// outcome is scored against the spread of simulated ones:
//   - attacker win: Brier score and log loss, against two baselines that know nothing about the map or the players
//     (the corpus win rate, and the win rate for the same man advantage), plus a reliability table;
//   - time left to decide the round, time to the next kill, kills: where the recorded value falls among the simulated
//     ones (PIT). Over many snapshots those positions are uniform when the simulated spread is right; a mean below 0.5
//     means the simulation runs long or high, above 0.5 short or low;
//   - per player (barrier snapshots): recorded and simulated per-round and shooting stats, with Metrics' and
//     Measurements' definitions on both sides.
// The score from the corpus is the objective for tuning profiles and model constants.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ValorantReplay.Playback;

namespace ValorantReplay.Sim
{
    public enum ProfileSource
    {
        /// <summary>Fitted from the same replay without the round being predicted (no leak of the outcome).</summary>
        LeaveRoundOut,
        /// <summary>Fitted from the whole replay, including the predicted round: optimistic.</summary>
        Fitted,
        /// <summary>Profile.Default for everyone: how much the fitted profiles add.</summary>
        Default,
    }

    /// <summary>Where the behaviour learned per map (LookModel, TempoModel) comes from.</summary>
    public enum LearnedSource
    {
        /// <summary>From the other scored replays only, so the predicted replay's own behaviour does not leak in. Looks:
        /// the other replays of the map (none if there are none). Tempo: the other replays of the map, or of every map if
        /// that is too few plants.</summary>
        Holdout,
        /// <summary>The map's models from every replay, including the scored one: optimistic.</summary>
        All,
        /// <summary>No learned models: agents look where they walk or at their objective, attacks go in at a fixed time.</summary>
        None,
    }

    public sealed class CalibrationOptions
    {
        public int Runs = 100;
        public int Seed = 0;
        /// <summary>Snapshot times, in seconds after the barrier drop. A snapshot is skipped once the round is decided or
        /// a team has nobody left.</summary>
        public float[] Offsets = { 0f, 20f, 40f };
        public ProfileSource Profiles = ProfileSource.LeaveRoundOut;
        /// <summary>Use fitted profiles' measured execution (accuracy, hit regions, reaction); false = knobs only.</summary>
        public bool Measured = true;
        /// <summary>Use fitted profiles' decision knobs (aggression, cohesion, style); false = neutral ones (0.5, Balanced),
        /// to measure what fitting them adds.</summary>
        public bool FittedDecisions = true;
        /// <summary>Use fitted profiles' Skill knob (memory, hearing, decision rate, reads); false = 0.5.</summary>
        public bool FittedSense = true;
        public LearnedSource Learned = LearnedSource.Holdout;
        /// <summary>How fitted profiles' measured execution becomes parameters (null = ProfileFitter.Transfer); use the one
        /// tuned for Sim's perception.</summary>
        public ExecutionTransfer Transfer;
        /// <summary>Parallel snapshots; 0 = all cores. Results do not depend on it.</summary>
        public int Threads = 0;
        public SimOptions Sim;
    }

    /// <summary>One round restarted from one moment: the recorded outcome and the simulated distribution.</summary>
    public sealed class SnapshotResult
    {
        public string Match = "", Map = "";
        public int Round;                       // 1-based
        public float Offset;                    // seconds after the barrier drop
        public int AttackersAlive, DefendersAlive;
        public bool PlantedAtStart;

        // recorded, from the snapshot on (times relative to it)
        public bool AttackersWon;
        public bool Planted;
        public RoundEndReason Reason;
        public float Duration;                  // until the round was decided
        public float FirstKill = -1f;           // -1 = no kill before it was decided
        public int Kills;                       // enemy kills by players
        public float ExecuteTime = -1f;         // RoundTempo: ExecuteCount attackers on one site; -1 = never
        public float PlantTime = -1f;           // -1 = no plant (or planted before the snapshot)

        // simulated
        public int Runs;
        public float WinRate, PlantRate, MeanDuration, MeanKills, NoKillRate;
        public float MedianFirstKill = -1f;     // over the runs that had a kill
        public float DurationPit, FirstKillPit, KillsPit;
        public float ExecuteRate, MedianExecute = -1f, ExecutePit, PlantPit;   // never = after everything
        public float MedianPlant = -1f;         // over the runs that planted
        public readonly Dictionary<RoundEndReason, int> SimReasons = new Dictionary<RoundEndReason, int>();
        public float BaselineWin, ManAdvantageWin;  // filled by the report

        public int ManAdvantage => AttackersAlive - DefendersAlive;
        public float Outcome => AttackersWon ? 1f : 0f;
    }

    /// <summary>Pacing over the barrier snapshots, recorded and simulated (summed over runs).</summary>
    public sealed class TempoComparison
    {
        public readonly RoundTempo Recorded = new RoundTempo(), Simulated = new RoundTempo();
    }

    /// <summary>One player of one match: recorded and simulated per-round stats over the barrier snapshots.</summary>
    public sealed class PlayerComparison
    {
        public string Match = "";
        public int Player;
        public string Name = "", Agent = "";
        public Profile Profile;
        public PlayerTally Recorded = new PlayerTally();
        public PlayerTally Simulated = new PlayerTally();   // summed over every run
    }

    public sealed class CalibrationReport
    {
        public CalibrationOptions Options;
        public int Replays, Rounds;
        public long SimulatedRounds;
        public readonly List<SnapshotResult> Snapshots = new List<SnapshotResult>();
        public readonly List<PlayerComparison> Players = new List<PlayerComparison>();
        public readonly TempoComparison Tempo = new TempoComparison();

        public static float Brier(IEnumerable<SnapshotResult> s, Func<SnapshotResult, float> predict) =>
            Mean(s, x => Sq(predict(x) - x.Outcome));

        /// <summary>Log loss of the win prediction, clipped to half a run from 0 and 1 (a finite batch never says
        /// certain).</summary>
        public static float LogLoss(IEnumerable<SnapshotResult> s) => Mean(s, x =>
        {
            float eps = 0.5f / Math.Max(1, x.Runs);
            float p = Math.Max(eps, Math.Min(1f - eps, x.WinRate));
            return (float)-(x.AttackersWon ? Math.Log(p) : Math.Log(1 - p));
        });

        /// <summary>1 - Brier / baseline Brier: above 0 beats the baseline, 1 is perfect.</summary>
        public static float Skill(IEnumerable<SnapshotResult> s, Func<SnapshotResult, float> baseline)
        {
            var l = s as IList<SnapshotResult> ?? s.ToList();
            float b = Brier(l, baseline);
            return b > 0f ? 1f - Brier(l, x => x.WinRate) / b : 0f;
        }

        /// <summary>Kolmogorov-Smirnov distance of PIT values from uniform: 0 = the simulated spread matches.</summary>
        public static float KsUniform(IEnumerable<float> pits)
        {
            var v = pits.OrderBy(x => x).ToArray();
            float d = 0f;
            for (int i = 0; i < v.Length; i++)
                d = Math.Max(d, Math.Max(Math.Abs((i + 1f) / v.Length - v[i]), Math.Abs(v[i] - i / (float)v.Length)));
            return d;
        }

        /// <summary>Predicted vs observed attacker win rate in bins of the prediction.</summary>
        public IEnumerable<(float Lo, float Hi, int Count, float Predicted, float Observed)> Reliability(int bins = 5)
        {
            for (int b = 0; b < bins; b++)
            {
                float lo = b / (float)bins, hi = (b + 1) / (float)bins;
                var inBin = Snapshots.Where(x => x.WinRate >= lo && (x.WinRate < hi || (b == bins - 1 && x.WinRate <= hi))).ToList();
                yield return (lo, hi, inBin.Count, Mean(inBin, x => x.WinRate), Mean(inBin, x => x.Outcome));
            }
        }

        /// <summary>Pearson correlation of recorded and simulated per-round values across players.</summary>
        public static float Correlation(IList<(float Rec, float Sim)> v)
        {
            if (v.Count < 3) return float.NaN;
            double mr = v.Average(x => x.Rec), ms = v.Average(x => x.Sim), sxy = 0, sxx = 0, syy = 0;
            foreach (var (r, s) in v) { sxy += (r - mr) * (s - ms); sxx += (r - mr) * (r - mr); syy += (s - ms) * (s - ms); }
            return sxx > 0 && syy > 0 ? (float)(sxy / Math.Sqrt(sxx * syy)) : float.NaN;
        }

        /// <summary>Fills each snapshot's baselines. Corpus: the attacker win rate over all other snapshots. Man
        /// advantage: the win rate of the other snapshots with the same alive difference (clamped to +-3) and plant state,
        /// with one pseudo-round at the corpus rate. Leaving the snapshot itself out keeps the baselines honest.</summary>
        public void FillBaselines()
        {
            int n = Snapshots.Count, wins = Snapshots.Count(x => x.AttackersWon);
            var groups = new Dictionary<(int, bool), (int Wins, int Count)>();
            foreach (var x in Snapshots)
            {
                var k = Key(x);
                groups.TryGetValue(k, out var g);
                groups[k] = (g.Wins + (x.AttackersWon ? 1 : 0), g.Count + 1);
            }
            foreach (var x in Snapshots)
            {
                int self = x.AttackersWon ? 1 : 0;
                x.BaselineWin = n > 1 ? (wins - self) / (float)(n - 1) : 0.5f;
                var g = groups[Key(x)];
                x.ManAdvantageWin = (g.Wins - self + x.BaselineWin) / g.Count;
            }
        }

        static (int, bool) Key(SnapshotResult x) => (Math.Max(-3, Math.Min(3, x.ManAdvantage)), x.PlantedAtStart);

        public static float Mean<T>(IEnumerable<T> s, Func<T, float> f)
        {
            double sum = 0;
            int n = 0;
            foreach (var x in s) { sum += f(x); n++; }
            return n > 0 ? (float)(sum / n) : float.NaN;
        }

        static float Sq(float v) => v * v;
    }

    public static class Calibration
    {
        /// <summary>Scores the simulation on a set of replays. maps gives the SimMap per level path (VrpReplay.Map), e.g.
        /// one built from more replays than are scored; missing ones are built from the scored replays of that map.
        /// progress gets (snapshots done, total).</summary>
        public static CalibrationReport Run(IList<VrpReplay> replays, CalibrationOptions options = null, Action<int, int> progress = null,
            IDictionary<string, SimMap> maps = null)
        {
            options = options ?? new CalibrationOptions();
            var sim = options.Sim ?? new SimOptions();
            var given = maps;
            maps = replays.GroupBy(r => r.Map).ToDictionary(g => g.Key,
                g => given != null && given.TryGetValue(g.Key, out var m) ? m : SimMap.FromReplays(g));
            var parallel = new ParallelOptions { MaxDegreeOfParallelism = options.Threads <= 0 ? Environment.ProcessorCount : options.Threads };
            var simMaps = new SimMap[replays.Count];
            Parallel.For(0, replays.Count, parallel, m =>
            {
                var map = maps[replays[m].Map];
                if (options.Learned == LearnedSource.None) simMaps[m] = map.WithLearned(null, null, null);
                else if (options.Learned == LearnedSource.All) simMaps[m] = map;
                else
                {
                    var others = replays.Where(o => o != replays[m] && o.Map == replays[m].Map).ToList();
                    var look = LookModel.FromReplays(others);
                    var tempo = TempoModel.FromReplays(others);
                    if (!tempo.Known) tempo = TempoModel.FromReplays(replays.Where(o => o != replays[m]));
                    var holds = HoldModel.FromReplays(others, map.Sites);
                    simMaps[m] = map.WithLearned(look.Samples > 0 ? look : null, tempo, holds.Known ? holds : null);
                }
            });

            // what to simulate
            var jobs = new List<(int Replay, int Round, int OffsetIndex, float Time)>();
            var report = new CalibrationReport { Options = options, Replays = replays.Count };
            for (int m = 0; m < replays.Count; m++)
            {
                var r = replays[m];
                for (int ri = 0; ri < r.Rounds.Count; ri++)
                {
                    var rd = r.Rounds[ri];
                    if (!Scorable(r, rd)) continue;
                    report.Rounds++;
                    float decided = DecidedAt(r, ri);
                    for (int o = 0; o < options.Offsets.Length; o++)
                    {
                        float t = rd.BarrierTime + options.Offsets[o];
                        if (t < decided - 0.5f && BothTeamsAlive(r, t)) jobs.Add((m, ri, o, t));
                    }
                }
            }

            // profiles per replay (and per round when leaving it out); every replay is measured once
            var whole = new FittedProfile[replays.Count][];
            var leaveOut = new Dictionary<(int, int), FittedProfile[]>();
            if (options.Profiles != ProfileSource.Default)
            {
                var measures = new ReplayMeasures[replays.Count];
                Parallel.For(0, replays.Count, parallel, m => measures[m] = ProfileFitter.Measure(replays[m], maps[replays[m].Map], sim));
                for (int m = 0; m < replays.Count; m++) whole[m] = ProfileFitter.Fit(measures[m], transfer: options.Transfer);
                if (options.Profiles == ProfileSource.LeaveRoundOut)
                    foreach (var (m, ri) in jobs.Select(j => (j.Replay, j.Round)).Distinct())
                        leaveOut[(m, ri)] = ProfileFitter.Fit(measures[m], x => x != ri, options.Transfer);
            }
            Profile Chosen(Profile p)
            {
                if (!options.Measured) p = p.Unmeasured();
                if (!options.FittedDecisions) p = p.With(aggression: 0.5f, cohesion: 0.5f, style: PlayStyle.Balanced);
                if (!options.FittedSense) p = p.With(skill: 0.5f);
                return p;
            }
            Profile ProfileOf(int m, int ri, int p) => options.Profiles switch
            {
                ProfileSource.LeaveRoundOut => Chosen(leaveOut[(m, ri)][p].Profile),
                ProfileSource.Fitted => Chosen(whole[m][p].Profile),
                _ => Profile.Default,
            };

            // simulate: snapshots in parallel, the runs of one snapshot in order
            var results = new SnapshotResult[jobs.Count];
            var simTallies = new Dictionary<int, PlayerTally>[jobs.Count];
            var simTempo = new RoundTempo[jobs.Count];
            int done = 0;
            Parallel.For(0, jobs.Count, parallel, j =>
            {
                var (m, ri, o, t) = jobs[j];
                var r = replays[m];
                int scenario = (m * 1000 + ri) * 16 + o;
                var x = Recorded(r, ri, t, maps[r.Map]);
                x.Offset = options.Offsets[o];
                x.Runs = options.Runs;
                var durations = new float[options.Runs];
                var firstKills = new float[options.Runs];
                var kills = new float[options.Runs];
                var executes = new float[options.Runs];
                var plants = new float[options.Runs];
                var tempo = new RoundTempo();
                var tallies = new Dictionary<int, PlayerTally>();
                int wins = 0, planted = 0;
                for (int i = 0; i < options.Runs; i++)
                {
                    var s = RoundSim.FromReplay(r, simMaps[m], t, p => ProfileOf(m, ri, p), sim,
                        SimRandom.RunSeed(options.Seed, i, scenario), followRecording: false);
                    var res = s.RunToEnd();
                    if (res.AttackersWon) wins++;
                    if (res.Planted) planted++;
                    x.SimReasons.TryGetValue(res.Reason, out int c);
                    x.SimReasons[res.Reason] = c + 1;
                    durations[i] = res.Duration;
                    var enemy = res.Kills.Where(EnemyKill).ToList();
                    kills[i] = enemy.Count;
                    firstKills[i] = enemy.Count > 0 ? enemy[0].Time : float.PositiveInfinity;
                    executes[i] = res.Tempo.ExecuteTime >= 0f ? res.Tempo.ExecuteTime : float.PositiveInfinity;
                    plants[i] = res.Tempo.PlantTime >= 0f ? res.Tempo.PlantTime : float.PositiveInfinity;
                    if (o == 0) { Metrics.AddRound(res.Kills, res.Players, tallies); Metrics.AddShooting(res.Shooting, tallies); tempo.Add(res.Tempo); }
                }
                x.WinRate = wins / (float)options.Runs;
                x.PlantRate = planted / (float)options.Runs;
                x.MeanDuration = durations.Average();
                x.MeanKills = kills.Average();
                x.NoKillRate = firstKills.Count(float.IsPositiveInfinity) / (float)options.Runs;
                var withKill = firstKills.Where(v => !float.IsPositiveInfinity(v)).OrderBy(v => v).ToArray();
                if (withKill.Length > 0) x.MedianFirstKill = withKill[withKill.Length / 2];
                x.DurationPit = Pit(durations, x.Duration);
                x.FirstKillPit = Pit(firstKills, x.FirstKill >= 0f ? x.FirstKill : float.PositiveInfinity);
                x.KillsPit = Pit(kills, x.Kills);
                x.ExecuteRate = executes.Count(v => !float.IsPositiveInfinity(v)) / (float)options.Runs;
                var executed = executes.Where(v => !float.IsPositiveInfinity(v)).OrderBy(v => v).ToArray();
                if (executed.Length > 0) x.MedianExecute = executed[executed.Length / 2];
                x.ExecutePit = Pit(executes, x.ExecuteTime >= 0f ? x.ExecuteTime : float.PositiveInfinity);
                x.PlantPit = Pit(plants, x.PlantTime >= 0f ? x.PlantTime : float.PositiveInfinity);
                var plantTimes = plants.Where(v => !float.IsPositiveInfinity(v)).OrderBy(v => v).ToArray();
                if (plantTimes.Length > 0) x.MedianPlant = plantTimes[plantTimes.Length / 2];
                results[j] = x;
                if (o == 0) { simTallies[j] = tallies; simTempo[j] = tempo; }
                progress?.Invoke(Interlocked.Increment(ref done), jobs.Count);
            });
            report.Snapshots.AddRange(results);
            report.SimulatedRounds = (long)jobs.Count * options.Runs;
            report.FillBaselines();

            // players: recorded vs simulated over the barrier snapshots
            var players = new Dictionary<(int, int), PlayerComparison>();
            for (int j = 0; j < jobs.Count; j++)
            {
                var (m, ri, o, t) = jobs[j];
                if (o != 0) continue;
                var r = replays[m];
                var rec = new Dictionary<int, PlayerTally>();
                Metrics.AddRound(RecordedKills(r, ri, t, DecidedAt(r, ri)), AliveAt(r, t), rec);
                Measurements.AddShooting(r, maps[r.Map], t, DecidedAt(r, ri), rec, sim);
                foreach (var kv in rec) Get(m, kv.Key).Recorded.Add(kv.Value);
                foreach (var kv in simTallies[j]) Get(m, kv.Key).Simulated.Add(kv.Value);
                report.Tempo.Recorded.Add(RoundTempo.Recorded(r, ri, maps[r.Map], t, DecidedAt(r, ri)));
                report.Tempo.Simulated.Add(simTempo[j]);
            }
            PlayerComparison Get(int m, int p)
            {
                if (!players.TryGetValue((m, p), out var c))
                {
                    var pl = replays[m].Players[p];
                    players[(m, p)] = c = new PlayerComparison
                    {
                        Match = replays[m].MatchId ?? "", Player = p, Name = pl.DisplayName, Agent = pl.AgentName ?? "",
                        Profile = options.Profiles == ProfileSource.Default ? Profile.Default : Chosen(whole[m][p].Profile),
                    };
                }
                return c;
            }
            report.Players.AddRange(players.OrderBy(kv => kv.Key).Select(kv => kv.Value));
            return report;
        }

        /// <summary>Rounds with a known attacker, winner and combat phase, decided by play (not a surrender).</summary>
        public static bool Scorable(VrpReplay r, Round rd) =>
            rd.AttackingTeam <= 1 && rd.WinningTeam <= 1 && rd.BarrierTime >= 0f &&
            rd.EndReason != RoundEndReason.Surrender && rd.EndReason != RoundEndReason.Unknown;

        /// <summary>When the round was decided. Round.EndTime is the next round's start, so it includes the end phase.</summary>
        public static float DecidedAt(VrpReplay r, int ri)
        {
            var rd = r.Rounds[ri];
            float decided = rd.EndReason switch
            {
                RoundEndReason.Defused when rd.DefuseTime >= 0f => rd.DefuseTime,
                RoundEndReason.Detonated when rd.PlantTime >= 0f => rd.PlantTime + 45f,
                RoundEndReason.TimeExpired => rd.BarrierTime + 100f,
                _ => -1f,
            };
            if (decided < 0f)
            {
                // elimination: the last kill of the round (no kill, e.g. an unknown end: the round's end)
                foreach (var k in r.Kills)
                    if (k.Time >= rd.StartTime && k.Time < rd.EndTime) decided = Math.Max(decided, k.Time);
                if (decided < 0f) decided = rd.EndTime;
            }
            return Math.Min(decided, rd.EndTime);
        }

        static SnapshotResult Recorded(VrpReplay r, int ri, float t, SimMap map)
        {
            var rd = r.Rounds[ri];
            float decided = DecidedAt(r, ri);
            var kills = RecordedKills(r, ri, t, decided).Where(EnemyKill).ToList();
            var x = new SnapshotResult
            {
                Match = r.MatchId ?? "", Map = MapCatalog.DisplayName(r.Map), Round = ri + 1,
                AttackersWon = rd.WinningTeam == rd.AttackingTeam, Planted = rd.PlantTime >= 0f, Reason = rd.EndReason,
                PlantedAtStart = rd.PlantTime >= 0f && rd.PlantTime <= t,
                Duration = decided - t, Kills = kills.Count, FirstKill = kills.Count > 0 ? kills[0].Time : -1f,
            };
            foreach (int p in AliveAt(r, t))
                if (r.Players[p].Team == rd.AttackingTeam) x.AttackersAlive++; else x.DefendersAlive++;
            var tempo = RoundTempo.Recorded(r, ri, map, t, decided);
            x.ExecuteTime = tempo.ExecuteTime;
            x.PlantTime = tempo.PlantTime;
            return x;
        }

        /// <summary>Recorded kills after t up to the decision, with times relative to t (like a simulation's).</summary>
        static List<KillRecord> RecordedKills(VrpReplay r, int ri, float t, float decided)
        {
            int n = r.Players.Count;
            var l = new List<KillRecord>();
            foreach (var k in r.Kills)
            {
                if (k.Time <= t || k.Time > decided + 0.01f || k.Victim >= n) continue;
                bool player = k.Killer < n;
                l.Add(new KillRecord
                {
                    Time = k.Time - t, Killer = player ? k.Killer : -1, Victim = k.Victim,
                    KillerTeam = player ? r.Players[k.Killer].Team : -1, VictimTeam = r.Players[k.Victim].Team,
                });
            }
            return l;
        }

        static bool EnemyKill(KillRecord k) => k.Killer >= 0 && k.KillerTeam != k.VictimTeam;

        static List<int> AliveAt(VrpReplay r, float t)
        {
            var l = new List<int>();
            for (int p = 0; p < r.Players.Count; p++) if (r.Players[p].Team <= 1 && r.Sample(p, t).Alive) l.Add(p);
            return l;
        }

        static bool BothTeamsAlive(VrpReplay r, float t)
        {
            bool a = false, b = false;
            foreach (int p in AliveAt(r, t)) { if (r.Players[p].Team == 0) a = true; else b = true; }
            return a && b;
        }

        /// <summary>Mid-rank position of observed among the samples: ties count half, so a discrete value the simulation
        /// always produces lands at 0.5.</summary>
        public static float Pit(float[] samples, float observed)
        {
            int below = 0, equal = 0;
            foreach (float v in samples)
            {
                if (v < observed - 1e-4f) below++;
                else if (Math.Abs(v - observed) <= 1e-4f || (float.IsPositiveInfinity(v) && float.IsPositiveInfinity(observed))) equal++;
            }
            return samples.Length > 0 ? (below + 0.5f * equal) / samples.Length : 0.5f;
        }
    }
}
