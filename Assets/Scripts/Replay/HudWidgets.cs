using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using ValorantReplay.Playback;

namespace ValoReplay.Replay
{
    internal static class Ui
    {
        public static Label Label(string text, params string[] classes)
        {
            var l = new Label(text);
            foreach (var c in classes) l.AddToClassList(c);
            return l;
        }

        public static VisualElement Box(params string[] classes)
        {
            var e = new VisualElement();
            foreach (var c in classes) e.AddToClassList(c);
            return e;
        }

        /// <summary>Assigns only on change (label text changes re-layout).</summary>
        public static void Text(Label label, string text)
        {
            if (label.text != text) label.text = text;
        }

        public static void Show(VisualElement e, bool show)
        {
            var d = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (e.style.display != d) e.style.display = d;
        }

        public static string TeamClass(int team) => team == 0 ? "rv-team0" : "rv-team1";
        public static string TeamName(int team) => team == 0 ? "RED" : "TEAL";

        public static string Credits(int credits) => credits < 0 ? "–" : credits.ToString("N0");

        public static string Armor(ArmorType type, int value, bool alive)
        {
            string name = type == ArmorType.Heavy ? "HEAVY" : type == ArmorType.Light ? "LIGHT" : type == ArmorType.Regen ? "REGEN" : "";
            if (!alive) return name;
            return value > 0 ? (name.Length > 0 ? $"{name} {value}" : value.ToString()) : name.Length > 0 ? $"{name} 0" : "";
        }
    }

    /// <summary>C / Q / E charge pips with cooldown seconds, plus ult points (X).</summary>
    internal sealed class AbilityChips
    {
        private const int MaxPips = 4;

        private sealed class Chip
        {
            public VisualElement Root;
            public VisualElement[] Pips;
            public Label Cooldown;
        }

        public readonly VisualElement Root;
        private readonly Chip[] _chips;
        private readonly Label _ult;

        public AbilityChips(bool compact)
        {
            Root = Ui.Box("rv-abilities");
            if (compact) Root.AddToClassList("rv-abilities--compact");
            _chips = new Chip[ReplayInsights.AbilitySlots.Length];
            for (int i = 0; i < _chips.Length; i++)
            {
                var chip = new Chip { Root = Ui.Box("rv-ability"), Pips = new VisualElement[MaxPips] };
                chip.Root.Add(Ui.Label(ReplayInsights.AbilitySlots[i], "rv-ability-key"));
                for (int p = 0; p < MaxPips; p++) chip.Root.Add(chip.Pips[p] = Ui.Box("rv-pip"));
                chip.Root.Add(chip.Cooldown = Ui.Label("", "rv-ability-cd"));
                Root.Add(chip.Root);
                _chips[i] = chip;
            }
            Root.Add(_ult = Ui.Label("", "rv-ult"));
        }

        public void Update(VrpReplay r, ReplayInsights insights, int player, float t)
        {
            for (int i = 0; i < _chips.Length; i++)
            {
                var chip = _chips[i];
                var c = r.ChargesAt(player, ReplayInsights.AbilitySlots[i], t);
                int charges = c.Time < 0 ? 0 : c.Charges;
                // always show at least one pip: hollow when empty
                for (int p = 0; p < MaxPips; p++)
                {
                    Ui.Show(chip.Pips[p], p < Mathf.Max(1, charges));
                    chip.Pips[p].EnableInClassList("rv-pip--full", p < charges);
                    chip.Pips[p].EnableInClassList("rv-pip--temp", p >= charges - c.TemporaryCharges && p < charges);
                }
                chip.Root.EnableInClassList("rv-ability--empty", charges == 0);
                bool cooling = c.CooldownEnd > t;
                Ui.Text(chip.Cooldown, cooling ? $"{Mathf.CeilToInt(c.CooldownEnd - t)}s" : "");
                Ui.Show(chip.Cooldown, cooling);
            }

            var stats = r.StatsAt(player, t);
            int points = stats.Time < 0 ? 0 : stats.UltPoints;
            int cost = insights.UltCost(player);
            bool ready = cost > 0 && points >= cost;
            Ui.Text(_ult, ready ? "X READY" : cost > 0 ? $"X {points}/{cost}" : $"X {points}");
            _ult.EnableInClassList("rv-ult--ready", ready);
        }
    }

    /// <summary>A player in the side panels: agent, HP/armor, credits, primary + sidearm, abilities, status effects.</summary>
    internal sealed class PlayerCard
    {
        public readonly VisualElement Root;
        public readonly Label Tag;
        private readonly Label _hp, _armor, _guns, _credits, _status;
        private readonly Button _play;
        private readonly VisualElement _hpFill;
        private readonly AbilityChips _abilities;
        private readonly int _player;
        private int _shownHp = -1;

        public PlayerCard(VrpReplay r, int player, Action onClick, Action onPlay)
        {
            _player = player;
            var p = r.Players[player];
            Root = Ui.Box("rv-player", Ui.TeamClass(p.Team));
            Root.RegisterCallback<ClickEvent>(_ => onClick());
            Root.Add(Ui.Box("rv-player-stripe"));

            var body = Ui.Box("rv-player-body");
            var line1 = Ui.Box("rv-line");
            line1.Add(Ui.Label(p.AgentName, "rv-player-agent"));
            line1.Add(Ui.Label(p.Name, "rv-player-name"));
            line1.Add(_play = new Button(onPlay) { text = "PLAY", tooltip = "Play this round in first person from here" });
            _play.AddToClassList("rv-play-button");
            _play.RegisterCallback<ClickEvent>(e => e.StopPropagation());   // not a follow click
            line1.Add(_hp = Ui.Label("", "rv-player-hp"));
            body.Add(line1);

            var bar = Ui.Box("rv-hp-bar");
            bar.Add(_hpFill = Ui.Box("rv-hp-fill"));
            body.Add(bar);

            var line2 = Ui.Box("rv-line");
            line2.Add(_guns = Ui.Label("", "rv-player-guns"));
            line2.Add(_armor = Ui.Label("", "rv-player-armor"));
            body.Add(line2);

            var line3 = Ui.Box("rv-line");
            line3.Add(_credits = Ui.Label("", "rv-credits"));
            line3.Add((_abilities = new AbilityChips(compact: true)).Root);
            body.Add(line3);

            body.Add(_status = Ui.Label("", "rv-status"));
            Root.Add(body);

            Tag = Ui.Label(p.AgentName, "rv-tag", Ui.TeamClass(p.Team));
            Tag.pickingMode = PickingMode.Ignore;
        }

        public void Update(ReplayViewer v, float t)
        {
            var r = v.Replay;
            var f = r.Sample(_player, t);
            bool alive = f.Alive;
            // the round AI's health and armor when it plays this player
            int drivenHp = 0, drivenArmor = 0;
            bool driven = v.Driver != null && v.Driver.TryGetStatus(_player, out drivenHp, out drivenArmor, out _);
            if (driven) alive = drivenHp > 0;
            Root.EnableInClassList("rv-player--dead", !alive);
            _play.SetEnabled(alive);
            Root.EnableInClassList("rv-player--followed", v.FollowPlayer == _player);

            int hp = !alive ? 0 : driven ? drivenHp : f.Health;
            if (hp != _shownHp)
            {
                _shownHp = hp;
                _hp.text = alive ? hp.ToString() : "DEAD";
                _hpFill.style.width = Length.Percent(Mathf.Clamp01(hp / 100f) * 100f);
            }
            string armor = Ui.Armor(v.Insights.ArmorAt(_player, t), driven ? drivenArmor : f.Armor, alive);
            if (alive && !driven && f.Shield > 0) armor += $" +{f.Shield}";
            Ui.Text(_armor, armor);

            v.Insights.GunsAt(_player, t, out string primary, out string sidearm);
            string guns = primary.Length > 0 && sidearm.Length > 0 ? $"{WeaponNames.Pretty(primary)}  ·  {WeaponNames.Pretty(sidearm)}"
                : primary.Length > 0 ? WeaponNames.Pretty(primary) : WeaponNames.Pretty(sidearm);
            Ui.Text(_guns, guns);

            Ui.Text(_credits, Ui.Credits(r.StatsAt(_player, t).Credits));
            _abilities.Update(r, v.Insights, _player, t);

            string status = alive && !driven ? StatusText(r, t) : "";
            Ui.Text(_status, status);
            Ui.Show(_status, status.Length > 0);
        }

        private string StatusText(VrpReplay r, float t)
        {
            var parts = new List<string>();
            if (r.SpikeHolderAt(t) == _player) parts.Add("SPIKE");
            foreach (var e in r.StatusEffectsAt(_player, t))
            {
                if (e.IsPing) continue;
                string name = e.Type.ToString().ToUpperInvariant();
                if (!parts.Contains(name)) parts.Add(name);
            }
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Tab overlay: both teams sorted by ACS, with K/D/A, loadout, armor, credits, abilities and ult.</summary>
    internal sealed class ScoreboardView
    {
        private sealed class Row
        {
            public int Player, Team;
            public VisualElement Root;
            public Label Acs, K, D, A, Diff, Primary, Sidearm, Armor, Credits;
            public AbilityChips Abilities;
            public float SortKey;
        }

        private sealed class TeamHeader
        {
            public Label Name, Score, Bank, Loadout;
        }

        private readonly VisualElement _overlay;
        private readonly Label _title;
        private readonly VisualElement[] _teamBodies = new VisualElement[2];
        private readonly TeamHeader[] _headers = new TeamHeader[2];
        private readonly List<Row> _rows = new List<Row>();

        public ScoreboardView(VisualElement overlay, VrpReplay r)
        {
            _overlay = overlay;
            _overlay.Clear();
            var panel = Ui.Box("rv-sb-panel");
            _overlay.Add(panel);

            var titleRow = Ui.Box("rv-sb-title-row");
            titleRow.Add(_title = Ui.Label("", "rv-sb-title"));
            titleRow.Add(Ui.Label("Hold Tab", "rv-sb-hint"));
            panel.Add(titleRow);

            panel.Add(HeaderRow());
            for (int team = 0; team < 2; team++)
            {
                var h = new TeamHeader();
                var teamRow = Ui.Box("rv-sb-team", Ui.TeamClass(team));
                teamRow.Add(h.Name = Ui.Label("", "rv-sb-team-name"));
                teamRow.Add(h.Score = Ui.Label("", "rv-sb-team-score"));
                teamRow.Add(Ui.Box("rv-grow"));
                teamRow.Add(h.Loadout = Ui.Label("", "rv-sb-team-stat"));
                teamRow.Add(h.Bank = Ui.Label("", "rv-sb-team-stat"));
                panel.Add(teamRow);
                _headers[team] = h;
                panel.Add(_teamBodies[team] = Ui.Box("rv-sb-body"));
            }

            for (int i = 0; i < r.Players.Count; i++)
            {
                var row = BuildRow(r, i);
                _rows.Add(row);
                _teamBodies[row.Team == 0 ? 0 : 1].Add(row.Root);
            }
            Visible = false;
        }

        public bool Visible
        {
            get => _overlay.style.display == DisplayStyle.Flex;
            set => Ui.Show(_overlay, value);
        }

        private static VisualElement HeaderRow()
        {
            var h = Ui.Box("rv-sb-row", "rv-sb-header");
            h.Add(Ui.Label("AGENT", "rv-sb-agent"));
            foreach (var c in new[] { "ACS", "K", "D", "A", "+/-" }) h.Add(Ui.Label(c, "rv-sb-num"));
            h.Add(Ui.Label("PRIMARY", "rv-sb-gun"));
            h.Add(Ui.Label("SIDEARM", "rv-sb-gun"));
            h.Add(Ui.Label("ARMOR", "rv-sb-armor"));
            h.Add(Ui.Label("CREDITS", "rv-sb-credits"));
            h.Add(Ui.Label("ABILITIES", "rv-sb-abilities"));
            return h;
        }

        private static Row BuildRow(VrpReplay r, int i)
        {
            var p = r.Players[i];
            var row = new Row { Player = i, Team = p.Team, Root = Ui.Box("rv-sb-row", Ui.TeamClass(p.Team)) };
            var agent = Ui.Box("rv-sb-agent");
            agent.Add(Ui.Box("rv-sb-stripe"));
            agent.Add(Ui.Label(p.AgentName, "rv-sb-agent-name"));
            agent.Add(Ui.Label(p.Name, "rv-sb-player-name"));
            row.Root.Add(agent);
            row.Root.Add(row.Acs = Ui.Label("", "rv-sb-num", "rv-sb-acs"));
            row.Root.Add(row.K = Ui.Label("", "rv-sb-num"));
            row.Root.Add(row.D = Ui.Label("", "rv-sb-num"));
            row.Root.Add(row.A = Ui.Label("", "rv-sb-num"));
            row.Root.Add(row.Diff = Ui.Label("", "rv-sb-num"));
            row.Root.Add(row.Primary = Ui.Label("", "rv-sb-gun"));
            row.Root.Add(row.Sidearm = Ui.Label("", "rv-sb-gun"));
            row.Root.Add(row.Armor = Ui.Label("", "rv-sb-armor"));
            row.Root.Add(row.Credits = Ui.Label("", "rv-sb-credits", "rv-credits"));
            var abilities = Ui.Box("rv-sb-abilities");
            abilities.Add((row.Abilities = new AbilityChips(compact: false)).Root);
            row.Root.Add(abilities);
            return row;
        }

        public void Update(ReplayViewer v, float t)
        {
            var r = v.Replay;
            int round = r.RoundAt(t);
            Ui.Text(_title, $"{r.MapName.ToUpperInvariant()}  ·  {(round >= 0 ? $"ROUND {r.Rounds[round].Number + 1}" : "PRE-GAME")}");

            int[] wins = new int[2], bank = new int[2];
            foreach (var rd in r.Rounds)
                if (rd.EndTime <= t && rd.WinningTeam < 2) wins[rd.WinningTeam]++;

            foreach (var row in _rows)
            {
                var s = r.StatsAt(row.Player, t);
                var f = r.Sample(row.Player, t);
                bool known = s.Time >= 0;
                float acs = r.AverageCombatScore(row.Player, t);
                row.SortKey = acs;
                if (known && s.Credits > 0 && row.Team < 2) bank[row.Team] += s.Credits;

                Ui.Text(row.Acs, known ? Mathf.RoundToInt(acs).ToString() : "–");
                Ui.Text(row.K, known ? s.Kills.ToString() : "–");
                Ui.Text(row.D, known ? s.Deaths.ToString() : "–");
                Ui.Text(row.A, known ? s.Assists.ToString() : "–");
                int diff = s.Kills - s.Deaths;
                Ui.Text(row.Diff, known ? (diff > 0 ? "+" + diff : diff.ToString()) : "–");
                row.Diff.EnableInClassList("rv-sb-pos", diff > 0);
                row.Diff.EnableInClassList("rv-sb-neg", diff < 0);

                v.Insights.GunsAt(row.Player, t, out string primary, out string sidearm);
                Ui.Text(row.Primary, WeaponNames.Pretty(primary));
                Ui.Text(row.Sidearm, WeaponNames.Pretty(sidearm));
                Ui.Text(row.Armor, Ui.Armor(v.Insights.ArmorAt(row.Player, t), f.Armor, f.Alive));
                Ui.Text(row.Credits, Ui.Credits(known ? s.Credits : -1));
                row.Abilities.Update(r, v.Insights, row.Player, t);
                row.Root.EnableInClassList("rv-sb-row--dead", !f.Alive);
                row.Root.EnableInClassList("rv-sb-row--followed", v.FollowPlayer == row.Player);
            }

            for (int team = 0; team < 2; team++)
            {
                var h = _headers[team];
                string role = r.RoleOf(team, round) == TeamRole.Attacking ? "ATTACK" : r.RoleOf(team, round) == TeamRole.Defending ? "DEFENSE" : "";
                Ui.Text(h.Name, role.Length > 0 ? $"{Ui.TeamName(team)}  ·  {role}" : Ui.TeamName(team));
                Ui.Text(h.Score, wins[team].ToString());
                var values = round >= 0 ? r.Rounds[round].TeamLoadoutValue : null;
                Ui.Text(h.Loadout, values != null && team < values.Length ? $"LOADOUT {values[team]:N0}" : "");
                Ui.Text(h.Bank, $"BANK {bank[team]:N0}");
                SortTeam(_teamBodies[team], team);
            }
        }

        /// <summary>Highest ACS first; only touches the hierarchy when the order changed.</summary>
        private void SortTeam(VisualElement body, int team)
        {
            var ordered = _rows.Where(x => x.Team == team).OrderByDescending(x => x.SortKey).ThenBy(x => x.Player).ToList();
            for (int i = 0; i < ordered.Count; i++)
                if (body.IndexOf(ordered[i].Root) != i) body.Insert(i, ordered[i].Root);
        }
    }
}
