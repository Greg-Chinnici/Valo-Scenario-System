// Scorecard.cs - a calibration (CalibrationReport) boiled down to named numbers, to tell whether a change to the
// simulation made it better or worse. Each metric says which way is better: lower (Brier, KS distance, an error),
// higher (skill, a correlation) or nearer a target (the recorded value; 0.5 for a PIT mean). A scorecard over several
// seeds keeps each metric's mean and its spread between seeds, so Compare can tell a real change from the dice: a
// metric regressed when it got worse by more than Tolerance standard errors of the difference. Saved as tab-separated
// text (sim/scorecard.tsv) so a change of the baseline reads as a diff.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ValorantReplay.Playback;

namespace ValorantReplay.Sim
{
    public enum Better : byte { Lower = 0, Higher = 1, Target = 2 }

    public sealed class ScoreMetric
    {
        public string Name = "";
        public Better Better;
        public float Target = float.NaN;    // Better.Target only
        public float Mean;
        /// <summary>Standard deviation between seeds; NaN with one seed.</summary>
        public float Sd = float.NaN;

        /// <summary>How bad value is: lower is better, whatever the metric's direction.</summary>
        public float Badness(float value) => Better == Better.Lower ? value : Better == Better.Higher ? -value : Math.Abs(value - Target);
    }

    public enum ScoreVerdict : byte { Same = 0, Better = 1, Worse = 2, Improved = 3, Regressed = 4, Missing = 5 }

    public sealed class ScoreChange
    {
        public string Name = "";
        public Better Better;
        public float Target, Baseline, Current;
        /// <summary>Change in badness: above 0 = worse.</summary>
        public float Delta;
        /// <summary>Standard error of Delta from the seed spreads; NaN when neither side has one.</summary>
        public float Noise;
        /// <summary>Delta / Noise.</summary>
        public float Z => Noise > 0f ? Delta / Noise : float.NaN;
        /// <summary>Better / Worse: within the noise, or no noise to judge by. Improved / Regressed: beyond Tolerance
        /// standard errors. Missing: in the baseline, not in the current scorecard (or not a number there).</summary>
        public ScoreVerdict Verdict;
    }

    public sealed class Scorecard
    {
        /// <summary>Standard errors a metric must get worse by to count as a regression. Well above 2 because a run checks
        /// some fifty metrics: on the published replays, single seeds against a 16-seed baseline stayed within 3.3 except
        /// for one in eleven reaching 4.4 (the attacker win rate).</summary>
        public static float Tolerance = 4.5f;

        /// <summary>What it was run on and with (replays, runs, seeds, options), in order.</summary>
        public readonly List<KeyValuePair<string, string>> Info = new List<KeyValuePair<string, string>>();
        public readonly List<ScoreMetric> Metrics = new List<ScoreMetric>();

        public int Seeds => int.TryParse(Get("seeds_count"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 1;

        public string Get(string key) => Info.FirstOrDefault(kv => kv.Key == key).Value;

        public void Set(string key, string value)
        {
            int i = Info.FindIndex(kv => kv.Key == key);
            if (i >= 0) Info[i] = new KeyValuePair<string, string>(key, value);
            else Info.Add(new KeyValuePair<string, string>(key, value));
        }

        public ScoreMetric this[string name] => Metrics.FirstOrDefault(m => m.Name == name);

        // ------------------------------------------------ from a calibration

        /// <summary>One seed's scorecard. Every number is computed the way vrpsim calibrate prints it.</summary>
        public static Scorecard From(CalibrationReport rep)
        {
            var card = new Scorecard();
            card.Set("replays", rep.Replays.ToString(CultureInfo.InvariantCulture));
            card.Set("rounds", rep.Rounds.ToString(CultureInfo.InvariantCulture));
            card.Set("snapshots", rep.Snapshots.Count.ToString(CultureInfo.InvariantCulture));
            card.Set("runs", rep.Options.Runs.ToString(CultureInfo.InvariantCulture));
            card.Set("seeds", rep.Options.Seed.ToString(CultureInfo.InvariantCulture));
            card.Set("seeds_count", "1");

            var all = rep.Snapshots;
            float first = rep.Options.Offsets.Min();
            var barrier = all.Where(x => x.Offset == first).ToList();
            var unplanted = all.Where(x => !x.PlantedAtStart).ToList();
            float M(IEnumerable<SnapshotResult> s, Func<SnapshotResult, float> f) => CalibrationReport.Mean(s, f);

            // who wins
            card.Add("win.brier", Better.Lower, CalibrationReport.Brier(all, x => x.WinRate));
            card.Add("win.skill_vs_corpus", Better.Higher, CalibrationReport.Skill(all, x => x.BaselineWin));
            card.Add("win.skill_vs_man_adv", Better.Higher, CalibrationReport.Skill(all, x => x.ManAdvantageWin));
            card.Add("win.log_loss", Better.Lower, CalibrationReport.LogLoss(all));
            card.Add("win.reliability_error", Better.Lower, rep.ReliabilityError());

            // the spread of what happens next
            card.Add("spread.duration_pit", Better.Target, M(all, x => x.DurationPit), 0.5f);
            card.Add("spread.duration_ks", Better.Lower, CalibrationReport.KsUniform(all.Select(x => x.DurationPit)));
            card.Add("spread.first_kill_pit", Better.Target, M(all, x => x.FirstKillPit), 0.5f);
            card.Add("spread.first_kill_ks", Better.Lower, CalibrationReport.KsUniform(all.Select(x => x.FirstKillPit)));
            card.Add("spread.kills_pit", Better.Target, M(all, x => x.KillsPit), 0.5f);
            card.Add("spread.kills_ks", Better.Lower, CalibrationReport.KsUniform(all.Select(x => x.KillsPit)));

            // rates against the recorded ones
            card.Add("rate.attackers_win", Better.Target, M(all, x => x.WinRate), M(all, x => x.Outcome));
            card.Add("rate.plant", Better.Target, M(unplanted, x => x.PlantRate), M(unplanted, x => x.Planted ? 1f : 0f));
            card.Add("rate.no_kill", Better.Target, M(all, x => x.NoKillRate), M(all, x => x.FirstKill < 0f ? 1f : 0f));
            card.Add("rate.kills", Better.Target, M(all, x => x.MeanKills), M(all, x => (float)x.Kills));
            card.Add("rate.time_to_decide", Better.Target, M(all, x => x.MeanDuration), M(all, x => x.Duration));

            // pacing (barrier snapshots)
            card.Add("tempo.execute_rate", Better.Target, M(barrier, x => x.ExecuteRate), M(barrier, x => x.ExecuteTime >= 0f ? 1f : 0f));
            card.Add("tempo.execute_pit", Better.Target, M(barrier, x => x.ExecutePit), 0.5f);
            card.Add("tempo.execute_ks", Better.Lower, CalibrationReport.KsUniform(barrier.Select(x => x.ExecutePit)));
            card.Add("tempo.plant_pit", Better.Target, M(barrier, x => x.PlantPit), 0.5f);
            card.Add("tempo.plant_ks", Better.Lower, CalibrationReport.KsUniform(barrier.Select(x => x.PlantPit)));
            card.Add("tempo.attacker_speed_error", Better.Lower, SpeedError(rep.Tempo.Recorded.AttackerSpeedAt, rep.Tempo.Simulated.AttackerSpeedAt));
            card.Add("tempo.defender_speed_error", Better.Lower, SpeedError(rep.Tempo.Recorded.DefenderSpeedAt, rep.Tempo.Simulated.DefenderSpeedAt));

            // how rounds end (barrier snapshots)
            foreach (var reason in new[] { RoundEndReason.Elimination, RoundEndReason.Detonated, RoundEndReason.Defused, RoundEndReason.TimeExpired })
                card.Add("end." + Snake(reason.ToString()), Better.Target,
                    M(barrier, x => x.SimReasons.TryGetValue(reason, out int c) ? c / (float)x.Runs : 0f), M(barrier, x => x.Reason == reason ? 1f : 0f));

            // players: do fitted profiles keep them apart (r), and how far off is each (MAE)
            void Player(string name, Func<PlayerTally, float> f, Func<PlayerTally, bool> has = null)
            {
                var v = rep.PlayerPairs(f, has);
                card.Add($"player.{name}.r", Better.Higher, CalibrationReport.Correlation(v));
                card.Add($"player.{name}.mae", Better.Lower, CalibrationReport.Mean(v, x => Math.Abs(x.Rec - x.Sim)));
            }
            Player("kills", t => t.Kills / (float)t.Rounds);
            Player("deaths", t => t.Deaths / (float)t.Rounds);
            Player("opening", t => t.OpeningRate);
            Player("first_kill", t => t.FirstKillRate);
            Player("traded", t => t.TradedRate);
            Player("accuracy", t => t.Accuracy, t => t.Bullets >= 30);
            Player("head_share", t => t.HeadHitRate, t => t.Hits >= 10);
            Player("leg_share", t => t.LegHitRate, t => t.Hits >= 10);
            Player("reaction", t => t.ReactionMean, t => t.Reactions >= 5);
            Player("shoots_after_notice", t => t.ShootOnNoticeRate, t => t.Notices >= 5);
            Player("aim_error", t => t.AimErrorMean, t => t.Notices >= 5);
            return card;
        }

        void Add(string name, Better better, float value, float target = float.NaN) =>
            Metrics.Add(new ScoreMetric { Name = name, Better = better, Mean = value, Target = better == Better.Target ? target : float.NaN });

        /// <summary>Mean absolute difference of recorded and simulated speed over the 10 s bins both have.</summary>
        static float SpeedError(Func<int, float> recorded, Func<int, float> simulated)
        {
            var d = Enumerable.Range(0, RoundTempo.Bins).Select(b => Math.Abs(recorded(b) - simulated(b))).Where(x => !float.IsNaN(x)).ToList();
            return d.Count > 0 ? d.Average() : float.NaN;
        }

        static string Snake(string s) => string.Concat(s.Select((c, i) => char.IsUpper(c) && i > 0 ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

        /// <summary>Several seeds' scorecards as one: each metric's mean and standard deviation between seeds.</summary>
        public static Scorecard Combine(IList<Scorecard> seeds)
        {
            if (seeds.Count == 0) throw new ArgumentException("no scorecards");
            if (seeds.Count == 1) return seeds[0];
            var card = new Scorecard();
            foreach (var kv in seeds[0].Info) card.Set(kv.Key, kv.Value);
            card.Set("seeds", string.Join(",", seeds.Select(s => s.Get("seeds"))));
            card.Set("seeds_count", seeds.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var m in seeds[0].Metrics)
            {
                var v = seeds.Select(s => s[m.Name]?.Mean ?? float.NaN).Where(x => !float.IsNaN(x)).ToList();
                var t = seeds.Select(s => s[m.Name]?.Target ?? float.NaN).Where(x => !float.IsNaN(x)).ToList();
                float mean = v.Count > 0 ? v.Average() : float.NaN;
                card.Metrics.Add(new ScoreMetric
                {
                    Name = m.Name, Better = m.Better, Mean = mean, Target = t.Count > 0 ? t.Average() : float.NaN,
                    Sd = v.Count > 1 ? (float)Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / (v.Count - 1)) : float.NaN,
                });
            }
            return card;
        }

        // ------------------------------------------------ comparing

        /// <summary>Every metric of baseline against current. The noise of a difference is the standard error of the two
        /// means, from the seed spread pooled over both sides (each weighted by its seeds less one), so a side with one or
        /// two seeds leans on the other's.</summary>
        public static List<ScoreChange> Compare(Scorecard baseline, Scorecard current, float tolerance = float.NaN)
        {
            if (float.IsNaN(tolerance)) tolerance = Tolerance;
            int kb = baseline.Seeds, kc = current.Seeds;
            var changes = new List<ScoreChange>();
            foreach (var b in baseline.Metrics)
            {
                var c = current[b.Name];
                var ch = new ScoreChange { Name = b.Name, Better = b.Better, Target = b.Target, Baseline = b.Mean, Current = c?.Mean ?? float.NaN };
                if (c == null || float.IsNaN(c.Mean) || float.IsNaN(b.Mean))
                {
                    ch.Verdict = float.IsNaN(b.Mean) && (c == null || float.IsNaN(c.Mean)) ? ScoreVerdict.Same : ScoreVerdict.Missing;
                    ch.Noise = float.NaN;
                    changes.Add(ch);
                    continue;
                }
                // judged against the baseline's target, so a moved target (other replays) does not read as a change
                var judge = c.Better == Better.Target ? new ScoreMetric { Better = Better.Target, Target = b.Target } : c;
                ch.Delta = judge.Badness(c.Mean) - b.Badness(b.Mean);
                ch.Noise = (float)(Pooled(b.Sd, kb, c.Sd, kc) * Math.Sqrt(1.0 / kb + 1.0 / kc));
                if (ch.Delta == 0f) ch.Verdict = ScoreVerdict.Same;
                else if (ch.Noise > 0f && ch.Delta > tolerance * ch.Noise) ch.Verdict = ScoreVerdict.Regressed;
                else if (ch.Noise > 0f && -ch.Delta > tolerance * ch.Noise) ch.Verdict = ScoreVerdict.Improved;
                else ch.Verdict = ch.Delta > 0f ? ScoreVerdict.Worse : ScoreVerdict.Better;
                changes.Add(ch);
            }
            return changes;
        }

        /// <summary>Standard deviation pooled from two sides' (sd, seeds); NaN when neither has one.</summary>
        static double Pooled(float sa, int ka, float sb, int kb)
        {
            double sum = 0;
            int df = 0;
            if (!float.IsNaN(sa) && ka > 1) { sum += (ka - 1) * (double)sa * sa; df += ka - 1; }
            if (!float.IsNaN(sb) && kb > 1) { sum += (kb - 1) * (double)sb * sb; df += kb - 1; }
            return df > 0 ? Math.Sqrt(sum / df) : double.NaN;
        }

        // ------------------------------------------------ text

        const string Header = "metric\tbetter\ttarget\tmean\tsd";

        public void Write(TextWriter w)
        {
            w.WriteLine("# vrpsim scorecard. better: lower, higher, or target (nearer the target is better: the recorded value, 0.5 for a PIT mean).");
            w.WriteLine("# mean and sd over the seeds; regenerate with vrpsim scorecard ... --out after a change that is meant to move these.");
            foreach (var kv in Info) w.WriteLine($"# {kv.Key}\t{kv.Value}");
            w.WriteLine(Header);
            foreach (var m in Metrics)
                w.WriteLine($"{m.Name}\t{m.Better.ToString().ToLowerInvariant()}\t{F(m.Target)}\t{F(m.Mean)}\t{F(m.Sd)}");
        }

        public static Scorecard Read(TextReader r)
        {
            var card = new Scorecard();
            for (string line; (line = r.ReadLine()) != null;)
            {
                if (line.Length == 0 || line == Header) continue;
                var parts = line.Split('\t');
                if (line.StartsWith("# "))
                {
                    if (parts.Length == 2) card.Set(parts[0].Substring(2), parts[1]);
                    continue;
                }
                if (parts.Length != 5 || !Enum.TryParse(parts[1], true, out Better better)) throw new InvalidDataException($"scorecard: bad line '{line}'");
                card.Metrics.Add(new ScoreMetric { Name = parts[0], Better = better, Target = P(parts[2]), Mean = P(parts[3]), Sd = P(parts[4]) });
            }
            return card;
        }

        public void Save(string path)
        {
            using var w = new StreamWriter(path) { NewLine = "\n" };
            Write(w);
        }

        public static Scorecard Load(string path)
        {
            using var r = new StreamReader(path);
            return Read(r);
        }

        static string F(float v) => float.IsNaN(v) ? "" : v.ToString("0.######", CultureInfo.InvariantCulture);
        static float P(string s) => s.Length == 0 ? float.NaN : float.Parse(s, CultureInfo.InvariantCulture);
    }
}
