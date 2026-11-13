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
    public void Callouts_CohesionControlsDelayAndDrops()
    {
        int Delivered(Profile caller, float after)
        {
            int got = 0;
            var rng = new Random(1);
            for (int i = 0; i < 400; i++)
            {
                var board = new TeamBlackboard(0, 2);
                board.Callout(At(0f, 1f), caller, 0f, rng);
                board.Update(after);
                if (board.Board.TryGet(1, out _)) got++;
            }
            return got;
        }
        var tight = Profile.Default.With(cohesion: 1f);
        var loose = Profile.Default.With(cohesion: 0f);
        Assert.Equal(0, Delivered(tight, 0.3f));                    // nothing arrives instantly
        Assert.InRange(Delivered(tight, 0.5f), 360, 400);          // 0.4 s delay, 5% dropped
        Assert.Equal(0, Delivered(loose, 2f));                      // 2.5 s delay
        Assert.InRange(Delivered(loose, 3f), 160, 240);            // half dropped

        var board2 = new TeamBlackboard(0, 2);
        board2.Callout(At(0f, 1f), tight, 0f, new Random(3));
        board2.Update(1f);
        Assert.True(board2.Board.TryGet(1, out var s));
        Assert.Equal(Cue.Callout, s.Cue);
        Assert.Equal(0.9f, s.Certainty, 4);
        Assert.Equal(0f, s.Time);                                   // stamped with when it was seen
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
        Assert.Same(WeaponStats.Pistol, WeaponStats.For("LugerPistol"));
        Assert.Same(WeaponStats.Rifle, WeaponStats.For(""));
        Assert.Equal(120f * 0.25f, WeaponStats.Shotgun.DamageAt(20f, 120f), 3);
        Assert.Equal(40f, WeaponStats.Vandal.DamageAt(60f, 40f));
    }
}
