using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using ValorantReplay.Playback;
using ValoReplay.Knowledge;

namespace ValoReplay.Replay
{
    /// <summary>
    /// Shows one team's <see cref="TeamBlackboard"/> in the replay viewer. B cycles off / red / teal, H hides the
    /// enemy's real positions so only what the team believes is left.
    /// In the world: each ally's view cone and sight lines to enemies they see; where each enemy is believed to be (a ring
    /// that grows and fades while they are unseen, as their possible position spreads and certainty drops); sounds heard
    /// as expanding rings coloured by kind; enemy utility that was seen.
    /// Under it all, a heatmap of the expected number of enemies per callout area (click an enemy's row for just theirs).
    /// In the panel: per enemy, round-start credits and predicted buy (public), how they are known (seen / heard /
    /// revealed / last seen), certainty, most likely area, guns identified (or likely for their buy), ability uses
    /// against their kit, and ult points; and the enemy team's buy and ready ults. Layout: Assets/UI/ReplayViewer/ReplayViewer.uxml (knowledge, kb-tags).
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    [DefaultExecutionOrder(110)]     // after the camera's LateUpdate
    public sealed class BlackboardView : MonoBehaviour
    {
        [SerializeField] private ReplayViewer viewer;
        [SerializeField] private Camera worldCamera;
        [SerializeField] private KnowledgeSettings settings = new KnowledgeSettings();
        [Tooltip("Sensor updates simulated per frame at most, so catching up after a seek is spread over frames.")]
        [SerializeField] private int maxStepsPerFrame = 150;
        [Tooltip("Length of the drawn view cones (m); sight itself reaches Settings.sightRange.")]
        [SerializeField] private float viewConeLength = 9f;
        [SerializeField] private float soundRingSeconds = 1.5f;

        public int ViewTeam { get; private set; } = -1;
        public bool HideTruth { get; private set; }
        public KnowledgeSim Sim { get; private set; }
        /// <summary>Enemy whose area belief the heatmap shows, or -1 for the expected count of all enemies.</summary>
        public int FocusEnemy { get; private set; } = -1;

        private sealed class Row
        {
            public VisualElement Root, Fill;
            public Label Agent, Eco, State, Where, Gun, Util;
        }

        private VisualElement _panel, _rowsBox, _tags;
        private Label _title, _status, _summary;
        private Button _teamButton, _truthButton;
        private readonly Dictionary<int, Row> _rows = new Dictionary<int, Row>();
        private readonly Dictionary<int, Label> _tagLabels = new Dictionary<int, Label>();
        private bool _loadingMap, _caughtUp = true, _rebuild;
        private readonly List<Label> _areaLabels = new List<Label>();
        private Mesh _heat;
        private MeshRenderer _heatRenderer;
        private KnowledgeSim _heatFor;
        private bool _heatOnMinimap;
        private Color[] _heatColors = new Color[0];
        private float[] _heatValues = new float[0];
        private float _mapProgress;

        private Transform _overlay;
        private Pool<LineRenderer> _rings, _lines, _cones;
        private Pool<Renderer> _discs, _markers;
        private MaterialPropertyBlock _block;
        private readonly StringBuilder _sb = new StringBuilder();

        private static readonly string[] StateClasses = { "rv-kb-state--seen", "rv-kb-state--heard", "rv-kb-state--lost", "rv-kb-state--dead" };
        private static readonly string[] Slots = { "C", "Q", "E", "X", "" };

        private VrpReplay Replay => viewer != null ? viewer.Replay : null;

        // ------------------------------------------------------------------------------------------ setup
        private void OnEnable()
        {
            if (viewer == null) viewer = FindFirstObjectByType<ReplayViewer>();
            if (worldCamera == null) worldCamera = Camera.main;
            var root = GetComponent<UIDocument>().rootVisualElement;
            _panel = root.Q("knowledge");
            _rowsBox = root.Q("kb-rows");
            _tags = root.Q("kb-tags");
            _title = root.Q<Label>("kb-title");
            _status = root.Q<Label>("kb-status");
            _summary = root.Q<Label>("kb-summary");
            _teamButton = root.Q<Button>("kb-team-button");
            _truthButton = root.Q<Button>("kb-truth-button");
            if (_teamButton != null) _teamButton.clicked += CycleTeam;
            if (_truthButton != null) _truthButton.clicked += ToggleTruth;
            if (_panel != null) Ui.Show(_panel, false);
            if (viewer != null) viewer.Loaded += OnReplayLoaded;
            UpdateButtons();
        }

        private void OnDisable()
        {
            if (viewer != null)
            {
                viewer.Loaded -= OnReplayLoaded;
                viewer.HiddenPlayers.Clear();
            }
            if (_overlay != null) Destroy(_overlay.gameObject);
            _overlay = null;
        }

        private void OnValidate()
        {
            _rebuild = Sim != null;     // area settings change the map model: rebuild on the next frame
        }

        private void OnReplayLoaded()
        {
            Sim = null;
            _rows.Clear();
            _rowsBox?.Clear();
            _tagLabels.Clear();
            _areaLabels.Clear();
            _tags?.Clear();
            FocusEnemy = -1;
            if (ViewTeam >= 0) EnsureSim();
            ApplyTruth();
        }

        public void CycleTeam() => SetTeam(ViewTeam == -1 ? 0 : ViewTeam == 0 ? 1 : -1);

        public void SetTeam(int team)
        {
            if (Replay == null) team = -1;
            ViewTeam = team;
            if (team >= 0) EnsureSim();
            ApplyTruth();
            BuildRows();
            if (_panel != null) Ui.Show(_panel, team >= 0);
            if (team < 0) HideOverlay();
            UpdateButtons();
        }

        public void ToggleTruth()
        {
            HideTruth = !HideTruth;
            ApplyTruth();
            UpdateButtons();
        }

        private void ApplyTruth()
        {
            if (viewer == null) return;
            viewer.HiddenPlayers.Clear();
            if (!HideTruth || ViewTeam < 0 || Replay == null) return;
            for (int i = 0; i < Replay.Players.Count; i++)
                if (Replay.Players[i].Team != ViewTeam) viewer.HiddenPlayers.Add(i);
        }

        private void UpdateButtons()
        {
            if (_teamButton != null) _teamButton.text = ViewTeam < 0 ? "INTEL OFF" : $"INTEL {Ui.TeamName(ViewTeam)}";
            if (_truthButton != null)
            {
                _truthButton.text = HideTruth ? "SHOW ENEMIES" : "HIDE ENEMIES";
                _truthButton.SetEnabled(ViewTeam >= 0);
            }
        }

        /// <summary>Sight needs the map's colliders: load the built map (hidden in the minimap view) first.</summary>
        private void EnsureSim()
        {
            if (Sim != null || _loadingMap || Replay == null) return;
            if (viewer.HasBuiltMap && !viewer.MapGeometryReady) StartCoroutine(LoadGeometry());
            else CreateSim();
        }

        private IEnumerator LoadGeometry()
        {
            _loadingMap = true;
            _mapProgress = 0f;
            var replay = Replay;
            yield return viewer.LoadBuiltMapAsync(p => _mapProgress = p, show: viewer.ShowBuiltMap);
            _loadingMap = false;
            if (Replay == replay) CreateSim();
        }

        private void CreateSim()
        {
            ISightTest sight = viewer.MapGeometryReady ? new MapSightTest(viewer.IsMapCollider) : new NoOcclusion();
            Sim = new KnowledgeSim(Replay, settings, sight);
            BuildRows();
        }

        private void BuildRows()
        {
            _rows.Clear();
            if (_rowsBox == null) return;
            _rowsBox.Clear();
            if (ViewTeam < 0 || Replay == null) return;
            for (int i = 0; i < Replay.Players.Count; i++)
            {
                var p = Replay.Players[i];
                if (p.Team == ViewTeam) continue;
                var row = new Row { Root = Ui.Box("rv-kb-row", Ui.TeamClass(p.Team)) };
                row.Root.Add(Ui.Box("rv-kb-stripe"));
                row.Root.Add(row.Agent = Ui.Label(p.AgentName.ToUpperInvariant(), "rv-kb-agent"));
                row.Root.Add(row.Eco = Ui.Label("", "rv-kb-eco"));
                row.Root.Add(row.State = Ui.Label("", "rv-kb-state"));
                var bar = Ui.Box("rv-kb-bar");
                bar.Add(row.Fill = Ui.Box("rv-kb-bar-fill"));
                row.Root.Add(bar);
                row.Root.Add(row.Where = Ui.Label("", "rv-kb-where"));
                int player = i;
                row.Root.RegisterCallback<ClickEvent>(_ => FocusEnemy = FocusEnemy == player ? -1 : player);
                row.Root.Add(row.Gun = Ui.Label("", "rv-kb-gun"));
                row.Root.Add(row.Util = Ui.Label("", "rv-kb-util"));
                _rowsBox.Add(row.Root);
                _rows[i] = row;
            }
        }

        // ------------------------------------------------------------------------------------------ per frame
        private void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && Replay != null)
            {
                if (kb.bKey.wasPressedThisFrame) CycleTeam();
                if (kb.hKey.wasPressedThisFrame) ToggleTruth();
            }
            if (_rebuild && Sim != null && !_loadingMap)
            {
                _rebuild = false;
                CreateSim();
            }
            if (Sim != null && ViewTeam >= 0 && Replay != null)
                _caughtUp = Sim.AdvanceTo(viewer.Time, maxStepsPerFrame);
        }

        private void LateUpdate()
        {
            if (ViewTeam < 0 || Replay == null)
            {
                HideOverlay();
                return;
            }
            UpdateStatus();
            if (Sim == null || Sim.Round < 0)
            {
                HideOverlay();
                return;
            }
            float t = _caughtUp ? viewer.Time : Sim.Time;
            var bb = Sim.Teams[ViewTeam];
            DrawOverlay(bb, t);
            UpdatePanel(bb, t);
        }

        private void UpdateStatus()
        {
            if (_status == null) return;
            string s = _loadingMap ? $"Loading map geometry for sight lines  {_mapProgress * 100f:0}%"
                : Sim != null && !_caughtUp ? "Catching up…" : "";
            Ui.Text(_status, s);
            if (_title != null)
            {
                Ui.Text(_title, $"{Ui.TeamName(ViewTeam)} TEAM KNOWLEDGE");
                _title.EnableInClassList("rv-team0-text", ViewTeam == 0);
                _title.EnableInClassList("rv-team1-text", ViewTeam == 1);
            }
        }

        // ------------------------------------------------------------------------------------------ panel
        private void UpdatePanel(TeamBlackboard bb, float t)
        {
            int heard = 0, utilSeen = 0, casts = 0;
            foreach (var c in bb.Sounds) if (c.Time <= t) heard++;
            foreach (var u in bb.Utility) if (u.FirstSeen <= t) utilSeen++;

            foreach (var ei in bb.EachEnemy())
            {
                casts += ei.Casts.Count;
                if (!_rows.TryGetValue(ei.Player, out var row)) continue;
                float certainty = ei.Visible ? 1f : ei.CertaintyAt(t, Sim.Settings);
                Ui.Text(row.State, StateText(ei, t, out string cls));
                foreach (var c in StateClasses) row.State.EnableInClassList(c, c == cls);
                row.Fill.style.width = Length.Percent(certainty * 100f);
                Ui.Text(row.Where, WhereText(ei));
                row.Root.EnableInClassList("rv-kb-row--focused", FocusEnemy == ei.Player);
                Ui.Text(row.Eco, ei.RoundStartCredits < 0 ? "" : $"{Ui.Credits(ei.RoundStartCredits)} {Economy.Name(ei.Buy)}");
                Ui.Text(row.Gun, GunText(ei, t));
                row.Gun.EnableInClassList("rv-kb-gun--guess", ei.Guns.Count == 0);
                Ui.Text(row.Util, UtilText(ei));
            }

            if (_summary == null) return;
            _sb.Clear();
            if (bb.EnemyBuy != BuyType.Unknown)
                _sb.Append("Enemy buy: ").Append(Economy.Name(bb.EnemyBuy)).Append(" (avg ").Append(Ui.Credits(bb.EnemyAverageCredits)).Append(")  ·  ");
            int ready = 0;
            foreach (var ei in bb.EachEnemy())
            {
                if (ei.Dead || !ei.UltReady) continue;
                _sb.Append(ready++ == 0 ? "ULT READY: " : ", ").Append(Replay.Players[ei.Player].AgentName);
            }
            if (ready > 0) _sb.Append("  ·  ");
            _sb.Append(heard).Append(" sounds heard  ·  ").Append(utilSeen).Append(" utility seen  ·  ")
               .Append(casts).Append(" enemy ability uses known");
            if (!Sim.HasOcclusion) _sb.Append("  ·  no built 3D map: sight ignores walls");
            if (Sim.Areas != null)
            {
                _sb.Append("\nExpected enemies:");
                foreach (var region in Sim.Areas.SuperRegions)
                {
                    float sum = 0f;
                    foreach (var a in Sim.Areas.Areas) if (a.SuperRegion == region) sum += bb.Expected[a.Index];
                    if (sum >= 0.05f) _sb.Append("  ").Append(region).Append(' ').Append(sum.ToString("0.0"));
                }
                _sb.Append(FocusEnemy >= 0 ? "   (heatmap: " + Replay.Players[FocusEnemy].AgentName + ", click the row again for all)"
                                           : "   (click an enemy for their heatmap)");
            }
            Ui.Text(_summary, _sb.ToString());
        }

        private string WhereText(EnemyIntel ei)
        {
            if (ei.Dead || Sim.Areas == null) return "";
            int a = ei.MostLikelyArea(out float p);
            if (a < 0) return "";
            string name = Sim.Areas.Areas[a].Name;
            return ei.Visible ? name : $"{name} {p * 100f:0}%";
        }

        private string StateText(EnemyIntel ei, float t, out string cls)
        {
            if (ei.Dead) { cls = "rv-kb-state--dead"; return "DEAD"; }
            if (ei.Visible)
            {
                cls = "rv-kb-state--seen";
                return ei.Spotter >= 0 ? $"SEEN by {Replay.Players[ei.Spotter].AgentName}" : "SEEN";
            }
            if (!ei.HasPosition) { cls = "rv-kb-state--lost"; return "UNKNOWN"; }
            float age = Mathf.Max(0f, t - ei.PositionTime);
            switch (ei.PositionSource)
            {
                case IntelSource.Revealed: cls = "rv-kb-state--heard"; return $"REVEALED {age:0.0}s";
                case IntelSource.Heard: cls = "rv-kb-state--heard"; return $"HEARD {CueName(ei.PositionCue)} {age:0.0}s";
                case IntelSource.HitBy: cls = "rv-kb-state--heard"; return $"SHOT AN ALLY {age:0.0}s";
                default: cls = "rv-kb-state--lost"; return $"LAST SEEN {age:0.0}s";
            }
        }

        private static string CueName(CueKind k)
        {
            switch (k)
            {
                case CueKind.Footstep: return "steps";
                case CueKind.Landing: return "landing";
                case CueKind.Gunshot: return "shots";
                case CueKind.Reload: return "reload";
                case CueKind.Ability: return "ability";
                case CueKind.Plant: return "plant";
                case CueKind.Defuse: return "defuse";
                default: return "hit";
            }
        }

        private static string HowName(IntelSource s)
        {
            switch (s)
            {
                case IntelSource.Seen: return "seen";
                case IntelSource.Heard: return "heard";
                case IntelSource.HitBy: return "hit an ally";
                case IntelSource.KillFeed: return "kill feed";
                case IntelSource.Revealed: return "revealed";
                case IntelSource.LastRound: return "kept from last round";
                default: return "";
            }
        }

        private string GunText(EnemyIntel ei, float t)
        {
            if (ei.Guns.Count == 0)
            {
                string likely = Economy.LikelyGuns(ei.Buy, ei.RoundStartCredits);
                return likely.Length > 0 ? "likely " + likely : "gun unknown";
            }
            _sb.Clear();
            for (int i = ei.Guns.Count - 1; i >= 0; i--)
            {
                if (_sb.Length > 0) _sb.Append(", ");
                _sb.Append(WeaponNames.Pretty(ei.Guns[i]));
            }
            _sb.Append("  · ").Append(HowName(ei.GunSource));
            return _sb.ToString();
        }

        private string UtilText(EnemyIntel ei)
        {
            // used of kit: "C 1/2" (signature abilities recharge, so E can pass its kit)
            _sb.Clear();
            int onUs = 0;
            foreach (var c in ei.Casts) if (c.How == IntelSource.HitBy) onUs++;
            for (int k = 0; k < Slots.Length; k++)
            {
                string slot = Slots[k];
                int n = ei.CastCount(slot);
                bool kit = k < ei.Kit.Length && ei.Kit[k] > 0;
                if (n == 0 && (!kit || slot.Length == 0)) continue;
                if (_sb.Length > 0) _sb.Append("  ");
                _sb.Append(slot.Length > 0 ? slot : "?").Append(' ').Append(n);
                if (kit) _sb.Append('/').Append(ei.Kit[k]);
            }
            if (ei.UltReady) _sb.Append("  ULT READY");
            else if (ei.UltPoints >= 0) _sb.Append("  ult ").Append(ei.UltPoints).Append(ei.UltCost > 0 ? "/" + ei.UltCost : "");
            if (onUs > 0) _sb.Append("  · ").Append(onUs).Append(" on us");
            return _sb.ToString();
        }

        // ------------------------------------------------------------------------------------------ world overlay
        private void EnsureOverlay()
        {
            if (_overlay != null) return;
            _overlay = new GameObject("Blackboard Overlay").transform;
            _block = new MaterialPropertyBlock();
            _rings = new Pool<LineRenderer>(() => NewLine("Ring", true, 48));
            _lines = new Pool<LineRenderer>(() => NewLine("Sight", false, 2));
            _cones = new Pool<LineRenderer>(() => NewLine("View Cone", true, 15));
            _discs = new Pool<Renderer>(() => NewShape("Belief", PrimitiveType.Cylinder));
            _markers = new Pool<Renderer>(() => NewShape("Utility", PrimitiveType.Cube));
        }

        private LineRenderer NewLine(string name, bool loop, int points)
        {
            var line = new GameObject(name).AddComponent<LineRenderer>();
            line.transform.SetParent(_overlay, false);
            line.useWorldSpace = true;
            line.loop = loop;
            line.positionCount = points;
            line.numCapVertices = 0;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            if (viewer.LineMaterial != null) line.sharedMaterial = viewer.LineMaterial;
            return line;
        }

        private Renderer NewShape(string name, PrimitiveType type)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(_overlay, false);
            var r = go.GetComponent<Renderer>();
            if (viewer.SmokeMaterial != null) r.sharedMaterial = viewer.SmokeMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return r;
        }

        private void HideOverlay()
        {
            if (_heatRenderer != null) _heatRenderer.enabled = false;
            foreach (var l in _areaLabels) Ui.Show(l, false);
            if (_overlay != null)
            {
                _rings.Begin(); _rings.End();
                _lines.Begin(); _lines.End();
                _cones.Begin(); _cones.End();
                _discs.Begin(); _discs.End();
                _markers.Begin(); _markers.End();
            }
            foreach (var l in _tagLabels.Values) Ui.Show(l, false);
        }

        private float Width(Vector3 at) =>
            worldCamera != null ? Mathf.Max(0.05f, Vector3.Distance(worldCamera.transform.position, at) * 0.0022f) : 0.1f;

        private void DrawOverlay(TeamBlackboard bb, float t)
        {
            EnsureOverlay();
            _rings.Begin(); _lines.Begin(); _cones.Begin(); _discs.Begin(); _markers.Begin();
            var s = Sim.Settings;
            DrawHeat(bb);
            var ally = viewer.TeamColor(ViewTeam);
            var enemy = viewer.TeamColor(1 - ViewTeam);

            // allies: what they are looking at
            for (int a = 0; a < Replay.Players.Count; a++)
            {
                if (Replay.Players[a].Team != ViewTeam) continue;
                var f = Replay.Sample(a, t);
                if (!f.Alive || (f.Flags & FrameFlags.OffMap) != 0) continue;
                DrawCone(Ground(Replay.UnityPosition(f) + Vector3.down * 0.95f), f.Yaw, s.fieldOfView, WithAlpha(ally, 0.45f));
            }

            foreach (var ei in bb.EachEnemy())
            {
                if (ei.Dead || !ei.HasPosition) { ShowTag(ei.Player, false); continue; }
                float certainty = ei.Visible ? 1f : ei.CertaintyAt(t, s);
                float radius = ei.Visible ? 0.6f : ei.RadiusAt(t, s);
                var ground = Ground(ei.Position + Vector3.down * 0.95f);

                var ring = _rings.Next();
                DrawCircle(ring, ground, radius, ei.Visible ? Color.white : WithAlpha(enemy, 0.3f + 0.7f * certainty));
                var disc = _discs.Next();
                disc.transform.position = ground;
                disc.transform.rotation = Quaternion.identity;
                disc.transform.localScale = new Vector3(radius * 2f, 0.01f, radius * 2f);
                SetColor(disc, WithAlpha(enemy, ei.Visible ? 0.55f : 0.06f + 0.24f * certainty));

                if (ei.Visible && ei.Spotter >= 0)
                {
                    var sf = Replay.Sample(ei.Spotter, t);
                    var line = _lines.Next();
                    var from = Replay.UnityPosition(sf) + Vector3.up * (sf.Crouching ? 0.07f : 0.62f);
                    line.SetPosition(0, from);
                    line.SetPosition(1, ei.Position + Vector3.up * 0.5f);
                    line.widthMultiplier = Width(from) * 0.8f;
                    var c = Color.Lerp(ally, Color.white, 0.5f);
                    line.startColor = WithAlpha(c, 0.9f);
                    line.endColor = WithAlpha(c, 0.35f);
                }
                PlaceTag(ei, ground + Vector3.up * 1.6f, t, certainty);
            }

            // sounds: a ring that expands over the sound's blur and fades
            foreach (var cue in bb.Sounds)
            {
                float age = t - cue.Time;
                if (age < 0f || age > soundRingSeconds) continue;
                float u = age / soundRingSeconds;
                var ring = _rings.Next();
                DrawCircle(ring, Ground(cue.Position + Vector3.down * 0.95f), Mathf.Lerp(0.3f, Mathf.Max(1f, cue.Radius), Mathf.Sqrt(u)),
                           WithAlpha(CueColor(cue.Kind), (1f - u) * (0.35f + 0.65f * cue.Certainty)));
            }

            // enemy utility that was seen, fading after it was last seen
            foreach (var u in bb.Utility)
            {
                float since = t - u.LastSeen;
                if (u.Gone || u.FirstSeen > t || since > s.utilityMemory) continue;
                var m = _markers.Next();
                m.transform.position = u.Position + Vector3.up * 0.3f;
                m.transform.rotation = Quaternion.Euler(45f, 45f, 0f);
                m.transform.localScale = Vector3.one * Mathf.Max(0.45f, Width(u.Position) * 6f);
                SetColor(m, WithAlpha(Color.Lerp(enemy, Color.white, 0.4f), Mathf.Lerp(0.95f, 0.25f, since / s.utilityMemory)));
            }

            _rings.End(); _lines.End(); _cones.End(); _discs.End(); _markers.End();
        }

        /// <summary>A point on the floor, lifted above the minimap image when the 3D map is hidden.</summary>
        private Vector3 Ground(Vector3 feet)
        {
            float y = feet.y + 0.06f;
            if (!viewer.ShowBuiltMap) y = Mathf.Max(y, viewer.MinimapY + 0.05f);
            return new Vector3(feet.x, y, feet.z);
        }

        // ------------------------------------------------------------------------------------------ area heatmap
        /// <summary>One mesh of standable cells, coloured per area by the expected enemy count (or the focused enemy's
        /// probability).</summary>
        private void DrawHeat(TeamBlackboard bb)
        {
            var model = Sim.Areas;
            if (model == null || model.Cells.Length == 0)
            {
                if (_heatRenderer != null) _heatRenderer.enabled = false;
                foreach (var l in _areaLabels) Ui.Show(l, false);
                return;
            }
            if (_heatFor != Sim || _heatOnMinimap != !viewer.ShowBuiltMap) BuildHeat(model);

            var focus = FocusEnemy >= 0 && FocusEnemy < bb.Enemies.Length ? bb.Enemies[FocusEnemy] : null;
            var values = focus != null ? focus.Areas : bb.Expected;
            if (_heatValues.Length != values.Length) _heatValues = new float[values.Length];
            var enemy = viewer.TeamColor(1 - ViewTeam);
            var hot = new Color(1f, 0.93f, 0.55f);
            for (int a = 0; a < values.Length; a++)
            {
                float v = focus != null && focus.Dead ? 0f : values[a];
                _heatValues[a] = v;
            }
            for (int i = 0; i < model.Cells.Length; i++)
            {
                float v = _heatValues[model.Cells[i].Area];
                // probabilities: sqrt so a 10% area still shows; counts: brighter and whiter above one enemy
                float alpha = focus != null ? Mathf.Sqrt(v) * 0.6f : Mathf.Clamp01(v) * 0.55f;
                var c = focus != null ? enemy : Color.Lerp(enemy, hot, Mathf.Clamp01(v - 1f));
                c.a = alpha < 0.02f ? 0f : alpha;
                int k = i * 4;
                _heatColors[k] = _heatColors[k + 1] = _heatColors[k + 2] = _heatColors[k + 3] = c;
            }
            _heat.SetColors(_heatColors);
            _heatRenderer.enabled = true;
            DrawAreaLabels(model, focus != null);
        }

        private void BuildHeat(AreaModel model)
        {
            _heatFor = Sim;
            _heatOnMinimap = !viewer.ShowBuiltMap;
            if (_heat == null)
            {
                _heat = new Mesh { name = "Area Heat", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                _heat.MarkDynamic();
                var go = new GameObject("Area Heat");
                go.transform.SetParent(_overlay, false);
                go.AddComponent<MeshFilter>().sharedMesh = _heat;
                _heatRenderer = go.AddComponent<MeshRenderer>();
                _heatRenderer.sharedMaterial = viewer.LineMaterial;
                _heatRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _heatRenderer.receiveShadows = false;
            }
            int n = model.Cells.Length;
            var verts = new Vector3[n * 4];
            var tris = new int[n * 6];
            float h = model.CellSize * 0.46f;
            for (int i = 0; i < n; i++)
            {
                var c = Ground(model.Cells[i].Floor) + Vector3.down * 0.03f;     // under the rings
                int v = i * 4, t = i * 6;
                verts[v] = c + new Vector3(-h, 0f, -h);
                verts[v + 1] = c + new Vector3(-h, 0f, h);
                verts[v + 2] = c + new Vector3(h, 0f, h);
                verts[v + 3] = c + new Vector3(h, 0f, -h);
                tris[t] = v; tris[t + 1] = v + 1; tris[t + 2] = v + 2;
                tris[t + 3] = v; tris[t + 4] = v + 2; tris[t + 5] = v + 3;
            }
            _heat.Clear();
            _heat.vertices = verts;
            _heat.triangles = tris;
            _heat.RecalculateBounds();
            _heatColors = new Color[n * 4];
        }

        /// <summary>"A MAIN 1.2" (expected enemies) or "A MAIN 40%" (focused enemy) over the likelier areas.</summary>
        private void DrawAreaLabels(AreaModel model, bool probability)
        {
            var panel = _tags != null ? _tags.panel : null;
            int used = 0;
            if (panel != null && worldCamera != null)
                foreach (var area in model.Areas)
                {
                    float v = _heatValues[area.Index];
                    if (v < (probability ? 0.1f : 0.25f) || area.CellCount == 0) continue;
                    var world = Ground(area.Center + Vector3.down * 0.95f) + Vector3.up * 0.4f;
                    if (worldCamera.WorldToViewportPoint(world).z <= 0f) continue;
                    if (used == _areaLabels.Count)
                    {
                        var l = Ui.Label("", "rv-kb-area");
                        l.pickingMode = PickingMode.Ignore;
                        _tags.Insert(0, l);
                        _areaLabels.Add(l);
                    }
                    var label = _areaLabels[used++];
                    Ui.Text(label, probability ? $"{area.Name.ToUpperInvariant()}  {v * 100f:0}%" : $"{area.Name.ToUpperInvariant()}  {v:0.0}");
                    var pos = RuntimePanelUtils.CameraTransformWorldToPanel(panel, world, worldCamera);
                    label.style.left = pos.x;
                    label.style.top = pos.y;
                    Ui.Show(label, true);
                }
            for (int i = used; i < _areaLabels.Count; i++) Ui.Show(_areaLabels[i], false);
        }

        private void DrawCircle(LineRenderer ring, Vector3 center, float radius, Color color)
        {
            int n = ring.positionCount;
            for (int i = 0; i < n; i++)
            {
                float a = i * Mathf.PI * 2f / n;
                ring.SetPosition(i, center + new Vector3(Mathf.Cos(a) * radius, 0.03f, Mathf.Sin(a) * radius));
            }
            ring.widthMultiplier = Width(center);
            ring.startColor = ring.endColor = color;
        }

        private void DrawCone(Vector3 origin, float yaw, float fov, Color color)
        {
            var cone = _cones.Next();
            int n = cone.positionCount;
            cone.SetPosition(0, origin);
            for (int i = 1; i < n; i++)
            {
                float a = (yaw - fov * 0.5f + fov * (i - 1) / (n - 2)) * Mathf.Deg2Rad;
                cone.SetPosition(i, origin + new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)) * viewConeLength);
            }
            cone.widthMultiplier = Width(origin) * 0.7f;
            cone.startColor = cone.endColor = color;
        }

        private void SetColor(Renderer r, Color c)
        {
            _block.SetColor("_BaseColor", c);
            _block.SetColor("_Color", c);
            r.SetPropertyBlock(_block);
        }

        private static Color WithAlpha(Color c, float a) => new Color(c.r, c.g, c.b, Mathf.Clamp01(a));

        private static Color CueColor(CueKind k)
        {
            switch (k)
            {
                case CueKind.Gunshot: return new Color(1f, 0.8f, 0.3f);
                case CueKind.Ability: return new Color(0.75f, 0.5f, 1f);
                case CueKind.Plant:
                case CueKind.Defuse: return new Color(1f, 0.3f, 0.3f);
                case CueKind.Damage: return new Color(1f, 0.4f, 0.35f);
                case CueKind.Reload: return new Color(0.65f, 0.85f, 1f);
                default: return new Color(0.88f, 0.92f, 1f);
            }
        }

        // ------------------------------------------------------------------------------------------ tags
        private void PlaceTag(EnemyIntel ei, Vector3 world, float t, float certainty)
        {
            var panel = _tags != null ? _tags.panel : null;
            if (panel == null || worldCamera == null || worldCamera.WorldToViewportPoint(world).z <= 0f)
            {
                ShowTag(ei.Player, false);
                return;
            }
            if (!_tagLabels.TryGetValue(ei.Player, out var label))
            {
                var p = Replay.Players[ei.Player];
                label = Ui.Label("", "rv-kb-tag", p.Team == 0 ? "rv-team0-text" : "rv-team1-text");
                label.pickingMode = PickingMode.Ignore;
                _tags.Add(label);
                _tagLabels[ei.Player] = label;
            }
            string name = Replay.Players[ei.Player].AgentName;
            Ui.Text(label, ei.Visible ? name : $"{name}?  {Mathf.Max(0f, t - ei.PositionTime):0}s");
            var pos = RuntimePanelUtils.CameraTransformWorldToPanel(panel, world, worldCamera);
            label.style.left = pos.x;
            label.style.top = pos.y;
            label.style.opacity = ei.Visible ? 1f : 0.35f + 0.65f * certainty;
            Ui.Show(label, true);
        }

        private void ShowTag(int player, bool show)
        {
            if (_tagLabels.TryGetValue(player, out var l)) Ui.Show(l, show);
        }

        /// <summary>Reuses scene objects frame to frame: Begin, Next for each one drawn, End hides the rest.</summary>
        private sealed class Pool<T> where T : Component
        {
            private readonly List<T> _items = new List<T>();
            private readonly System.Func<T> _create;
            private int _used;

            public Pool(System.Func<T> create) { _create = create; }

            public void Begin() => _used = 0;

            public T Next()
            {
                if (_used == _items.Count) _items.Add(_create());
                var item = _items[_used++];
                if (!item.gameObject.activeSelf) item.gameObject.SetActive(true);
                return item;
            }

            public void End()
            {
                for (int i = _used; i < _items.Count; i++)
                    if (_items[i].gameObject.activeSelf) _items[i].gameObject.SetActive(false);
            }
        }
    }
}
