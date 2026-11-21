using System.Numerics;
using ValorantReplay.Playback;
using Xunit;

namespace ValorantReplay.Sim.Tests;

/// <summary>Each team's buy: where it comes from, and what an outgunned team does with it.</summary>
public class EconomyTests
{
    /// <summary>The ASCII world's teams, attackers on pistols without armor.</summary>
    static List<AgentSetup> EcoAttack()
    {
        var setup = TestWorld.Teams(_ => Profile.Default, _ => Profile.Default);
        foreach (var s in setup.Where(s => s.Attacking)) { s.Weapon = "BasePistol"; s.Armor = 0f; }
        return setup;
    }

    [Fact]
    public void Economy_FromWhatTeamsCarry()
    {
        var even = new RoundSim(TestWorld.Map, TestWorld.Teams(_ => Profile.Default, _ => Profile.Default));
        Assert.Equal(0f, even.Economy(0));
        Assert.Equal(3900f, even.Buy(0));         // rifle and heavy armor

        var eco = new RoundSim(TestWorld.Map, EcoAttack());
        Assert.Equal(0f, eco.Buy(0));
        Assert.Equal(-1f, eco.Economy(0));
        Assert.Equal(1f, eco.Economy(1));

        // a given loadout value wins over the estimate
        var setup = EcoAttack();
        foreach (var s in setup.Where(s => s.Attacking)) s.Loadout = 2000;
        Assert.Equal(-1900f / 3900f, new RoundSim(TestWorld.Map, setup).Economy(0), 3);
    }

    [Fact]
    public void Economy_FromRecordedLoadouts()
    {
        var r = TestWorld.StandingReplay(new[] { (0, TestWorld.Cell(3, 10), 90f), (1, TestWorld.Cell(36, 10), -90f) }, new List<Kill>());
        r.Rounds[0].Loadouts.Add(new PlayerLoadout { Player = 0, Sidearm = "LugerPistol", Armor = ArmorType.Light });
        r.Rounds[0].Loadouts.Add(new PlayerLoadout { Player = 1, Primary = "AssaultRifle_AK", Sidearm = "BasePistol", Armor = ArmorType.Heavy });
        var sim = RoundSim.FromReplay(r, TestWorld.Map, 0.5f, _ => Profile.Default, followRecording: false);
        Assert.Equal(900, sim.Agents[0].Loadout);
        Assert.Equal(3900, sim.Agents[1].Loadout);
        Assert.Equal(WeaponStats.Ghost, sim.Agents[0].Weapon);
        Assert.True(sim.Economy(0) < -0.7f, $"{sim.Economy(0)}");
    }

    [Fact]
    public void Outgunned_AttackersRush_DefendersPush()
    {
        var even = new RoundSim(TestWorld.Map, TestWorld.Teams(_ => Profile.Default, _ => Profile.Default));
        Assert.False(even.Director(0).Executing);
        Assert.True(new RoundSim(TestWorld.Map, EcoAttack()).Director(0).Executing, "an eco attack goes in from the start");
        Assert.False(new RoundSim(TestWorld.Map, EcoAttack(), new RoundSetup { Attack = new DefaultAttack { EcoRush = float.PositiveInfinity } }).Director(0).Executing);

        // defenders on pistols take space towards the attackers instead of holding their sites
        var setup = TestWorld.Teams(_ => Profile.Default, _ => Profile.Default);
        foreach (var s in setup.Where(s => !s.Attacking)) { s.Weapon = "BasePistol"; s.Armor = 0f; }
        float Spread(List<AgentSetup> teams)
        {
            var sim = new RoundSim(TestWorld.Map, teams, seed: 3);
            for (int i = 0; i < 20 * 8; i++) sim.Step();       // 8 s: set up, the attack has not committed yet
            var defenders = sim.Agents.Where(a => !a.Attacking && a.Alive).ToList();
            return defenders.Average(a => Vector3.Distance(a.Position, TestWorld.Map.AttackerSpawn));
        }
        float holding = Spread(TestWorld.Teams(_ => Profile.Default, _ => Profile.Default)), pushing = Spread(setup);
        Assert.True(pushing < holding - 3f, $"eco defenders {pushing:0.0} m from the attacker spawn, full buy {holding:0.0} m");
    }

    [Fact]
    public void Eco_LosesMoreThanItWins()
    {
        // pistols against rifles still win some rounds, but far fewer
        var even = SimBatch.Run(i => new RoundSim(TestWorld.Map, TestWorld.Teams(_ => Profile.Default, _ => Profile.Default), seed: i), 300);
        var eco = SimBatch.Run(i => new RoundSim(TestWorld.Map, EcoAttack(), seed: i), 300);
        Assert.True(eco.AttackerWinRate < even.AttackerWinRate - 0.2f, $"eco {eco.AttackerWinRate:P0} vs even {even.AttackerWinRate:P0}");
        Assert.True(eco.AttackerWinRate > 0.02f, $"eco {eco.AttackerWinRate:P0}");
    }
}
