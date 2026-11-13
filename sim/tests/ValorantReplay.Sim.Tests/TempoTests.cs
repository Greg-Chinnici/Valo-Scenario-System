using System.Numerics;
using ValorantReplay.Playback;
using Xunit;

namespace ValorantReplay.Sim.Tests;

/// <summary>Round pacing: the shared tempo sampler, learned execute timing, urgency and planting.</summary>
public class TempoTests
{
    static TempoPlayer P(bool attacking, Vector3 now, Vector3 before, bool alive = true) =>
        new TempoPlayer { Attacking = attacking, Alive = alive, WasAlive = alive, Position = now, Previous = before };

    [Fact]
    public void Sampler_SpeedExecuteAndPlant()
    {
        var map = TestWorld.Map;
        var a = map.SiteNamed("A");
        var t = new RoundTempo();
        var far = TestWorld.Cell(3, 10);
        t.Sample(5f, map, false, new[] { P(true, far + new Vector3(1.5f, 0, 0), far), P(false, far, far) });
        Assert.Equal(3f, t.AttackerSpeedAt(0), 3);           // 1.5 m in RoundTempo.Interval
        Assert.Equal(0f, t.DefenderSpeedAt(0), 3);
        Assert.Equal(-1f, t.ExecuteTime);
        t.Sample(12f, map, false, new[] { P(true, a.Center, a.Center), P(true, far, far) });
        Assert.Equal(-1f, t.ExecuteTime);                     // one attacker on site is not an execute
        t.Sample(13f, map, false, new[] { P(true, a.Center, a.Center), P(true, a.Center + new Vector3(1f, 0, 0), a.Center) });
        Assert.Equal(13f, t.ExecuteTime);
        t.Sample(20f, map, true, new[] { P(true, a.Center, a.Center) });
        t.Sample(25f, map, true, new[] { P(true, a.Center, a.Center) });
        Assert.Equal(20f, t.PlantTime);
        Assert.Equal(0, t.AttackerSamples[2]);                // nothing after the plant
    }

    [Fact]
    public void TempoModel_LearnsPlantTimesAndDrawsExecutes()
    {
        var r = TestWorld.StandingReplay(new[] { (0, TestWorld.Cell(2, 1), 90f), (1, TestWorld.Cell(14, 1), -90f) }, new List<Kill>(), duration: 60f);
        r.Rounds[0].BarrierTime = 5f;
        r.Rounds[0].PlantTime = 45f;
        var m = TempoModel.FromReplays(new[] { r });
        Assert.Equal(new[] { 40f }, m.PlantTimes);
        Assert.False(m.Known);                                // too few plants to have an opinion
        for (int i = 0; i < 5; i++) m.PlantTimes.Add(40f);
        Assert.Equal(40f - TempoModel.ExecuteLead, m.DrawExecute(new Random(1)));
        var sim = new RoundSim(TestWorld.Map.WithLearned(null, m), TestWorld.Teams(_ => Profile.Default, _ => Profile.Default));
        Assert.Equal(sim.Options.RoundTime - (40f - TempoModel.ExecuteLead), sim.Director(0).ExecuteAt, 3);
        var rush = new RoundSim(TestWorld.Map.WithLearned(null, m), TestWorld.Teams(_ => Profile.Default, _ => Profile.Default), new RoundSetup { Plan = AttackPlan.Rush });
        Assert.True(float.IsPositiveInfinity(rush.Director(0).ExecuteAt));   // a chosen plan keeps its own timing
    }

    [Fact]
    public void Urgency_RaisesAggression()
    {
        var p = new Profile(0.5f, 0.2f, 0.5f);
        Assert.Equal(0.2f, UtilityBrain.EffectiveAggression(new Situation(), p), 3);
        Assert.Equal(1f, UtilityBrain.EffectiveAggression(new Situation { Urgency = 1f }, p), 3);
    }

    // passive attackers with 35 s left: urgency makes them go for the plant instead of running the clock out
    [Fact]
    public void Urgency_PassiveAttackersStillGoIn()
    {
        int TimeOuts(SimOptions o) => SimBatch.Run(i => new RoundSim(TestWorld.Map, TestWorld.Teams(_ => new Profile(0.5f, 0f, 0.5f), _ => Profile.Default),
            new RoundSetup { Remaining = 35f, Plan = AttackPlan.Slow }, o, i), 200, threads: 0).Reasons.GetValueOrDefault(RoundEndReason.TimeExpired);
        int urgent = TimeOuts(new SimOptions()), calm = TimeOuts(new SimOptions { UrgencyMargin = -1000f });
        Assert.True(urgent < calm, $"time-outs with urgency {urgent}, without {calm}");
    }

    // low cohesion spreads attackers wide around the site; the carrier still goes to the plant spots
    [Fact]
    public void Carrier_PlantsDespiteASpreadOutTeam()
    {
        var b = SimBatch.Run(i => TestWorld.Round(_ => new Profile(0.5f, 0.5f, 0f), _ => Profile.Default, i), 300, threads: 0);
        Assert.True(b.Reasons.GetValueOrDefault(RoundEndReason.TimeExpired) < 300 * 0.03f, string.Join(", ", b.Reasons.Select(kv => $"{kv.Key} {kv.Value}")));
    }
}
