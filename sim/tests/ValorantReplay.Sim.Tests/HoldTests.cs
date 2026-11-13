using System.Numerics;
using ValorantReplay.Playback;
using Xunit;

namespace ValorantReplay.Sim.Tests;

/// <summary>Learned defender holds (HoldModel) and wall clearance for wide corners (NavGrid.Clearance).</summary>
public class HoldTests
{
    // attackers (team 0) in their spawn, two defenders standing on A at (31, 3) and (27, 1) for a minute
    static VrpReplay Holding() => TestWorld.StandingReplay(new[]
    {
        (0, TestWorld.Cell(2, 9), 90f), (0, TestWorld.Cell(3, 10), 90f),
        (1, TestWorld.Cell(31, 3), -90f), (1, TestWorld.Cell(27, 1), -90f),
    }, new List<Kill>(), duration: 60f);

    [Fact]
    public void HoldModel_LearnsWhereDefendersStand_AndKeepsTeammatesApart()
    {
        var holds = HoldModel.FromReplays(new[] { Holding() }, TestWorld.Map.Sites);
        Assert.True(holds.Known);
        var spots = holds.SpotsOf("A");
        Assert.Equal(2, spots.Count);
        Assert.Empty(holds.SpotsOf("B"));
        Assert.Contains(spots, s => SimSpace.FlatDistance(s.Position, TestWorld.Cell(31, 3)) < 0.5f);

        var rng = new Random(1);
        Assert.True(holds.Draw("A", rng, new List<Vector3>(), out var first));
        for (int i = 0; i < 20; i++)
        {
            Assert.True(holds.Draw("A", rng, new List<Vector3> { first }, out var second));
            Assert.True(SimSpace.FlatDistance(first, second) >= HoldModel.MinSeparation);
        }
        Assert.False(holds.Draw("B", rng, new List<Vector3>(), out _));
    }

    [Fact]
    public void Defenders_GoToTheirLearnedSpot()
    {
        var holds = HoldModel.FromReplays(new[] { Holding() }, TestWorld.Map.Sites);
        var map = TestWorld.Map.WithLearned(null, null, holds);
        // one attacker in spawn; defender 1 is dealt A (learned spots), defender 2 B (none learned)
        var setup = new List<AgentSetup>
        {
            new AgentSetup { Team = 0, Attacking = true, Position = TestWorld.Cell(2, 10), Yaw = 90f, Name = "atk" },
            new AgentSetup { Team = 1, Position = TestWorld.Cell(36, 10), Yaw = -90f, Name = "defA" },
            new AgentSetup { Team = 1, Position = TestWorld.Cell(36, 11), Yaw = -90f, Name = "defB" },
        };
        var sim = new RoundSim(map, setup, new RoundSetup { Plan = AttackPlan.Slow }, new SimOptions { GlanceRate = 0f }, seed: 1) { SightTest = (_, _) => false };
        for (int i = 0; i < 300; i++) sim.Step();
        var a = sim.Agents[1];
        Assert.Equal("A", a.AssignedSite);
        Assert.True(holds.SpotsOf("A").Min(p => SimSpace.FlatDistance(p.Position, a.Position)) < 3f, $"defender on A at {a.Position}");
        Assert.Equal("B", sim.Agents[2].AssignedSite);
    }

    [Fact]
    public void Clearance_IsSmallNextToWalls()
    {
        var g = TestWorld.Map.Grid;
        float At(int c, int r) => g.Clearance[g.Nearest(TestWorld.Cell(c, r))];
        Assert.Equal(g.CellSize, At(1, 10));              // against the outer wall
        Assert.True(At(15, 10) > At(15, 8));               // mid lane: the middle row is further from the walls
    }
}
