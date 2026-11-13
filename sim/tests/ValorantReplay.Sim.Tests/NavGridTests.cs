using System.Numerics;
using ValorantReplay.Playback;
using Xunit;

namespace ValorantReplay.Sim.Tests;

/// <summary>Walkable space from samples, multi-floor columns, line of sight and pathing.</summary>
public class NavGridTests
{
    static Vector3 Cell(int c, int r) => TestWorld.Cell(c, r);
    static Vector3 Eye(Vector3 p) => p + new Vector3(0f, 0.65f, 0f);

    [Fact]
    public void Ascii_SitesAndSpawns()
    {
        var m = TestWorld.Map;
        Assert.Equal(new[] { "A", "B" }, m.Sites.Select(s => s.Name));
        Assert.True(m.InSite(m.SiteNamed("A"), Cell(28, 3)));
        Assert.False(m.InSite(m.SiteNamed("A"), Cell(28, 17)));
        Assert.Equal(Cell(3, 10).X, m.AttackerSpawn.X, 3);
        Assert.Equal(Cell(36, 10).X, m.DefenderSpawn.X, 3);
    }

    [Fact]
    public void LineOfSight_WallsBlock_OpenFloorDoesNot()
    {
        var g = TestWorld.Map.Grid;
        Assert.True(g.LineOfSight(Eye(Cell(2, 2)), Eye(Cell(25, 2))));      // along the top lane
        Assert.False(g.LineOfSight(Eye(Cell(10, 2)), Eye(Cell(10, 10))));   // lane -> mid through the wall
        Assert.False(g.LineOfSight(Eye(Cell(20, 10)), Eye(Cell(36, 2))));   // mid -> defender side past the block
        Assert.True(g.LineOfSight(Eye(Cell(20, 10)), Eye(Cell(32, 10))));   // straight through the mid door
        Assert.False(g.LineOfSight(Eye(Cell(20, 10)), Eye(Cell(36, 10))));  // ... but the pillar hides defender spawn
    }

    [Fact]
    public void Path_GoesAroundWalls()
    {
        var g = TestWorld.Map.Grid;
        int a = g.Nearest(Cell(10, 2)), b = g.Nearest(Cell(10, 18));
        float walk = g.PathLength(a, b);
        Assert.True(float.IsFinite(walk));
        Assert.True(walk > 20f * TestWorld.CellSize, $"walk {walk}");   // straight line is 16 cells; the wall forces a detour via x <= 6
        var path = new List<int>();
        Assert.True(g.FindPath(a, b, path));
        Assert.Equal(b, path[^1]);
        Assert.All(path, n => Assert.True(g.Nodes[n].X <= 6 || g.Nodes[n].Z <= 3 || g.Nodes[n].Z >= 17));
    }

    [Fact]
    public void Builder_SeparatesFloors_AndDropsAreOneWay()
    {
        var b = new NavGrid.Builder { CellSize = 1f, FillHolePasses = 0 };
        for (int x = 0; x < 6; x++) b.Add(new Vector3(x + 0.5f, 0.9f, 0.5f));    // ground floor x 0..5
        for (int x = 2; x < 6; x++) b.Add(new Vector3(x + 0.5f, 4.9f, 0.5f));    // a ledge 4 m up over x 2..5
        b.Add(new Vector3(1.5f, 3.9f, 0.5f));                                      // one step below the ledge, 3 m up
        var g = b.Build();
        Assert.Equal(2, g.NodesAt(3.5f, 0.5f).Count());
        Assert.Equal(2, g.NodesAt(1.5f, 0.5f).Count());    // ground + the 3.9 m step

        int high = g.NodesAt(2.5f, 0.5f).Last(), low = g.NodesAt(1.5f, 0.5f).First();
        Assert.Contains(low, g.Links(high).ToArray());     // drop 4 m: allowed
        Assert.DoesNotContain(high, g.Links(low).ToArray()); // climb 4 m: not allowed
    }

    [Fact]
    public void Builder_FillsSamplingHoles()
    {
        var b = new NavGrid.Builder { CellSize = 1f };
        for (int z = 0; z < 3; z++)
            for (int x = 0; x < 3; x++)
                if (x != 1 || z != 1) b.Add(new Vector3(x + 0.5f, 0.9f, z + 0.5f));
        var g = b.Build();
        var filled = g.NodesAt(1.5f, 1.5f).Single();
        Assert.Equal(0, g.Nodes[filled].Samples);
        Assert.Equal(0.9f, g.Nodes[filled].Y, 2);
    }

    [Fact]
    public void LineOfSight_IgnoresSingleMissingCell_ButNotAWall()
    {
        var b = new NavGrid.Builder { CellSize = 0.5f, FillHolePasses = 0 };
        for (float x = 0.25f; x < 20f; x += 0.5f)
        {
            if (Math.Abs(x - 5.25f) < 0.01f) continue;          // one unsampled cell
            if (x > 10f && x < 11.5f) continue;                  // a 1.5 m wall
            b.Add(new Vector3(x, 0.9f, 0.25f));
        }
        var g = b.Build();
        var eye = 0.9f + 0.65f;
        Assert.True(g.LineOfSight(new Vector3(0.25f, eye, 0.25f), new Vector3(9.75f, eye, 0.25f)));
        Assert.False(g.LineOfSight(new Vector3(0.25f, eye, 0.25f), new Vector3(15f, eye, 0.25f)));
    }

    [Fact]
    public void LineOfSight_ThroughFloorSlabIsBlocked()
    {
        var b = new NavGrid.Builder { CellSize = 1f, FillHolePasses = 0 };
        for (int x = 0; x < 10; x++) { b.Add(new Vector3(x + 0.5f, 0.9f, 0.5f)); b.Add(new Vector3(x + 0.5f, 5.9f, 0.5f)); }
        var g = b.Build();
        // under the upper floor: fine; from below up through it: blocked
        Assert.True(g.LineOfSight(new Vector3(0.5f, 1.55f, 0.5f), new Vector3(9.5f, 1.55f, 0.5f)));
        Assert.False(g.LineOfSight(new Vector3(0.5f, 1.55f, 0.5f), new Vector3(9.5f, 6.55f, 0.5f)));
    }

    [Fact]
    public void FromReplays_TrailsSitesAndSpawns()
    {
        var r = TestWorld.StandingReplay(new[] { (0, Cell(3, 10), 90f), (1, Cell(36, 10), -90f) }, new List<Kill>());
        // walk player 0 along mid (6 m -> 20 m) over the round: ~0.07 m per tick
        float z = Cell(3, 10).Z;
        for (int t = 0; t < r.TickCount; t++)
        {
            ref var f = ref r.Frames[t * 2];
            f.X = 6f + 14f * t / (r.TickCount - 1);
        }
        r.Rounds[0].PlantTime = 5f;
        r.Rounds[0].Site = "B";
        (r.Rounds[0].PlantX, r.Rounds[0].PlantY, r.Rounds[0].PlantZ) = (74f, 0.9f, 44f);
        var m = SimMap.FromReplays(new[] { r });
        Assert.Equal(1, m.Replays);
        foreach (float x in new[] { 6.1f, 10f, 15f, 19.9f }) Assert.NotEmpty(m.Grid.NodesAt(x, z));
        Assert.Empty(m.Grid.NodesAt(25f, z));
        var b = Assert.Single(m.Sites);
        Assert.Equal("B", b.Name);
        Assert.Equal(1, b.Plants);
        Assert.Equal(6f, m.AttackerSpawn.X, 2);     // barrier drop at t = 0
        Assert.Equal(Cell(36, 10).X, m.DefenderSpawn.X, 2);
        Assert.Throws<ArgumentException>(() => SimMap.FromReplays(new[] { r, new VrpReplay { Map = "other" } }));
    }
}
