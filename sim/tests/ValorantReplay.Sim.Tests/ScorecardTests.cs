using ValorantReplay.Playback;
using Xunit;

namespace ValorantReplay.Sim.Tests;

/// <summary>Scorecards: a calibration as named metrics, combined over seeds, saved, and compared with the seed spread as
/// the noise.</summary>
public class ScorecardTests
{
    static Scorecard Card(params (string Name, Better Better, float Target, float Mean, float Sd)[] metrics)
    {
        var c = new Scorecard();
        c.Set("seeds_count", "4");
        foreach (var m in metrics) c.Metrics.Add(new ScoreMetric { Name = m.Name, Better = m.Better, Target = m.Target, Mean = m.Mean, Sd = m.Sd });
        return c;
    }

    [Fact]
    public void FromACalibration_SavesAndLoads()
    {
        var r = TestWorld.StandingReplay(new[] { (0, TestWorld.Cell(2, 1), 90f), (1, TestWorld.Cell(14, 1), -90f) },
            new List<Kill> { new Kill { Time = 2f, Killer = 0, Victim = 1, Round = 0 } });
        r.Rounds[0].WinningTeam = 0;
        r.Rounds[0].EndReason = RoundEndReason.Elimination;
        var maps = new Dictionary<string, SimMap> { ["ascii"] = TestWorld.Map };
        var seeds = new[] { 0, 1, 2 }.Select(seed => Scorecard.From(Calibration.Run(new[] { r },
            new CalibrationOptions { Runs = 20, Seed = seed, Offsets = new[] { 0f, 1f }, Profiles = ProfileSource.Default }, maps: maps))).ToList();
        var card = Scorecard.Combine(seeds);

        Assert.Equal("0,1,2", card.Get("seeds"));
        Assert.Equal(3, card.Seeds);
        var brier = card["win.brier"];
        Assert.Equal(Better.Lower, brier.Better);
        Assert.Equal(seeds.Average(s => s["win.brier"].Mean), brier.Mean, 5);
        Assert.False(float.IsNaN(brier.Sd));
        Assert.Equal(1f, card["rate.attackers_win"].Target);                  // the recorded round: attackers won
        Assert.Equal(0.5f, card["spread.kills_pit"].Target);

        var w = new StringWriter();
        card.Write(w);
        var back = Scorecard.Read(new StringReader(w.ToString()));
        Assert.Equal(card.Info, back.Info);
        Assert.Equal(card.Metrics.Select(m => (m.Name, m.Better)), back.Metrics.Select(m => (m.Name, m.Better)));
        Assert.Equal(card.Metrics.Select(m => m.Mean), back.Metrics.Select(m => m.Mean), new Close());
        Assert.All(Scorecard.Compare(card, back), c => Assert.True(c.Verdict == ScoreVerdict.Same || Math.Abs(c.Delta) < 1e-5f, c.Name));
    }

    [Fact]
    public void Compare_JudgesEachMetricByItsDirectionAndNoise()
    {
        var baseline = Card(("brier", Better.Lower, float.NaN, 0.20f, 0.01f), ("skill", Better.Higher, float.NaN, 0.10f, 0.01f),
            ("pit", Better.Target, 0.5f, 0.45f, 0.01f), ("rate", Better.Target, 0.6f, 0.60f, 0.01f), ("gone", Better.Lower, float.NaN, 1f, 0.1f));
        var current = Card(("brier", Better.Lower, float.NaN, 0.25f, 0.01f), ("skill", Better.Higher, float.NaN, 0.15f, 0.01f),
            ("pit", Better.Target, 0.5f, 0.55f, 0.01f), ("rate", Better.Target, 0.6f, 0.605f, 0.01f));
        var changes = Scorecard.Compare(baseline, current, tolerance: 4f).ToDictionary(c => c.Name);

        // noise: sqrt(0.01^2 / 4 + 0.01^2 / 4) = 0.0071
        Assert.Equal(ScoreVerdict.Regressed, changes["brier"].Verdict);     // +0.05 = 7 standard errors worse
        Assert.Equal(ScoreVerdict.Improved, changes["skill"].Verdict);
        Assert.Equal(0f, changes["pit"].Delta, 5);                          // 0.05 off the target either side
        Assert.Equal(ScoreVerdict.Worse, changes["rate"].Verdict);          // worse, but within the noise
        Assert.Equal(ScoreVerdict.Missing, changes["gone"].Verdict);
        Assert.Equal(0.05f / 0.00707f, changes["brier"].Z, 1);
    }

    [Fact]
    public void Compare_WithOneSeed_BorrowsTheBaselinesSpread()
    {
        var baseline = Card(("brier", Better.Lower, float.NaN, 0.20f, 0.01f));
        var current = new Scorecard();
        current.Metrics.Add(new ScoreMetric { Name = "brier", Better = Better.Lower, Mean = 0.23f });
        var c = Scorecard.Compare(baseline, current, tolerance: 4f).Single();
        Assert.Equal(0.01 * Math.Sqrt(1.0 / 4 + 1.0 / 1), c.Noise, 4);
        Assert.Equal(ScoreVerdict.Worse, c.Verdict);                         // 2.7 standard errors: not yet a regression
    }

    [Fact]
    public void Compare_PoolsTheSpreadByHowManySeedsMeasuredIt()
    {
        // two seeds that happened to agree say little about the noise: the baseline's 16 seeds carry it
        var baseline = Card(("brier", Better.Lower, float.NaN, 0.20f, 0.01f));
        baseline.Set("seeds_count", "16");
        var current = Card(("brier", Better.Lower, float.NaN, 0.21f, 0.0001f));
        current.Set("seeds_count", "2");
        var c = Scorecard.Compare(baseline, current, tolerance: 4f).Single();
        Assert.Equal(Math.Sqrt((15 * 0.01 * 0.01 + 1 * 0.0001 * 0.0001) / 16) * Math.Sqrt(1.0 / 16 + 1.0 / 2), c.Noise, 5);
        Assert.Equal(ScoreVerdict.Worse, c.Verdict);
    }

    sealed class Close : IEqualityComparer<float>
    {
        public bool Equals(float a, float b) => (float.IsNaN(a) && float.IsNaN(b)) || Math.Abs(a - b) <= 1e-5f * Math.Max(1f, Math.Abs(a));
        public int GetHashCode(float v) => 0;
    }
}
