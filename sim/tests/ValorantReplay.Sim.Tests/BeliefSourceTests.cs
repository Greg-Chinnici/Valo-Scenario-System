using System.Numerics;
using ValorantReplay.Playback;
using Xunit;

namespace ValorantReplay.Sim.Tests;

/// <summary>An outside knowledge model (Unity's blackboard) drives decisions; the sim's own memory is not consulted.</summary>
public class BeliefSourceTests
{
    sealed class Fixed : IBeliefSource
    {
        public Func<int, int, (bool, Vector3, float)> Answer = (_, _) => (false, default, 0f);
        public int Calls;
        public bool Believes(int team, int enemy, out Vector3 position, out float certainty)
        {
            Calls++;
            var (ok, p, c) = Answer(team, enemy);
            position = p;
            certainty = c;
            return ok;
        }
    }

    // attacker 0 alone on B facing defender 1, all AI, nobody can see anybody
    static RoundSim Duel(IBeliefSource source)
    {
        var setup = new List<AgentSetup>
        {
            new AgentSetup { Team = 0, Attacking = true, Position = TestWorld.Cell(27, 17), Yaw = 90f, Name = "atk" },
            new AgentSetup { Team = 1, Position = TestWorld.Cell(35, 17), Yaw = -90f, Name = "def" },
        };
        var sim = new RoundSim(TestWorld.Map, setup, new RoundSetup { TargetSite = "B" }, seed: 3)
        {
            SightTest = (_, _) => false,
            BeliefSource = source,
        };
        return sim;
    }

    [Fact]
    public void NoBelief_NoThreat()
    {
        var src = new Fixed();
        var sim = Duel(src);
        sim.Step();
        Assert.True(src.Calls > 0);
        var a = sim.Agents[1];
        Assert.Equal(0f, a.Scores[2]);   // no peek: nobody believed anywhere
        Assert.Equal(0f, a.Scores[3]);   // no fall back
    }

    [Fact]
    public void BlackboardThreat_DrivesThePeek_ThoughNoOneWasSeenOrHeard()
    {
        var src = new Fixed();
        var sim = Duel(src);
        var atk = sim.Agents[0].Position;
        src.Answer = (team, enemy) => team == 1 && enemy == 0 ? (true, atk, 1f) : (false, default, 0f);
        sim.Step();
        var d = sim.Agents[1];
        Assert.False(d.Beliefs.TryGet(0, out _));      // its own memory is empty
        Assert.True(d.Scores[2] > 0f);                 // yet it weighs peeking the believed attacker
        Assert.Equal(0f, sim.Agents[0].Scores[2]);     // the attackers' board knows nothing
    }

    [Fact]
    public void Hits_AndTaskStarts_AreEvents()
    {
        var site = TestWorld.Map.SiteNamed("B");
        var setup = TestWorld.Teams(_ => Profile.Default, _ => Profile.Default);
        setup[0].Position = site.Center;
        setup[0].Mode = AgentMode.External;
        var sim = new RoundSim(TestWorld.Map, setup, new RoundSetup { SpikeCarrier = 0 }, seed: 1) { SightTest = (_, _) => false };
        sim.Step();
        Assert.True(sim.BeginPlant(0));
        Assert.Contains(sim.Events, e => e.Type == SimEventType.PlantStarted && e.Agent == 0);
        sim.ExternalHit(0, 5, 30f);
        Assert.Contains(sim.Events, e => e.Type == SimEventType.Hit && e.Agent == 5 && e.Other == 0);
    }
}
