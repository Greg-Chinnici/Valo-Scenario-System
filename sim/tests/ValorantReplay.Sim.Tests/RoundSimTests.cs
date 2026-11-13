using System.Diagnostics;
using System.Numerics;
using ValorantReplay.Playback;
using Xunit;
using Xunit.Abstractions;

namespace ValorantReplay.Sim.Tests;

/// <summary>Whole rounds on the ASCII map: they finish, are deterministic, and each knob moves the numbers it should.</summary>
public class RoundSimTests
{
    readonly ITestOutputHelper log;
    public RoundSimTests(ITestOutputHelper log) => this.log = log;

    const int Rounds = 1000;
    static readonly int[] Attackers = { 0, 1, 2, 3, 4 }, Defenders = { 5, 6, 7, 8, 9 };

    BatchResult Batch(Func<int, Profile> atk, Func<int, Profile> def, int rounds = Rounds, int seed = 0, AttackPlan plan = AttackPlan.Default)
    {
        var sw = Stopwatch.StartNew();
        var b = SimBatch.Run(i => TestWorld.Round(atk, def, seed + i, plan), rounds);
        log.WriteLine($"{rounds} rounds in {sw.ElapsedMilliseconds} ms: attackers win {b.AttackerWinRate:P0}, planted {b.Planted}, " +
            $"mean {b.MeanDuration:0.0}s, {string.Join(", ", b.Reasons.Select(kv => $"{kv.Key} {kv.Value}"))}");
        log.WriteLine($"  attackers {b.Combined(Attackers)}");
        log.WriteLine($"  defenders {b.Combined(Defenders)}");
        return b;
    }

    // Balance is not calibrated: with equal profiles this map comes out around 85% for the attack (no utility, and holders
    // watch one direction while attackers walk at them). These tests check that nothing is degenerate and that each knob
    // moves the numbers it should; matching real win rates is the job of profiles fitted from replays.
    [Fact]
    public void Rounds_Finish_BothSidesWin_SpikeGetsPlanted()
    {
        var b = Batch(_ => Profile.Default, _ => Profile.Default);
        Assert.Equal(Rounds, b.Rounds);
        Assert.InRange(b.AttackerWinRate, 0.05f, 0.95f);
        Assert.True(b.Planted > Rounds / 10, $"planted {b.Planted}");
        Assert.True(b.Reasons.ContainsKey(RoundEndReason.Elimination));
        Assert.True(b.MeanDuration < 100f);
        Assert.True(b.Reasons.ContainsKey(RoundEndReason.Defused));
        Assert.True(b.Combined(Attackers).Kills + b.Combined(Defenders).Kills >= Rounds * 5);   // rounds are fights
        // on this small attack-sided map urgent defenders contest every planted spike to the end (no detonations);
        // Defender_TooFarToDefuse_Saves covers the detonation path
    }

    [Fact]
    public void Defender_TooFarToDefuse_Saves()
    {
        var site = TestWorld.Map.SiteNamed("A").Center;
        var setup = new List<AgentSetup>
        {
            new AgentSetup { Team = 0, Attacking = true, Position = TestWorld.Cell(2, 10), Yaw = 90f },
            new AgentSetup { Team = 1, Position = TestWorld.Cell(36, 10), Yaw = -90f },     // ~25 m away: 12 s to reach and defuse
        };
        var sim = new RoundSim(TestWorld.Map, setup, new RoundSetup { SpikePlanted = true, SpikePosition = site, SpikeTimer = 6f }, seed: 1);
        float start = Vector3.Distance(sim.Agents[1].Position, site);
        var r = sim.RunToEnd();
        Assert.Equal(RoundEndReason.Detonated, r.Reason);
        Assert.True(sim.Agents[1].Alive);
        Assert.True(Vector3.Distance(sim.Agents[1].Position, site) >= start - 1f, "the defender went for the spike instead of saving");
    }

    [Fact]
    public void SameSeed_SameRound()
    {
        var a = TestWorld.Round(_ => Profile.Default, _ => Profile.Default, 42).RunToEnd();
        var b = TestWorld.Round(_ => Profile.Default, _ => Profile.Default, 42).RunToEnd();
        Assert.Equal(a.Winner, b.Winner);
        Assert.Equal(a.Duration, b.Duration);
        Assert.Equal(a.Kills.Select(k => (k.Time, k.Killer, k.Victim)), b.Kills.Select(k => (k.Time, k.Killer, k.Victim)));
    }

    [Fact]
    public void Skill_WinsRounds()
    {
        var even = Batch(_ => Profile.Default, _ => Profile.Default, 500);
        var b = Batch(_ => Profile.Default.With(skill: 0.9f), _ => Profile.Default.With(skill: 0.2f));
        var c = Batch(_ => Profile.Default.With(skill: 0.2f), _ => Profile.Default.With(skill: 0.9f));
        Assert.True(b.AttackerWinRate > even.AttackerWinRate, $"skilled attackers win {b.AttackerWinRate:P0}, even teams {even.AttackerWinRate:P0}");
        Assert.True(c.AttackerWinRate < even.AttackerWinRate - 0.3f, $"unskilled attackers win {c.AttackerWinRate:P0}, even teams {even.AttackerWinRate:P0}");
        Assert.True(b.AttackerWinRate - c.AttackerWinRate > 0.5f);
    }

    [Fact]
    public void Aggression_TakesOpeningDuels()
    {
        // attackers 0-1 aggressive, 2-4 passive, same skill: the aggressive pair should open far more often
        var b = Batch(i => Profile.Default.With(aggression: i < 2 ? 0.95f : 0.05f), _ => Profile.Default);
        var hot = b.Combined(new[] { 0, 1 });
        var cold = b.Combined(new[] { 2, 3, 4 });
        Assert.True(hot.OpeningRate > 1.5f * cold.OpeningRate, $"aggressive {hot.OpeningRate:P0} vs passive {cold.OpeningRate:P0}");
        Assert.True(hot.FirstDeathRate > cold.FirstDeathRate);
    }

    [Fact]
    public void Cohesion_GetsDeathsTraded()
    {
        var tight = Batch(_ => Profile.Default.With(cohesion: 0.95f), _ => Profile.Default);
        var loose = Batch(_ => Profile.Default.With(cohesion: 0.05f), _ => Profile.Default);
        float t = tight.Combined(Attackers).TradedRate, l = loose.Combined(Attackers).TradedRate;
        Assert.True(t > l + 0.05f, $"traded: cohesive {t:P0} vs loose {l:P0}");
    }

    [Fact]
    public void Rush_EndsSooner()
    {
        var rush = Batch(_ => Profile.Default, _ => Profile.Default, 300, plan: AttackPlan.Rush);
        var slow = Batch(_ => Profile.Default, _ => Profile.Default, 300, plan: AttackPlan.Slow);
        Assert.True(rush.MeanDuration + 5f < slow.MeanDuration, $"rush {rush.MeanDuration:0.0}s vs slow {slow.MeanDuration:0.0}s");
    }

    [Fact]
    public void Defuse_WhenAttackersAreDead()
    {
        // spike planted on A, one defender next to it, attackers all dead: the defender defuses in 7 s
        var setup = TestWorld.Teams(_ => Profile.Default, _ => Profile.Default);
        foreach (var s in setup.Take(5)) s.Alive = false;
        var spike = TestWorld.Cell(28, 3);
        setup[5].Position = spike + new Vector3(1f, 0f, 0f);
        var sim = new RoundSim(TestWorld.Map, setup, new RoundSetup { SpikePlanted = true, SpikePosition = spike, SpikeTimer = 30f });
        var r = sim.RunToEnd();
        Assert.Equal(RoundEndReason.Defused, r.Reason);
        Assert.Equal(1, r.Winner);
        Assert.InRange(r.Duration, 7f, 9f);
    }

    [Fact]
    public void External_CanShootAndPlant()
    {
        // defenders frozen (External, never driven) so nothing shoots back
        var setup = TestWorld.Teams(_ => Profile.Default, _ => Profile.Default);
        foreach (var s in setup.Skip(5)) s.Mode = AgentMode.External;
        var sim = new RoundSim(TestWorld.Map, setup, new RoundSetup { SpikeCarrier = 0 });
        sim.TakeControl(0);
        var target = sim.Agents[5];
        target.Position = TestWorld.Cell(32, 10);
        var me = TestWorld.Cell(24, 10);                      // 20 m away through the mid door
        sim.SetExternalState(0, me, 90f);
        var eye = me + new Vector3(0f, 0.65f, 0f);
        Assert.Equal(-1, sim.ExternalFire(0, new Vector3(0f, 0f, 1f)));   // shooting at the wall
        int hits = 0;
        for (int i = 0; i < 40 && target.Alive; i++)
        {
            sim.Step();
            if (sim.ExternalFire(0, target.Eye + new Vector3(0f, 0.1f, 0f) - eye) == 5) hits++;
        }
        Assert.False(target.Alive);
        Assert.Equal(1, hits);         // 160 to the head through 50 armor
        Assert.Contains(sim.Kills, k => k.Killer == 0 && k.Victim == 5 && k.Simulated);

        Assert.False(sim.BeginPlant(0));                       // not on a site
        sim.SetExternalState(0, TestWorld.Cell(28, 3), 0f);
        Assert.True(sim.BeginPlant(0));
        for (int i = 0; i < 81 && sim.SpikeState != SpikeStatus.Planted; i++)
        {
            sim.SetExternalState(0, TestWorld.Cell(28, 3), 0f);
            sim.Step();
        }
        Assert.Equal(SpikeStatus.Planted, sim.SpikeState);
        Assert.InRange(sim.Time, 4f, 6.5f);
    }
}
