using ValorantReplay.Playback;
using Xunit;

namespace ValorantReplay.Sim.Tests;

/// <summary>Random streams, batch statistics and the calibration harness that scores simulations against recordings.</summary>
public class CalibrationTests
{
    [Fact]
    public void Streams_AreReproducibleAndIndependent()
    {
        Assert.Equal(SimRandom.StreamSeed(7, 3, RandomStream.Aim), SimRandom.StreamSeed(7, 3, RandomStream.Aim));
        var seeds = new HashSet<int>();
        for (int agent = -1; agent < 10; agent++)
            foreach (RandomStream s in Enum.GetValues(typeof(RandomStream)))
                seeds.Add(SimRandom.StreamSeed(7, agent, s));
        Assert.Equal(11 * Enum.GetValues(typeof(RandomStream)).Length, seeds.Count);
        Assert.NotEqual(SimRandom.StreamSeed(7, 3, RandomStream.Aim), SimRandom.StreamSeed(8, 3, RandomStream.Aim));
        Assert.NotEqual(SimRandom.RunSeed(0, 1), SimRandom.RunSeed(0, 2));
        Assert.NotEqual(SimRandom.RunSeed(0, 1, scenario: 1), SimRandom.RunSeed(0, 1, scenario: 2));
    }

    [Fact]
    public void Batch_ParallelEqualsSequential()
    {
        RoundSim Make(int i) => TestWorld.Round(_ => Profile.Default, _ => Profile.Default, i);
        var a = SimBatch.Run(Make, 60);
        var b = SimBatch.Run(Make, 60, threads: 0);
        Assert.Equal((a.AttackerWins, a.Planted, a.MeanDuration), (b.AttackerWins, b.Planted, b.MeanDuration));
        Assert.Equal(a.Players.OrderBy(kv => kv.Key).Select(kv => kv.Value.ToString()), b.Players.OrderBy(kv => kv.Key).Select(kv => kv.Value.ToString()));
    }

    [Fact]
    public void RunUntil_StopsWhenTheWinRateIsPreciseEnough()
    {
        RoundSim Make(int i) => TestWorld.Round(_ => Profile.Default, _ => Profile.Default, i);
        var b = SimBatch.RunUntil(Make, halfWidth: 0.1f, maxRounds: 1000, minRounds: 50, block: 50, threads: 0);
        Assert.True(b.Rounds < 1000, $"{b.Rounds} rounds");
        Assert.True(b.AttackerWinHalfWidth <= 0.1f);
        Assert.Equal(0, b.Rounds % 50);
        Assert.Equal(120, SimBatch.RunUntil(Make, 0f, maxRounds: 120, minRounds: 50, block: 50).Rounds);   // never precise enough: capped
    }

    [Fact]
    public void Wilson_HalfWidth()
    {
        Assert.InRange(BatchResult.WilsonHalfWidth(50, 100), 0.095f, 0.097f);
        Assert.InRange(BatchResult.WilsonHalfWidth(0, 100), 0.01f, 0.03f);     // not zero at 0 successes
        Assert.True(BatchResult.WilsonHalfWidth(500, 1000) < BatchResult.WilsonHalfWidth(50, 100));
    }

    [Fact]
    public void Pit_MidRankWithTiesAndNoKill()
    {
        var s = new[] { 1f, 2f, 2f, 3f };
        Assert.Equal(0f, Calibration.Pit(s, 0.5f));
        Assert.Equal(0.5f, Calibration.Pit(s, 2f));             // 1 below + half of 2 ties
        Assert.Equal(1f, Calibration.Pit(s, 9f));
        var withNone = new[] { 1f, float.PositiveInfinity };    // run 2 had no kill
        Assert.Equal(0.75f, Calibration.Pit(withNone, float.PositiveInfinity));
        Assert.Equal(0.5f, Calibration.Pit(withNone, 5f));
    }

    [Fact]
    public void DecidedAt_FollowsTheEndReason()
    {
        var r = TestWorld.StandingReplay(new[] { (0, TestWorld.Cell(2, 1), 90f), (1, TestWorld.Cell(14, 1), -90f) },
            new List<Kill> { new Kill { Time = 4f, Killer = 0, Victim = 1, Round = 0 } }, duration: 200f);
        var rd = r.Rounds[0];
        rd.EndReason = RoundEndReason.Elimination;
        Assert.Equal(4f, Calibration.DecidedAt(r, 0));
        rd.EndReason = RoundEndReason.Defused; rd.DefuseTime = 60f;
        Assert.Equal(60f, Calibration.DecidedAt(r, 0));
        rd.EndReason = RoundEndReason.Detonated; rd.PlantTime = 50f;
        Assert.Equal(95f, Calibration.DecidedAt(r, 0));
        rd.EndReason = RoundEndReason.TimeExpired;
        Assert.Equal(100f, Calibration.DecidedAt(r, 0));
    }

    [Fact]
    public void Fit_LeavesRoundsOut()
    {
        var r = TestWorld.StandingReplay(new[] { (0, TestWorld.Cell(2, 1), 90f), (1, TestWorld.Cell(14, 1), -90f) },
            new List<Kill> { new Kill { Time = 2f, Killer = 0, Victim = 1, Round = 0 } });
        Assert.Equal(1, ProfileFitter.Fit(r)[0].Tally.Kills);
        Assert.All(ProfileFitter.Fit(r, _ => false), f => Assert.Equal(0, f.Tally.Rounds));
    }

    // attacker 0 and defender 1 face each other down the top lane; in the recording 0 kills 1 at t = 2
    [Fact]
    public void Calibrate_ScoresARecordedRound()
    {
        var r = TestWorld.StandingReplay(new[] { (0, TestWorld.Cell(2, 1), 90f), (1, TestWorld.Cell(14, 1), -90f) },
            new List<Kill> { new Kill { Time = 2f, Killer = 0, Victim = 1, Round = 0 } });
        r.Rounds[0].WinningTeam = 0;
        r.Rounds[0].EndReason = RoundEndReason.Elimination;
        var options = new CalibrationOptions { Runs = 40, Offsets = new[] { 0f, 1f, 5f }, Profiles = ProfileSource.Default };
        var rep = Calibration.Run(new[] { r }, options, maps: new Dictionary<string, SimMap> { ["ascii"] = TestWorld.Map });

        Assert.Equal(1, rep.Rounds);
        Assert.Equal(new[] { 0f, 1f }, rep.Snapshots.Select(x => x.Offset));   // +5 s is after the round was decided
        var x = rep.Snapshots[0];
        Assert.True(x.AttackersWon);
        Assert.Equal((1, 1, 1), (x.AttackersAlive, x.DefendersAlive, x.Kills));
        Assert.Equal(2f, x.Duration, 2);
        Assert.Equal(2f, x.FirstKill, 2);
        Assert.Equal(1f, rep.Snapshots[1].FirstKill, 2);                      // relative to its snapshot
        Assert.Equal(40, x.Runs);
        Assert.InRange(x.WinRate, 0.05f, 0.95f);                              // a 1v1 duel goes either way
        Assert.InRange(x.MeanKills, 1f, 1.2f);                                // always a kill, now and then a same-tick trade
        Assert.All(new[] { x.DurationPit, x.FirstKillPit, x.KillsPit }, p => Assert.InRange(p, 0f, 1f));
        Assert.InRange(x.KillsPit, 0.4f, 0.5f);                               // the recorded 1 is the usual count

        var p0 = Assert.Single(rep.Players, p => p.Player == 0);
        Assert.Equal((1, 1, 0), (p0.Recorded.Rounds, p0.Recorded.Kills, p0.Recorded.Deaths));
        Assert.Equal(40, p0.Simulated.Rounds);
        Assert.InRange(p0.Simulated.Kills + p0.Simulated.Deaths, 40, 48);   // each run a kill or a death; a trade is both

        var again = Calibration.Run(new[] { r }, options, maps: new Dictionary<string, SimMap> { ["ascii"] = TestWorld.Map });
        Assert.Equal(rep.Snapshots.Select(s => s.WinRate), again.Snapshots.Select(s => s.WinRate));
    }
}
