using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace ValorantReplay.Sim.Tests;

/// <summary>Peek styles in whole rounds: every style gets used, and a jiggle steps out and comes back.</summary>
public class PeekTests
{
    readonly ITestOutputHelper log;
    public PeekTests(ITestOutputHelper log) => this.log = log;

    [Fact]
    public void AllStylesPlay_JigglesComeBack()
    {
        var starts = new Dictionary<PeekStyle, int> { [PeekStyle.Wide] = 0, [PeekStyle.Slice] = 0, [PeekStyle.Jiggle] = 0 };
        int completed = 0, home = 0;
        float furthest = 0f, worstReturn = 0f;
        for (int seed = 0; seed < 150; seed++)
        {
            var sim = TestWorld.Round(i => Profile.Default.With(aggression: 0.2f + 0.15f * (i % 5)), _ => Profile.Default, seed);
            int n = sim.Agents.Count;
            var style = new PeekStyle[n];
            var origin = new Vector3[n];
            var reach = new float[n];
            while (!sim.Over)
            {
                sim.Step();
                foreach (var a in sim.Agents)
                {
                    var now = a.Alive ? a.PeekStyle : PeekStyle.None;
                    if (now != style[a.Index] && now != PeekStyle.None) starts[now]++;
                    if (now == PeekStyle.Jiggle && style[a.Index] != PeekStyle.Jiggle) { origin[a.Index] = a.Position; reach[a.Index] = 0f; }
                    if (now == PeekStyle.Jiggle) reach[a.Index] = Math.Max(reach[a.Index], SimSpace.FlatDistance(a.Position, origin[a.Index]));
                    // a jiggle that ran its course ends holding where it started
                    if (style[a.Index] == PeekStyle.Jiggle && now == PeekStyle.None && a.Alive && a.Action == AgentAction.Hold)
                    {
                        completed++;
                        furthest = Math.Max(furthest, reach[a.Index]);
                        float off = SimSpace.FlatDistance(a.Position, origin[a.Index]);
                        worstReturn = Math.Max(worstReturn, off);
                        if (off <= 1f) home++;
                    }
                    style[a.Index] = now;
                }
            }
        }
        log.WriteLine($"starts: {string.Join(", ", starts.Select(kv => $"{kv.Key} {kv.Value}"))}; jiggles completed {completed}, {home} within 1 m of where they started, " +
            $"furthest out {furthest:0.0} m, worst return {worstReturn:0.0} m");
        Assert.All(starts.Values, v => Assert.True(v >= 10, string.Join(", ", starts)));
        Assert.True(completed >= 10, $"completed jiggles {completed}");
        // back where it started, unless a teammate has taken the spot or time ran out: never left out where it peeked
        Assert.True(home >= 0.9f * completed, $"{home} of {completed} jiggles came back");
        Assert.True(worstReturn <= new SimOptions().JiggleDistance, $"a jiggle ended {worstReturn:0.0} m from where it started");
        // JiggleDistance along the path, give or take a grid cell (2.5 m on this map)
        Assert.True(furthest <= new SimOptions().JiggleDistance + TestWorld.Map.Grid.CellSize, $"a jiggle went {furthest:0.0} m out");
    }
}
