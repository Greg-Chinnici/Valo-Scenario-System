using ValorantReplay.Playback;
using Xunit;

namespace ValorantReplay.Tests;

/// <summary>Generated agent and map catalogs (AgentCatalog.cs / MapCatalog.cs).</summary>
public class CatalogTests
{
    [Theory]
    [InlineData("Sprinter_PC_C", "Neon")]
    [InlineData("Sequoia_PC_C", "Iso")]
    [InlineData("Aggrobot_PC_C", "Gekko")]
    [InlineData("/Game/Characters/Wraith/Wraith_PC.Default__Wraith_PC_C", "Omen")]
    public void AgentDisplayNames(string agentClass, string name) => Assert.Equal(name, AgentCatalog.DisplayName(agentClass));

    [Fact]
    public void UnknownAgent_FallsBackToDeveloperName() => Assert.Equal("Newbie", AgentCatalog.DisplayName("Newbie_PC_C"));

    [Fact]
    public void AgentCatalog_HasUniqueDeveloperNamesAndUuids()
    {
        Assert.Equal(AgentCatalog.All.Length, AgentCatalog.All.Select(a => a.DeveloperName).Distinct().Count());
        Assert.Equal(AgentCatalog.All.Length, AgentCatalog.All.Select(a => a.Uuid).Distinct().Count());
    }

    [Theory]
    [InlineData("/Game/Maps/Jam/Jam", "Lotus")]
    [InlineData("/Game/Maps/Ascent/Ascent", "Ascent")]
    [InlineData("/Game/Maps/Triad/Triad", "Haven")]
    [InlineData("/Game/Maps/Bonsai/Bonsai", "Split")]
    public void MapDisplayNames(string level, string name) => Assert.Equal(name, MapCatalog.DisplayName(level));

    [Fact]
    public void Maps_WithCallouts_ResolveASite()
    {
        foreach (var m in MapCatalog.All.Where(m => m.Callouts.Length > 0))
        {
            var c = m.Callouts[0];
            Assert.Same(c, m.NearestCallout(c.X, c.Y));
        }
    }
}
