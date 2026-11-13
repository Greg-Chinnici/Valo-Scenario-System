using System.Numerics;
using ValorantReplay.Playback;
using Xunit;

namespace ValorantReplay.Sim.Tests;

/// <summary>A commander's orders (RoundSim.Command): the agent does what it is told instead of what the brain picks, then
/// the brain takes over again.</summary>
public class OrderTests
{
    // both teams in their spawns, all AI, nobody can see anybody
    static RoundSim Quiet(RoundSetup round = null)
    {
        var setup = TestWorld.Teams(_ => Profile.Default, _ => Profile.Default);
        for (int i = 5; i < 10; i++) setup[i].Position = TestWorld.Cell(36, 9 + i % 3);
        return new RoundSim(TestWorld.Map, setup, round ?? new RoundSetup { SpikeCarrier = 0 }, seed: 2) { SightTest = (_, _) => false };
    }

    static void RunUntil(RoundSim sim, Func<bool> done, float seconds)
    {
        float end = sim.Time + seconds;
        while (!done() && !sim.Over && sim.Time < end) sim.Step();
    }

    static bool OrderDone(RoundSim sim, int agent, out bool carriedOut)
    {
        foreach (var e in sim.Events)
            if (e.Type == SimEventType.OrderDone && e.Agent == agent) { carriedOut = e.Other == 1; return true; }
        carriedOut = false;
        return false;
    }

    [Fact]
    public void Move_GoesThere_ThenTheBrainDecides()
    {
        var sim = Quiet();
        var target = TestWorld.Cell(16, 1);              // the top lane, nowhere the brain sends anyone early
        Assert.True(sim.Command(1, OrderType.Move, target));
        var a = sim.Agents[1];
        Assert.Equal(OrderType.Move, a.Order.Type);
        RunUntil(sim, () => a.Order.Type == OrderType.None, 20f);
        Assert.Equal(OrderType.None, a.Order.Type);
        Assert.True(OrderDone(sim, 1, out bool ok) && ok);
        Assert.InRange(SimSpace.FlatDistance(a.Position, target), 0f, 3f);
        sim.Step();
        Assert.NotEqual(AgentAction.None, a.Action);    // decided again
    }

    [Fact]
    public void Hold_StaysAndLooksUntilCleared()
    {
        var sim = Quiet();
        var a = sim.Agents[6];
        var spot = a.Position;
        Assert.True(sim.Command(6, OrderType.Hold, spot, yaw: 33f));
        for (int i = 0; i < 200; i++) sim.Step();       // 10 s: the brain would have moved by now
        Assert.Equal(OrderType.Hold, a.Order.Type);
        Assert.Equal(AgentAction.Hold, a.Action);
        Assert.InRange(SimSpace.FlatDistance(a.Position, spot), 0f, 3f);
        Assert.Equal(33f, a.Yaw);
        sim.ClearOrder(6);
        Assert.Equal(OrderType.None, a.Order.Type);
        Assert.False(OrderDone(sim, 6, out _));         // cleared, not done
    }

    [Fact]
    public void Plant_OnlyTheCarrier_GoesToTheSiteAndPlants()
    {
        var sim = Quiet();
        var site = TestWorld.Map.SiteNamed("B");
        Assert.False(sim.Command(1, OrderType.Plant, site.Center));   // no spike
        Assert.False(sim.Command(5, OrderType.Plant, site.Center));   // defender
        Assert.True(sim.Command(0, OrderType.Plant, TestWorld.Cell(5, 5)));   // off any site: the nearest site's centre
        var a = sim.Agents[0];
        Assert.True(TestWorld.Map.InSite(TestWorld.Map.NearestSite(a.Order.Target), a.Order.Target));
        RunUntil(sim, () => sim.SpikeState == SpikeStatus.Planted, 40f);
        Assert.Equal(SpikeStatus.Planted, sim.SpikeState);
        Assert.True(OrderDone(sim, 0, out bool ok) && ok);
        Assert.Equal(OrderType.None, a.Order.Type);
    }

    [Fact]
    public void Defuse_GoesToTheSpike()
    {
        var site = TestWorld.Map.SiteNamed("A");
        var sim = Quiet(new RoundSetup { SpikePlanted = true, SpikePosition = site.Center, SpikeTimer = 45f });
        Assert.False(sim.Command(0, OrderType.Defuse));   // attacker
        Assert.True(sim.Command(7, OrderType.Defuse));
        foreach (int d in new[] { 5, 6, 8, 9 }) Assert.True(sim.Command(d, OrderType.Hold, sim.Agents[d].Position));   // not them
        RunUntil(sim, () => sim.Over, 45f);
        Assert.Equal(RoundEndReason.Defused, sim.EndReason);
        Assert.True(OrderDone(sim, 7, out bool ok) && ok);
    }

    [Fact]
    public void PickUp_FetchesTheDroppedSpike()
    {
        // off the cell centre, as where someone died usually is
        var sim = Quiet(new RoundSetup { SpikeDropped = true, SpikePosition = TestWorld.Cell(15, 10) + new Vector3(1.1f, 0f, -0.9f) });
        Assert.False(sim.Command(5, OrderType.PickUp));   // defender
        Assert.True(sim.Command(3, OrderType.PickUp));
        RunUntil(sim, () => sim.SpikeState == SpikeStatus.Carried, 20f);
        Assert.Equal(SpikeStatus.Carried, sim.SpikeState);
        Assert.True(OrderDone(sim, 3, out bool ok) && ok);
    }

    [Fact]
    public void PickUp_GivesUp_WhenSomeoneElseTakesIt()
    {
        var spike = TestWorld.Cell(15, 10);
        var sim = Quiet(new RoundSetup { SpikeDropped = true, SpikePosition = spike });
        Assert.True(sim.Command(3, OrderType.PickUp));
        sim.TakeControl(4);
        sim.SetExternalState(4, spike, 0f);
        sim.Step();
        Assert.Equal(4, sim.SpikeCarrier);
        sim.Step();
        Assert.True(OrderDone(sim, 3, out bool ok) && !ok);
        Assert.Equal(OrderType.None, sim.Agents[3].Order.Type);
    }

    [Fact]
    public void Orders_AreForTheLivingAi()
    {
        var sim = Quiet();
        Assert.True(sim.Command(0, OrderType.Plant, TestWorld.Map.SiteNamed("A").Center));
        Assert.True(sim.Command(5, OrderType.Move, TestWorld.Cell(30, 10)));
        sim.Step();
        sim.TakeControl(5);                             // External: orders are for the AI
        Assert.Equal(OrderType.None, sim.Agents[5].Order.Type);
        Assert.False(sim.Command(5, OrderType.Move, TestWorld.Cell(30, 10)));
        Assert.True(sim.ExternalHit(5, 0, 500f));       // the carrier dies: its order goes with it
        Assert.Equal(OrderType.None, sim.Agents[0].Order.Type);
        Assert.False(sim.Command(0, OrderType.Move, TestWorld.Cell(5, 5)));
        Assert.False(sim.Command(2, OrderType.Plant, TestWorld.Map.SiteNamed("A").Center));   // the spike is on the floor
    }

    [Fact]
    public void Command_TakesAnAgentOffTheRecording()
    {
        var replay = TestWorld.StandingReplay(
            new[] { (0, TestWorld.Cell(2, 1), 90f), (1, TestWorld.Cell(38, 19), -90f) }, new List<Kill>());
        var sim = RoundSim.FromReplay(replay, TestWorld.Map, 0.5f, _ => Profile.Default);
        Assert.Equal(0.5f, sim.SourceStart);
        Assert.Equal(AgentMode.Replay, sim.Agents[0].Mode);
        Assert.True(sim.Command(0, OrderType.Move, TestWorld.Cell(10, 1)));
        Assert.Equal(AgentMode.Ai, sim.Agents[0].Mode);
        Assert.Equal(AgentMode.Replay, sim.Agents[1].Mode);

        var allAi = RoundSim.FromReplay(replay, TestWorld.Map, 0.5f, _ => Profile.Default, followRecording: false);
        Assert.Equal(0.5f, allAi.SourceStart);           // replay time of sim time 0, recording followed or not
        Assert.Null(allAi.Source);
    }

    [Fact]
    public void Move_Nowhere_IsRefused()
    {
        var sim = Quiet();
        Assert.False(sim.Command(1, OrderType.Move, new Vector3(-500f, 0f, -500f)));
        Assert.False(sim.Command(1, OrderType.None, TestWorld.Cell(5, 5)));
    }
}
