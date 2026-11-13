using ValorantReplay.Playback;
using Xunit;

namespace ValorantReplay.Tests;

/// <summary>Tag sidecar parsing/saving, anchor resolution and auto highlight detection.</summary>
public class TagTests
{
    // ---------------------------------------------------------------- clock strings
    [Theory]
    [InlineData("75", 75f)]
    [InlineData("1:15", 75f)]
    [InlineData("1:15.5", 75.5f)]
    [InlineData("1:02:03", 3723f)]
    [InlineData("0:04", 4f)]
    [InlineData("-4", -4f)]
    [InlineData("-0:04.5", -4.5f)]
    [InlineData(" 12:34.5 ", 754.5f)]
    public void Clock_Parses(string s, float expected)
    {
        Assert.True(TagAnchor.TryParseClock(s, out float v));
        Assert.Equal(expected, v, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1:75")]
    [InlineData("1:2:3:4")]
    [InlineData("1.5:00")]
    [InlineData("1::2")]
    [InlineData("1e3")]
    public void Clock_RejectsBadInput(string s) => Assert.False(TagAnchor.TryParseClock(s, out _));

    [Fact]
    public void Clock_Formats()
    {
        Assert.Equal("12:34.5", TagAnchor.FormatClock(754.5f));
        Assert.Equal("0:04", TagAnchor.FormatClock(4f));
        Assert.Equal("-0:04", TagAnchor.FormatClock(-4f));
        Assert.Equal("1:02:03", TagAnchor.FormatClock(3723f));
    }

    [Fact]
    public void Sidecar_AcceptsNumbersAndClocks()
    {
        var sc = TagSidecar.Parse("""{ "tags": [ { "time": 754.5 }, { "time": "12:34.5" }, { "round": 2, "roundTime": 23 } ] }""");
        Assert.Empty(sc.Warnings);
        Assert.Equal(new[] { 754.5f, 754.5f, 23f }, sc.Tags.Select(t => t.Anchor.Offset));
        Assert.Null(sc.Tags[0].Anchor.Text);
        Assert.Equal("12:34.5", sc.Tags[1].Anchor.Text);
    }

    // ---------------------------------------------------------------- anchors
    /// <summary>Round 1: 0-100 s, barrier at 30 s. Round 2: 100-200 s, no combat phase. Match: 200 s.</summary>
    private static VrpReplay TwoRounds()
    {
        var v = new VrpReplay { Duration = 200f, TickRate = 20f };
        v.Rounds.Add(new Round { Number = 0, StartTime = 0f, EndTime = 100f, BarrierTime = 30f });
        v.Rounds.Add(new Round { Number = 1, StartTime = 100f, EndTime = 200f, BarrierTime = -1f });
        return v;
    }

    private static float Resolve(VrpReplay v, TagAnchor a)
    {
        Assert.True(a.Resolve(v, out float t, out string err), err);
        return t;
    }

    [Fact]
    public void Anchor_ResolvesMatchRoundAndBarrierTime()
    {
        var v = TwoRounds();
        Assert.Equal(75f, Resolve(v, TagAnchor.AtTime(75f)));
        Assert.Equal(123f, Resolve(v, TagAnchor.InRound(1, 23f)));
        Assert.Equal(40f, Resolve(v, TagAnchor.FromBarrier(0, 10f)));
        Assert.Equal(26f, Resolve(v, TagAnchor.FromBarrier(0, -4f)));     // before the barrier drops
        Assert.Equal(0f, Resolve(v, TagAnchor.FromBarrier(0, -40f)));     // clamped to [0, Duration]
        Assert.Equal(200f, Resolve(v, TagAnchor.AtTime(999f)));
    }

    [Fact]
    public void Anchor_RoundIsOneBasedInTheSidecar()
    {
        var sc = TagSidecar.Parse("""{ "tags": [ { "round": 2, "roundTime": "0:23" } ] }""");
        Assert.Equal(2, sc.Tags[0].Anchor.Round);
        Assert.Equal(123f, Resolve(TwoRounds(), sc.Tags[0].Anchor));
        Assert.Equal(2, TagAnchor.InRound(1, 0f).Round);
    }

    [Fact]
    public void Anchor_ErrorsForMissingRoundAndMissingBarrier()
    {
        var v = TwoRounds();
        Assert.False(TagAnchor.InRound(5, 1f).Resolve(v, out _, out string e1));
        Assert.Contains("round 6 does not exist", e1);
        Assert.False(TagAnchor.FromBarrier(1, 1f).Resolve(v, out _, out string e2));
        Assert.Contains("no barrier", e2);
    }

    [Theory]
    [InlineData("time=12:34.5")]
    [InlineData("time=754.5")]
    [InlineData("round=7 roundTime=0:23")]
    [InlineData("round=7 barrierTime=-4")]
    public void Anchor_TextRoundTrips(string s) => Assert.Equal(s, TagAnchor.Parse(s).ToString());

    [Fact]
    public void ApplyTags_SkipsUnresolvableWithWarnings()
    {
        var v = TwoRounds();
        var sc = TagSidecar.Parse("""
        { "tags": [
            { "time": 10, "label": "ok" },
            { "round": 9, "roundTime": 1, "label": "no such round" },
            { "round": 2, "barrierTime": -4, "label": "no barrier" },
            { "label": "no anchor" },
            { "time": 1, "roundTime": 2, "label": "two anchors" },
            { "roundTime": 2, "label": "no round" },
            { "time": "soon", "label": "bad clock" }
        ] }
        """);
        v.ApplyTags(sc, highlights: false, out var warnings);
        Assert.Equal(new[] { "ok" }, v.Tags.Select(t => t.Label));
        Assert.Equal(6, warnings.Count);
        foreach (var label in new[] { "no such round", "no barrier", "no anchor", "two anchors", "no round", "bad clock" })
            Assert.Contains(warnings, w => w.Contains(label));
    }

    [Fact]
    public void ApplyTags_ResolvesPlayerAndRound()
    {
        var v = TwoRounds();
        v.Players.Add(new Player { Puuid = "aaaa-bbbb", AgentName = "Jett" });
        v.Players.Add(new Player { Puuid = "cccc-dddd", AgentName = "Sage" });
        var sc = TagSidecar.Parse("""
        { "tags": [ { "time": 150, "player": "cccc-dddd" }, { "round": 1, "barrierTime": 5, "player": 0 }, { "time": 1, "player": "nobody" } ] }
        """);
        v.ApplyTags(sc, false, out var warnings);
        // sorted by time; the unknown PUUID keeps the tag without a player
        Assert.Equal(new[] { (1f, (short)0, (byte)255), (35f, (short)0, (byte)0), (150f, (short)1, (byte)1) }, v.Tags.Select(t => (t.Time, t.Round, t.Player)));
        Assert.Contains("nobody", Assert.Single(warnings));
    }

    // ---------------------------------------------------------------- sidecar round trip
    private const string Example = """
    {
      "version": 1,
      "comment": "unknown fields are ignored",
      "highlights": { "enabled": true, "clutchMin": 3, "clutchRequireWin": false, "ace": false, "multiKill": 4, "multiKillWindow": 6.5, "firstBlood": true, "extra": 1 },
      "tags": [
        { "time": "1:34.5", "label": "Retake starts", "color": "red" },
        { "time": 54.5, "duration": 8, "label": "Range moment" },
        { "round": 2, "roundTime": "0:23", "label": "Smoke \"timing\"", "player": "cccc-dddd", "tags": ["util", "smokes"] },
        { "round": 1, "barrierTime": -4, "note": "4 s before the barrier drops" }
      ]
    }
    """;

    [Fact]
    public void Sidecar_LoadSaveLoad_KeepsTagsAndAnchorForms()
    {
        var a = TagSidecar.Parse(Example);
        Assert.Empty(a.Warnings);
        var h = a.Highlights;
        Assert.Equal((true, 3, false, false, 4, 6.5f, true), (h.Enabled, h.ClutchMin, h.ClutchRequireWin, h.Ace, h.MultiKill, h.MultiKillWindow, h.FirstBlood));
        var path = Path.Combine(Path.GetTempPath(), $"tags-{Guid.NewGuid():N}.tags.json");
        try
        {
            a.Save(path);
            var b = TagSidecar.Load(path);
            Assert.Empty(b.Warnings);
            Assert.Equal(Describe(a), Describe(b));
            Assert.Equal(new[] { "time=1:34.5", "time=54.5", "round=2 roundTime=0:23", "round=1 barrierTime=-4" }, b.Tags.Select(t => t.Anchor.ToString()));
            Assert.Equal("Smoke \"timing\"", b.Tags[2].Label);
            Assert.Equal(a.ToJson(), b.ToJson());
        }
        finally { File.Delete(path); }
    }

    private static IEnumerable<string> Describe(TagSidecar s) =>
        s.Tags.Select(t => $"{t.Anchor}|{t.Duration}|{t.Label}|{t.Note}|{string.Join(",", t.Categories)}|{t.PlayerPuuid}|{t.PlayerIndex}");

    [Fact]
    public void Sidecar_MissingHighlightsUsesDefaults()
    {
        var s = TagSidecar.Parse("""{ "tags": [] }""");
        var d = new HighlightSettings();
        Assert.Equal((d.Enabled, d.ClutchMin, d.ClutchRequireWin, d.Ace, d.MultiKill, d.MultiKillWindow, d.FirstBlood),
                     (s.Highlights.Enabled, s.Highlights.ClutchMin, s.Highlights.ClutchRequireWin, s.Highlights.Ace, s.Highlights.MultiKill, s.Highlights.MultiKillWindow, s.Highlights.FirstBlood));
        Assert.Equal((true, 2, true, true, 3, 5f, false), (d.Enabled, d.ClutchMin, d.ClutchRequireWin, d.Ace, d.MultiKill, d.MultiKillWindow, d.FirstBlood));
    }

    [Fact]
    public void Sidecar_MalformedJsonThrows()
    {
        var e = Assert.Throws<FormatException>(() => TagSidecar.Parse("{ \"tags\": [ { \"time\": 1 }, ]\n}"));
        Assert.Contains("line 1", e.Message);
    }

    [Fact]
    public void ToSidecar_KeepsUserTagsDropsAutoTags()
    {
        var v = TwoRounds();
        v.Players.Add(new Player { Puuid = "aaaa-bbbb" });
        v.Players.Add(new Player { Puuid = "cccc-dddd" });
        v.ApplyTags(TagSidecar.Parse(Example), false, out _);
        v.Tags.Add(new ReplayTag { Time = 5f, Label = "auto", Source = TagSource.Auto, Kind = Highlights.Ace });
        var back = v.ToSidecar();
        Assert.Equal(Describe(TagSidecar.Parse(Example)).OrderBy(x => x), Describe(back).OrderBy(x => x));
        Assert.Equal(3, back.Highlights.ClutchMin);
    }

    [Fact]
    public void ToSidecar_MovesAnEditedTagsAnchorKeepingItsForm()
    {
        var v = TwoRounds();
        v.ApplyTags(TagSidecar.Parse("""{ "tags": [ { "round": 1, "barrierTime": "-0:04", "label": "x" } ] }"""), false, out _);
        Assert.Equal(26f, v.Tags[0].Time);
        v.Tags[0].Time = 32.5f;                  // dragged in the UI
        Assert.Equal("round=1 barrierTime=0:02.5", v.ToSidecar().Tags[0].Anchor.ToString());
    }

    [Fact]
    public void AddTag_AndNavigation()
    {
        var v = TwoRounds();
        var b = v.AddTag(TagAnchor.FromBarrier(0, -4f), "buy");
        var c = v.AddTag(TagAnchor.InRound(1, 10f), "late", duration: 5f);
        var a = v.AddTag(TagAnchor.AtTime(5f), "early");
        Assert.Equal(new[] { a, b, c }, v.Tags);
        Assert.Equal((short)1, c.Round);
        Assert.Equal((short)0, a.Round);
        Assert.Same(b, v.NextTag(5f));
        Assert.Same(a, v.PrevTag(26f));
        Assert.Null(v.PrevTag(5f));
        Assert.Null(v.NextTag(110f));
        Assert.Equal(new[] { b }, v.TagsInRound(0).Where(t => t.Label == "buy"));
        Assert.Equal(new[] { c }, v.TagsBetween(112f, 113f));             // inside the range
        Assert.True(c.ActiveAt(114f));
        Assert.False(c.ActiveAt(116f));
        Assert.Throws<ArgumentException>(() => v.AddTag(TagAnchor.FromBarrier(1, 0f), "no barrier"));
        Assert.True(v.RemoveTag(b));
        Assert.Equal(new[] { a, c }, v.Tags);
        Assert.Equal(new[] { "time=5", "round=2 roundTime=10" }, v.ToSidecar().Tags.Select(t => t.Anchor.ToString()));
    }

    // ---------------------------------------------------------------- highlights
    /// <summary>A synthetic single-round match: team 0 = players [0, nA), team 1 = [nA, nA + nB); 30 s at 20 Hz, barrier at 5 s.
    /// Alive flags follow the kills and revives.</summary>
    private static VrpReplay Match(int nA, int nB, byte winner, (float T, int Killer, int Victim)[] kills, (float T, int Player)[] revives = null,
                                   RoundEndReason end = RoundEndReason.Elimination)
    {
        var v = new VrpReplay { Duration = 30f, TickRate = 20f, TickCount = 600 };
        string[] agents = { "Jett", "Sage", "Omen", "Sova", "Neon", "Reyna", "Viper", "Kayo", "Fade", "Skye" };
        for (int p = 0; p < nA + nB; p++)
            v.Players.Add(new Player { Puuid = "p" + p, AgentName = agents[p], Team = (byte)(p < nA ? 0 : 1) });
        v.Teams.Add(new Team());
        v.Teams.Add(new Team());
        v.Rounds.Add(new Round { StartTime = 0f, EndTime = 30f, BarrierTime = 5f, WinningTeam = winner, EndReason = end, DefuseTime = end == RoundEndReason.Defused ? 25f : -1f });
        foreach (var k in kills) v.Kills.Add(new Kill { Time = k.T, Killer = (byte)k.Killer, Victim = (byte)k.Victim, Round = 0 });
        int np = v.Players.Count;
        v.Frames = new Frame[v.TickCount * np];
        for (int t = 0; t < v.TickCount; t++)
            for (int p = 0; p < np; p++)
            {
                float time = t / v.TickRate, lastDeath = -1f, lastRevive = -1f;
                foreach (var k in kills) if (k.Victim == p && k.T <= time) lastDeath = k.T;
                if (revives != null) foreach (var r in revives) if (r.Player == p && r.T <= time) lastRevive = r.T;
                bool alive = lastDeath < 0 || lastRevive > lastDeath;
                v.Frames[t * np + p] = new Frame { Flags = alive ? FrameFlags.Alive | FrameFlags.HasData : FrameFlags.HasData, Health = (byte)(alive ? 100 : 0) };
            }
        return v;
    }

    private static readonly HighlightSettings ClutchOnly = new HighlightSettings { Ace = false, MultiKill = 0 };

    [Fact]
    public void Clutch_1v2_Won()
    {
        // team 0: 0,1,2; team 1: 3,4,5. Player 0 is left 1v2 at 12 s and wins.
        var v = Match(3, 3, 0, new[] { (8f, 0, 5), (10f, 3, 1), (12f, 4, 2), (15f, 0, 3), (18f, 0, 4) });
        var tags = Highlights.Detect(v, ClutchOnly);
        var c = Assert.Single(tags);
        Assert.Equal((Highlights.Clutch, 12f, 6f, (byte)0, (short)0), (c.Kind, c.Time, c.Duration, c.Player, c.Round));
        Assert.Equal("1v2 clutch - Jett", c.Label);
        Assert.Equal(TagSource.Auto, c.Source);
    }

    [Fact]
    public void Clutch_EndsAtTheDefuseWhenTheSpikeDecides()
    {
        var v = Match(3, 3, 0, new[] { (8f, 0, 5), (10f, 3, 1), (12f, 4, 2), (15f, 0, 3) }, end: RoundEndReason.Defused);
        var c = Assert.Single(Highlights.Detect(v, ClutchOnly));
        Assert.Equal(25f, c.EndTime, 3);
    }

    [Fact]
    public void Clutch_Lost_OnlyWithoutRequireWin()
    {
        var v = Match(3, 3, 1, new[] { (8f, 0, 5), (10f, 3, 1), (12f, 4, 2), (15f, 3, 0) });
        Assert.Empty(Highlights.Detect(v, ClutchOnly));
        var loose = new HighlightSettings { Ace = false, MultiKill = 0, ClutchRequireWin = false };
        var c = Assert.Single(Highlights.Detect(v, loose));
        Assert.Equal((12f, 15f, "1v2 clutch - Jett"), (c.Time, c.EndTime, c.Label));
    }

    [Fact]
    public void Clutch_ClutchMinFiltersSmallClutches()
    {
        var v = Match(3, 3, 0, new[] { (8f, 0, 5), (10f, 3, 1), (12f, 4, 2), (15f, 0, 3), (18f, 0, 4) });
        Assert.Empty(Highlights.Detect(v, new HighlightSettings { Ace = false, MultiKill = 0, ClutchMin = 3 }));
    }

    [Fact]
    public void Clutch_ReviveBreaksAFalseClutch()
    {
        // team 0: 0 (Sage), 1, 2; team 1: 3, 4. Player 1 dies at 5 s and is revived at 7 s, so when player 2 dies at 9 s team 0
        // still has two alive: counting kills alone would call it a 1v2.
        var kills = new[] { (5f, 3, 1), (9f, 3, 2), (11f, 0, 3), (12f, 1, 4) };
        var loose = new HighlightSettings { Ace = false, MultiKill = 0, ClutchRequireWin = false };
        var revived = Match(3, 2, 0, kills, new[] { (7f, 1) });
        Assert.DoesNotContain(Highlights.Detect(revived, loose), t => t.Player < 3);
        var noRevive = Match(3, 2, 0, kills);
        Assert.Contains(Highlights.Detect(noRevive, loose), t => t.Player == 0 && t.Time == 9f);   // control: without the revive it is one
    }

    [Fact]
    public void Ace_SuppressesTheMultiKill()
    {
        var v = Match(3, 3, 0, new[] { (10f, 0, 3), (11f, 0, 4), (12f, 0, 5) });
        var tags = Highlights.Detect(v, new HighlightSettings { ClutchMin = 0 });
        var a = Assert.Single(tags);
        Assert.Equal((Highlights.Ace, 10f, 12f, (byte)0, "Ace - Jett"), (a.Kind, a.Time, a.EndTime, a.Player, a.Label));
        // with aces off the same kills are a multikill
        var m = Assert.Single(Highlights.Detect(v, new HighlightSettings { ClutchMin = 0, Ace = false }));
        Assert.Equal(Highlights.MultiKill, m.Kind);
    }

    [Fact]
    public void MultiKill_InsideAndOutsideTheWindow()
    {
        // four enemies, so three kills are no ace
        var fast = Match(2, 4, 0, new[] { (10f, 0, 2), (12f, 0, 3), (14f, 0, 4) });
        var m = Assert.Single(Highlights.Detect(fast, new HighlightSettings { ClutchMin = 0 }));
        Assert.Equal((Highlights.MultiKill, 10f, 14f, "3 kills in 4.0s - Jett"), (m.Kind, m.Time, m.EndTime, m.Label));

        var slow = Match(2, 4, 0, new[] { (10f, 0, 2), (13f, 0, 3), (17f, 0, 4) });
        Assert.Empty(Highlights.Detect(slow, new HighlightSettings { ClutchMin = 0 }));
        Assert.Single(Highlights.Detect(slow, new HighlightSettings { ClutchMin = 0, MultiKillWindow = 7f }));
    }

    [Fact]
    public void FirstBlood_IsOffByDefault()
    {
        var v = Match(2, 2, 0, new[] { (10f, 2, 0), (12f, 1, 2) });
        Assert.DoesNotContain(Highlights.Detect(v, new HighlightSettings()), t => t.Kind == Highlights.FirstBlood);
        var fb = Assert.Single(Highlights.Detect(v, new HighlightSettings { FirstBlood = true, ClutchMin = 0 }));
        Assert.Equal((10f, (byte)2, "First blood - Omen"), (fb.Time, fb.Player, fb.Label));
    }

    [Fact]
    public void ApplyTags_HighlightsFlagAndSettings()
    {
        var v = Match(3, 3, 0, new[] { (10f, 0, 3), (11f, 0, 4), (12f, 0, 5) });
        v.ApplyTags(null, highlights: true, out _);
        Assert.Single(v.Tags, t => t.Kind == Highlights.Ace);
        v.ApplyTags(null, highlights: false, out _);
        Assert.Empty(v.Tags);
        v.ApplyTags(TagSidecar.Parse("""{ "highlights": { "enabled": false } }"""), true, out _);
        Assert.Empty(v.Tags);
    }

    [Fact]
    public void MakeUserTag_KeepsAnEditedHighlightWithoutDuplicating()
    {
        var v = Match(3, 3, 0, new[] { (10f, 0, 3), (11f, 0, 4), (12f, 0, 5) });
        v.ApplyTags(null, true, out _);
        var ace = v.Tags.Single();
        ace.MakeUserTag(v);
        ace.Label = "Jett ace on B";
        var sc = v.ToSidecar();
        var spec = Assert.Single(sc.Tags);
        Assert.Equal(("round=1 roundTime=10", "ace", "p0"), (spec.Anchor.ToString(), spec.Categories.Single(), spec.PlayerPuuid));
        v.ApplyTags(TagSidecar.Parse(sc.ToJson()), true, out var warnings);   // what --retag does
        Assert.Empty(warnings);
        var t = Assert.Single(v.Tags);
        Assert.Equal((TagSource.User, "Jett ace on B", 2f), (t.Source, t.Label, t.Duration));
    }
}
