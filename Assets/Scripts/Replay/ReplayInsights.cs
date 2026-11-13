using System.Collections.Generic;
using ValorantReplay.Playback;

namespace ValoReplay.Replay
{
    /// <summary>
    /// Derived per-player state the .vrp does not store directly, precomputed once per replay:
    /// the primary/sidearm a player carries at any time (round loadout, then pickups seen in hand, cleared on death),
    /// and each player's ult cost (their ult points just before they used it).
    /// </summary>
    public sealed class ReplayInsights
    {
        public static readonly string[] AbilitySlots = { "C", "Q", "E" };

        private struct GunState { public float Time; public string Primary, Sidearm; }

        private readonly VrpReplay _replay;
        private readonly List<GunState>[] _guns;
        private readonly int[] _ultCost;

        public ReplayInsights(VrpReplay replay)
        {
            _replay = replay;
            int n = replay.Players.Count;
            _guns = new List<GunState>[n];
            _ultCost = new int[n];
            for (int p = 0; p < n; p++) _guns[p] = new List<GunState>();
            BuildGuns();
            BuildUltCosts();
        }

        /// <summary>Primary and sidearm classes at a time ("" = none).</summary>
        public void GunsAt(int player, float time, out string primary, out string sidearm)
        {
            primary = sidearm = "";
            foreach (var g in _guns[player])
            {
                if (g.Time > time) break;
                primary = g.Primary;
                sidearm = g.Sidearm;
            }
        }

        /// <summary>Ult points needed, learned from the player's own ult uses; 0 = unknown (never used it).</summary>
        public int UltCost(int player) => _ultCost[player];

        public bool UltReady(int player, float time)
        {
            int cost = _ultCost[player];
            return cost > 0 && _replay.StatsAt(player, time).UltPoints >= cost;
        }

        /// <summary>Armor bought for the round a time falls in; None during the buy phase (the loadout is only
        /// known from the barrier drop, so earlier it would give away the purchase).</summary>
        public ArmorType ArmorAt(int player, float time)
        {
            int ri = _replay.RoundAt(time);
            if (ri < 0 || _replay.Rounds[ri].BarrierTime < 0 || time < _replay.Rounds[ri].BarrierTime) return ArmorType.None;
            return _replay.LoadoutOf(ri, player)?.Armor ?? ArmorType.None;
        }

        private void BuildGuns()
        {
            var r = _replay;
            // classify guns by where they show up in loadouts; abilities and ult guns never appear there
            var primaries = new HashSet<string>();
            var sidearms = new HashSet<string>();
            foreach (var round in r.Rounds)
                foreach (var l in round.Loadouts)
                {
                    if (l.Primary.Length > 0) primaries.Add(l.Primary);
                    if (l.Sidearm.Length > 0) sidearms.Add(l.Sidearm);
                }
            foreach (var g in WeaponNames.KnownSidearms) sidearms.Add(g);
            foreach (var g in WeaponNames.KnownPrimaries) primaries.Add(g);

            for (int p = 0; p < r.Players.Count; p++)
            {
                var list = _guns[p];
                string primary = "", sidearm = "";
                bool dead = true;
                for (int ri = 0; ri < r.Rounds.Count; ri++)
                {
                    var round = r.Rounds[ri];
                    // buy phase: survivors keep their guns, everyone else starts with the free Classic; purchases show up
                    // as they are equipped, and the barrier-drop loadout is authoritative from then on
                    if (dead || ri == 0 || IsSideSwitch(ri)) { primary = ""; sidearm = "BasePistol"; }
                    list.Add(new GunState { Time = round.StartTime, Primary = primary, Sidearm = sidearm });
                    var l = r.LoadoutOf(ri, p);
                    bool loadoutApplied = l == null || round.BarrierTime < 0;

                    float end = ri + 1 < r.Rounds.Count ? r.Rounds[ri + 1].StartTime : r.Duration;
                    dead = false;
                    for (int t = r.TickAt(round.StartTime); t < r.TickCount && t / r.TickRate < end; t++)
                    {
                        var f = r.GetFrame(t, p);
                        float time = t / r.TickRate;
                        if (!loadoutApplied && time >= round.BarrierTime)
                        {
                            loadoutApplied = true;
                            primary = l.Primary;
                            sidearm = l.Sidearm;
                            list.Add(new GunState { Time = round.BarrierTime, Primary = primary, Sidearm = sidearm });
                        }
                        if (!f.Alive)
                        {
                            // ignore the respawn blink at round start and the reset after the round ended
                            if (!dead && time > round.StartTime + 1f && (round.EndTime < 0 || time < round.EndTime))
                            {
                                dead = true;
                                primary = sidearm = "";
                                list.Add(new GunState { Time = time, Primary = "", Sidearm = "" });
                            }
                            continue;
                        }
                        dead = false;
                        string w = r.WeaponOf(f);
                        if (w.Length == 0) continue;
                        if (primaries.Contains(w) && w != primary) primary = w;
                        else if (sidearms.Contains(w) && w != sidearm) sidearm = w;
                        else continue;
                        list.Add(new GunState { Time = time, Primary = primary, Sidearm = sidearm });
                    }
                }
            }
        }

        /// <summary>Sides swap (halftime, overtime): everyone's guns are reset.</summary>
        private bool IsSideSwitch(int roundIndex)
        {
            var rounds = _replay.Rounds;
            return roundIndex > 0 && rounds[roundIndex].AttackingTeam != 255 && rounds[roundIndex - 1].AttackingTeam != 255 &&
                   rounds[roundIndex].AttackingTeam != rounds[roundIndex - 1].AttackingTeam;
        }

        private void BuildUltCosts()
        {
            foreach (var e in _replay.Events)
            {
                if (e.Type != MatchEventType.UltimateUsed || e.Player >= _ultCost.Length) continue;
                int best = 0;
                foreach (var s in _replay.PlayerStats)
                {
                    if (s.Time > e.Time) break;
                    if (s.Player == e.Player && s.Time >= e.Time - 10f && s.UltPoints > best) best = s.UltPoints;
                }
                if (best > _ultCost[e.Player]) _ultCost[e.Player] = best;
            }
        }
    }
}
