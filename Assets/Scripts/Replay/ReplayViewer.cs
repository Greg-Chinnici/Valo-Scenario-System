using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using ValorantReplay.Playback;
using ValoReplay.Maps;

namespace ValoReplay.Replay
{
    /// <summary>
    /// Plays a <see cref="VrpReplay"/> in the scene: a capsule per player (with facing nose and aim line), utility
    /// (smokes, walls and area effects as volumes, other ability objects as small spheres), the spike, shot tracers and
    /// death markers, over the map's minimap image or the built 3D map (<see cref="MapRegistry"/>).
    /// Round play reuses it in "ghost" mode: the rest keep moving as recorded, and <see cref="PossessedPlayer"/> (the
    /// agent you took over) replays their original run as a gold, non-solid ghost.
    /// Based on ValorantReplayViewer/csharp/unity/ReplayPlayback.cs. The replay comes from <see cref="ReplaySession"/>;
    /// without one (pressing Play in this scene directly) it loads <see cref="debugVrpPath"/>.
    /// </summary>
    public sealed class ReplayViewer : MonoBehaviour
    {
        [Tooltip("Load the ReplaySession replay (or Debug Vrp Path) on Start. Off when another script calls Load.")]
        [SerializeField] private bool loadOnStart = true;
        [Tooltip("Loaded when the scene is played directly (no ReplaySession).")]
        [SerializeField] private string debugVrpPath = "";
        [Tooltip("Start on the built 3D map when one exists (M toggles in the viewer); otherwise the minimap floor.")]
        [SerializeField] private bool preferBuiltMap = false;
        [Tooltip("Give walls colliders (round play).")]
        [SerializeField] private bool utilityColliders = false;
        [Tooltip("Transparent URP/Unlit material for smokes and area effects.")]
        [SerializeField] private Material smokeMaterial;
        [Tooltip("URP/Unlit with alpha clipping, for the minimap floor.")]
        [SerializeField] private Material minimapMaterial;
        [Tooltip("A vertex-colour material (Sprites/Default) for aim lines and tracers.")]
        [SerializeField] private Material lineMaterial;
        [SerializeField] private Color team0Color = new Color(0.96f, 0.33f, 0.36f);
        [SerializeField] private Color team1Color = new Color(0.24f, 0.78f, 0.72f);
        [Tooltip("How long a shot tracer stays visible (replay seconds).")]
        [SerializeField] private float tracerSeconds = 0.12f;
        [SerializeField] private float tracerLength = 40f;
        [SerializeField] private float aimLength = 3f;

        public VrpReplay Replay { get; private set; }
        /// <summary>Derived per-player state (guns carried, ult cost).</summary>
        public ReplayInsights Insights { get; private set; }
        public string ReplayPath { get; private set; }
        public float Time { get; private set; }
        public bool Playing { get; set; }
        public float Speed { get; set; } = 1f;
        /// <summary>Player the camera follows, or -1.</summary>
        public int FollowPlayer { get; set; } = -1;
        /// <summary>World-space box around where players went (for framing the camera).</summary>
        public Bounds PlayArea { get; private set; }
        public int CurrentRound => Replay?.RoundAt(Time) ?? -1;
        /// <summary>Player not drawn (round play: the one you control), or -1.</summary>
        public int HiddenPlayer { get; set; } = -1;
        /// <summary>Players not drawn, with their shots and utility (the blackboard view hides the enemy's truth).</summary>
        public readonly HashSet<int> HiddenPlayers = new HashSet<int>();
        /// <summary>Round play: the agent you took over. Their recording still plays, as a gold ghost you can walk and
        /// shoot through, so you can see what they originally did. Set before <see cref="Load"/>.</summary>
        public int PossessedPlayer { get; set; } = -1;
        public static readonly Color Gold = new Color(1f, 0.76f, 0.2f);
        /// <summary>Player whose eyes the camera is in (first-person spectating): their body, nose and aim line are not drawn.</summary>
        public int FirstPersonTarget { get; set; } = -1;
        /// <summary>Draw recorded shots as tracers / each player's aim line.</summary>
        public bool ShowShots { get; set; } = true;
        public bool ShowAimLines { get; set; } = true;
        /// <summary>Round play: players you (would have) killed; drawn dark and no longer hittable.</summary>
        public readonly HashSet<int> Eliminated = new HashSet<int>();
        public bool HasBuiltMap => Replay != null && MapRegistry.Has(Replay.Map);
        /// <summary>Show the built 3D map (true) or the minimap image floor (false).</summary>
        public bool ShowBuiltMap
        {
            get => _builtMap != null && _mapVisible;
            set => SetMapMode(value);
        }
        /// <summary>The built map is in the scene (possibly hidden), so its colliders can be raycast.</summary>
        public bool MapGeometryReady => _builtMap != null;
        public Material LineMaterial => lineMaterial;
        /// <summary>Height of the minimap image (the lowest recorded player position).</summary>
        public float MinimapY => _floorY;
        public Material SmokeMaterial => smokeMaterial;

        /// <summary>A collider of the built map (not a player or utility).</summary>
        public bool IsMapCollider(Collider c) => _builtMap != null && c != null && c.transform.IsChildOf(_builtMap.transform);

        /// <summary>Raised once the replay is loaded and its scene objects exist.</summary>
        public event Action Loaded;
        /// <summary>The clock jumped (<see cref="Seek"/>: timeline, skip buttons, rounds).</summary>
        public event Action Seeked;
        /// <summary>Keep the clock running past the end of the recording (the round AI plays on a round the recording
        /// cuts off).</summary>
        public bool RunsPastEnd { get; set; }
        /// <summary>Why nothing could be loaded (null when fine).</summary>
        public string LoadError { get; private set; }

        private sealed class PlayerVisual
        {
            public Transform Root;
            public Transform Body;              // invisible capsule: the hit box (shrinks when crouching)
            public IPlayerFigure Figure;        // what you see: the character or the mannequin
            public LineRenderer Aim;
            public Collider Collider;
            public Color Color;
            public Color Tinted = new Color(-1f, 0f, 0f);
            public float AnimatedAt = float.NaN;
        }

        /// <summary>The gold ghost is this see-through (0 invisible, 1 solid).</summary>
        public const float GhostAlpha = 0.18f;
        private Material _ghostMaterial;
        private readonly RaycastHit[] _groundHits = new RaycastHit[8];

        private enum UtilityKind { Object, Smoke, Wall, Area }

        private PlayerVisual[] _players = new PlayerVisual[0];
        private Transform _world, _spike;
        private GameObject _builtMap, _minimapFloor;
        private Renderer[] _mapRenderers = new Renderer[0];
        private bool _mapVisible, _mapLoading, _mapWanted;
        private bool _minimapRequested;
        private UtilityKind[] _kinds = new UtilityKind[0];
        private readonly Dictionary<UtilityKind, List<Transform>> _utilityPools = new Dictionary<UtilityKind, List<Transform>>();
        private readonly List<LineRenderer> _tracerPool = new List<LineRenderer>();
        private readonly List<Transform> _deathPool = new List<Transform>();
        private float _floorY;
        private MaterialPropertyBlock _block;

        public Color TeamColor(int team) => team == 0 ? team0Color : team1Color;

        /// <summary>Round AI: decides where some players are and what they did instead of the recording (null = none).</summary>
        public IGhostDriver Driver { get; set; }

        public Vector3 PlayerPosition(int player) => _players[player].Root.position;

        /// <summary>Alive and on the map at the current time.</summary>
        public bool PlayerVisible(int player) => _players[player].Root.gameObject.activeSelf;

        /// <summary>Player index under a collider (for click-to-follow), or -1.</summary>
        public int PlayerOf(Collider c)
        {
            for (int i = 0; i < _players.Length; i++) if (_players[i].Collider == c) return i;
            return -1;
        }

        // ------------------------------------------------------------------------------------------ loading
        private void Start()
        {
            if (!loadOnStart) return;
            var session = ReplaySession.Instance;
            if (session != null && session.Replay != null)
            {
                Load(session.Replay, session.VrpPath);
                if (session.ViewerResumeTime >= 0f)     // back from round play
                {
                    Seek(session.ViewerResumeTime);
                    Playing = false;
                    FollowPlayer = session.ViewerResumeFollow;
                    session.ViewerResumeTime = -1f;
                }
            }
            else if (!string.IsNullOrEmpty(debugVrpPath) && File.Exists(debugVrpPath))
            {
                var r = VrpReplay.Load(debugVrpPath);
                r.ConvertToUnitySpace();
                Load(r, debugVrpPath);
            }
            else
            {
                LoadError = "No replay loaded. Convert one on the import screen, or set Debug Vrp Path on the Replay Viewer.";
                Loaded?.Invoke();
            }
        }

        /// <summary>Builds the scene objects for a replay. <paramref name="loadMap"/> false: the caller loads the map
        /// itself (e.g. with <see cref="LoadBuiltMapAsync"/>).</summary>
        public void Load(VrpReplay replay, string path, bool loadMap = true)
        {
            Replay = replay;
            ReplayPath = path;
            Insights = new ReplayInsights(replay);
            _block = new MaterialPropertyBlock();
            if (_world != null) Destroy(_world.gameObject);
            _world = new GameObject("Replay World").transform;
            _world.SetParent(transform, false);
            _builtMap = _minimapFloor = null;
            _mapRenderers = new Renderer[0];
            _mapVisible = _mapLoading = false;
            _minimapRequested = false;
            _utilityPools.Clear();
            _kinds = new UtilityKind[Replay.Entities.Count];
            for (int i = 0; i < _kinds.Length; i++) _kinds[i] = Classify(Replay.Entities[i]);
            if (smokeMaterial == null) smokeMaterial = RuntimeTransparentMaterial();

            MeasurePlayArea();
            BuildPlayers();
            var spike = GameObject.CreatePrimitive(PrimitiveType.Cube);
            spike.name = "Spike";
            Destroy(spike.GetComponent<Collider>());
            spike.GetComponent<Renderer>().material.color = new Color(1f, 0.85f, 0.2f);
            _spike = spike.transform;
            _spike.SetParent(_world, false);
            _spike.localScale = new Vector3(0.3f, 0.3f, 0.3f);

            Time = Replay.Rounds.Count > 0 ? Replay.Rounds[0].StartTime : 0f;
            Playing = true;
            Apply();
            if (loadMap) SetMapMode(preferBuiltMap && HasBuiltMap);
            Loaded?.Invoke();
        }

        /// <summary>Bounds of real, on-map movement samples; the minimap sits at the lowest recorded position.</summary>
        private void MeasurePlayArea()
        {
            bool any = false;
            var b = new Bounds();
            // every tick: a sampled pass can miss the single lowest point
            for (int t = 0; t < Replay.TickCount; t++)
                for (int p = 0; p < Replay.Players.Count; p++)
                {
                    var f = Replay.GetFrame(t, p);
                    if (!f.Alive || (f.Flags & FrameFlags.HasData) == 0 || (f.Flags & FrameFlags.OffMap) != 0) continue;
                    var pos = Replay.UnityPosition(f);
                    if (!any) { b = new Bounds(pos, Vector3.zero); any = true; }
                    else b.Encapsulate(pos);
                }
            if (!any) b = new Bounds(Vector3.zero, new Vector3(100f, 10f, 100f));
            PlayArea = b;
            // Maps have several levels, so a flat minimap cannot match everyone: it sits at the lowest recorded
            // position (a capsule centre, not feet), and players on higher ground float above it.
            _floorY = b.min.y;
        }

        private void BuildPlayers()
        {
            var parent = new GameObject("Players").transform;
            parent.SetParent(_world, false);
            _players = new PlayerVisual[Replay.Players.Count];
            for (int i = 0; i < _players.Length; i++)
            {
                var p = Replay.Players[i];
                var root = new GameObject($"{i}: {p.AgentName} {p.Name}".Trim()).transform;
                root.SetParent(parent, false);

                // the hit box: an invisible capsule, 2 m tall, centre pivot like the replay
                var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                body.name = "Hitbox";
                body.transform.SetParent(root, false);
                body.transform.localScale = new Vector3(0.7f, 1f, 0.7f);
                body.GetComponent<Renderer>().enabled = false;

                var aim = new GameObject("Aim").AddComponent<LineRenderer>();
                aim.transform.SetParent(root, false);
                SetupLine(aim, 0.03f);

                var figure = PlayerFigures.Create(root);
                if (i == PossessedPlayer && GhostMaterial() != null) figure.UseMaterial(GhostMaterial(), castShadows: false);

                var v = new PlayerVisual
                {
                    Root = root, Body = body.transform, Figure = figure,
                    Aim = aim, Collider = body.GetComponent<Collider>(), Color = i == PossessedPlayer ? Gold : TeamColor(p.Team),
                };
                aim.startColor = new Color(v.Color.r, v.Color.g, v.Color.b, 0.9f);
                aim.endColor = new Color(v.Color.r, v.Color.g, v.Color.b, 0f);
                _players[i] = v;
            }
        }

        /// <summary>See-through material for the gold ghost (the smoke material's settings); null if none can be made.</summary>
        private Material GhostMaterial()
        {
            if (_ghostMaterial != null) return _ghostMaterial;
            var source = smokeMaterial != null ? smokeMaterial : RuntimeTransparentMaterial();
            if (source == null) return null;
            _ghostMaterial = new Material(source) { name = "Gold ghost" };
            return _ghostMaterial;
        }

        /// <summary>How far the built map's floor is from 1 m below a player position (recorded positions float a little,
        /// AI positions sit 0.1 m low); 0 without a built map or with nothing below.</summary>
        private float FloorOffset(Vector3 position)
        {
            if (_builtMap == null) return 0f;
            bool backfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            int n = Physics.RaycastNonAlloc(position + Vector3.up * 0.3f, Vector3.down, _groundHits, 1.8f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            Physics.queriesHitBackfaces = backfaces;
            float best = float.MaxValue, floor = 0f;
            for (int i = 0; i < n; i++)
            {
                if (!IsMapCollider(_groundHits[i].collider) || _groundHits[i].distance >= best) continue;
                best = _groundHits[i].distance;
                floor = _groundHits[i].point.y;
            }
            return best < float.MaxValue ? floor - (position.y - 1f) : 0f;
        }

        // ------------------------------------------------------------------------------------------ map textures
        private bool _mapTextures = true;
        private readonly Dictionary<Renderer, Material[]> _texturedMaterials = new Dictionary<Renderer, Material[]>();
        private readonly Dictionary<Material, Material> _plainMaterials = new Dictionary<Material, Material>();

        /// <summary>Off: the built map is drawn in plain greys (shape and lighting only), which reads geometry and
        /// players more clearly. Cut-out surfaces (fences, foliage) keep their texture so their holes stay.</summary>
        public bool MapTextures
        {
            get => _mapTextures;
            set
            {
                if (_mapTextures == value) return;
                _mapTextures = value;
                ApplyMapTextures();
            }
        }

        private void ApplyMapTextures()
        {
            if (_builtMap == null) return;
            foreach (var r in _mapRenderers)
            {
                if (r == null) continue;
                if (!_texturedMaterials.TryGetValue(r, out var original))
                {
                    if (_mapTextures) continue;
                    original = r.sharedMaterials;
                    _texturedMaterials[r] = original;
                }
                if (_mapTextures) { r.sharedMaterials = original; continue; }
                var plain = new Material[original.Length];
                for (int i = 0; i < plain.Length; i++) plain[i] = Plain(original[i]);
                r.sharedMaterials = plain;
            }
        }

        private Material Plain(Material m)
        {
            if (m == null) return null;
            if (_plainMaterials.TryGetValue(m, out var p)) return p;
            p = new Material(m) { name = m.name + " (plain)" };
            bool cutout = m.HasProperty("_AlphaClip") && m.GetFloat("_AlphaClip") > 0.5f;
            foreach (var tex in new[] { "_BaseMap", "_MainTex", "_BumpMap", "_MetallicGlossMap", "_OcclusionMap", "_EmissionMap" })
                if (p.HasProperty(tex) && !(cutout && (tex == "_BaseMap" || tex == "_MainTex"))) p.SetTexture(tex, null);
            p.DisableKeyword("_NORMALMAP");
            p.DisableKeyword("_METALLICSPECGLOSSMAP");
            p.DisableKeyword("_OCCLUSIONMAP");
            p.DisableKeyword("_EMISSION");
            // a stable grey per material, so neighbouring surfaces stay apart
            uint h = 2166136261u;
            foreach (char ch in m.name) h = (h ^ ch) * 16777619u;
            float g = 0.55f + 0.3f * (h % 1000) / 999f;
            var c = new Color(g, g * 1.01f, g * 1.04f, 1f);
            if (p.HasProperty("_BaseColor")) p.SetColor("_BaseColor", c);
            if (p.HasProperty("_Color")) p.SetColor("_Color", c);
            _plainMaterials[m] = p;
            return p;
        }

        private void SetupLine(LineRenderer line, float width)
        {
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.widthMultiplier = width;
            line.numCapVertices = 0;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            if (lineMaterial != null) line.sharedMaterial = lineMaterial;
        }

        // ------------------------------------------------------------------------------------------ map
        private void SetMapMode(bool built)
        {
            if (Replay == null) return;
            if (_mapLoading)
            {
                _mapWanted = built;     // applied when the async load finishes
                return;
            }
            if (built && _builtMap == null)
            {
                var prefab = MapRegistry.Instance != null ? MapRegistry.Instance.Find(Replay.Map) : null;
                if (prefab != null)
                {
                    var map = Instantiate(prefab);     // prefabs are already in replay space
                    map.transform.SetParent(_world, false);
                    AdoptMap(map, prefab.name);
                }
                else built = false;
            }
            // a hidden map keeps its colliders (sight lines for the blackboard), only its renderers go off
            SetMapVisible(built && _builtMap != null);
            if (!ShowBuiltMap && !_minimapRequested)
            {
                _minimapRequested = true;
                StartCoroutine(LoadMinimap());
            }
            if (_minimapFloor != null) _minimapFloor.SetActive(!ShowBuiltMap);
        }

        private void AdoptMap(GameObject map, string name)
        {
            _builtMap = map;
            _builtMap.name = name;
            var renderers = new List<Renderer>();
            foreach (var r in map.GetComponentsInChildren<Renderer>(true)) if (r.enabled) renderers.Add(r);
            _mapRenderers = renderers.ToArray();
            _mapVisible = true;
            _texturedMaterials.Clear();
            if (!_mapTextures) ApplyMapTextures();
            MapLighting.Ensure();
            Physics.SyncTransforms();
        }

        private void SetMapVisible(bool visible)
        {
            if (_builtMap == null) { _mapVisible = false; return; }
            if (_mapVisible == visible) return;
            _mapVisible = visible;
            foreach (var r in _mapRenderers) if (r != null) r.enabled = visible;
        }

        /// <summary>
        /// Instantiates the built map over several frames (the map is thousands of objects and colliders), then shows
        /// it (or keeps it hidden with <paramref name="show"/> false, for its colliders only); falls back to the
        /// minimap floor when the map is not built. <paramref name="progress"/> gets 0..1.
        /// </summary>
        public IEnumerator LoadBuiltMapAsync(Action<float> progress = null, bool show = true)
        {
            var prefab = MapRegistry.Instance != null && Replay != null ? MapRegistry.Instance.Find(Replay.Map) : null;
            if (prefab == null)
            {
                SetMapMode(false);
                yield break;
            }
            if (_mapLoading)
            {
                if (show) _mapWanted = true;
                while (_mapLoading) yield return null;
                yield break;
            }
            if (_builtMap != null)
            {
                if (show) SetMapMode(true);
                yield break;
            }
            _mapLoading = true;
            _mapWanted = show || ShowBuiltMap;
            var world = _world;
            float budget = AsyncInstantiateOperation.GetIntegrationTimeMS();
            AsyncInstantiateOperation.SetIntegrationTimeMS(12f);    // keep frames (and the loading spinner) moving
            var op = InstantiateAsync(prefab, _world);
            while (!op.isDone)
            {
                progress?.Invoke(op.progress);
                yield return null;
            }
            AsyncInstantiateOperation.SetIntegrationTimeMS(budget);
            if (world != _world)        // another replay was loaded meanwhile
            {
                foreach (var o in op.Result) Destroy(o);
                yield break;
            }
            _mapLoading = false;
            AdoptMap(op.Result[0], prefab.name);
            progress?.Invoke(1f);
            SetMapMode(_mapWanted);
        }

        private IEnumerator LoadMinimap()
        {
            var info = Replay.MapInfo;
            if (info == null || !info.HasMinimap || string.IsNullOrEmpty(info.MinimapUrl) || minimapMaterial == null) yield break;

            Texture2D tex = null;
            string cache = Path.Combine(Application.persistentDataPath, "minimaps", info.Uuid + ".png");
            if (File.Exists(cache))
            {
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                if (!tex.LoadImage(File.ReadAllBytes(cache))) tex = null;
            }
            if (tex == null)
            {
                using (var req = UnityWebRequestTexture.GetTexture(info.MinimapUrl))
                {
                    yield return req.SendWebRequest();
                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        Debug.LogWarning($"[ReplayViewer] Minimap download failed ({info.MinimapUrl}): {req.error}");
                        yield break;
                    }
                    tex = DownloadHandlerTexture.GetContent(req);
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(cache));
                        File.WriteAllBytes(cache, req.downloadHandler.data);
                    }
                    catch (Exception e) { Debug.LogWarning($"[ReplayViewer] Could not cache minimap: {e.Message}"); }
                }
            }
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.anisoLevel = 4;

            // MapInfo.ToMinimap is linear: u = ueY * XMultiplier + XScalarToAdd, v = ueX * YMultiplier + YScalarToAdd,
            // with Unity x = ueY / 100 and z = ueX / 100. Invert it for the image corners (v runs downward in the image).
            float X(float u) => (u - info.XScalarToAdd) / (info.XMultiplier * 100f);
            float Z(float v) => (v - info.YScalarToAdd) / (info.YMultiplier * 100f);
            var mesh = new Mesh { name = "Minimap" };
            mesh.vertices = new[]
            {
                new Vector3(X(0), _floorY, Z(0)), new Vector3(X(1), _floorY, Z(0)),
                new Vector3(X(0), _floorY, Z(1)), new Vector3(X(1), _floorY, Z(1)),
            };
            mesh.uv = new[] { new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, 0), new Vector2(1, 0) };
            mesh.triangles = new[] { 0, 1, 2, 2, 1, 3 };
            mesh.RecalculateNormals();
            if (mesh.normals[0].y < 0) { mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 }; mesh.RecalculateNormals(); }
            mesh.RecalculateBounds();

            var floor = new GameObject("Minimap Floor");
            floor.transform.SetParent(_world, false);
            floor.SetActive(!ShowBuiltMap);
            _minimapFloor = floor;
            floor.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = floor.AddComponent<MeshRenderer>();
            mr.sharedMaterial = new Material(minimapMaterial) { mainTexture = tex };
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        // ------------------------------------------------------------------------------------------ playback
        public void Seek(float time)
        {
            if (Replay == null) return;
            Time = Mathf.Clamp(time, 0f, Replay.Duration);
            Apply();
            Seeked?.Invoke();
        }

        public void SeekRound(int round)
        {
            if (Replay == null || Replay.Rounds.Count == 0) return;
            Seek(Replay.Rounds[Mathf.Clamp(round, 0, Replay.Rounds.Count - 1)].StartTime);
        }

        public void TogglePlay()
        {
            if (Replay == null) return;
            if (!Playing && !RunsPastEnd && Time >= Replay.Duration - 0.01f) Time = 0f;
            Playing = !Playing;
        }

        private void Update()
        {
            if (Replay == null) return;
            if (Playing)
            {
                Time += UnityEngine.Time.deltaTime * Speed;
                if (Time >= Replay.Duration && !RunsPastEnd) { Time = Replay.Duration; Playing = false; }
            }
            Apply();
        }

        private void Apply()
        {
            UpdatePlayers();
            UpdateSpike();
            UpdateAbilities();
            UpdateTracers();
            UpdateDeaths();
        }

        private void UpdatePlayers()
        {
            for (int i = 0; i < _players.Length; i++)
            {
                var v = _players[i];
                var f = Replay.Sample(i, Time);
                var pose = default(GhostPose);
                bool driven = Driver != null && Driver.TryGetPose(i, out pose);
                if (!driven) pose = new GhostPose
                {
                    Visible = f.Alive && (f.Flags & FrameFlags.OffMap) == 0, Position = Replay.UnityPosition(f), Yaw = f.Yaw, Crouching = f.Crouching,
                };
                bool visible = pose.Visible && i != HiddenPlayer && !HiddenPlayers.Contains(i);
                if (visible != v.Root.gameObject.activeSelf) v.Figure.ResetMotion();     // reappearing is not a step
                v.Root.gameObject.SetActive(visible);
                if (!visible) { v.AnimatedAt = float.NaN; continue; }

                v.Root.localPosition = pose.Position;
                v.Root.localRotation = Quaternion.Euler(0f, pose.Yaw, 0f);
                // replay positions are the capsule centre, 1.0 m above the floor; the hit box shrinks from the top
                v.Body.localScale = new Vector3(0.7f, pose.Crouching ? 0.7f : 1f, 0.7f);
                v.Body.localPosition = new Vector3(0f, pose.Crouching ? -0.3f : 0f, 0f);

                bool debuffed = false, blinded = !driven && (f.Flags & FrameFlags.Blinded) != 0;
                if (!driven)
                    foreach (var s in Replay.StatusEffectsAt(i, Time))
                        if (s.Type != StatusEffectType.Blind && !s.IsPing) { debuffed = true; break; }
                var color = blinded ? Color.white : debuffed ? v.Color * 0.45f : v.Color;
                if (i == FollowPlayer) color = Color.Lerp(color, Color.white, 0.25f);
                bool eliminated = Eliminated.Contains(i);
                if (eliminated) color = new Color(0.12f, 0.12f, 0.12f);
                bool possessed = i == PossessedPlayer;
                v.Collider.enabled = !eliminated && !possessed;
                // the gold ghost (the original run of the agent you play) is see-through
                color.a = possessed && _ghostMaterial != null ? GhostAlpha : 1f;
                if (color != v.Tinted) { v.Figure.Tint(color); v.Tinted = color; }

                bool firstPerson = i == FirstPersonTarget;
                v.Figure.SetVisible(!firstPerson);
                // animate on replay time: paused holds the pose, slow motion slows the steps
                float dt = float.IsNaN(v.AnimatedAt) ? 0f : Time - v.AnimatedAt;
                if (dt < 0f) { v.Figure.ResetMotion(); dt = 0f; }               // seeked backwards
                v.AnimatedAt = Time;
                float pitch = driven ? 0f : Mathf.DeltaAngle(0f, Replay.UnityRotation(f).eulerAngles.x);   // down positive
                v.Figure.Animate(v.Root.position, pose.Yaw, dt, pitch, pose.Crouching,
                                 airborne: !driven && f.Airborne, floorOffset: FloorOffset(v.Root.position));
                v.Aim.enabled = ShowAimLines && !firstPerson;
                var eye = v.Root.position + Vector3.up * 0.6f;
                var look = driven ? Quaternion.Euler(0f, pose.Yaw, 0f) : Replay.UnityRotation(f);
                v.Aim.SetPosition(0, eye);
                v.Aim.SetPosition(1, eye + look * Vector3.forward * aimLength);
            }
        }

        private void UpdateSpike()
        {
            if (Driver != null && Driver.TryGetSpike(out var at, out bool shown))
            {
                _spike.gameObject.SetActive(shown);
                _spike.position = at;
                return;
            }
            byte holder = Replay.SpikeHolderAt(Time);
            if (holder < _players.Length && _players[holder].Root.gameObject.activeSelf)
            {
                _spike.gameObject.SetActive(true);
                _spike.position = _players[holder].Root.position + Vector3.up * 1.3f;
                return;
            }
            // planted: sit on the plant location for the rest of the round
            int r = CurrentRound;
            var state = Replay.RoundStateAt(Time);
            if (r >= 0 && Replay.Rounds[r].PlantTime >= 0 && Time >= Replay.Rounds[r].PlantTime && Time < Replay.Rounds[r].EndTime &&
                (state.Spike == SpikeState.Planted || state.Spike == SpikeState.Defused))
            {
                var rd = Replay.Rounds[r];
                _spike.gameObject.SetActive(true);
                _spike.position = Replay.UnityPoint(rd.PlantX, rd.PlantY, rd.PlantZ) + Vector3.up * 0.15f;
                return;
            }
            _spike.gameObject.SetActive(false);
        }

        /// <summary>Smokes, walls and ground effects get volumes; everything else (projectiles, traps, drones) a sphere.</summary>
        private static UtilityKind Classify(Entity e)
        {
            string c = e.Class.ToLowerInvariant();
            bool projectile = c.StartsWith("projectile");
            if (!projectile && c.Contains("smoke")) return UtilityKind.Smoke;
            if (!projectile && c.Contains("wall")) return UtilityKind.Wall;
            if (!projectile && (c.Contains("molotov") || c.Contains("decay") || c.Contains("fire") || c.Contains("slowfield") ||
                                c.Contains("healpool") || c.Contains("nanoswarm") || c.Contains("snakebite") || c.Contains("pool") ||
                                c.StartsWith("patch_") || c.Contains("aoe"))) return UtilityKind.Area;
            return UtilityKind.Object;
        }

        /// <summary>A smoke (a vision-blocking sphere of <see cref="SmokeRadius"/>).</summary>
        public static bool IsSmoke(Entity e) => Classify(e) == UtilityKind.Smoke;

        /// <summary>Jett's cloudburst is smaller than controller smokes.</summary>
        public static float SmokeRadius(Entity e) => e.Agent == "Wushu" ? 3f : 4.1f;

        private void UpdateAbilities()
        {
            var used = new Dictionary<UtilityKind, int> { [UtilityKind.Object] = 0, [UtilityKind.Smoke] = 0, [UtilityKind.Wall] = 0, [UtilityKind.Area] = 0 };
            for (int i = 0; i < Replay.Entities.Count; i++)
            {
                var e = Replay.Entities[i];
                if (e.Agent.Length == 0 || Time < e.SpawnTime || (e.DespawnTime >= 0 && Time >= e.DespawnTime)) continue;
                if (HiddenPlayers.Contains(e.Owner) || (Driver != null && Driver.RecordingHides(e.Owner, e.SpawnTime))) continue;
                var kind = _kinds[i];
                var t = Pooled(kind, used[kind]++);
                e.PositionAt(Time, out float x, out float y, out float z);
                t.localPosition = Replay.UnityPoint(x, y, z);
                t.localRotation = Quaternion.identity;
                t.name = e.Class;
                var team = e.Owner < Replay.Players.Count ? TeamColor(Replay.Players[e.Owner].Team) : Color.gray;
                Color c;
                switch (kind)
                {
                    case UtilityKind.Smoke:
                        t.localScale = Vector3.one * SmokeRadius(e) * 2f;
                        c = new Color(0.72f, 0.75f, 0.8f, 0.93f);
                        break;
                    case UtilityKind.Wall:
                        t.localRotation = Quaternion.Euler(0f, e.Yaw, 0f);
                        t.localScale = new Vector3(3.3f, 3f, 0.5f);
                        t.localPosition += Vector3.up * 1.5f;
                        c = Color.Lerp(team, Color.white, 0.6f);
                        break;
                    case UtilityKind.Area:
                        t.localScale = new Vector3(7f, 0.05f, 7f);
                        c = e.Class.IndexOf("heal", StringComparison.OrdinalIgnoreCase) >= 0 ? new Color(0.3f, 0.95f, 0.5f, 0.4f)
                            : e.Class.IndexOf("slow", StringComparison.OrdinalIgnoreCase) >= 0 ? new Color(0.4f, 0.7f, 1f, 0.4f)
                            : new Color(1f, 0.45f, 0.1f, 0.45f);
                        break;
                    default:
                        t.localScale = Vector3.one * 0.4f;
                        c = Color.Lerp(team, Color.white, 0.5f);
                        break;
                }
                _block.SetColor("_BaseColor", c);
                _block.SetColor("_Color", c);
                t.GetComponent<Renderer>().SetPropertyBlock(_block);
                t.gameObject.SetActive(true);
            }
            foreach (var kv in _utilityPools)
                for (int i = used[kv.Key]; i < kv.Value.Count; i++) kv.Value[i].gameObject.SetActive(false);
        }

        private Transform Pooled(UtilityKind kind, int index)
        {
            if (!_utilityPools.TryGetValue(kind, out var pool)) _utilityPools[kind] = pool = new List<Transform>();
            if (index < pool.Count) return pool[index];
            var go = GameObject.CreatePrimitive(kind == UtilityKind.Wall ? PrimitiveType.Cube : kind == UtilityKind.Area ? PrimitiveType.Cylinder : PrimitiveType.Sphere);
            if (kind != UtilityKind.Wall || !utilityColliders) Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(_world, false);
            var r = go.GetComponent<Renderer>();
            if ((kind == UtilityKind.Smoke || kind == UtilityKind.Area) && smokeMaterial != null)
            {
                r.sharedMaterial = smokeMaterial;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            pool.Add(go.transform);
            return go.transform;
        }

        /// <summary>Fallback when no smoke material is assigned. Reliable in the editor only: a build may strip the variant.</summary>
        private static Material RuntimeTransparentMaterial()
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) return null;
            var m = new Material(shader) { name = "Smoke (runtime)" };
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetFloat("_Cull", 0f);
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.SetOverrideTag("RenderType", "Transparent");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            return m;
        }

        private void UpdateTracers()
        {
            int used = 0;
            foreach (var s in Replay.Shots)
            {
                if (!ShowShots || s.Time > Time || s.Time < Time - tracerSeconds || HiddenPlayers.Contains(s.Player)) continue;
                // driven: the gold ghost's shots while it is shown; others' only until they left the recording
                if (Driver != null && (s.Player == PossessedPlayer ? !PlayerVisible(s.Player) : Driver.RecordingHides(s.Player, s.Time))) continue;
                var origin = Replay.UnityPoint(s.X, s.Y, s.Z);
                var color = s.Player == PossessedPlayer ? Gold
                    : s.Player < Replay.Players.Count ? Color.Lerp(TeamColor(Replay.Players[s.Player].Team), Color.yellow, 0.6f) : Color.yellow;
                if (s.Directions.Count == 0)
                {
                    var dir = Quaternion.Euler(s.Pitch, s.Yaw, 0f) * Vector3.forward;
                    DrawTracer(ref used, origin, dir, color);
                }
                foreach (var d in s.Directions) DrawTracer(ref used, origin, Replay.UnityDirection(d.X, d.Y, d.Z), color);
            }
            for (int i = used; i < _tracerPool.Count; i++) _tracerPool[i].gameObject.SetActive(false);
        }

        private void DrawTracer(ref int used, Vector3 origin, Vector3 dir, Color color)
        {
            if (used == _tracerPool.Count)
            {
                var line = new GameObject("Tracer").AddComponent<LineRenderer>();
                line.transform.SetParent(_world, false);
                SetupLine(line, 0.04f);
                _tracerPool.Add(line);
            }
            var l = _tracerPool[used++];
            l.gameObject.SetActive(true);
            l.startColor = color;
            l.endColor = new Color(color.r, color.g, color.b, 0f);
            l.SetPosition(0, origin);
            l.SetPosition(1, origin + dir.normalized * tracerLength);
        }

        /// <summary>A flat marker where each player died this round (up to the current time).</summary>
        private void UpdateDeaths()
        {
            if (Driver != null)
            {
                int n = 0;
                foreach (var d in Driver.Deaths)
                {
                    if (d.Victim < 0 || d.Victim >= Replay.Players.Count) continue;
                    var marker = DeathMarker(n++);
                    marker.localPosition = d.Position + Vector3.down * 0.88f;
                    var dc = d.Victim == PossessedPlayer ? Gold * 0.8f : TeamColor(Replay.Players[d.Victim].Team) * 0.6f;
                    _block.SetColor("_BaseColor", dc);
                    _block.SetColor("_Color", dc);
                    marker.GetComponent<Renderer>().SetPropertyBlock(_block);
                    marker.gameObject.SetActive(true);
                }
                for (int i = n; i < _deathPool.Count; i++) _deathPool[i].gameObject.SetActive(false);
                return;
            }
            int used = 0, r = CurrentRound;
            float roundStart = r >= 0 ? Replay.Rounds[r].StartTime : 0f;
            foreach (var k in Replay.Kills)
            {
                if (k.Time < roundStart || k.Time > Time || k.Victim >= Replay.Players.Count) continue;
                var t = DeathMarker(used++);
                var f = Replay.Sample(k.Victim, k.Time - 0.1f);
                t.localPosition = Replay.UnityPosition(f) + Vector3.down * 0.88f;
                var c = k.Victim == PossessedPlayer ? Gold * 0.8f : TeamColor(Replay.Players[k.Victim].Team) * 0.6f;
                _block.SetColor("_BaseColor", c);
                _block.SetColor("_Color", c);
                t.GetComponent<Renderer>().SetPropertyBlock(_block);
                t.gameObject.SetActive(true);
            }
            for (int i = used; i < _deathPool.Count; i++) _deathPool[i].gameObject.SetActive(false);
        }

        private Transform DeathMarker(int index)
        {
            if (index < _deathPool.Count) return _deathPool[index];
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = "Death";
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(_world, false);
            go.transform.localScale = new Vector3(0.6f, 0.02f, 0.6f);
            _deathPool.Add(go.transform);
            return go.transform;
        }
    }
}
