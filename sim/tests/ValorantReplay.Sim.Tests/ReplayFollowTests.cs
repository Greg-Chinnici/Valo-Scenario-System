using ValorantReplay.Playback;
using Xunit;

namespace ValorantReplay.Sim.Tests;

/// <summary>Replay-driven agents: they reproduce the recording until they perceive something that did not happen in it.</summary>
public class ReplayFollowTests
{
    // top lane: attacker 0 kills defender 1 (30 m apart, facing each other) at t = 2. Bottom right: attacker 2 on B and
    // defender 3, facing each other, no kill. The pairs are 75+ m apart with walls in between: no sight, no sound.
    static VrpReplay Recording() => TestWorld.StandingReplay(
        new[]
        {
            (0, TestWorld.Cell(2, 1), 90f), (1, TestWorld.Cell(14, 1), -90f),
            (0, TestWorld.Cell(27, 19), 90f), (1, TestWorld.Cell(38, 19), -90f),
        },
        new List<Kill> { new Kill { Time = 2f, Killer = 0, Victim = 1, Round = 0, Weapon = "AssaultRifle_AK" } });

    static RoundSim Start(VrpReplay r, float at = 0.5f) => RoundSim.FromReplay(r, TestWorld.Map, at, _ => Profile.Default);

    [Fact]
    public void NoTakeover_ReproducesTheRecording()
    {
        var sim = Start(Recording());
        while (sim.Time < 2f) sim.Step();
        var k = Assert.Single(sim.Kills);
        Assert.Equal((0, 1, false), (k.Killer, k.Victim, k.Simulated));
        Assert.Equal(2f, sim.SourceStart + k.Time, 1);
        Assert.All(sim.Agents, a => Assert.True(a.DivergedAt < 0f));
        Assert.All(sim.Agents, a => Assert.Equal(AgentMode.Replay, a.Mode));
    }

    [Fact]
    public void Takeover_OnlyThoseWhoNoticeDiverge()
    {
        var sim = Start(Recording());
        sim.TakeControl(2);                         // the user takes the attacker on B and stands still
        while (sim.Time < 1.6f) sim.Step();
        Assert.Equal(AgentMode.Ai, sim.Agents[3].Mode);          // the defender facing them sees it at once
        Assert.InRange(sim.Agents[3].DivergedAt, 0f, 0.2f);
        Assert.Equal(AgentMode.Replay, sim.Agents[0].Mode);      // the top lane never notices
        Assert.Contains(sim.Kills, k => k.Victim == 1 && k.Killer == 0 && !k.Simulated);   // their recorded kill still happens
        Assert.Contains(sim.Events, e => e.Type == SimEventType.Diverged && e.Agent == 3);
    }

    [Fact]
    public void Takeover_OfTheKiller_SavesTheVictim()
    {
        var sim = Start(Recording());
        sim.TakeControl(0);                         // the user takes the top-lane attacker and looks away
        while (sim.Time < 2.5f)
        {
            sim.SetExternalState(0, TestWorld.Cell(2, 1), -90f);
            sim.Step();
        }
        Assert.DoesNotContain(sim.Kills, k => !k.Simulated && k.Victim == 1);
        Assert.True(sim.Agents[1].DivergedAt >= 0f);             // it saw a player who is not doing what the recording says
    }

    [Fact]
    public void AllAi_StartsFromTheSnapshot()
    {
        var r = Recording();
        var sim = RoundSim.FromReplay(r, TestWorld.Map, 0.5f, _ => Profile.Default, followRecording: false, seed: 7);
        Assert.Null(sim.Source);
        Assert.All(sim.Agents, a => Assert.Equal(AgentMode.Ai, a.Mode));
        Assert.Equal(TestWorld.Cell(14, 1).X, sim.Agents[1].Position.X, 3);
        var result = sim.RunToEnd();
        Assert.True(result.Kills.Count > 0);
        Assert.All(result.Kills, k => Assert.True(k.Simulated));
    }

    [Fact]
    public void ProfileFitter_ReadsTheRecording()
    {
        var fitted = ProfileFitter.Fit(Recording());
        Assert.Equal(4, fitted.Length);
        Assert.Equal(1, fitted[0].Tally.FirstKills);
        Assert.Equal(1, fitted[1].Tally.FirstDeaths);
        Assert.True(fitted[0].Profile.Aggression > 0.5f && fitted[1].Profile.Aggression > 0.5f);   // both took the opening duel
        Assert.True(fitted[2].Profile.Aggression < 0.5f);
        Assert.Equal(PlayStyle.Lurker, fitted[0].Profile.Style);  // its only teammate is 78 m away
        Assert.True(fitted[0].TeammateDistance > 70f);
    }
}
