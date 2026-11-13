using System.Numerics;
using Xunit;

namespace ValorantReplay.Sim.Tests;

/// <summary>Beliefs, callouts and the decision scores, each knob on its own.</summary>
public class KnowledgeTests
{
    static Sighting At(float time, float certainty, Cue cue = Cue.Seen, float x = 0f) =>
        new Sighting { Enemy = 1, Position = new Vector3(x, 0f, 0f), Time = time, Certainty = certainty, Cue = cue };

    [Fact]
    public void Certainty_HalvesPerHalfLife()
    {
        var s = At(0f, 1f);
        Assert.Equal(1f, s.CertaintyAt(0f, 5f), 4);
        Assert.Equal(0.5f, s.CertaintyAt(5f, 5f), 4);
        Assert.Equal(0.25f, s.CertaintyAt(10f, 5f), 4);
    }

    [Fact]
    public void Beliefs_KeepWhatIsWorthMoreNow()
    {
        var b = new Beliefs(2);
        b.Observe(At(0f, 1f, x: 1f), 0f, 5f);
        Assert.False(b.Observe(At(1f, 0.5f, Cue.Heard, x: 2f), 1f, 5f));   // fresh vision (0.87) beats a vague sound
        Assert.True(b.Observe(At(10f, 0.5f, Cue.Heard, x: 3f), 10f, 5f));  // 10 s later the sighting is worth 0.25
        Assert.True(b.TryGet(1, out var s));
        Assert.Equal(3f, s.Position.X);
        Assert.False(b.TryGet(0, out _));
    }

    [Fact]
    public void Callouts_CohesionAndSenseControlDelayDropsAndPrecision()
    {
        (int got, float err) Delivered(Profile caller, float after)
        {
            int got = 0;
            float err = 0f;
            var rng = new Random(1);
            for (int i = 0; i < 400; i++)
            {
                var board = new TeamBlackboard(0, 2);
                board.Callout(At(0f, 1f), 0, caller, 0f, rng);
                board.Update(after);
                if (board.Board.TryGet(1, out var s)) { got++; err += System.Numerics.Vector3.Distance(s.Position, At(0f, 1f).Position); }
            }
            return (got, got > 0 ? err / got : 0f);
        }
        var sharp = new Profile(1f, 0.5f, 1f);       // sense 1, cohesion 1: 0.4 s, 5% dropped
        var quiet = new Profile(0.5f, 0.5f, 0f);     // average sense, never talks much
        var lost = new Profile(0f, 0.5f, 0.5f);      // talks, but slow and vague
        Assert.Equal(0, Delivered(sharp, 0.3f).got);                 // nothing arrives instantly
        Assert.InRange(Delivered(sharp, 0.5f).got, 360, 400);
        Assert.InRange(Delivered(quiet, 3f).got, 200, 260);         // drops mostly follow cohesion (43%)
        Assert.InRange(Delivered(lost, 3f).got, 235, 290);          // ... less so sense (34%)
        Assert.Equal(0, Delivered(lost, 1.7f).got);                  // slow: 1.87 s
        Assert.True(Delivered(lost, 3f).err > 3f * Delivered(sharp, 3f).err, "a sharp caller says where far more precisely");

        var board2 = new TeamBlackboard(0, 2);
        board2.Callout(At(0f, 1f), 0, sharp, 0f, new Random(3));
        board2.Update(1f);
        Assert.True(board2.Board.TryGet(1, out var s2));
        Assert.Equal(Cue.Callout, s2.Cue);
        Assert.Equal(0.95f, s2.Certainty, 4);
        Assert.Equal(0f, s2.Time);                                   // stamped with when it was seen
    }

    static float[] Scores(Situation s, Profile p)
    {
        var scores = new float[UtilityBrain.Choices.Length];
        UtilityBrain.Score(s, p, scores);
        return scores;
    }

    static float Get(float[] scores, AgentAction a) => scores[Array.IndexOf(UtilityBrain.Choices, a)];

    static readonly Situation Contact = new Situation
    {
        Attacking = true, Health = 1f, NearestThreat = 12f, ThreatCertainty = 0.8f, AlliesNear = 1, EnemiesNear = 1,
        ObjectiveDistance = 20f, TimePressure = 0.3f,
    };

    [Fact]
    public void Brain_AggressionPeeks_PassivityHolds()
    {
        var aggressive = Scores(Contact, Profile.Default.With(aggression: 1f));
        var passive = Scores(Contact, Profile.Default.With(aggression: 0f));
        Assert.True(Get(aggressive, AgentAction.Peek) > Get(aggressive, AgentAction.Hold));
        Assert.True(Get(passive, AgentAction.Hold) > Get(passive, AgentAction.Peek));
    }

    [Fact]
    public void Brain_CohesionTrades()
    {
        var s = Contact;
        s.TradeAvailable = true;
        Assert.True(Get(Scores(s, Profile.Default.With(cohesion: 1f)), AgentAction.Trade) > Get(Scores(s, Profile.Default.With(cohesion: 0f)), AgentAction.Trade));
        s.TradeAvailable = false;
        Assert.Equal(0f, Get(Scores(s, Profile.Default), AgentAction.Trade));
    }

    [Fact]
    public void Brain_HurtAndOutnumberedFallsBack()
    {
        var s = Contact;
        s.Health = 0.15f;
        s.EnemiesNear = 3;
        s.AlliesNear = 0;
        s.NearestThreat = 6f;
        var p = Profile.Default.With(aggression: 0.3f);
        var sc = Scores(s, p);
        Assert.Equal(AgentAction.FallBack, UtilityBrain.Choices[Array.IndexOf(sc, sc.Max())]);
    }

    [Fact]
    public void Brain_TimePressureAndPlantMoveTheRightSide()
    {
        var calm = new Situation { Attacking = true, Health = 1f, NearestThreat = float.PositiveInfinity, ObjectiveDistance = 30f, TimePressure = 0f };
        var late = calm;
        late.TimePressure = 1f;
        var p = Profile.Default.With(aggression: 0.2f);
        Assert.True(Get(Scores(late, p), AgentAction.Advance) > Get(Scores(calm, p), AgentAction.Advance));

        var retake = new Situation { Attacking = false, SpikePlanted = true, Health = 1f, NearestThreat = float.PositiveInfinity, ObjectiveDistance = 30f };
        var sc = Scores(retake, Profile.Anchor);
        Assert.True(Get(sc, AgentAction.Advance) > Get(sc, AgentAction.Hold));

        var anchored = new Situation { Attacking = false, Health = 1f, NearestThreat = float.PositiveInfinity, AtObjective = true };
        sc = Scores(anchored, Profile.Anchor);
        Assert.True(Get(sc, AgentAction.Hold) > Get(sc, AgentAction.Advance));
    }

    [Fact]
    public void Weapons_GuessedFromClass()
    {
        Assert.Same(WeaponStats.Vandal, WeaponStats.For("AssaultRifle_AK"));
        Assert.Same(WeaponStats.Ghost, WeaponStats.For("LugerPistol"));
        Assert.Same(WeaponStats.Pistol, WeaponStats.For("BasePistol"));
        Assert.Same(WeaponStats.Smg, WeaponStats.For("SubMachineGun_MP5"));
        Assert.Same(WeaponStats.Smg, WeaponStats.For("Vector"));
        Assert.Same(WeaponStats.MachineGun, WeaponStats.For("HeavyMachineGun"));
        Assert.Same(WeaponStats.Marshal, WeaponStats.For("LeverSniperRifle"));
        Assert.Same(WeaponStats.Outlaw, WeaponStats.For("DS_Gun"));
        Assert.Same(WeaponStats.Sniper, WeaponStats.For("BoltSniper"));
        Assert.Same(WeaponStats.Rifle, WeaponStats.For(""));
        Assert.Equal(120f * 0.25f, WeaponStats.Shotgun.DamageAt(20f, 120f), 3);
        Assert.Equal(40f, WeaponStats.Vandal.DamageAt(60f, 40f));
    }
}
