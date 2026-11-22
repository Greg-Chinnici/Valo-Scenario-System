using Xunit;

namespace ValorantReplay.Sim.Tests;

/// <summary>The event log of what the AI meant to do: decisions, objectives, callouts and the plans' calls.</summary>
public class EventLogTests
{
    static RoundSim Played(int seed)
    {
        var sim = TestWorld.Round(_ => Profile.Default, _ => Profile.Default, seed);
        for (int i = 0; i < 2400 && !sim.Over; i++) sim.Step();
        return sim;
    }

    [Fact]
    public void Decisions_AreLoggedWhenTheyChange()
    {
        var sim = Played(3);
        var decided = sim.Events.Where(e => e.Type == SimEventType.Decided).ToList();
        Assert.NotEmpty(decided);
        foreach (var g in decided.GroupBy(e => e.Agent))
        {
            var list = g.ToList();
            Assert.All(list, e => Assert.Equal(sim.Agents[g.Key].Team, e.Team));
            for (int i = 1; i < list.Count; i++)
                Assert.False(list[i].Action == list[i - 1].Action && list[i].Swing == list[i - 1].Swing && list[i].Other == list[i - 1].Other,
                    $"agent {g.Key} logged the same decision twice at {list[i].Time}");
        }
        // a peek or a trade is about an enemy, and only those carry a swing
        Assert.All(decided.Where(e => e.Action == AgentAction.Peek), e => Assert.True(e.Other >= 0 && sim.Agents[e.Other].Team != e.Team));
        Assert.All(decided.Where(e => e.Action != AgentAction.Peek && e.Action != AgentAction.Trade), e => Assert.Equal(PeekStyle.None, e.Swing));
        // each agent's first decision comes with its objective
        foreach (var first in decided.GroupBy(e => e.Agent).Select(g => g.First()))
            Assert.Contains(sim.Events, e => e.Type == SimEventType.Objective && e.Agent == first.Agent && e.Time <= first.Time);
    }

    [Fact]
    public void Callouts_ReachTheBoardOnlyAfterTheyWereMade()
    {
        var sim = Played(5);
        var heard = sim.Events.Where(e => e.Type == SimEventType.CalloutHeard).ToList();
        Assert.NotEmpty(heard);
        foreach (var h in heard)
        {
            Assert.Equal(sim.Agents[h.Agent].Team, h.Team);
            Assert.Contains(sim.Events, m => m.Type == SimEventType.CalloutMade && m.Agent == h.Agent && m.Other == h.Other && m.Time <= h.Time);
        }
        Assert.All(sim.Events.Where(e => e.Type == SimEventType.CalloutMade || e.Type == SimEventType.CalloutDropped),
            e => Assert.NotEqual(sim.Agents[e.Agent].Team, sim.Agents[e.Other].Team));
    }

    [Fact]
    public void TheAttack_LogsItsExecute_Once()
    {
        var sim = Played(1);
        var exec = sim.Events.Where(e => e.Type == SimEventType.Executed).ToList();
        Assert.Equal(sim.Director(sim.AttackingTeam).Executing ? 1 : 0, exec.Count);
        Assert.All(exec, e =>
        {
            Assert.Equal(sim.AttackingTeam, e.Team);
            Assert.Equal(sim.Director(sim.AttackingTeam).TargetSite, e.Site);
            Assert.Equal(-1, e.Agent);
        });
    }
}
