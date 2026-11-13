using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using ValorantReplay.Sim;
using ValoReplay.Replay;
using SVector3 = System.Numerics.Vector3;

namespace ValoReplay.Play
{
    /// <summary>
    /// Command mode of the replay viewer. C hands the round to the AI from the current moment: every agent on both teams
    /// is AI from there (self-play with round play's AI: fitted profiles, learned looks and tempo, the team blackboards)
    /// and takes orders, MOBA style. Left-click a player to select them (Shift adds or removes), right-click the floor to
    /// send the selection there, F for the objective (the spike carrier plants on the site nearest the cursor, defenders
    /// defuse, attackers pick up a dropped spike), G to hold where they stand looking at the cursor, X to hand them back
    /// to the AI. Ordered players still stop to fight whoever they see; once an order is done the AI plays them again,
    /// and a new order replaces the old one at any time. Seeking or C again leaves command mode: the recording plays on
    /// from wherever the clock is.
    /// </summary>
    public sealed class RoundCommander : MonoBehaviour
    {
        [SerializeField] private ReplayViewer viewer;
        [SerializeField] private ReplayCameraController cameraController;
        [SerializeField] private float killFeedSeconds = 6f;

        /// <summary>The AI plays the round and takes orders.</summary>
        public bool Active => _ai != null && !_briefing;
        /// <summary>In command mode or getting there (briefing the AI).</summary>
        public bool Busy => _ai != null || _briefing;
        public RoundAi Ai => _ai;

        /// <summary>Takes command from the current moment, or gives it back (the HUD's mode bar and C).</summary>
        public void Toggle()
        {
            if (viewer == null || viewer.Replay == null) return;
            if (Busy) End("Back to the recording");
            else StartCoroutine(Begin());
        }

        private static readonly Color MoveColor = new Color(0.9f, 0.95f, 1f), HoldColor = new Color(0.35f, 0.85f, 1f);

        private RoundAi _ai;
        private bool _briefing;
        private int _generation;        // bumped by End: a briefing in progress gives up
        private Camera _camera;
        private readonly List<int> _selected = new List<int>();
        private string _note;           // briefing progress, or why something did not work
        private float _noteUntil;
        private bool _overSeen;
        private readonly List<LineRenderer> _lines = new List<LineRenderer>();
        private int _linesUsed;
        private GUIStyle _box;

        private void Awake()
        {
            if (viewer == null) viewer = FindFirstObjectByType<ReplayViewer>();
            if (cameraController == null) cameraController = FindFirstObjectByType<ReplayCameraController>();
            _camera = cameraController != null ? cameraController.GetComponent<Camera>() : Camera.main;
        }

        private void OnEnable()
        {
            if (viewer == null) return;
            viewer.Seeked += OnSeeked;
            viewer.Loaded += OnLoaded;
        }

        private void OnDisable()
        {
            if (viewer == null) return;
            viewer.Seeked -= OnSeeked;
            viewer.Loaded -= OnLoaded;
            End();
        }

        private void OnSeeked()
        {
            if (_ai != null || _briefing) End("Back to the recording: the simulation cannot jump in time");
        }

        private void OnLoaded()
        {
            if (_ai != null || _briefing) End();
        }

        // ------------------------------------------------------------------ taking and giving back command

        private IEnumerator Begin()
        {
            int generation = ++_generation;
            var replay = viewer.Replay;
            float takeover = viewer.Time;
            bool wasPlaying = viewer.Playing;
            viewer.Playing = false;
            _briefing = true;
            Note("Briefing the AI…", float.PositiveInfinity);

            // sight is raycast against the built map's colliders: load it first (hidden in the minimap view)
            if (viewer.HasBuiltMap && !viewer.MapGeometryReady)
                yield return viewer.LoadBuiltMapAsync(p => Note($"Loading the map for sight  {p * 100f:0}%", float.PositiveInfinity), show: false);
            if (generation != _generation) yield break;

            var task = RoundAi.CreateAsync(replay, viewer.ReplayPath, takeover, System.Environment.TickCount, followRecording: false);
            while (!task.IsCompleted) yield return null;
            if (generation != _generation) yield break;
            if (task.IsFaulted || task.IsCanceled)
            {
                _briefing = false;
                viewer.Playing = wasPlaying;
                Note($"Cannot take command here: {task.Exception?.GetBaseException().Message ?? "cancelled"}", 5f);
                yield break;
            }

            var go = new GameObject("Round AI (command)");
            go.transform.SetParent(transform, false);
            _ai = go.AddComponent<RoundAi>();
            _ai.InitCommanded(viewer, task.Result, takeover);
            // the blackboards learn the round so far from the recording (step by step, so End can stop it in between)
            var warmUp = _ai.WarmUp(p => Note($"Briefing the AI  {p * 100f:0}%", float.PositiveInfinity));
            while (true)
            {
                if (generation != _generation) yield break;
                if (!warmUp.MoveNext()) break;
                yield return warmUp.Current;
            }

            _briefing = false;
            _overSeen = false;
            viewer.RunsPastEnd = true;
            if (cameraController != null) cameraController.Clicked = OnClick;
            Note(null, 0f);
            viewer.Playing = true;
        }

        /// <summary>Leaves command mode: the recording plays on from the current moment.</summary>
        public void End(string why = null)
        {
            _generation++;
            _briefing = false;
            if (_ai != null)
            {
                if (ReferenceEquals(viewer.Driver, _ai)) viewer.Driver = null;
                Destroy(_ai.gameObject);
                _ai = null;
            }
            _selected.Clear();
            if (cameraController != null) cameraController.Clicked = null;
            if (viewer != null)
            {
                viewer.RunsPastEnd = false;
                if (viewer.Replay != null && viewer.Time > viewer.Replay.Duration) viewer.Seek(viewer.Replay.Duration);
            }
            HideMarkers();
            Note(why, 4f);
        }

        // ------------------------------------------------------------------ per frame

        private void Update()
        {
            if (viewer == null || viewer.Replay == null) return;
            var kb = Keyboard.current;
            if (kb != null && kb.cKey.wasPressedThisFrame) Toggle();
            if (!Active) return;

            _ai.Advance(null);
            var sim = _ai.Sim;
            if (sim.Over && !_overSeen)
            {
                _overSeen = true;
                viewer.Playing = false;
            }
            _selected.RemoveAll(p => !sim.Agents[p].Alive);
            if (kb != null) HandleKeys(kb, sim);
            DrawMarkers(sim);
        }

        private void HandleKeys(Keyboard kb, RoundSim sim)
        {
            if (kb.escapeKey.wasPressedThisFrame) _selected.Clear();
            if (_selected.Count == 0) return;
            if (kb.xKey.wasPressedThisFrame)
                foreach (int p in _selected) sim.ClearOrder(p);

            bool objective = kb.fKey.wasPressedThisFrame, hold = kb.gKey.wasPressedThisFrame;
            if (!objective && !hold) return;
            bool aimed = CursorFloor(sim.Map.Grid, out var cursor);
            int given = 0;
            foreach (int p in _selected)
            {
                var a = sim.Agents[p];
                if (hold)
                {
                    var look = aimed ? S(cursor) - a.Position : a.Forward;
                    if (sim.Command(p, OrderType.Hold, a.Position, SimSpace.YawOf(look))) given++;
                    continue;
                }
                // plant on the site nearest the cursor (or the carrier), defuse, or fetch the dropped spike
                var type = !a.Attacking ? OrderType.Defuse : sim.SpikeState == SpikeStatus.Dropped ? OrderType.PickUp : OrderType.Plant;
                if (sim.Command(p, type, aimed ? S(cursor) : a.Position)) given++;
            }
            if (given == 0 && objective)
                Note(sim.SpikeState == SpikeStatus.Planted ? "Only defenders can defuse" : "Plant needs the spike carrier, defuse a planted spike", 2.5f);
        }

        private bool OnClick(Vector2 screen, int button)
        {
            if (!Active) return false;
            var sim = _ai.Sim;
            if (button == 0)
            {
                int p = PlayerAt(screen, sim);
                bool add = Keyboard.current != null && Keyboard.current.shiftKey.isPressed;
                if (!add) _selected.Clear();
                if (p >= 0 && !_selected.Remove(p)) _selected.Add(p);
                return true;
            }
            if (button != 1 || _selected.Count == 0) return false;
            if (!FloorPoint(screen, sim.Map.Grid, out var point))
            {
                Note("Nowhere to walk there", 1.5f);
                return true;
            }
            foreach (int p in _selected) sim.Command(p, OrderType.Move, S(point));
            return true;
        }

        // ------------------------------------------------------------------ picking

        /// <summary>The living player under a screen point, or -1 (the map may be loaded but hidden: look past it).</summary>
        private int PlayerAt(Vector2 screen, RoundSim sim)
        {
            if (_camera == null) return -1;
            var ray = _camera.ScreenPointToRay(screen);
            int best = -1;
            float bestDistance = float.MaxValue;
            foreach (var hit in Physics.RaycastAll(ray, 1000f))
            {
                int p = viewer.PlayerOf(hit.collider);
                if (p >= 0 && p < sim.Agents.Count && sim.Agents[p].Alive && hit.distance < bestDistance) { best = p; bestDistance = hit.distance; }
            }
            return best;
        }

        private bool CursorFloor(NavGrid grid, out Vector3 point)
        {
            point = default;
            var mouse = Mouse.current;
            return mouse != null && FloorPoint(mouse.position.ReadValue(), grid, out point);
        }

        /// <summary>Where a screen point meets the walkable floor: the first cell of the AI's grid whose standing height
        /// the ray has come down to. Roofs and walls do not stop it (the minimap view has none to see).</summary>
        private bool FloorPoint(Vector2 screen, NavGrid grid, out Vector3 point)
        {
            point = default;
            if (_camera == null) return false;
            var ray = _camera.ScreenPointToRay(screen);
            for (float d = 0f; d < 1000f; d += 0.25f)
            {
                var p = ray.GetPoint(d);
                foreach (int n in grid.NodesAt(p.x, p.z))
                {
                    float stand = grid.Nodes[n].Y;          // floor + ActorHeight
                    if (p.y > stand || p.y < stand - NavGrid.ActorHeight - 0.5f) continue;
                    point = new Vector3(p.x, stand, p.z);
                    return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------------ markers: selection rings and order lines

        private void DrawMarkers(RoundSim sim)
        {
            _linesUsed = 0;
            for (int i = 0; i < sim.Agents.Count; i++)
            {
                var a = sim.Agents[i];
                if (!a.Alive || !viewer.PlayerVisible(i)) continue;
                var feet = Floor(viewer.PlayerPosition(i));
                bool selected = _selected.Contains(i);
                if (selected) Ring(feet, 0.7f, ReplayViewer.Gold, 0.07f);
                if (a.Order.Type == OrderType.None) continue;
                var color = OrderColor(a.Order.Type);
                if (!selected) color.a = 0.45f;
                var target = Floor(U(a.Order.Target));
                if (a.Order.Type == OrderType.Hold)
                {
                    // where they look
                    var dir = Quaternion.Euler(0f, a.Order.Yaw, 0f) * Vector3.forward;
                    Line(feet, feet + dir * 2.5f, color, 0.05f);
                    if ((target - feet).sqrMagnitude > 1f) Line(feet, target, color, 0.04f);
                    continue;
                }
                Line(feet, target, color, 0.04f);
                Ring(target, 0.35f, color, 0.05f);
            }
            for (int i = _linesUsed; i < _lines.Count; i++) _lines[i].gameObject.SetActive(false);
        }

        private static Color OrderColor(OrderType type) =>
            type == OrderType.Move ? MoveColor : type == OrderType.Hold ? HoldColor : ReplayViewer.Gold;

        /// <summary>The floor under a capsule centre, lifted above the minimap image when the 3D map is hidden.</summary>
        private Vector3 Floor(Vector3 centre)
        {
            float y = centre.y - NavGrid.ActorHeight + 0.05f;
            if (!viewer.ShowBuiltMap) y = Mathf.Max(y, viewer.MinimapY + 0.05f);
            return new Vector3(centre.x, y, centre.z);
        }

        private void Ring(Vector3 centre, float radius, Color color, float width)
        {
            var l = NextLine(color, width, 25, loop: true);
            for (int k = 0; k < 25; k++)
            {
                float ang = k * Mathf.PI * 2f / 25;
                l.SetPosition(k, centre + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * radius);
            }
        }

        private void Line(Vector3 from, Vector3 to, Color color, float width)
        {
            var l = NextLine(color, width, 2, loop: false);
            l.SetPosition(0, from);
            l.SetPosition(1, to);
        }

        private LineRenderer NextLine(Color color, float width, int points, bool loop)
        {
            if (_linesUsed == _lines.Count)
            {
                var l = new GameObject("Command Marker").AddComponent<LineRenderer>();
                l.transform.SetParent(transform, false);
                l.sharedMaterial = viewer.LineMaterial;
                l.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                l.receiveShadows = false;
                _lines.Add(l);
            }
            var line = _lines[_linesUsed++];
            line.gameObject.SetActive(true);
            line.positionCount = points;
            line.loop = loop;
            line.widthMultiplier = width;
            line.startColor = line.endColor = color;
            return line;
        }

        private void HideMarkers()
        {
            foreach (var l in _lines) if (l != null) l.gameObject.SetActive(false);
            _linesUsed = 0;
        }

        // ------------------------------------------------------------------ overlay

        private void Note(string text, float seconds)
        {
            _note = text;
            _noteUntil = float.IsPositiveInfinity(seconds) ? float.PositiveInfinity : Time.unscaledTime + seconds;
        }

        private void OnGUI()
        {
            if (viewer == null || viewer.Replay == null) return;
            _box ??= new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperCenter, fontSize = 13, richText = true, padding = new RectOffset(12, 12, 8, 8) };
            var text = new StringBuilder();
            if (Active)
            {
                var sim = _ai.Sim;
                text.Append("<b>COMMAND</b>   ").Append(Clock(sim));
                if (sim.Over)
                    text.Append($"\n<size=16><b>{(sim.Winner == sim.AttackingTeam ? "ATTACKERS" : "DEFENDERS")} WIN  ·  " +
                                $"{RoundPlayController.Reason(sim.EndReason, sim.Winner == sim.AttackingTeam)}</b></size>");
                if (_selected.Count > 0)
                {
                    text.Append('\n');
                    foreach (int p in _selected) text.Append(Who(p)).Append(' ').Append(Doing(sim.Agents[p])).Append("    ");
                }
                foreach (var k in _ai.Kills)
                {
                    if (_ai.Now - k.Time > killFeedSeconds) continue;
                    text.Append('\n').Append(k.Killer >= 0 ? Who(k.Killer) : "World").Append($"  <color=#9AA4AE>{WeaponNames.Pretty(k.Weapon)}</color>")
                        .Append(k.Headshot ? "  <color=#FFC233>HS</color>  " : "  ").Append(Who(k.Victim));
                }
                text.Append("\n<size=11><color=#9AA4AE>L-click select (Shift adds) · R-click move · F plant / defuse / pick up · " +
                            "G hold, facing the cursor · X back to the AI · C leave</color></size>");
            }
            // (how to enter command mode is the HUD's mode bar now)
            if (_note != null && Time.unscaledTime < _noteUntil) text.Append(text.Length > 0 ? "\n" : "").Append($"<color=#FFC233>{_note}</color>");
            if (text.Length == 0) return;
            var content = new GUIContent(text.ToString());
            var size = _box.CalcSize(content);
            GUI.Box(new Rect((Screen.width - size.x) * 0.5f, 96f, size.x, size.y), content, _box);
        }

        private static string Clock(RoundSim sim)
        {
            if (sim.SpikeState == SpikeStatus.Planted) return $"<color=#FF4655>SPIKE  {Mathf.CeilToInt(sim.SpikeTimer)}s</color>";
            int s = Mathf.Max(0, Mathf.CeilToInt(sim.Remaining));
            return $"{s / 60}:{s % 60:00}";
        }

        private string Who(int player)
        {
            var p = viewer.Replay.Players[player];
            return $"<color=#{ColorUtility.ToHtmlStringRGB(viewer.TeamColor(p.Team))}>{p.AgentName}</color>";
        }

        private static string Doing(SimAgent a)
        {
            switch (a.Order.Type)
            {
                case OrderType.Move: return "MOVE";
                case OrderType.Hold: return "HOLD";
                case OrderType.Plant: return a.Action == AgentAction.Plant ? $"PLANTING {a.TaskSeconds:0.0}s" : "PLANT";
                case OrderType.Defuse: return a.Action == AgentAction.Defuse ? $"DEFUSING {a.TaskSeconds:0.0}s" : "DEFUSE";
                case OrderType.PickUp: return "PICK UP";
                default: return $"<color=#9AA4AE>AI {a.Action.ToString().ToUpperInvariant()}</color>";
            }
        }

        private static Vector3 U(SVector3 v) => new Vector3(v.X, v.Y, v.Z);
        private static SVector3 S(Vector3 v) => new SVector3(v.x, v.y, v.z);
    }
}
