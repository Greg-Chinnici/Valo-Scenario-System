// BotMatch.cs - two bot lineups play each other from the recorded rounds: every round of the replays restarts at its
// barrier drop with the recorded positions, health and guns, all agents AI, one lineup in place of each team. Each round
// is played twice, once with each lineup attacking, so a lineup's win rate does not depend on which side the corpus
// favours. Which recorded player a bot replaces rotates from round to round (spawn spots and guns even out), and the
// seeds depend only on the round, the side and the run, so two matchups see the same dice (common random numbers) and
// differ only where the bots play differently.
using System;
using System.Collections.Generic;
using System.Linq;
using ValorantReplay.Playback;

namespace ValorantReplay.Sim
{
    public sealed class BotMatchResult
    {
        public BotLineup A, B;
        public int Rounds, WinsA;
        public int AttackRoundsA, AttackWinsA;      // rounds A attacked, and won
        public int DefenseRoundsA, DefenseWinsA;
        public int Planted;
        public double DurationSum;
        public readonly Dictionary<RoundEndReason, int> Reasons = new Dictionary<RoundEndReason, int>();
        /// <summary>Per bot, in lineup order.</summary>
        public PlayerTally[] TallyA, TallyB;

        public float WinRateA => Rounds > 0 ? WinsA / (float)Rounds : 0f;
        /// <summary>95% interval half-width of WinRateA (Monte Carlo error over the rounds played).</summary>
        public float HalfWidth => BatchResult.WilsonHalfWidth(WinsA, Rounds);
        public float AttackWinRateA => AttackRoundsA > 0 ? AttackWinsA / (float)AttackRoundsA : 0f;
        public float DefenseWinRateA => DefenseRoundsA > 0 ? DefenseWinsA / (float)DefenseRoundsA : 0f;
        public float MeanDuration => Rounds > 0 ? (float)(DurationSum / Rounds) : 0f;

        public PlayerTally TotalA => Sum(TallyA);
        public PlayerTally TotalB => Sum(TallyB);

        static PlayerTally Sum(PlayerTally[] t)
        {
            var s = new PlayerTally();
            foreach (var x in t) s.Add(x);
            return s;
        }
    }

    public sealed class BotArena
    {
        sealed class Start
        {
            public VrpReplay Replay;
            public SimMap Map;
            public int Round;
            public float Time;
            public int AttackingTeam;
            public int[] SlotOf;        // per player: its index among its team's players, rotated by the round
        }

        readonly List<Start> starts = new List<Start>();

        /// <summary>Recorded rounds the matches start from.</summary>
        public int Rounds => starts.Count;
        public IReadOnlyList<string> Maps { get; }

        /// <summary>Every round of the replays with a barrier drop and known attackers. Maps are built from all the given
        /// replays of each map, learned behaviour included (maps = prebuilt ones by map id, optional).</summary>
        public BotArena(IEnumerable<VrpReplay> replays, IDictionary<string, SimMap> maps = null)
        {
            var list = replays.ToList();
            var built = new Dictionary<string, SimMap>();
            foreach (var g in list.GroupBy(r => r.Map))
                built[g.Key] = maps != null && maps.TryGetValue(g.Key, out var m) ? m : SimMap.FromReplays(g);
            Maps = built.Keys.ToList();
            foreach (var r in list)
            {
                if (r.Players.Any(p => p.Team > 1)) continue;
                for (int ri = 0; ri < r.Rounds.Count; ri++)
                {
                    var rd = r.Rounds[ri];
                    if (rd.BarrierTime < 0f || rd.AttackingTeam > 1 || rd.BarrierTime >= rd.EndTime) continue;
                    var slot = new int[r.Players.Count];
                    var seen = new int[2];
                    int[] size = { r.Players.Count(p => p.Team == 0), r.Players.Count(p => p.Team == 1) };
                    for (int p = 0; p < slot.Length; p++)
                    {
                        int t = r.Players[p].Team;
                        slot[p] = (seen[t]++ + ri) % Math.Max(1, size[t]);
                    }
                    starts.Add(new Start { Replay = r, Map = built[r.Map], Round = ri, Time = rd.BarrierTime, AttackingTeam = rd.AttackingTeam, SlotOf = slot });
                }
            }
        }

        /// <summary>Plays every round runs times with each lineup attacking. threads: 0 = all cores, the result does not
        /// depend on it. sim must match the ExecutionTransfer the bots were made with (null = defaults, ProfileFitter.Transfer).</summary>
        public BotMatchResult Play(BotLineup a, BotLineup b, int runs = 4, int seed = 0, int threads = 0, SimOptions sim = null)
        {
            if (a.Bots.Length == 0 || b.Bots.Length == 0) throw new ArgumentException("empty lineup");
            int perStart = 2 * runs;
            var res = new BotMatchResult { A = a, B = b, TallyA = NewTallies(a.Bots.Length), TallyB = NewTallies(b.Bots.Length) };

            // job i: start i / perStart; A attacks in the first half of a start's jobs
            RoundSim Make(int i)
            {
                var s = starts[i / perStart];
                int k = i % perStart;
                int teamA = k < runs ? s.AttackingTeam : 1 - s.AttackingTeam;
                Profile ProfileOf(int p)
                {
                    var lineup = s.Replay.Players[p].Team == teamA ? a : b;
                    return lineup.Bots[s.SlotOf[p] % lineup.Bots.Length].Profile;
                }
                return RoundSim.FromReplay(s.Replay, s.Map, s.Time, ProfileOf, sim, SimRandom.RunSeed(seed, k, i / perStart), followRecording: false);
            }

            const int block = 512;      // results kept at once
            int total = starts.Count * perStart;
            for (int first = 0; first < total; first += block)
            {
                var results = SimBatch.RunAll(Make, first, Math.Min(block, total - first), threads);
                for (int j = 0; j < results.Length; j++)
                {
                    int i = first + j;
                    var s = starts[i / perStart];
                    bool aAttacks = i % perStart < runs;
                    int teamA = aAttacks ? s.AttackingTeam : 1 - s.AttackingTeam;
                    var r = results[j];
                    bool aWon = r.Winner == teamA;
                    res.Rounds++;
                    if (aWon) res.WinsA++;
                    if (aAttacks) { res.AttackRoundsA++; if (aWon) res.AttackWinsA++; }
                    else { res.DefenseRoundsA++; if (aWon) res.DefenseWinsA++; }
                    if (r.Planted) res.Planted++;
                    res.DurationSum += r.Duration;
                    res.Reasons.TryGetValue(r.Reason, out int c);
                    res.Reasons[r.Reason] = c + 1;

                    var byAgent = new Dictionary<int, PlayerTally>();
                    Metrics.AddRound(r.Kills, r.Players, byAgent);
                    Metrics.AddShooting(r.Shooting, byAgent);
                    foreach (var kv in byAgent)
                    {
                        if (kv.Key < 0 || kv.Key >= s.SlotOf.Length) continue;
                        bool onA = s.Replay.Players[kv.Key].Team == teamA;
                        var into = onA ? res.TallyA : res.TallyB;
                        var t = into[s.SlotOf[kv.Key] % into.Length];
                        t.Add(kv.Value);
                    }
                }
            }
            return res;
        }

        static PlayerTally[] NewTallies(int n)
        {
            var t = new PlayerTally[n];
            for (int i = 0; i < n; i++) t[i] = new PlayerTally();
            return t;
        }
    }
}
