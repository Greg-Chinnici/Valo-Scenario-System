using System.Numerics;
using ValorantReplay.Playback;
using Xunit;

namespace ValorantReplay.Sim.Tests;

/// <summary>The hooks a host (Unity) uses to play one agent itself: its own sight test, hits decided outside the sim,
/// planting with a key that can be let go.</summary>
public class ExternalAgentTests
{
    // the bottom-right pair of ReplayFollowTests: attacker 2 on B facing defender 3, 11 cells apart, no kill recorded
    static VrpReplay Recording() => TestWorld.StandingReplay(
        new[]
        {
            (0, TestWorld.Cell(2, 1), 90f), (1, TestWorld.Cell(14, 1), -90f),
            (0, TestWorld.Cell(27, 19), 90f), (1, TestWorld.Cell(38, 19), -90f),
        },
        new List<Kill>());

    static RoundSim Start() => RoundSim.FromReplay(Recording(), TestWorld.Map, 0.5f, _ => Profile.Default);

    [Fact]
    public void SightTest_ReplacesTheGrid()
    {
        var sim = Start();
        sim.SightTest = (_, _) => false;            // e.g. a smoke between everyone
        sim.TakeControl(2);
        while (sim.Time < 1f) sim.Step();
        Assert.False(sim.Sees(3, 2));
        Assert.Equal(AgentMode.Replay, sim.Agents[3].Mode);   // nothing noticed, so still on the recording

        var open = Start();
        open.TakeControl(2);
        while (open.Time < 1f) open.Step();
        Assert.True(open.Sees(3, 2));
    }

    [Fact]
    public void ExternalHit_DamagesThroughArmor_AndKills()
    {
        var sim = Start();
        sim.SightTest = (_, _) => false;            // keep the defender from shooting back
        sim.TakeControl(2);
        sim.Step();
        var d = sim.Agents[3];
        Assert.False(sim.ExternalHit(2, 3, 39f));
        Assert.Equal(AgentMode.Ai, d.Mode);         // a hit is not in the recording
        Assert.True(d.Health < 100f && d.Armor < 50f);
        Assert.False(sim.ExternalHit(2, 2, 500f));  // not an enemy
        Assert.False(sim.ExternalHit(0, 3, 500f));  // not External
        Assert.True(sim.ExternalHit(2, 3, 160f));
        Assert.False(d.Alive);
        var k = Assert.Single(sim.Kills, x => x.Victim == 3);
        Assert.Equal((2, true), (k.Killer, k.Simulated));
    }

    [Fact]
    public void ExternalGunshot_IsHeard()
    {
        var sim = Start();
        sim.SightTest = (_, _) => false;
        sim.TakeControl(2);
        sim.Step();
        sim.ExternalGunshot(2);
        Assert.True(sim.Agents[3].Beliefs.TryGet(2, out var s));
        Assert.Equal(Cue.Heard, s.Cue);
        Assert.Equal(AgentMode.Replay, sim.Agents[3].Mode);   // 27.5 m: past footstep range, heard but not acted on
    }

    [Fact]
    public void Plant_CanBeCancelled()
    {
        var site = TestWorld.Map.SiteNamed("B");
        var setup = TestWorld.Teams(_ => Profile.Default, _ => Profile.Default);
        setup[0].Position = site.Center;
        setup[0].Mode = AgentMode.External;
        for (int i = 5; i < 10; i++) setup[i].Position = TestWorld.Cell(36, 9 + i % 3);   // in their spawn, out of sight
        var sim = new RoundSim(TestWorld.Map, setup, new RoundSetup { SpikeCarrier = 0 }, seed: 1);
        sim.SightTest = (_, _) => false;
        Assert.True(sim.BeginPlant(0));
        for (int i = 0; i < 20; i++) { sim.SetExternalState(0, site.Center, 0f); sim.Step(); }
        Assert.InRange(sim.Agents[0].TaskSeconds, 0.9f, 1.1f);
        sim.CancelTask(0);
        Assert.Equal(0f, sim.Agents[0].TaskSeconds);
        Assert.True(sim.BeginPlant(0));
        while (sim.SpikeState != SpikeStatus.Planted && sim.Time < 10f) { sim.SetExternalState(0, site.Center, 0f); sim.Step(); }
        Assert.Equal(SpikeStatus.Planted, sim.SpikeState);
        Assert.InRange(sim.Time, 5f, 5.2f);         // 1 s, cancelled, then the full 4 s
    }
}
