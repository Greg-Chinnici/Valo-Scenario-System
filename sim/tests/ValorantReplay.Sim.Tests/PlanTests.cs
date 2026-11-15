using System.Numerics;
using Xunit;

namespace ValorantReplay.Sim.Tests;

/// <summary>Team plans: choosing them, copying them per round, the built-in fake and late call, and plans written outside
/// the simulation's assembly (as Unity code would).</summary>
public class PlanTests
{
    // ---------------------------------------------------------------- plans from another assembly

    sealed class CampAttack : AttackPlan
    {
        public int Updates, Objectives;
        protected override float ExecuteTime(PlanContext c) => 0f;
        protected override void Update(PlanContext c) { Updates++; base.Update(c); }
        protected override Vector3 Objective(PlanContext c, SimAgent a, out float radius)
        {
            Objectives++;
            radius = 4f;
            return c.Map.AttackerSpawn;
        }
    }

    sealed class StackADefense : DefensePlan
    {
        protected override void Begin(PlanContext c)
        {
            foreach (var a in c.Members) a.AssignedSite = "A";
        }
        protected override Vector3 Objective(PlanContext c, SimAgent a, out float radius) => HoldSite(c, a, out radius);
    }

    [Fact]
    public void Plans_FromOutsideTheAssembly_Run()
    {
        var camp = new CampAttack();
        var sim = new RoundSim(TestWorld.Map, TestWorld.Teams(_ => Profile.Default, _ => Profile.Default),
            new RoundSetup { Attack = camp, Defense = new StackADefense() }, seed: 1);
        for (int i = 0; i < 100; i++) sim.Step();
        var played = (CampAttack)sim.Director(0).Attack;
        Assert.Equal("Camp", sim.Director(0).PlanName);
        Assert.Equal("StackA", sim.Director(1).PlanName);
        Assert.True(played.Updates > 0 && played.Objectives > 0);
        Assert.Equal(0, camp.Updates);                       // the template itself is not played
        Assert.All(sim.Agents.Where(a => !a.Attacking), a => Assert.Equal("A", a.AssignedSite));
    }

    // ---------------------------------------------------------------- choosing

    [Fact]
    public void Picker_WithOnePlan_DrawsNothing()
    {
        var picker = new PlanPicker<AttackPlan> { { new RushAttack(), 1f } };
        Random a = new(7), b = new(7);
        Assert.IsType<RushAttack>(picker.Pick(a));
        Assert.Equal(b.Next(), a.Next());
    }

    [Fact]
    public void Picker_FollowsWeights()
    {
        var picker = new PlanPicker<AttackPlan> { { new DefaultAttack(), 3f }, { new FakeAttack(), 1f }, { new RushAttack(), 0f } };
        var rng = new Random(1);
        int fakes = 0, rushes = 0;
        for (int i = 0; i < 10000; i++)
        {
            var p = picker.Pick(rng);
            if (p is FakeAttack) fakes++;
            if (p is RushAttack) rushes++;
        }
        Assert.InRange(fakes / 10000f, 0.23f, 0.27f);
        Assert.Equal(0, rushes);
    }

    [Fact]
    public void Plans_ByName()
    {
        Assert.IsType<FakeAttack>(Plans.Attack("fake"));
        Assert.IsType<LateCallAttack>(Plans.Attack("LateCall"));
        Assert.IsType<DefaultDefense>(Plans.Defense("default"));
        var mix = Plans.AttackMix("default=3, fake");
        Assert.Equal(new[] { 3f, 1f }, mix.Select(p => p.Weight));
        Assert.Throws<ArgumentException>(() => Plans.Attack("yolo"));
    }

    [Fact]
    public void OptionsMix_DrawsAPlanPerRound()
    {
        var options = new SimOptions { AttackPlans = Plans.AttackMix("rush,slow") };
        var names = Enumerable.Range(0, 40)
            .Select(seed => new RoundSim(TestWorld.Map, TestWorld.Teams(_ => Profile.Default, _ => Profile.Default), null, options, seed).Director(0).PlanName)
            .ToHashSet();
        Assert.Equal(new HashSet<string> { "Rush", "Slow" }, names);
    }

    [Fact]
    public void Template_IsCopiedForEachRound()
    {
        var template = new FakeAttack { Fakers = 1 };
        var setup = new RoundSetup { Attack = template, TargetSite = "B" };
        var one = new RoundSim(TestWorld.Map, TestWorld.Teams(_ => Profile.Default, _ => Profile.Default), setup, seed: 1);
        var two = new RoundSim(TestWorld.Map, TestWorld.Teams(_ => Profile.Default, _ => Profile.Default), setup, seed: 2);
        Assert.NotSame(one.Director(0).Attack, two.Director(0).Attack);
        Assert.Equal("A", ((FakeAttack)one.Director(0).Attack).FakeSite);
        Assert.Single(((FakeAttack)one.Director(0).Attack).FakerAgents);
        Assert.Null(template.FakeSite);
        Assert.Equal("", template.TargetSite);
    }

    // ---------------------------------------------------------------- built-in plans

    [Fact]
    public void Fake_ShowsOnTheFakeSite_ThenExecutesOnTheTarget()
    {
        var sim = new RoundSim(TestWorld.Map, TestWorld.Teams(_ => Profile.Default, _ => Profile.Default),
            new RoundSetup { Attack = new FakeAttack { SwitchAfter = 8f }, TargetSite = "B" }, seed: 4);
        var fake = (FakeAttack)sim.Director(0).Attack;
        Assert.Equal("A", fake.FakeSite);
        Assert.Equal(55f, fake.ExecuteAt);                   // no TempoModel on the test map: fixed timing
        Assert.False(sim.Director(0).Committed);

        float fakeAt = -1f, switchAt = -1f;
        while (!sim.Over && switchAt < 0f)
        {
            sim.Step();
            if (fakeAt < 0f && fake.Faking) { fakeAt = sim.Remaining; Assert.True(sim.Director(0).Committed); Assert.False(fake.Executing); }
            if (fake.Executing) switchAt = sim.Remaining;
        }
        Assert.True(fakeAt <= 55f && fakeAt > 54.8f, $"fake began at {fakeAt:0.0} s left");
        Assert.True(switchAt < fakeAt && switchAt >= fakeAt - 8.1f, $"switched at {switchAt:0.0} s left, fake at {fakeAt:0.0}");
        Assert.Equal("B", fake.TargetSite);

        // the fakers went towards A, the others waited on the way to B
        var a = TestWorld.Map.SiteNamed("A").Center;
        var b = TestWorld.Map.SiteNamed("B").Center;
        foreach (var i in fake.FakerAgents.Where(i => sim.Agents[i].Alive))
            Assert.True(SimSpace.FlatDistance(sim.Agents[i].Position, a) < SimSpace.FlatDistance(sim.Agents[i].Position, b), $"faker {i}");
    }

    // the attackers are told all five defenders sit on A: the call is B
    sealed class AllOnA : IBeliefSource
    {
        public bool Believes(int team, int enemy, out Vector3 position, out float certainty)
        {
            position = TestWorld.Map.SiteNamed("A").Center;
            certainty = team == 0 ? 1f : 0f;
            return team == 0;
        }
    }

    [Fact]
    public void LateCall_Spreads_ThenCallsTheWeakSite()
    {
        var sim = new RoundSim(TestWorld.Map, TestWorld.Teams(_ => Profile.Default, _ => Profile.Default),
            new RoundSetup { Attack = new LateCallAttack() }, seed: 2) { BeliefSource = new AllOnA(), BeliefSourceMode = BeliefSourceMode.Supplement };
        var plan = sim.Director(0).Attack;
        Assert.Equal("", plan.TargetSite);
        while (!sim.Over && sim.Remaining > 60f) sim.Step();
        Assert.Equal("", plan.TargetSite);

        // both lanes are taken before the call
        var a = TestWorld.Map.SiteNamed("A").Center;
        var b = TestWorld.Map.SiteNamed("B").Center;
        var attackers = sim.Agents.Where(x => x.Attacking && x.Alive).ToList();
        Assert.Contains(attackers, x => SimSpace.FlatDistance(x.Position, a) < SimSpace.FlatDistance(x.Position, b));
        Assert.Contains(attackers, x => SimSpace.FlatDistance(x.Position, b) < SimSpace.FlatDistance(x.Position, a));

        while (!sim.Over && !plan.Executing) sim.Step();
        sim.Step();
        Assert.Equal("B", plan.TargetSite);
        Assert.Equal(0, plan.Rotations);
    }

    [Fact]
    public void LateCall_WithANamedSite_GoesThere()
    {
        var sim = new RoundSim(TestWorld.Map, TestWorld.Teams(_ => Profile.Default, _ => Profile.Default),
            new RoundSetup { Attack = new LateCallAttack(), TargetSite = "A" }, seed: 2) { SightTest = (_, _) => false };
        while (!sim.Over && !sim.Director(0).Executing) sim.Step();
        sim.Step();
        Assert.Equal("A", sim.Director(0).TargetSite);
    }

    [Fact]
    public void FixedExecuteAt_KeepsTheDice()
    {
        // overriding the timing leaves the drawn site alone, so a variant differs only by its timing
        for (int seed = 0; seed < 10; seed++)
        {
            var own = new RoundSim(TestWorld.Map, TestWorld.Teams(_ => Profile.Default, _ => Profile.Default), null, seed: seed);
            var fixedAt = new RoundSim(TestWorld.Map, TestWorld.Teams(_ => Profile.Default, _ => Profile.Default),
                new RoundSetup { Attack = new DefaultAttack { FixedExecuteAt = 70f } }, seed: seed);
            Assert.Equal(own.Director(0).TargetSite, fixedAt.Director(0).TargetSite);
            Assert.Equal(70f, fixedAt.Director(0).Attack.ExecuteAt);
        }
    }
}
