using ValorantReplay.Playback;
using Xunit;

namespace ValorantReplay.Sim.Tests;

/// <summary>Example bots: tiers set through the execution transfer, aim and sense split, lineups playing each other.</summary>
public class BotTests
{
    [Fact]
    public void Tiers_ExecutionImprovesWithTier()
    {
        Profile prev = null;
        foreach (var t in Bots.Tiers)
        {
            var p = Bots.Make(t, BotArchetype.Flex);
            Assert.True(p.Measured);
            if (prev != null)
            {
                Assert.True(p.BaseAccuracy > prev.BaseAccuracy, $"{t} accuracy");
                Assert.True(p.HeadshotShare > prev.HeadshotShare, $"{t} headshots");
                Assert.True(p.ReactionTime < prev.ReactionTime, $"{t} reaction");
                Assert.True(p.LookSpread <= prev.LookSpread, $"{t} placement");
                Assert.True(p.Skill > prev.Skill, $"{t} sense");
            }
            prev = p;
        }
    }

    [Fact]
    public void Average_IsThePopulation()
    {
        var x = ProfileFitter.Transfer;
        var p = Bots.Make(BotTier.Average, BotArchetype.Flex);
        var population = ProfileFitter.ToProfile(new FittedProfile(), x);
        Assert.Equal(population.BaseAccuracy, p.BaseAccuracy, 3);
        Assert.Equal(population.HeadshotShare, p.HeadshotShare, 3);
        Assert.Equal(population.ReactionTime, p.ReactionTime, 2);
        Assert.Equal(population.LookSpread, p.LookSpread, 1);
    }

    [Fact]
    public void Split_AimFromOneTierSenseFromAnother()
    {
        var p = Bots.Split(BotTier.Pro, BotTier.Novice, BotArchetype.Anchor);
        var pro = Bots.Make(BotTier.Pro, BotArchetype.Anchor);
        Assert.Equal(pro.BaseAccuracy, p.BaseAccuracy);
        Assert.Equal(pro.ReactionTime, p.ReactionTime);
        Assert.Equal(Bots.Stats(BotTier.Novice).Sense, p.Skill);
        Assert.Equal(PlayStyle.Anchor, p.Style);
        Assert.Equal(BotArchetype.Anchor.Aggression, p.Aggression);
    }

    [Fact]
    public void Lineup_CohesionOverrideKeepsExecution()
    {
        var solo = Bots.Lineup("solo", BotTier.Skilled, Bots.Standard, cohesion: 0.1f);
        Assert.Equal(5, solo.Bots.Length);
        Assert.All(solo.Bots, b => Assert.Equal(0.1f, b.Profile.Cohesion));
        Assert.Equal(Bots.Make(BotTier.Skilled, BotArchetype.Entry).BaseAccuracy, solo.Bots[0].Profile.BaseAccuracy);
    }

    static VrpReplay Replay()
    {
        var players = new List<(int, System.Numerics.Vector3, float)>();
        for (int i = 0; i < 5; i++) players.Add((0, TestWorld.Cell(2 + i % 3, 9 + i / 3), 90f));
        foreach (var (c, r) in new[] { (31, 3), (31, 17), (33, 4), (33, 16), (28, 4) }) players.Add((1, TestWorld.Cell(c, r), -90f));
        return TestWorld.StandingReplay(players, new List<Kill>(), duration: 120f);
    }

    [Fact]
    public void Arena_BetterTierWinsAndBothSidesArePlayed()
    {
        var arena = new BotArena(new[] { Replay() }, new Dictionary<string, SimMap> { ["ascii"] = TestWorld.Map });
        Assert.Equal(1, arena.Rounds);
        var m = arena.Play(Bots.Lineup("pro", BotTier.Pro, Bots.Standard), Bots.Lineup("novice", BotTier.Novice, Bots.Standard), runs: 20, threads: 1);
        Assert.Equal(40, m.Rounds);
        Assert.Equal(20, m.AttackRoundsA);
        Assert.Equal(20, m.DefenseRoundsA);
        Assert.True(m.WinRateA > 0.7f, $"pro won {m.WinRateA:P0}");
        Assert.True(m.TotalA.Kills > m.TotalB.Kills);
        Assert.All(m.TallyA, t => Assert.Equal(40, t.Rounds));     // every bot played every round
    }

    [Fact]
    public void Arena_DoesNotDependOnThreads()
    {
        var arena = new BotArena(new[] { Replay() }, new Dictionary<string, SimMap> { ["ascii"] = TestWorld.Map });
        var a = Bots.Lineup("a", BotTier.Average, Bots.Aggro);
        var b = Bots.Lineup("b", BotTier.Average, Bots.Passive);
        var one = arena.Play(a, b, runs: 8, seed: 3, threads: 1);
        var many = arena.Play(a, b, runs: 8, seed: 3, threads: 4);
        Assert.Equal(one.WinsA, many.WinsA);
        Assert.Equal(one.TotalA.Kills, many.TotalA.Kills);
        Assert.Equal(one.DurationSum, many.DurationSum);
    }
}
