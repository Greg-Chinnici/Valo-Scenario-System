using ValorantReplay.Playback;
using Xunit;

namespace ValorantReplay.Tests;

/// <summary>VrpReplay.Anonymize: what a published .vrp may still say about the players and the match.</summary>
public class AnonymizeTests
{
    static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    static readonly byte[] OtherKey = Enumerable.Range(101, 32).Select(i => (byte)i).ToArray();

    static VrpReplay RoundTrip(VrpReplay v)
    {
        var ms = new MemoryStream();
        v.Save(ms);
        ms.Position = 0;
        return VrpReplay.Load(ms);
    }

    [Fact]
    public void NothingIdentifyingSurvivesTheFile()
    {
        var v = VrpFormatTests.Sample();
        v.Anonymize(Key);
        var back = RoundTrip(v);

        Assert.NotEqual("match-1", back.MatchId);
        Assert.Matches("^[0-9a-f]{24}$", back.MatchId);
        Assert.Equal(0, back.RecordedAtTicks);
        foreach (var p in back.Players)
        {
            Assert.Matches("^[0-9a-f]{24}$", p.Puuid);
            Assert.Equal(VrpReplay.GeneratedName(p.Puuid), p.Name);   // a made-up name, from the pseudonym
            Assert.Equal("", p.CrosshairProfile);
        }
        Assert.DoesNotContain(back.Tags, t => t.Source == TagSource.User);
        var auto = Assert.Single(back.Tags);                       // the highlight stays, with the new name in it
        Assert.Equal("First blood - Frosty Cardinal 659", auto.Label);
        Assert.DoesNotContain(back.Tags, t => t.Label.Contains("Alpha") || t.Note.Contains("Alpha"));
        // what the replay is for is untouched
        Assert.Equal("Neon", back.Players[0].AgentName);
        Assert.Equal(v.Frames.Length, back.Frames.Length);
        Assert.Equal(v.Kills.Count, back.Kills.Count);
    }

    [Fact]
    public void SamePlayer_SamePseudonym_AcrossMatches()
    {
        var a = VrpFormatTests.Sample();
        var b = VrpFormatTests.Sample();
        b.MatchId = "match-2";
        b.Players[1].Puuid = "someone-else";
        a.Anonymize(Key);
        b.Anonymize(Key);
        Assert.Equal(a.Players[0].Puuid, b.Players[0].Puuid);      // p0 played both: followable by pseudonym
        Assert.Equal(a.Players[0].Name, b.Players[0].Name);        // and by the same made-up name
        Assert.NotEqual(a.Players[1].Puuid, b.Players[1].Puuid);
        Assert.NotEqual(a.Players[1].Name, b.Players[1].Name);
        Assert.NotEqual(a.MatchId, b.MatchId);
    }

    [Fact]
    public void GeneratedNames_ArePinned()
    {
        // these break when the word lists or the derivation change: that would rename every published player
        Assert.Equal("d3654417ebc43ef6bbe86ee2", VrpReplay.Pseudonym(Key, "player", "p0"));
        Assert.Equal("Frosty Cardinal 659", VrpReplay.GeneratedName("d3654417ebc43ef6bbe86ee2"));
        Assert.Equal("Polar Beaver 836", VrpReplay.GeneratedName("000000000000000000000000"));
        Assert.Equal(128, VrpReplay.GeneratedNameAdjectives.Count);
        Assert.Equal(128, VrpReplay.GeneratedNameAnimals.Count);
    }

    [Fact]
    public void GeneratedNames_LookLikeNames_NotRiotIds()
    {
        for (int i = 0; i < 2000; i++)
        {
            string name = VrpReplay.GeneratedName(VrpReplay.Pseudonym(Key, "player", "player-" + i));
            Assert.Matches(@"^[A-Z][a-z]+ [A-Z][a-z]+ [1-9][0-9]{1,2}$", name);
            Assert.DoesNotContain("#", name);
        }
        Assert.Equal("", VrpReplay.GeneratedName(""));
    }

    [Fact]
    public void GeneratedNames_AreDistinctWithinAMatch()
    {
        var names = VrpReplay.GeneratedNames(new[] { "same", "same", "other" });
        Assert.Equal(VrpReplay.GeneratedName("same"), names[0]);   // the first keeps its usual name
        Assert.Equal(3, names.Distinct().Count());
    }

    [Fact]
    public void WordLists_AreDistinct_AndNameNoAgent()
    {
        Assert.Equal(VrpReplay.GeneratedNameAdjectives.Count, VrpReplay.GeneratedNameAdjectives.Distinct().Count());
        Assert.Equal(VrpReplay.GeneratedNameAnimals.Count, VrpReplay.GeneratedNameAnimals.Distinct().Count());
        var agents = AgentCatalog.All.SelectMany(a => new[] { a.DisplayName, a.DeveloperName })
            .Where(n => !string.IsNullOrEmpty(n)).Select(n => n.ToLowerInvariant()).Append("wingman").ToHashSet();
        foreach (var w in VrpReplay.GeneratedNameAdjectives.Concat(VrpReplay.GeneratedNameAnimals))
            Assert.DoesNotContain(w.ToLowerInvariant(), agents);
    }

    [Fact]
    public void NamesCanBeLeftEmpty()
    {
        var v = VrpFormatTests.Sample();
        v.Anonymize(Key, generateNames: false);
        Assert.All(v.Players, p => Assert.Equal("", p.Name));
        Assert.Equal("First blood - Neon", Assert.Single(v.Tags).Label);   // then the agent name stands in
    }

    [Fact]
    public void PseudonymsDependOnTheKey_AndTheKind()
    {
        Assert.NotEqual(VrpReplay.Pseudonym(Key, "player", "p0"), VrpReplay.Pseudonym(OtherKey, "player", "p0"));
        Assert.NotEqual(VrpReplay.Pseudonym(Key, "player", "x"), VrpReplay.Pseudonym(Key, "match", "x"));
        Assert.Equal(VrpReplay.Pseudonym(Key, "player", "ABC"), VrpReplay.Pseudonym(Key, "player", "abc"));
        Assert.Equal("", VrpReplay.Pseudonym(Key, "player", ""));
    }

    [Fact]
    public void UserTagsCanBeKept()
    {
        var v = VrpFormatTests.Sample();
        v.Anonymize(Key, keepUserTags: true);
        Assert.Contains(v.Tags, t => t.Source == TagSource.User && t.Label == "Plant");
    }

    [Fact]
    public void ShortKeysAreRefused()
    {
        var v = VrpFormatTests.Sample();
        Assert.Throws<ArgumentException>(() => v.Anonymize(new byte[8]));
        Assert.Throws<ArgumentException>(() => v.Anonymize(null));
        Assert.Equal("match-1", v.MatchId);                        // untouched on refusal
    }
}
