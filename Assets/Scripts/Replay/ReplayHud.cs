using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using ValorantReplay.Playback;

namespace ValoReplay.Replay
{
    /// <summary>
    /// UI Toolkit HUD for <see cref="ReplayViewer"/>: score and round clock, both teams' player cards (HP, armor, credits,
    /// primary/sidearm, abilities; click to follow), kill feed, name tags, transport controls, speed, a scrubbable timeline
    /// with round/plant marks, and a scoreboard while Tab is held.
    /// Layout: Assets/UI/ReplayViewer/ReplayViewer.uxml.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    [DefaultExecutionOrder(100)]    // after the camera's LateUpdate, so name tags do not lag a frame
    public sealed class ReplayHud : MonoBehaviour
    {
        [SerializeField] private ReplayViewer viewer;
        [SerializeField] private Camera worldCamera;
        [SerializeField] private float killFeedSeconds = 6f;
        [SerializeField] private int killFeedMax = 6;

        private static readonly float[] Speeds = { 0.25f, 0.5f, 1f, 2f, 4f, 8f };

        private VisualElement _root, _team0List, _team1List, _killFeed, _nameTags, _timeline, _timelineFill, _timelineHead, _timelineMarks, _speedGroup, _scoreboardOverlay;
        private Label _mapLabel, _team0Role, _team1Role, _team0Score, _team1Score, _roundLabel, _timerLabel, _phaseLabel, _timeLabel, _followLabel, _message;
        private Button _playButton;

        // modes (top left) and tools (bottom right)
        private enum Mode { Watch, Intel, Command }
        private Button _modeWatch, _modeIntel, _modeCommand, _modePlay;
        private Button _toolMap, _toolTags, _toolAim, _toolShots, _toolBoard, _toolView;
        private VisualElement _modeOptions, _helpPanel;
        private Label _modeHint;
        private BlackboardView _intel;
        private Play.RoundCommander _commander;
        private ReplayCameraController _cameraController;
        private bool _showTags = true, _boardPinned, _pickingPlayer;
        private readonly List<Button> _speedButtons = new List<Button>();
        private PlayerCard[] _rows = new PlayerCard[0];
        private ScoreboardView _scoreboard;
        private int _feedFirst = -1, _feedLast = -1;
        private bool _scrubbing;

        private VrpReplay Replay => viewer != null ? viewer.Replay : null;

        private void OnEnable()
        {
            if (viewer == null) viewer = FindFirstObjectByType<ReplayViewer>();
            if (worldCamera == null) worldCamera = Camera.main;
            // team knowledge (B); add the component to the HUD object yourself to keep tuned settings
            // (explicit null checks: in the editor a missing component is a placeholder object, not null, so ?? fails)
            _intel = GetComponent<BlackboardView>();
            if (_intel == null) _intel = gameObject.AddComponent<BlackboardView>();
            // command mode (C): the AI plays the round from here and takes orders
            _commander = GetComponent<Play.RoundCommander>();
            if (_commander == null) _commander = gameObject.AddComponent<Play.RoundCommander>();
            _cameraController = FindFirstObjectByType<ReplayCameraController>();

            _root = GetComponent<UIDocument>().rootVisualElement;
            _root.pickingMode = PickingMode.Ignore;
            _team0List = _root.Q("team0-list");
            _team1List = _root.Q("team1-list");
            _killFeed = _root.Q("killfeed");
            _nameTags = _root.Q("name-tags");
            _timeline = _root.Q("timeline");
            _timelineFill = _root.Q("timeline-fill");
            _timelineHead = _root.Q("timeline-head");
            _timelineMarks = _root.Q("timeline-marks");
            _speedGroup = _root.Q("speed-group");
            _scoreboardOverlay = _root.Q("scoreboard");
            _scoreboardOverlay.style.display = DisplayStyle.None;
            _mapLabel = _root.Q<Label>("map-label");
            _team0Role = _root.Q<Label>("team0-role");
            _team1Role = _root.Q<Label>("team1-role");
            _team0Score = _root.Q<Label>("team0-score");
            _team1Score = _root.Q<Label>("team1-score");
            _roundLabel = _root.Q<Label>("round-label");
            _timerLabel = _root.Q<Label>("timer-label");
            _phaseLabel = _root.Q<Label>("phase-label");
            _timeLabel = _root.Q<Label>("time-label");
            _followLabel = _root.Q<Label>("follow-label");
            _message = _root.Q<Label>("message");
            _playButton = _root.Q<Button>("play-button");

            _root.Q<Button>("back-button").clicked += ReplaySession.BackToImport;
            _playButton.clicked += () => viewer.TogglePlay();
            _root.Q<Button>("prev-round").clicked += PrevRound;
            _root.Q<Button>("next-round").clicked += () => viewer.SeekRound(viewer.CurrentRound + 1);
            _root.Q<Button>("back-5").clicked += () => viewer.Seek(viewer.Time - 5f);
            _root.Q<Button>("fwd-5").clicked += () => viewer.Seek(viewer.Time + 5f);

            _speedGroup.Clear();
            _speedButtons.Clear();
            foreach (float s in Speeds)
            {
                float speed = s;
                var b = new Button(() => viewer.Speed = speed) { text = speed < 1f ? $"{speed:0.##}x" : $"{speed:0}x" };
                b.AddToClassList("rv-button");
                b.AddToClassList("rv-speed");
                _speedGroup.Add(b);
                _speedButtons.Add(b);
            }

            SetupModesAndTools();

            _timeline.RegisterCallback<PointerDownEvent>(OnTimelineDown);
            _timeline.RegisterCallback<PointerMoveEvent>(OnTimelineMove);
            _timeline.RegisterCallback<PointerUpEvent>(OnTimelineUp);
            _timeline.RegisterCallback<PointerCaptureOutEvent>(_ => _scrubbing = false);
            _timeline.RegisterCallback<GeometryChangedEvent>(_ => UpdateTimeline());

            ShowMessage("Loading replay…");
            if (viewer != null)
            {
                viewer.Loaded += Build;
                if (viewer.Replay != null || viewer.LoadError != null) Build();
            }
            else ShowMessage("No ReplayViewer in the scene.");
        }

        private void OnDisable()
        {
            if (viewer != null) viewer.Loaded -= Build;
        }

        // ------------------------------------------------------------------------------------------ build
        private void Build()
        {
            _team0List.Clear();
            _team1List.Clear();
            _nameTags.Clear();
            _timelineMarks.Clear();
            _killFeed.Clear();
            _scoreboard = null;
            _feedFirst = _feedLast = -1;

            var r = Replay;
            if (r == null)
            {
                ShowMessage(viewer.LoadError ?? "No replay loaded.");
                _rows = new PlayerCard[0];
                return;
            }
            ShowMessage(null);
            _mapLabel.text = r.MapName.ToUpperInvariant();

            _rows = new PlayerCard[r.Players.Count];
            for (int i = 0; i < r.Players.Count; i++)
            {
                int index = i;
                _rows[i] = new PlayerCard(r, i, () => viewer.FollowPlayer = viewer.FollowPlayer == index ? -1 : index, () => PlayAs(index));
                (r.Players[i].Team == 0 ? _team0List : _team1List).Add(_rows[i].Root);
                _nameTags.Add(_rows[i].Tag);
            }
            _scoreboard = new ScoreboardView(_scoreboardOverlay, r);

            foreach (var round in r.Rounds)
            {
                var mark = new VisualElement();
                mark.AddToClassList("rv-mark");
                mark.style.left = Length.Percent(100f * round.StartTime / r.Duration);
                var label = new Label((round.Number + 1).ToString());
                label.AddToClassList("rv-mark-label");
                mark.Add(label);
                _timelineMarks.Add(mark);
                if (round.PlantTime >= 0)
                {
                    var plant = new VisualElement();
                    plant.AddToClassList("rv-mark");
                    plant.AddToClassList("rv-mark--plant");
                    plant.style.left = Length.Percent(100f * round.PlantTime / r.Duration);
                    _timelineMarks.Add(plant);
                }
            }
        }

        // ------------------------------------------------------------------------------------------ modes and tools
        private void SetupModesAndTools()
        {
            _modeWatch = _root.Q<Button>("mode-watch");
            _modeIntel = _root.Q<Button>("mode-intel");
            _modeCommand = _root.Q<Button>("mode-command");
            _modePlay = _root.Q<Button>("mode-play");
            _modeOptions = _root.Q("mode-options");
            _modeHint = _root.Q<Label>("mode-hint");
            _toolMap = _root.Q<Button>("tool-map");
            _toolTags = _root.Q<Button>("tool-tags");
            _toolAim = _root.Q<Button>("tool-aim");
            _toolShots = _root.Q<Button>("tool-shots");
            _toolBoard = _root.Q<Button>("tool-board");
            _toolView = _root.Q<Button>("tool-view");
            _helpPanel = _root.Q("help-panel");

            _modeWatch.clicked += () => SetMode(Mode.Watch);
            _modeIntel.clicked += () => SetMode(Mode.Intel);
            _modeCommand.clicked += () => SetMode(Mode.Command);
            _modePlay.clicked += PlayAsClicked;

            _toolMap.clicked += () => { if (viewer.HasBuiltMap) viewer.ShowBuiltMap = !viewer.ShowBuiltMap; };
            _toolTags.clicked += () => _showTags = !_showTags;
            _toolAim.clicked += () => viewer.ShowAimLines = !viewer.ShowAimLines;
            _toolShots.clicked += () => viewer.ShowShots = !viewer.ShowShots;
            _toolBoard.clicked += () => _boardPinned = !_boardPinned;
            _toolView.clicked += Overview;
            _root.Q<Button>("tool-help").clicked += ToggleHelp;
            _root.Q<Button>("help-close").clicked += ToggleHelp;
        }

        private Mode CurrentMode => _commander != null && _commander.Busy ? Mode.Command
            : _intel != null && _intel.ViewTeam >= 0 ? Mode.Intel : Mode.Watch;

        /// <summary>One mode at a time: leaving command mode returns to the recording, intel shows the followed player's
        /// team (or red).</summary>
        private void SetMode(Mode mode)
        {
            _pickingPlayer = false;
            if (mode != Mode.Command && _commander != null && _commander.Busy) _commander.End("Back to the recording");
            if (mode != Mode.Intel && _intel != null && _intel.ViewTeam >= 0) _intel.SetTeam(-1);
            if (mode == Mode.Intel && _intel != null && _intel.ViewTeam < 0)
            {
                int fp = viewer.FollowPlayer;
                _intel.SetTeam(fp >= 0 && fp < Replay.Players.Count ? Replay.Players[fp].Team : 0);
            }
            if (mode == Mode.Command && _commander != null && !_commander.Busy) _commander.Toggle();
        }

        /// <summary>Plays the followed player; otherwise asks for one (the PLAY button on a card, or click a player and
        /// press the button again).</summary>
        private void PlayAsClicked()
        {
            int fp = viewer.FollowPlayer;
            if (fp >= 0 && Replay.Sample(fp, viewer.Time).Alive) { PlayAs(fp); return; }
            _pickingPlayer = !_pickingPlayer;
        }

        private void Overview()
        {
            viewer.FollowPlayer = -1;
            if (_cameraController != null) _cameraController.FrameReplay();
        }

        private void ToggleHelp() => _helpPanel.EnableInClassList("rv-help--open", !_helpPanel.ClassListContains("rv-help--open"));

        private void UpdateModesAndTools()
        {
            var mode = CurrentMode;
            bool briefing = _commander != null && _commander.Busy && !_commander.Active;
            _modeWatch.EnableInClassList("rv-mode--active", mode == Mode.Watch && !_pickingPlayer);
            _modeIntel.EnableInClassList("rv-mode--active", mode == Mode.Intel);
            _modeCommand.EnableInClassList("rv-mode--active", mode == Mode.Command && !briefing);
            _modeCommand.EnableInClassList("rv-mode--waiting", briefing);
            _modePlay.EnableInClassList("rv-mode--active", _pickingPlayer);
            Ui.Show(_modeOptions, mode == Mode.Intel);

            int fp = viewer.FollowPlayer;
            bool following = fp >= 0 && fp < Replay.Players.Count;
            _modeHint.text = _pickingPlayer
                ? (following ? $"Press PLAY AS again to take over {Replay.Players[fp].AgentName}, or PLAY on any player card."
                             : "Pick who to play: press PLAY on a player card, or click a player in the world and press PLAY AS again.")
                : mode == Mode.Command ? (briefing ? "Briefing the AI…" : "Left-click players to select · right-click the floor to move them · F objective · G hold · X back to the AI")
                : mode == Mode.Intel ? "Ghost markers: where this team last saw or heard each enemy. HIDE ENEMIES shows only what they know."
                : following ? $"Following {Replay.Players[fp].AgentName}. PLAY AS takes them over from here."
                : "Click a player to follow them. ? lists every shortcut.";

            _toolMap.SetEnabled(viewer.HasBuiltMap);
            _toolMap.EnableInClassList("rv-tool--on", viewer.HasBuiltMap && viewer.ShowBuiltMap);
            _toolTags.EnableInClassList("rv-tool--on", _showTags);
            _toolAim.EnableInClassList("rv-tool--on", viewer.ShowAimLines);
            _toolShots.EnableInClassList("rv-tool--on", viewer.ShowShots);
            _toolBoard.EnableInClassList("rv-tool--on", _boardPinned);
            _nameTags.style.display = _showTags ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private static Label MakeLabel(string text, string cls)
        {
            var l = new Label(text);
            l.AddToClassList(cls);
            return l;
        }

        private void ShowMessage(string text)
        {
            _message.text = text ?? "";
            _message.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        // ------------------------------------------------------------------------------------------ per frame
        private void Update()
        {
            if (Replay == null) return;
            HandleKeys();
            UpdateTopBar();
            UpdateModesAndTools();
            foreach (var row in _rows) row.Update(viewer, viewer.Time);
            bool board = _boardPinned || (Keyboard.current != null && Keyboard.current.tabKey.isPressed);
            if (_scoreboard != null)
            {
                _scoreboard.Visible = board;
                if (board) _scoreboard.Update(viewer, viewer.Time);
            }
            UpdateKillFeed();
            UpdateTransport();
            UpdateTimeline();
        }

        private void LateUpdate()
        {
            if (Replay == null || worldCamera == null || _root.panel == null) return;
            for (int i = 0; i < _rows.Length; i++)
            {
                var tag = _rows[i].Tag;
                bool alive = viewer.PlayerVisible(i);
                var world = alive ? viewer.PlayerPosition(i) + Vector3.up * 1.25f : Vector3.zero;
                if (!alive || worldCamera.WorldToViewportPoint(world).z <= 0f)
                {
                    tag.style.display = DisplayStyle.None;
                    continue;
                }
                var p = RuntimePanelUtils.CameraTransformWorldToPanel(_root.panel, world, worldCamera);
                tag.style.display = DisplayStyle.Flex;
                tag.style.left = p.x;
                tag.style.top = p.y;
                tag.EnableInClassList("rv-tag--followed", viewer.FollowPlayer == i);
            }
        }

        /// <summary>Round play as a player from the current moment (they must be alive).</summary>
        private void PlayAs(int player)
        {
            if (Replay == null || !Replay.Sample(player, viewer.Time).Alive) return;
            viewer.Playing = false;
            ReplaySession.PlayAs(player, viewer.Time);
        }

        private void HandleKeys()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            if (kb.enterKey.wasPressedThisFrame && viewer.FollowPlayer >= 0) PlayAs(viewer.FollowPlayer);
            if (kb.mKey.wasPressedThisFrame && viewer.HasBuiltMap) viewer.ShowBuiltMap = !viewer.ShowBuiltMap;
            if (kb.nKey.wasPressedThisFrame) _showTags = !_showTags;
            if (kb.f1Key.wasPressedThisFrame) ToggleHelp();
            if (kb.escapeKey.wasPressedThisFrame)
            {
                _pickingPlayer = false;
                _helpPanel.RemoveFromClassList("rv-help--open");
            }
            if (kb.spaceKey.wasPressedThisFrame) viewer.TogglePlay();
            float step = kb.shiftKey.isPressed ? 1f : 5f;
            if (kb.leftArrowKey.wasPressedThisFrame) viewer.Seek(viewer.Time - step);
            if (kb.rightArrowKey.wasPressedThisFrame) viewer.Seek(viewer.Time + step);
            if (kb.pageUpKey.wasPressedThisFrame) PrevRound();
            if (kb.pageDownKey.wasPressedThisFrame) viewer.SeekRound(viewer.CurrentRound + 1);
            if (kb.minusKey.wasPressedThisFrame || kb.numpadMinusKey.wasPressedThisFrame) StepSpeed(-1);
            if (kb.equalsKey.wasPressedThisFrame || kb.numpadPlusKey.wasPressedThisFrame) StepSpeed(1);
        }

        /// <summary>Restart the current round, or go to the previous one when already near its start.</summary>
        private void PrevRound()
        {
            int r = viewer.CurrentRound;
            if (r >= 0 && viewer.Time - Replay.Rounds[r].StartTime < 2f) r--;
            viewer.SeekRound(Mathf.Max(0, r));
        }

        private void StepSpeed(int dir)
        {
            int i = System.Array.IndexOf(Speeds, viewer.Speed);
            if (i < 0) i = 2;
            viewer.Speed = Speeds[Mathf.Clamp(i + dir, 0, Speeds.Length - 1)];
        }

        private void UpdateTopBar()
        {
            var r = Replay;
            float t = viewer.Time;
            int round = viewer.CurrentRound;
            int wins0 = 0, wins1 = 0;
            foreach (var rd in r.Rounds)
            {
                if (rd.EndTime > t || rd.WinningTeam == 255) continue;
                if (rd.WinningTeam == 0) wins0++; else wins1++;
            }
            _team0Score.text = wins0.ToString();
            _team1Score.text = wins1.ToString();
            _team0Role.text = RoleText(r.RoleOf(0, round));
            _team1Role.text = RoleText(r.RoleOf(1, round));
            _roundLabel.text = round >= 0 ? $"ROUND {r.Rounds[round].Number + 1}" : "PRE-GAME";

            var state = r.RoundStateAt(t);
            _timerLabel.text = FormatTime(state.Remaining);
            var defuse = r.DefuseAt(t);
            string phase = state.Phase == RoundPhase.Unknown ? "" : state.Phase.ToString().ToUpperInvariant();
            bool planted = state.Spike == SpikeState.Planted;
            if (planted) phase = "SPIKE PLANTED";
            if (defuse != null)
                phase = $"DEFUSING {defuse.ProgressAt(t):0.0}/7s";
            else if (round >= 0 && t >= r.Rounds[round].EndTime && r.Rounds[round].WinningTeam != 255)
                phase = $"{Ui.TeamName(r.Rounds[round].WinningTeam)} WINS · {r.Rounds[round].EndReason.ToString().ToUpperInvariant()}";
            _phaseLabel.text = phase;
            _phaseLabel.EnableInClassList("rv-phase--planted", planted || defuse != null);
        }

        private static string RoleText(TeamRole role) =>
            role == TeamRole.Attacking ? "ATTACK" : role == TeamRole.Defending ? "DEFENSE" : "";

        private void UpdateKillFeed()
        {
            var r = Replay;
            float t = viewer.Time;
            int first = -1, last = -1;
            for (int i = 0; i < r.Kills.Count; i++)
            {
                float kt = r.Kills[i].Time;
                if (kt > t) break;
                if (viewer.Driver != null && viewer.Driver.RecordingHides(r.Kills[i].Victim, kt)) break;   // from here on the round AI decides who dies
                if (kt >= t - killFeedSeconds) { if (first < 0) first = i; last = i; }
            }
            if (first >= 0 && last - first + 1 > killFeedMax) first = last - killFeedMax + 1;
            if (first == _feedFirst && last == _feedLast) return;
            _feedFirst = first;
            _feedLast = last;
            _killFeed.Clear();
            if (first < 0) return;
            for (int i = first; i <= last; i++)
            {
                var k = r.Kills[i];
                var row = new VisualElement();
                row.AddToClassList("rv-kill");
                row.pickingMode = PickingMode.Ignore;
                row.Add(KillName(k.Killer));
                row.Add(MakeLabel(WeaponNames.Pretty(k.Weapon), "rv-kill-weapon"));
                if (k.Headshot) row.Add(MakeLabel("HS", "rv-kill-hs"));
                row.Add(KillName(k.Victim));
                _killFeed.Add(row);
            }
        }

        private Label KillName(byte player)
        {
            var r = Replay;
            if (player >= r.Players.Count) return MakeLabel("World", "rv-kill-weapon");
            var p = r.Players[player];
            return MakeLabel(p.AgentName, p.Team == 0 ? "rv-team0-text" : "rv-team1-text");
        }

        private void UpdateTransport()
        {
            _playButton.text = viewer.Playing ? "PAUSE" : "PLAY";
            _timeLabel.text = $"{FormatTime(viewer.Time)} / {FormatTime(Replay.Duration)}";
            for (int i = 0; i < Speeds.Length; i++)
                _speedButtons[i].EnableInClassList("rv-speed--active", Mathf.Approximately(Speeds[i], viewer.Speed));
            int fp = viewer.FollowPlayer;
            _followLabel.text = fp >= 0 && fp < Replay.Players.Count ? ("Following " + Replay.Players[fp].AgentName + " " + Replay.Players[fp].Name).Trim() : "";
        }

        private void UpdateTimeline()
        {
            if (Replay == null || Replay.Duration <= 0f) return;
            float pct = 100f * Mathf.Min(1f, viewer.Time / Replay.Duration);
            _timelineFill.style.width = Length.Percent(pct);
            _timelineHead.style.left = Length.Percent(pct);
        }

        // ------------------------------------------------------------------------------------------ timeline scrubbing
        private void OnTimelineDown(PointerDownEvent e)
        {
            if (Replay == null || e.button != 0) return;
            _scrubbing = true;
            _timeline.CapturePointer(e.pointerId);
            ScrubTo(e.localPosition.x);
            e.StopPropagation();
        }

        private void OnTimelineMove(PointerMoveEvent e)
        {
            if (_scrubbing && _timeline.HasPointerCapture(e.pointerId)) ScrubTo(e.localPosition.x);
        }

        private void OnTimelineUp(PointerUpEvent e)
        {
            if (!_scrubbing) return;
            _scrubbing = false;
            _timeline.ReleasePointer(e.pointerId);
        }

        private void ScrubTo(float localX)
        {
            float w = _timeline.resolvedStyle.width;
            if (w <= 0f) return;
            viewer.Seek(Mathf.Clamp01(localX / w) * Replay.Duration);
            UpdateTimeline();
        }

        private static string FormatTime(float seconds)
        {
            int s = Mathf.Max(0, Mathf.FloorToInt(seconds));
            return $"{s / 60}:{s % 60:00}";
        }
    }
}
