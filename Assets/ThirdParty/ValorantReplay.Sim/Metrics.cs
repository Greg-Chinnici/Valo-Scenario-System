// Metrics.cs - round statistics with one definition for recorded and simulated rounds, so a profile fitted from a
// replay and a batch of simulated rounds can be compared number for number. Kill stats come from kill lists
// (Metrics.AddRound); shooting stats from Measurements (recorded) and RoundSim's own counters (simulated).
using System.Collections.Generic;

namespace ValorantReplay.Sim
{
    public sealed class PlayerTally
    {
        public int Rounds, Kills, Deaths, FirstKills, FirstDeaths, Trades, Traded;

        // shooting (Measurements): bullets of guns that count for accuracy (no shotguns or ability guns) and the hits on
        // enemies they made; notices = an enemy came into view (line of sight and field of view, after Measurements.NoticeGap
        // out of it); reactions = notices answered by a shot at that enemy within Measurements.ReactionWindow
        public int Bullets, Hits, HeadHits, LegHits;
        public int Notices, Reactions;
        public double ReactionSum, AimErrorSum;

        public float FirstDeathRate => Rounds > 0 ? FirstDeaths / (float)Rounds : 0f;
        public float FirstKillRate => Rounds > 0 ? FirstKills / (float)Rounds : 0f;
        /// <summary>Rounds in which this player took the opening duel, won or lost.</summary>
        public float OpeningRate => Rounds > 0 ? (FirstKills + FirstDeaths) / (float)Rounds : 0f;
        /// <summary>Share of this player's kills that avenged a teammate.</summary>
        public float TradeRate => Kills > 0 ? Trades / (float)Kills : 0f;
        /// <summary>Share of this player's deaths that a teammate avenged.</summary>
        public float TradedRate => Deaths > 0 ? Traded / (float)Deaths : 0f;
        /// <summary>Share of bullets that hit an enemy.</summary>
        public float Accuracy => Bullets > 0 ? Hits / (float)Bullets : 0f;
        /// <summary>Share of hits on the head (every hit, not only killing blows).</summary>
        public float HeadHitRate => Hits > 0 ? HeadHits / (float)Hits : 0f;
        public float LegHitRate => Hits > 0 ? LegHits / (float)Hits : 0f;
        /// <summary>Mean seconds from noticing an enemy to the first shot at it, over the notices that got one.</summary>
        public float ReactionMean => Reactions > 0 ? (float)(ReactionSum / Reactions) : 0f;
        /// <summary>Share of notices answered by a shot at that enemy in time.</summary>
        public float ShootOnNoticeRate => Notices > 0 ? Reactions / (float)Notices : 0f;
        /// <summary>Mean horizontal degrees between the view and the enemy at the notice (crosshair placement).</summary>
        public float AimErrorMean => Notices > 0 ? (float)(AimErrorSum / Notices) : 0f;

        public void Add(PlayerTally o)
        {
            Rounds += o.Rounds; Kills += o.Kills; Deaths += o.Deaths; FirstKills += o.FirstKills;
            FirstDeaths += o.FirstDeaths; Trades += o.Trades; Traded += o.Traded;
            AddShooting(o);
        }

        public void AddShooting(PlayerTally o)
        {
            Bullets += o.Bullets; Hits += o.Hits; HeadHits += o.HeadHits; LegHits += o.LegHits;
            Notices += o.Notices; Reactions += o.Reactions; ReactionSum += o.ReactionSum; AimErrorSum += o.AimErrorSum;
        }

        public override string ToString() =>
            $"rounds {Rounds} K {Kills} D {Deaths} first kill {FirstKillRate:P0} first death {FirstDeathRate:P0} trades {TradeRate:P0} traded {TradedRate:P0}";
    }

    public static class Metrics
    {
        public const float TradeWindow = 3f;

        /// <summary>Adds one round. players = everyone who played the round (alive at its start); team kills and deaths
        /// without a killer count as deaths only.</summary>
        public static void AddRound(IList<KillRecord> kills, IEnumerable<int> players, IDictionary<int, PlayerTally> into, float window = TradeWindow)
        {
            foreach (int p in players) Get(into, p).Rounds++;
            bool opened = false;
            for (int i = 0; i < kills.Count; i++)
            {
                var k = kills[i];
                Get(into, k.Victim).Deaths++;
                if (k.Killer < 0 || k.KillerTeam == k.VictimTeam) continue;
                var killer = Get(into, k.Killer);
                killer.Kills++;
                if (!opened)
                {
                    opened = true;
                    killer.FirstKills++;
                    Get(into, k.Victim).FirstDeaths++;
                }
                if (IsTrade(kills, i, window)) killer.Trades++;
                if (WasTraded(kills, i, window)) Get(into, k.Victim).Traded++;
            }
        }

        /// <summary>Kill i avenged a teammate: its victim killed someone on the killer's team within window before.</summary>
        public static bool IsTrade(IList<KillRecord> kills, int i, float window = TradeWindow)
        {
            var k = kills[i];
            for (int j = i - 1; j >= 0 && k.Time - kills[j].Time <= window; j--)
                if (kills[j].Killer == k.Victim && kills[j].VictimTeam == k.KillerTeam) return true;
            return false;
        }

        /// <summary>Kill i's victim was avenged: its killer died to the victim's team within window after.</summary>
        public static bool WasTraded(IList<KillRecord> kills, int i, float window = TradeWindow)
        {
            var k = kills[i];
            for (int j = i + 1; j < kills.Count && kills[j].Time - k.Time <= window; j++)
                if (kills[j].Victim == k.Killer && kills[j].KillerTeam == k.VictimTeam) return true;
            return false;
        }

        /// <summary>Adds each agent's shooting counters of a simulated round (RoundResult.Shooting, indexed by agent).</summary>
        public static void AddShooting(IList<PlayerTally> shooting, IDictionary<int, PlayerTally> into)
        {
            for (int p = 0; p < shooting.Count; p++) Get(into, p).AddShooting(shooting[p]);
        }

        internal static PlayerTally Get(IDictionary<int, PlayerTally> d, int p)
        {
            if (!d.TryGetValue(p, out var t)) d[p] = t = new PlayerTally();
            return t;
        }
    }
}
