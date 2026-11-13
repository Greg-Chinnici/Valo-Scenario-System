using System.Numerics;
using ValorantReplay.Playback;
using Xunit;

namespace ValorantReplay.Sim.Tests;

/// <summary>Where players look, learned per patch, side and movement.</summary>
public class LookModelTests
{
    static readonly Vector3 Spot = new Vector3(10.5f, 1f, 10.5f);

    static void Add(LookModel m, float yaw, int count, bool attacking = false, bool moving = false, Vector3? at = null)
    {
        for (int i = 0; i < count; i++) m.Add(at ?? Spot, yaw, attacking, moving);
    }

    [Fact]
    public void TooFewSamples_NoOpinion()
    {
        var m = new LookModel();
        Assert.False(m.TryLook(Spot, false, null, 0.5f, out _));
        Add(m, 90f, 5);
        Assert.False(m.TryLook(Spot, false, null, 0.5f, out _));
    }

    [Fact]
    public void Quantile_PicksAnglesInProportion()
    {
        var m = new LookModel();
        Add(m, 45f, 30);        // the angle checked most
        Add(m, 200f, 10);
        int near45 = 0;
        for (int i = 0; i < 100; i++)
        {
            Assert.True(m.TryLook(Spot, false, null, (i + 0.5f) / 100f, out float yaw));
            if (Math.Abs(Measurements.DeltaYaw(yaw, 45f)) < 12f) near45++;
            else Assert.True(Math.Abs(Measurements.DeltaYaw(yaw, 200f)) < 12f, $"{yaw}");
        }
        Assert.InRange(near45, 70, 80);
    }

    [Fact]
    public void SidesAndMovementAreSeparate_AndNeighboursCountHalf()
    {
        var m = new LookModel();
        Add(m, 0f, 30, attacking: true);
        Add(m, 180f, 30, attacking: false);
        Assert.True(m.TryLook(Spot, true, null, 0.5f, out float atk));
        Assert.True(m.TryLook(Spot, false, null, 0.5f, out float def));
        Assert.True(Math.Abs(Measurements.DeltaYaw(atk, 0f)) < 12f && Math.Abs(Measurements.DeltaYaw(def, 180f)) < 12f, $"{atk} {def}");
        Assert.False(m.TryLook(Spot, false, Vector3.UnitX, 0.5f, out _));        // nobody moved here
        var next = Spot + new Vector3(LookModel.CellSize, 0f, 0f);
        Assert.True(m.TryLook(next, true, null, 0.5f, out _));                    // 30 next door count as 15 >= MinSamples
        Assert.False(m.TryLook(next + new Vector3(LookModel.CellSize, 0f, 0f), true, null, 0.5f, out _));
    }

    [Fact]
    public void Moving_StaysWithinTheConeOfTheWalk()
    {
        var m = new LookModel();
        Add(m, 90f, 20, moving: true);      // walking east, checking east
        Add(m, 270f, 20, moving: true);     // walking west, checking west
        for (float u = 0.05f; u < 1f; u += 0.1f)
        {
            Assert.True(m.TryLook(Spot, false, new Vector3(1f, 0f, 0f), u, out float east));
            Assert.True(m.TryLook(Spot, false, new Vector3(-1f, 0f, 0f), u, out float west));
            Assert.True(Math.Abs(Measurements.DeltaYaw(east, 90f)) < 12f && Math.Abs(Measurements.DeltaYaw(west, 270f)) < 12f, $"{east} {west}");
        }
    }

    [Fact]
    public void FromReplay_LearnsWhereStillPlayersFace()
    {
        var r = TestWorld.StandingReplay(new[] { (0, TestWorld.Cell(2, 1), 90f), (1, TestWorld.Cell(14, 1), -90f) }, new List<Kill>());
        var m = LookModel.FromReplays(new[] { r });
        Assert.True(m.TryLook(TestWorld.Cell(2, 1), true, null, 0.5f, out float a));
        Assert.True(m.TryLook(TestWorld.Cell(14, 1), false, null, 0.5f, out float d));
        Assert.True(Math.Abs(Measurements.DeltaYaw(a, 90f)) < 12f && Math.Abs(Measurements.DeltaYaw(d, -90f)) < 12f, $"{a} {d}");
        Assert.False(m.TryLook(TestWorld.Cell(2, 1), false, null, 0.5f, out _));  // a defender never stood there
    }
}
