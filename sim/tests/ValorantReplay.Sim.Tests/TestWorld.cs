using System.Numerics;
using ValorantReplay.Playback;

namespace ValorantReplay.Sim.Tests;

/// <summary>Shared fixtures: a two-site ASCII map and synthetic replays on it.</summary>
internal static class TestWorld
{
    // Attackers spawn left (a), defenders right (d). Top lane to A, bottom lane to B, mid door into defender spawn,
    // with a pillar at x 33 so mid has no spawn-to-spawn sight line.
    public static readonly string[] Rows =
    {
        "########################################",
        "#..................##......AAAAAA......#",
        "#..........................AA##AA......#",
        "#..........................AAAAAA......#",
        "#......###################.AAAAAA......#",
        "#......###################.............#",
        "#......#########################.......#",
        "#......#########################.......#",
        "#......######.............######.......#",
        "#.aaa.....................######.#.ddd.#",
        "#.aaa............................#.ddd.#",
        "#.aaa.....................######.#.ddd.#",
        "#......######.............######.......#",
        "#......#########################.......#",
        "#......#########################.......#",
        "#......###################.............#",
        "#......###################.BBBBBB......#",
        "#..........................BBBBBB......#",
        "#..........................BB##BB......#",
        "#..................##......BBBBBB......#",
        "########################################",
    };

    /// <summary>Metres per ASCII cell: 100 x 52 m, about the size of a real map, so sight and hearing ranges matter.</summary>
    public const float CellSize = 2.5f;

    static SimMap map;
    public static SimMap Map => map ??= SimMap.FromAscii(Rows, CellSize);

    /// <summary>Centre of ASCII cell (column, row).</summary>
    public static Vector3 Cell(int column, int row) => new Vector3((column + 0.5f) * CellSize, NavGrid.ActorHeight, (row + 0.5f) * CellSize);

    // where defenders stand at the barrier drop (they set up during the buy phase): 5, 7, 9 on A, 6, 8 on B, matching
    // the director's round-robin site assignment
    static readonly (int c, int r)[] DefenderSpots = { (31, 3), (31, 17), (33, 4), (33, 16), (28, 4) };

    /// <summary>Five attackers (team 0) in their spawn, five defenders (team 1) set up on the sites.</summary>
    public static List<AgentSetup> Teams(Func<int, Profile> attackers, Func<int, Profile> defenders)
    {
        var l = new List<AgentSetup>();
        for (int i = 0; i < 5; i++)
            l.Add(new AgentSetup { Team = 0, Attacking = true, Position = Cell(2 + i % 3, 9 + i / 3), Yaw = 90f, Profile = attackers(i), Name = "atk" + i });
        for (int i = 0; i < 5; i++)
            l.Add(new AgentSetup { Team = 1, Position = Cell(DefenderSpots[i].c, DefenderSpots[i].r), Yaw = -90f, Profile = defenders(i), Name = "def" + i });
        return l;
    }

    public static RoundSim Round(Func<int, Profile> attackers, Func<int, Profile> defenders, int seed, AttackPlan plan = AttackPlan.Default) =>
        new RoundSim(Map, Teams(attackers, defenders), new RoundSetup { Plan = plan }, seed: seed);

    /// <summary>A Unity-space replay at 20 Hz in which each player stands still at the given cell, plus the given kills.
    /// Victims are dead from their kill on. One round from 0 to duration, team 0 attacking.</summary>
    public static VrpReplay StandingReplay(IList<(int team, Vector3 pos, float yaw)> players, IList<Kill> kills, float duration = 10f)
    {
        var r = new VrpReplay { MatchId = "synthetic", Map = "ascii", Space = CoordinateSpace.Unity, TickRate = 20f, Duration = duration };
        r.TickCount = (int)(duration * r.TickRate) + 1;
        for (int p = 0; p < players.Count; p++)
            r.Players.Add(new Player { Puuid = "p" + p, AgentName = "Agent" + p, AgentRole = "Duelist", Team = (byte)players[p].team, PlayerId = p });
        r.Teams.Add(new Team { InitialRole = TeamRole.Attacking });
        r.Teams.Add(new Team { InitialRole = TeamRole.Defending });
        r.Rounds.Add(new Round { Number = 0, StartTime = 0f, BarrierTime = 0f, EndTime = duration, AttackingTeam = 0 });
        foreach (var k in kills) r.Kills.Add(k);
        r.Frames = new Frame[r.TickCount * players.Count];
        for (int t = 0; t < r.TickCount; t++)
            for (int p = 0; p < players.Count; p++)
            {
                float time = t / r.TickRate;
                bool alive = !kills.Any(k => k.Victim == p && k.Time <= time);
                var (_, pos, yaw) = players[p];
                r.Frames[t * players.Count + p] = new Frame
                {
                    X = pos.X, Y = pos.Y, Z = pos.Z, YawCenti = (short)(yaw * 100f), Health = (byte)(alive ? 100 : 0), Armor = 50,
                    Flags = alive ? FrameFlags.Alive | FrameFlags.HasData : FrameFlags.HasData,
                };
            }
        return r;
    }
}
