using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using ValorantReplay.Playback;
using ValoReplay.Replay;

namespace ValoReplay.Play
{
    /// <summary>
    /// Round play HUD: round clock and score, kill feed (recorded kills plus yours), crosshair that opens with spread,
    /// hit markers and damage, your HP / armor / credits, ability charges, and the gun in hand with ammo and reload.
    /// Layout: Assets/UI/RoundPlay/RoundPlay.uxml.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class RoundPlayHud : MonoBehaviour
    {
        [SerializeField] private RoundPlayController round;
        [Tooltip("Pixels of crosshair gap per degree of spread (when the crosshair shows spread).")]
        [SerializeField] private float gapPerDegree = 9f;
        [SerializeField] private float killFeedSeconds = 6f;
        [SerializeField] private int killFeedMax = 6;

        private struct FeedKill
        {
            public float Time;
            public int Killer, Victim;
            public string Weapon;
            public bool Headshot, Yours;
        }

        private readonly List<FeedKill> _myKills = new List<FeedKill>();
        private readonly List<FeedKill> _feedRows = new List<FeedKill>();
        private string _feedKey = "";

        private Label _specName, _specInfo;
        private VisualElement _root, _chTop, _chBottom, _chLeft, _chRight, _chDot, _hitmarker, _hpFill, _reloadFill, _slots, _feed;
        private Label _playingAs, _score, _timer, _phase, _hitText, _message, _hp, _armor, _credits, _weaponName, _ammo, _reserve;
        private AbilityChips _abilities;
        private float _hitUntil;

        private void OnEnable()
        {
            if (round == null) round = FindFirstObjectByType<RoundPlayController>();
            _root = GetComponent<UIDocument>().rootVisualElement;
            _root.pickingMode = PickingMode.Ignore;
            _chTop = _root.Q("ch-top");
            _chBottom = _root.Q("ch-bottom");
            _chLeft = _root.Q("ch-left");
            _chRight = _root.Q("ch-right");
            _chDot = _root.Q("ch-dot");
            _hitmarker = _root.Q("hitmarker");
            _hpFill = _root.Q("hp-fill");
            _reloadFill = _root.Q("reload-fill");
            _slots = _root.Q("slots");
            _feed = _root.Q("feed");
            _playingAs = _root.Q<Label>("playing-as");
            _score = _root.Q<Label>("score");
            _timer = _root.Q<Label>("timer");
            _phase = _root.Q<Label>("phase");
            _hitText = _root.Q<Label>("hit-text");
            _message = _root.Q<Label>("message");
            _hp = _root.Q<Label>("hp");
            _armor = _root.Q<Label>("armor");
            _credits = _root.Q<Label>("credits");
            _weaponName = _root.Q<Label>("weapon-name");
            _ammo = _root.Q<Label>("ammo");
            _reserve = _root.Q<Label>("reserve");
            _specName = _root.Q<Label>("spec-name");
            _specInfo = _root.Q<Label>("spec-info");
            _abilities = new AbilityChips(compact: false);
            _root.Q("abilities").Add(_abilities.Root);
            Ui.Show(_message, false);

            if (round != null)
            {
                round.Ready += OnReady;
                round.PlayerHit += OnPlayerHit;
                round.TeammateHit += OnTeammateHit;
            }
        }

        private void OnDisable()
        {
            if (round == null) return;
            round.Ready -= OnReady;
            round.PlayerHit -= OnPlayerHit;
            round.TeammateHit -= OnTeammateHit;
        }

        private void OnReady()
        {
            if (round.Replay == null) { ShowMessage(round.Error); return; }
            var p = round.Replay.Players[round.Player];
            _playingAs.text = $"PLAYING AS {p.AgentName.ToUpperInvariant()}{(p.Name.Length > 0 ? "  ·  " + p.Name : "")}";
            _playingAs.EnableInClassList("rv-team0-text", p.Team == 0);
            if (round.Error != null) ShowMessage(round.Error, 6f);
        }

        private float _messageUntil = -1f;
        private bool _roundOverShown;

        private void ShowMessage(string text, float seconds = -1f)
        {
            _message.text = text ?? "";
            Ui.Show(_message, !string.IsNullOrEmpty(text));
            _messageUntil = seconds > 0f ? Time.time + seconds : -1f;
        }

        private void OnPlayerHit(int player, Region region, float damage, bool kill)
        {
            var name = round.Replay.Players[player].AgentName;
            _hitUntil = Time.time + (kill ? 0.5f : 0.18f);
            _hitmarker.EnableInClassList("rp-hitmarker--head", region == Region.Head);
            _hitmarker.EnableInClassList("rp-hitmarker--kill", kill);
            _hitText.text = kill ? $"{Mathf.RoundToInt(damage)}  ELIMINATED {name.ToUpperInvariant()}" : $"{Mathf.RoundToInt(damage)}{(region == Region.Head ? "  HEAD" : "")}";
            _hitText.EnableInClassList("rp-hit-text--head", region == Region.Head);
            _hitText.EnableInClassList("rp-hit-text--kill", kill);
            if (kill && round.Ai == null)          // with the AI, the sim's kill list has yours too
                _myKills.Add(new FeedKill
                {
                    Time = round.Now, Killer = round.Player, Victim = player, Weapon = round.Weapons.Equipped.Stats.Class,
                    Headshot = region == Region.Head, Yours = true,
                });
        }

        private void OnTeammateHit(int player)
        {
            _hitText.text = $"TEAMMATE ({round.Replay.Players[player].AgentName})";
            _hitText.EnableInClassList("rp-hit-text--head", false);
            _hitText.EnableInClassList("rp-hit-text--kill", false);
            _hitUntil = Time.time + 0.4f;
        }

        private void Update()
        {
            if (round == null || round.Replay == null || round.Fps == null) return;
            var r = round.Replay;
            float t = round.Now;

            // clock: the recording's, or the AI round's once it runs (it can outlast the recorded round)
            var ai = round.Ai != null && round.Ai.Started ? round.Ai : null;
            int ri = ai != null ? r.RoundAt(round.TakeoverTime) : r.RoundAt(t);
            int wins0 = 0, wins1 = 0;
            for (int i = 0; i < r.Rounds.Count; i++)
            {
                var rd = r.Rounds[i];
                bool done = ai != null ? i < ri : rd.EndTime <= t;
                if (done && rd.WinningTeam < 2) { if (rd.WinningTeam == 0) wins0++; else wins1++; }
            }
            if (ai != null && ai.Sim.Over && ai.Sim.Winner >= 0) { if (ai.Sim.Winner == 0) wins0++; else wins1++; }
            int mine = round.Team == 0 ? wins0 : wins1, theirs = round.Team == 0 ? wins1 : wins0;
            Ui.Text(_score, $"ROUND {(ri >= 0 ? r.Rounds[ri].Number + 1 : 0)}   ·   {mine} - {theirs}");
            bool planted;
            float remaining;
            string phase;
            if (ai != null)
            {
                var sim = ai.Sim;
                planted = sim.SpikeState == ValorantReplay.Sim.SpikeStatus.Planted;
                remaining = planted ? sim.SpikeTimer : sim.Remaining;
                phase = planted ? "SPIKE PLANTED" : sim.SpikeState == ValorantReplay.Sim.SpikeStatus.Defused ? "SPIKE DEFUSED"
                    : sim.SpikeState == ValorantReplay.Sim.SpikeStatus.Detonated ? "DETONATED" : "COMBAT";
            }
            else
            {
                var state = r.RoundStateAt(t);
                planted = state.Spike == SpikeState.Planted;
                remaining = state.Remaining;
                phase = planted ? "SPIKE PLANTED" : state.Phase == RoundPhase.Unknown ? "" : state.Phase.ToString().ToUpperInvariant();
            }
            int s = Mathf.Max(0, Mathf.CeilToInt(remaining));
            Ui.Text(_timer, $"{s / 60}:{s % 60:00}");
            Ui.Text(_phase, phase);
            _phase.EnableInClassList("rp-phase--planted", planted);
            UpdateTaskHint();

            // you
            Ui.Text(_hp, round.Health.ToString());
            _hpFill.style.width = Length.Percent(Mathf.Clamp01(round.Health / 100f) * 100f);
            string armor = Ui.Armor(round.Insights.ArmorAt(round.Player, round.TakeoverTime), round.Armor, true);
            Ui.Text(_armor, round.Shield > 0 ? $"{armor} +{round.Shield}" : armor);
            Ui.Text(_credits, $"{round.Credits:N0} CREDITS");
            // charges as they were at the takeover (casting is not implemented yet)
            _abilities.Update(r, round.Insights, round.Player, round.TakeoverTime);

            UpdateKillFeed(t);
            UpdateSpectating(t);
            UpdateWeapon();
            UpdateCrosshair();

            float hitAlpha = Mathf.Clamp01((_hitUntil - Time.time) * 6f);
            _hitmarker.style.opacity = hitAlpha;
            _hitText.style.opacity = hitAlpha;

            if (round.RoundOver && !_roundOverShown)
            {
                _roundOverShown = true;
                ShowMessage($"{round.ResultText ?? "ROUND OVER"}  ·  ESC to return to the replay");
            }
            else if (round.Dead && !_deathShown && !round.RoundOver)
            {
                _deathShown = true;
                ShowMessage("YOU DIED  ·  V to spectate", 4f);
            }
            else if (_messageUntil > 0f && Time.time > _messageUntil) ShowMessage(null);
        }

        private bool _deathShown;
        private Label _task;

        /// <summary>Plant / defuse prompt and progress, under the crosshair.</summary>
        private void UpdateTaskHint()
        {
            if (_task == null)
            {
                _task = Ui.Label("", "rp-task");
                _task.pickingMode = PickingMode.Ignore;
                _task.style.position = Position.Absolute;
                _task.style.left = 0;
                _task.style.right = 0;
                _task.style.top = Length.Percent(60);
                _task.style.unityTextAlign = TextAnchor.MiddleCenter;
                _task.style.fontSize = 18;
                _task.style.color = new Color(1f, 0.85f, 0.4f);
                _root.Add(_task);
            }
            string hint = round.Spectating ? null : round.TaskHint;
            Ui.Text(_task, hint ?? "");
            Ui.Show(_task, !string.IsNullOrEmpty(hint));
        }

        /// <summary>While spectating: who you are watching, with their recorded HP, armor and weapon.</summary>
        private void UpdateSpectating(float t)
        {
            var spec = round.Spectator;
            bool on = spec != null && spec.Active && spec.Target >= 0;
            _root.EnableInClassList("rp-root--spectating", on);
            if (!on) return;
            var r = round.Replay;
            var p = r.Players[spec.Target];
            bool original = spec.Target == round.Player;
            Ui.Text(_specName, original ? $"{p.AgentName.ToUpperInvariant()}  ·  YOUR ORIGINAL RUN" : p.AgentName.ToUpperInvariant());
            _specName.EnableInClassList("rp-spec-name--original", original);
            _specName.EnableInClassList("rp-spec-name--ally", !original && p.Team == round.Team);
            _specName.EnableInClassList("rp-spec-name--enemy", p.Team != round.Team);

            if (round.Ai != null && round.Ai.TryGetStatus(spec.Target, out int aiHp, out int aiArmor, out string aiGun))
            {
                if (aiHp <= 0) { Ui.Text(_specInfo, "DEAD"); return; }
                Ui.Text(_specInfo, $"{aiHp} HP{(aiArmor > 0 ? $"  ·  {aiArmor} ARMOR" : "")}  ·  {WeaponNames.Pretty(aiGun)}  ·  AI");
                return;
            }
            var f = r.Sample(spec.Target, t);
            if (!f.Alive) { Ui.Text(_specInfo, "DEAD"); return; }
            string weapon = WeaponNames.Pretty(r.WeaponOf(f));
            string ammo = f.Ammo == 255 ? "" : f.HasInfiniteReserve ? $"  {f.Ammo} / INF" : f.ReserveAmmo == 255 ? $"  {f.Ammo}" : $"  {f.Ammo} / {f.ReserveAmmo}";
            string armor = f.Armor > 0 ? $"  ·  {f.Armor} ARMOR" : "";
            Ui.Text(_specInfo, $"{f.Health} HP{armor}  ·  {weapon}{ammo}");
        }

        /// <summary>
        /// Recorded kills of the last few seconds plus your simulated ones. After the takeover, recorded kills by or of
        /// the agent you play are left out: that part of the recording no longer happens.
        /// </summary>
        private void UpdateKillFeed(float t)
        {
            var r = round.Replay;
            var ai = round.Ai;
            // with the AI, the recording only up to where the sim starts; the sim has every kill after that
            float recordedUntil = ai != null ? ai.SourceStart : float.PositiveInfinity;
            _feedRows.Clear();
            foreach (var k in r.Kills)
            {
                if (k.Time > t || k.Time > recordedUntil) break;
                if (k.Time < t - killFeedSeconds) continue;
                if (k.Time >= round.TakeoverTime && (k.Killer == round.Player || k.Victim == round.Player)) continue;
                _feedRows.Add(new FeedKill { Time = k.Time, Killer = k.Killer, Victim = k.Victim, Weapon = k.Weapon, Headshot = k.Headshot });
            }
            if (ai != null)
                foreach (var k in ai.Kills)
                {
                    if (k.Time > t || k.Time < t - killFeedSeconds) continue;
                    _feedRows.Add(new FeedKill { Time = k.Time, Killer = k.Killer, Victim = k.Victim, Weapon = k.Weapon, Headshot = k.Headshot, Yours = k.Killer == round.Player });
                }
            foreach (var k in _myKills)
                if (k.Time <= t && k.Time >= t - killFeedSeconds) _feedRows.Add(k);
            _feedRows.Sort((a, b) => a.Time.CompareTo(b.Time));
            if (_feedRows.Count > killFeedMax) _feedRows.RemoveRange(0, _feedRows.Count - killFeedMax);

            string key = _feedRows.Count == 0 ? "" : $"{_feedRows.Count}:{_feedRows[0].Time}:{_feedRows[_feedRows.Count - 1].Time}";
            if (key == _feedKey) return;
            _feedKey = key;
            _feed.Clear();
            foreach (var k in _feedRows)
            {
                var row = Ui.Box("rp-kill");
                row.pickingMode = PickingMode.Ignore;
                row.EnableInClassList("rp-kill--yours", k.Yours);
                row.Add(KillName(k.Killer));
                row.Add(Ui.Label(WeaponNames.Pretty(k.Weapon), "rp-kill-weapon"));
                if (k.Headshot) row.Add(Ui.Label("HS", "rp-kill-hs"));
                row.Add(KillName(k.Victim));
                _feed.Add(row);
            }
        }

        /// <summary>Agent name coloured by side relative to you (ally / enemy); you are "YOU".</summary>
        private Label KillName(int player)
        {
            var r = round.Replay;
            if (player < 0 || player >= r.Players.Count) return Ui.Label("World", "rp-kill-weapon");
            if (player == round.Player) return Ui.Label("YOU", "rp-kill-you");
            return Ui.Label(r.Players[player].AgentName, r.Players[player].Team == round.Team ? "rp-kill-ally" : "rp-kill-enemy");
        }

        private void UpdateWeapon()
        {
            var w = round.Weapons;
            var gun = w.Equipped;
            Ui.Text(_weaponName, gun.Stats.Name.ToUpperInvariant());
            if (gun.Stats.Melee)
            {
                Ui.Text(_ammo, "");
                Ui.Text(_reserve, "");
            }
            else
            {
                Ui.Text(_ammo, gun.Magazine.ToString());
                Ui.Text(_reserve, "/ " + gun.Reserve);
                _ammo.EnableInClassList("rp-ammo--low", gun.Magazine <= Mathf.Max(1, gun.Stats.Magazine / 5));
            }
            _reloadFill.style.width = Length.Percent(w.Reloading ? w.ReloadProgress * 100f : 0f);

            if (_slots.childCount != 3)
            {
                _slots.Clear();
                for (int i = 0; i < 3; i++) _slots.Add(Ui.Label("", "rp-slot"));
            }
            for (int i = 0; i < 3; i++)
            {
                var l = (Label)_slots[i];
                var g = w.Slots[i];
                Ui.Text(l, g == null ? "" : $"{g.Stats.Name}  {i + 1}");
                l.EnableInClassList("rp-slot--current", i == w.Current);
                Ui.Show(l, g != null);
            }
        }

        private void UpdateCrosshair()
        {
            var s = GameSettings.Current;
            float len = s.crosshairLength, thick = s.crosshairThickness;
            float gap = s.crosshairGap + (s.crosshairShowsSpread ? round.Weapons.CurrentSpread * gapPerDegree : 0f);
            var color = s.CrosshairColor;
            foreach (var line in new[] { _chTop, _chBottom })
            {
                line.style.width = thick;
                line.style.height = len;
                line.style.left = -thick * 0.5f;
                line.style.backgroundColor = color;
                Ui.Show(line, len > 0f);
            }
            foreach (var line in new[] { _chLeft, _chRight })
            {
                line.style.height = thick;
                line.style.width = len;
                line.style.top = -thick * 0.5f;
                line.style.backgroundColor = color;
                Ui.Show(line, len > 0f);
            }
            _chTop.style.top = -gap - len;
            _chBottom.style.top = gap;
            _chLeft.style.left = -gap - len;
            _chRight.style.left = gap;
            _chDot.style.width = thick;
            _chDot.style.height = thick;
            _chDot.style.left = -thick * 0.5f;
            _chDot.style.top = -thick * 0.5f;
            _chDot.style.backgroundColor = color;
            Ui.Show(_chDot, s.crosshairDot);
        }
    }
}
