using ValorantReplay.Playback;
using Xunit;

namespace ValorantReplay.Sim.Tests;

/// <summary>UtilityBrain on its own: confidence, trades, the buy, the range a gun wins at, and how agents peek.</summary>
public class BrainTests
{
    // measured execution at the population's mean and well either side of it
    static readonly Profile Average = new Profile { Accuracy = 0.289f, Headshots = 0.194f, Reaction = 0.412f };
    static readonly Profile Sharp = new Profile(0.5f, 0.8f, 0.5f, PlayStyle.Entry) { Accuracy = 0.37f, Headshots = 0.30f, Reaction = 0.37f };
    static readonly Profile Shaky = new Profile(0.5f, 0.2f, 0.5f) { Accuracy = 0.21f, Headshots = 0.10f, Reaction = 0.46f };

    /// <summary>An enemy believed distance metres away and not in sight; the agent is away from its objective.</summary>
    static Situation Contact(float distance = 12f, float certainty = 0.8f) =>
        new Situation { Health = 1f, NearestThreat = distance, ThreatCertainty = certainty, ObjectiveDistance = 10f };

    static float Score(in Situation s, Profile p, AgentAction action)
    {
        var scores = new float[UtilityBrain.Choices.Length];
        UtilityBrain.Score(s, p, scores);
        return scores[Array.IndexOf(UtilityBrain.Choices, action)];
    }

    static Dictionary<PeekStyle, float> Styles(Situation s, Profile p, bool trade)
    {
        var rng = new Random(1);
        var n = new Dictionary<PeekStyle, float> { [PeekStyle.Wide] = 0, [PeekStyle.Slice] = 0, [PeekStyle.Jiggle] = 0 };
        for (int i = 0; i < 4000; i++) n[UtilityBrain.ChoosePeek(s, p, trade, rng)]++;
        foreach (var k in n.Keys.ToList()) n[k] /= 4000f;
        return n;
    }

    [Fact]
    public void Confidence_IsThePopulationAtAverage_AndFollowsMeasurementsOverSkill()
    {
        Assert.Equal(0.5f, Profile.Default.Confidence, 3);
        Assert.Equal(0.5f, Average.Confidence, 2);
        Assert.True(Profile.Default.With(skill: 0.9f).Confidence > 0.8f);
        Assert.True(Profile.Default.With(skill: 0.1f).Confidence < 0.2f);
        // a measured sharpshooter is confident whatever its skill knob says
        var measured = new Profile { Skill = 0.2f, Accuracy = Sharp.Accuracy, Headshots = Sharp.Headshots, Reaction = Sharp.Reaction };
        Assert.True(measured.Confidence > 0.8f, $"{measured.Confidence}");
        Assert.True(Shaky.Confidence < 0.2f, $"{Shaky.Confidence}");
    }

    [Fact]
    public void Trade_SwingScalesWithConfidence()
    {
        var s = Contact();
        s.TradeAvailable = true;
        s.KillerCertainty = 0.8f;
        var even = Profile.Default.With(aggression: 0.5f);
        float sharp = Score(s, new Profile { Accuracy = Sharp.Accuracy, Headshots = Sharp.Headshots, Reaction = Sharp.Reaction }, AgentAction.Trade);
        float shaky = Score(s, new Profile { Accuracy = Shaky.Accuracy, Headshots = Shaky.Headshots, Reaction = Shaky.Reaction }, AgentAction.Trade);
        Assert.True(sharp > 1.8f * shaky, $"sharp {sharp:0.00} vs shaky {shaky:0.00}");
        // not knowing where the killer is, or being hurt, makes the swing less likely
        var blind = s;
        blind.KillerCertainty = 0f;
        Assert.True(Score(blind, even, AgentAction.Trade) < Score(s, even, AgentAction.Trade));
        var hurt = s;
        hurt.Health = 0.2f;
        Assert.True(Score(hurt, even, AgentAction.Trade) < Score(s, even, AgentAction.Trade));

        // how it swings: confident players swing wide, unsure ones slice; nobody jiggles for a trade
        var bold = Styles(s, Sharp, trade: true);
        var careful = Styles(s, Shaky, trade: true);
        Assert.Equal(0f, bold[PeekStyle.Jiggle]);
        Assert.Equal(0f, careful[PeekStyle.Jiggle]);
        Assert.True(bold[PeekStyle.Wide] > 0.6f, $"sharp wide {bold[PeekStyle.Wide]:P0}");
        Assert.True(careful[PeekStyle.Slice] > 0.6f, $"shaky slice {careful[PeekStyle.Slice]:P0}");
    }

    [Fact]
    public void AfterAKill_UnsurePlayersGiveUpTheAngle()
    {
        // an attacker mid-map who just won a fight and sees nobody else
        var s = new Situation { Attacking = true, Health = 1f, NearestThreat = float.PositiveInfinity, ObjectiveDistance = 10f, JustKilled = true };
        float shaky = Score(s, Shaky, AgentAction.FallBack), sharp = Score(s, Sharp, AgentAction.FallBack);
        foreach (var other in new[] { AgentAction.Hold, AgentAction.Advance })
            Assert.True(shaky > Score(s, Shaky, other), $"shaky falls back {shaky:0.00} vs {other} {Score(s, Shaky, other):0.00}");
        Assert.True(sharp < 0.15f, $"sharp aggressive player falls back {sharp:0.00}");
        s.JustKilled = false;
        Assert.Equal(0f, Score(s, Shaky, AgentAction.FallBack));
    }

    [Fact]
    public void RangeFit_PeekWhereTheGunWins_BackOffWhereItLoses()
    {
        var good = Contact();
        good.RangeFit = 1f;
        var bad = Contact();
        bad.RangeFit = -1f;
        var p = Profile.Default;
        Assert.True(Score(good, p, AgentAction.Peek) > Score(bad, p, AgentAction.Peek) + 0.5f);
        Assert.True(Score(bad, p, AgentAction.FallBack) > Score(good, p, AgentAction.FallBack) + 0.3f);
        Assert.True(Score(bad, p, AgentAction.FallBack) > Score(bad, p, AgentAction.Peek));
    }

    [Fact]
    public void RangeFit_FromTheGuns()
    {
        // a shotgun wins up close and loses at range; a sniper the other way round; a rifle is fine anywhere it is likely to fight
        Assert.Equal(1f, WeaponStats.Shotgun.Effectiveness(5f));
        Assert.True(WeaponStats.Shotgun.Effectiveness(25f) < 0.5f);
        Assert.True(WeaponStats.Sniper.Effectiveness(2f) < WeaponStats.Rifle.Effectiveness(2f));
        Assert.Equal(1f, WeaponStats.Sniper.Effectiveness(60f));
        Assert.True(WeaponStats.Smg.Effectiveness(30f) < WeaponStats.Rifle.Effectiveness(30f));
        Assert.Equal(1f, WeaponStats.Rifle.Effectiveness(40f));
    }

    [Fact]
    public void Economy_OutgunnedAttackersPush_BetterArmedSideWaits()
    {
        var go = new Situation { Attacking = true, Health = 1f, NearestThreat = float.PositiveInfinity, ObjectiveDistance = 20f };
        var eco = go;
        eco.Economy = -0.9f;
        Assert.True(Score(eco, Profile.Default, AgentAction.Advance) > Score(go, Profile.Default, AgentAction.Advance) + 0.1f);

        var even = Contact();
        var rich = Contact();
        rich.Economy = 0.9f;
        Assert.True(Score(rich, Profile.Default, AgentAction.Hold) > Score(even, Profile.Default, AgentAction.Hold) + 0.1f);
        Assert.True(Score(rich, Profile.Default, AgentAction.Peek) < Score(even, Profile.Default, AgentAction.Peek));
    }

    [Fact]
    public void ChoosePeek_HoldersJiggleOnAShakyRead_EntriesSwingWide()
    {
        // a defender on its site with a vague read
        var holding = Contact(certainty: 0.3f);
        holding.AtObjective = true;
        var anchor = new Profile(0.5f, 0.3f, 0.5f, PlayStyle.Anchor) { Accuracy = Shaky.Accuracy, Headshots = Shaky.Headshots, Reaction = Shaky.Reaction };
        var h = Styles(holding, anchor, trade: false);
        Assert.True(h[PeekStyle.Jiggle] > 0.5f, $"jiggle {h[PeekStyle.Jiggle]:P0}");

        // an attacking entry with a sure read and teammates around
        var entry = Contact(certainty: 0.9f);
        entry.Attacking = true;
        entry.AlliesNear = 2;
        var e = Styles(entry, Sharp, trade: false);
        Assert.True(e[PeekStyle.Wide] > 0.5f, $"wide {e[PeekStyle.Wide]:P0}");
        Assert.True(e[PeekStyle.Jiggle] < h[PeekStyle.Jiggle]);
    }
}
