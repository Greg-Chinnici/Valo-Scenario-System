using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem;
using ValorantReplay.Playback;
using ValorantReplay.Sim;
using ValoReplay.Knowledge;
using ValoReplay.Replay;
using SVector3 = System.Numerics.Vector3;
using Frame = ValorantReplay.Playback.Frame;

namespace ValoReplay.Play
{
    /// <summary>
    /// The character AI of round play: a <see cref="RoundSim"/> started at the takeover moment. You are its External
    /// agent. Everyone else keeps following the recording until they notice something it does not have (they see you,
    /// hear you, get shot, or their recorded killer is no longer there); from then on the utility brain plays them:
    /// Hold / Advance / Peek / FallBack / Trade, scored from what their team's knowledge blackboard says, their profile
    /// fitted from this replay (how they shot, reacted and placed their crosshair), and the team director's plan. Where
    /// they look and when attacks go in are learned from every replay of the map in the replay folders.
    /// The blackboard (<see cref="KnowledgeSim"/>) is the same one the replay viewer shows: up to the takeover it is
    /// filled from the recording, from then on from the live world (everyone's live positions, your shots and the AI's,
    /// hits, kills, plants and defuses), so the AI only acts on what its team could know.
    /// Unity supplies line of sight (built map colliders + the recorded smokes) and your hits; the sim supplies everyone
    /// else's pose, the spike, damage to you and the round result. F3 shows the behaviour matrix over each agent.
    /// Without a player (<see cref="InitCommanded"/>, the replay viewer's command mode) every agent is AI from the takeover
    /// and takes orders through <see cref="Sim"/> (<see cref="RoundSim.Command"/>).
    /// </summary>
    public sealed class RoundAi : MonoBehaviour, IGhostDriver, ILiveWorld, IBeliefSource
    {
        /// <summary>The per-team knowledge blackboards the AI decides from.</summary>
        public KnowledgeSim Knowledge { get; private set; }
        /// <summary>Area beliefs count as a position of this certainty times the area's probability (a sighting is 1).</summary>
        public float areaCertainty = 0.5f;
        private readonly KnowledgeSettings _knowledgeSettings = new KnowledgeSettings();
        private readonly Dictionary<string, byte> _equippable = new Dictionary<string, byte>();
        private int _roundIndex, _eventsSeen;

        /// <summary>A kill of this round after the AI started, in replay time.</summary>
        public struct AiKill
        {
            public float Time;
            public int Killer, Victim;
            public string Weapon;
            public bool Headshot;
        }

        public RoundSim Sim { get; private set; }
        /// <summary>Replay time sim time 0 is (the takeover, or the barrier drop when you took over in the buy phase).</summary>
        public float SourceStart => Sim.SourceStart;
        /// <summary>Replay time the AI is at (keeps running past the end of the recording).</summary>
        public float Now { get; private set; }
        public bool Started => Now >= SourceStart;
        public IReadOnlyList<AiKill> Kills => _kills;
        public IReadOnlyList<GhostDeath> Deaths => _deaths;
        public SimAgent You => _player >= 0 ? Sim.Agents[_player] : null;
        /// <summary>Your team (round play), or the attackers when nobody plays (command mode).</summary>
        public int Team => _player >= 0 ? _replay.Players[_player].Team : Sim.AttackingTeam;
        public bool ShowMatrix { get; set; }

        private const int MaxStepsPerFrame = 40;      // 2 s of sim at 20 Hz: a hitch never turns into a death spiral
        private const float TracerSeconds = 0.08f;

        private VrpReplay _replay;
        private ReplayViewer _ghosts;
        private RoundPlayController _round;
        private int _player;
        private bool _allAi;                // every agent left the recording at the takeover (command mode)
        private float _takeover, _roundStart, _recordedEnd;
        private string[] _guns;
        private SVector3[] _prev, _cur;
        private float[] _prevYaw, _curYaw, _lastShot;
        private readonly List<AiKill> _kills = new List<AiKill>();
        private readonly List<GhostDeath> _deaths = new List<GhostDeath>();
        private readonly List<(Vector3 Center, float Radius)> _smokes = new List<(Vector3, float)>();
        private readonly List<(LineRenderer Line, float Until)> _tracers = new List<(LineRenderer, float)>();
        private MapSightTest _mapSight;
        private int _killsSeen;
        private string _lastHitWeapon = "";
        private bool _lastHitHead;

        // ------------------------------------------------------------------ setup

        private static readonly object CacheLock = new object();
        private static string _cachedKey;
        private static SimMap _cachedMap;
        private static FittedProfile[] _cachedProfiles;
        // what each .vrp in the replay folders is (by last write time), so the library is read once per session
        private static readonly Dictionary<string, (DateTime Written, string Map, string Match)> LibraryIndex =
            new Dictionary<string, (DateTime, string, string)>();

        /// <summary>Other replays of the same map (newest first) the AI's map learns from as well.</summary>
        public const int MaxMapReplays = 16;

        /// <summary>Builds the AI's map and everyone's profile, then the round simulation, off the main thread (map and
        /// profiles are cached across restarts). The map (walkable space, sites, where players look, when attacks go in)
        /// learns from this replay and the other replays of its map in the replay folders; the profiles come from this
        /// match, measured with round play's perception. Throws from the task when the moment cannot be simulated.
        /// <paramref name="followRecording"/> false: every agent is AI from the takeover (self-play from that moment).</summary>
        public static Task<RoundSim> CreateAsync(VrpReplay replay, string vrpPath, float takeover, int seed, bool followRecording = true)
        {
            var folders = ReplayFolders(vrpPath);       // Application paths only on the main thread
            return Task.Run(() =>
            {
                // perceive (who sees and hears whom, so who can shoot and who leaves the recording) with the blackboard's numbers
                var ks = new KnowledgeSettings();
                var options = new SimOptions
                {
                    FovHalfAngle = ks.fieldOfView * 0.5f, MaxSightRange = ks.sightRange,
                    FootstepRange = ks.footstepRange, GunshotRange = ks.gunshotRange,
                };
                var (map, fitted) = MapFor(replay, vrpPath, folders, options);
                return RoundSim.FromReplay(replay, map, takeover, p => fitted[p].Profile, options, seed, followRecording);
            });
        }

        /// <summary>The loaded replay's folder and the import screen's default output folder.</summary>
        private static List<string> ReplayFolders(string vrpPath)
        {
            var folders = new List<string>();
            void Add(string folder)
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
                folder = Path.GetFullPath(folder);
                if (!folders.Contains(folder)) folders.Add(folder);
            }
            if (!string.IsNullOrEmpty(vrpPath)) Add(Path.GetDirectoryName(Path.GetFullPath(vrpPath)));
            Add(Path.Combine(Application.persistentDataPath, "Replays"));
            return folders;
        }

        private static (SimMap Map, FittedProfile[] Profiles) MapFor(VrpReplay replay, string vrpPath, List<string> folders, SimOptions options)
        {
            string key = replay.MatchId + "|" + replay.Map;
            lock (CacheLock)
            {
                if (_cachedKey == key) return (_cachedMap, _cachedProfiles);
                var replays = new List<VrpReplay> { replay };
                replays.AddRange(OtherReplaysOfMap(replay, vrpPath, folders));
                var map = SimMap.FromReplays(replays);
                AddCatalogSites(map, replay);
                // measured with the perception the sim runs with, converted with the transfer tuned for it
                var fitted = ProfileFitter.Fit(replay, map: map, options: options, transfer: ExecutionTransfer.ValoReplay);
                Debug.Log($"[RoundAi] map from {replays.Count} replay(s) of {replay.MapName}; " +
                          $"attacks learned from {map.Tempo?.PlantTimes.Count ?? 0} plants{(map.Tempo != null && map.Tempo.Known ? "" : " (too few: fixed timing)")}");
                _cachedKey = key;
                _cachedMap = map;
                _cachedProfiles = fitted;
                return (map, fitted);
            }
        }

        /// <summary>Up to MaxMapReplays other matches on the same map, newest first. Files that do not load (another
        /// format version, damaged) are skipped.</summary>
        private static List<VrpReplay> OtherReplaysOfMap(VrpReplay replay, string vrpPath, List<string> folders)
        {
            string self = string.IsNullOrEmpty(vrpPath) ? "" : Path.GetFullPath(vrpPath);
            var loaded = new Dictionary<string, VrpReplay>();
            var candidates = new List<(DateTime Written, string Path, string Match)>();
            foreach (var folder in folders)
                foreach (var file in Directory.GetFiles(folder, "*.vrp"))
                {
                    string full = Path.GetFullPath(file);
                    if (full == self) continue;
                    var written = File.GetLastWriteTimeUtc(full);
                    if (!LibraryIndex.TryGetValue(full, out var entry) || entry.Written != written)
                    {
                        // a .vrp only says its map once loaded: keep the ones on this map, remember what the rest are
                        try
                        {
                            var r = VrpReplay.Load(full);
                            entry = (written, r.Map, r.MatchId);
                            if (r.Map == replay.Map) loaded[full] = r;
                        }
                        catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException)
                        {
                            entry = (written, null, null);
                        }
                        LibraryIndex[full] = entry;
                    }
                    if (entry.Map == replay.Map && entry.Match != replay.MatchId) candidates.Add((written, full, entry.Match));
                }
            candidates.Sort((a, b) => b.Written.CompareTo(a.Written));
            var result = new List<VrpReplay>();
            var matches = new HashSet<string> { replay.MatchId };
            foreach (var c in candidates)
            {
                if (result.Count >= MaxMapReplays || !matches.Add(c.Match)) continue;   // the same match converted twice counts once
                if (!loaded.TryGetValue(c.Path, out var r))
                {
                    try { r = VrpReplay.Load(c.Path); }
                    catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException) { continue; }
                }
                result.Add(r);
            }
            return result;
        }

        /// <summary>Sites come from recorded plants, so a site nobody planted on in this match is missing. Add those from
        /// the map's callouts ("A Site"), snapped onto the walkable grid.</summary>
        private static void AddCatalogSites(SimMap map, VrpReplay replay)
        {
            var info = MapCatalog.Find(replay.Map);
            if (info == null) return;
            foreach (var c in info.Callouts)
            {
                if (c.Region != "Site" || c.SuperRegion.Length != 1 || map.SiteNamed(c.SuperRegion) != null) continue;
                if (c.X == 0f && c.Y == 0f) continue;          // placeholder coordinates
                VrpReplay.ToUnity(c.X, c.Y, c.Z, out float x, out float y, out float z);
                int n = map.Grid.Nearest(new SVector3(x, y, z), 12f);
                if (n < 0) continue;
                map.Sites.Add(new SimMap.Site { Name = c.SuperRegion, Center = map.Grid.Position(n), Radius = 6f });
            }
            map.Sites.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        }

        /// <summary>Hands your agent to you and takes over the ghosts.</summary>
        public void Init(RoundPlayController round, ReplayViewer ghosts, RoundSim sim)
        {
            _round = round;
            Init(ghosts, sim, round.Replay, round.Player, round.TakeoverTime);
        }

        /// <summary>No player: takes over every ghost from <paramref name="takeover"/> (the sim was created with
        /// followRecording false), for the replay viewer's command mode.</summary>
        public void InitCommanded(ReplayViewer ghosts, RoundSim sim, float takeover) => Init(ghosts, sim, ghosts.Replay, -1, takeover);

        private void Init(ReplayViewer ghosts, RoundSim sim, VrpReplay replay, int player, float takeover)
        {
            _ghosts = ghosts;
            _replay = replay;
            _player = player;
            _takeover = takeover;
            Sim = sim;
            _allAi = sim.Source == null;
            Now = ghosts.Time;
            int ri = _replay.RoundAt(SourceStart);
            _roundIndex = ri;
            _roundStart = ri >= 0 ? _replay.Rounds[ri].StartTime : 0f;
            _recordedEnd = ri >= 0 ? _replay.Rounds[ri].EndTime : float.PositiveInfinity;

            int n = sim.Agents.Count;
            _guns = new string[n];
            _prev = new SVector3[n];
            _cur = new SVector3[n];
            _prevYaw = new float[n];
            _curYaw = new float[n];
            _lastShot = new float[n];
            for (int i = 0; i < n; i++)
            {
                var lo = ri >= 0 ? _replay.LoadoutOf(ri, i) : null;
                _guns[i] = !string.IsNullOrEmpty(lo?.Primary) ? lo.Primary : !string.IsNullOrEmpty(lo?.Sidearm) ? lo.Sidearm : "";
                _prev[i] = _cur[i] = sim.Agents[i].Position;
                _prevYaw[i] = _curYaw[i] = sim.Agents[i].Yaw;
                _lastShot[i] = sim.Agents[i].LastShotTime;
            }
            // deaths of this round before the AI starts, as recorded
            foreach (var k in _replay.Kills)
            {
                if (k.Time < _roundStart || k.Time > SourceStart || k.Victim >= n) continue;
                _deaths.Add(new GhostDeath { Victim = k.Victim, Position = _replay.UnityPosition(_replay.Sample(k.Victim, k.Time - 0.1f)) });
            }

            if (_player >= 0) sim.TakeControl(_player);
            _mapSight = new MapSightTest(ghosts.IsMapCollider);
            sim.SightTest = Sight;
            ShowMatrix = GameSettings.Current.showAiMatrix;
            ghosts.Driver = this;

            for (int i = 0; i < _replay.Equippables.Count && i < 255; i++)
                if (!_equippable.ContainsKey(_replay.Equippables[i])) _equippable[_replay.Equippables[i]] = (byte)i;
            ISightTest sight = ghosts.MapGeometryReady ? _mapSight : new NoOcclusion();
            Knowledge = new KnowledgeSim(_replay, _knowledgeSettings, sight);
        }

        /// <summary>Fills the blackboards from the recording up to the takeover (what each team knew by then), then
        /// switches them to the live world and lets the AI decide from them. Spread over frames: sight is raycast.</summary>
        public System.Collections.IEnumerator WarmUp(Action<float> progress)
        {
            float from = _roundStart, span = Mathf.Max(0.01f, SourceStart - _roundStart);
            while (!Knowledge.AdvanceTo(SourceStart, 15))
            {
                progress?.Invoke(Mathf.Clamp01((Knowledge.Time - from) / span));
                yield return null;
            }
            Knowledge.GoLive(this);
            Sim.BeliefSource = this;
        }

        private void OnDestroy()
        {
            if (_ghosts != null && ReferenceEquals(_ghosts.Driver, this)) _ghosts.Driver = null;
        }

        // ------------------------------------------------------------------ running

        /// <summary>Feeds your pose in and steps the sim up to the current replay time (call once per frame).</summary>
        public void Advance(FpsController fps)
        {
            // follow the ghosts' clock; once the recording has run out, keep going on our own (unless the ghosts do)
            Now = _ghosts.Time < _replay.Duration - 0.01f || _ghosts.RunsPastEnd ? _ghosts.Time : Now + Time.deltaTime;
            float target = Now - SourceStart;
            if (target < 0f || Sim.Over) return;

            RefreshSmokes();
            float dt = 1f / Sim.Options.TickRate;
            var you = _player >= 0 ? Sim.Agents[_player] : null;
            for (int steps = 0; steps < MaxStepsPerFrame && !Sim.Over && Sim.Time + dt <= target + 1e-4f; steps++)
            {
                if (you != null && you.Alive && fps != null)
                {
                    // the sim puts a crouched eye 0.35 m above the centre, Unity 0.07 m: lower the centre to match
                    var p = fps.transform.position;
                    if (fps.Crouching) p.y -= 0.28f;
                    Sim.SetExternalState(_player, S(p), fps.transform.eulerAngles.y, walking: fps.Walking || fps.Crouching, crouching: fps.Crouching);
                }
                for (int i = 0; i < _cur.Length; i++)
                {
                    _prev[i] = _cur[i];
                    _prevYaw[i] = _curYaw[i];
                }
                Sim.Step();
                for (int i = 0; i < _cur.Length; i++)
                {
                    _cur[i] = Sim.Agents[i].Position;
                    _curYaw[i] = Sim.Agents[i].Yaw;
                }
                // what this step did, into the blackboards; then they sense the new state
                CollectKills();
                ReportEvents();
                ReportShots();
                if (Knowledge.Live != null) Knowledge.AdvanceTo(SourceStart + Sim.Time);
            }
            UpdateTracers();
        }

        private float SimNow => SourceStart + Sim.Time;

        private void ReportEvents()
        {
            for (; _eventsSeen < Sim.Events.Count; _eventsSeen++)
            {
                var e = Sim.Events[_eventsSeen];
                float t = SourceStart + e.Time;
                switch (e.Type)
                {
                    case SimEventType.Hit:
                        Knowledge.ReportDamage(e.Other, e.Agent, t, e.Other >= 0 && e.Other == _player ? _lastHitWeapon : e.Other >= 0 ? _guns[e.Other] : "");
                        break;
                    case SimEventType.Planted:
                        Knowledge.ReportPlant(e.Agent, t, U(Sim.SpikePosition));
                        break;
                    case SimEventType.DefuseStarted:
                        Knowledge.ReportDefuseStart(e.Agent, t);
                        break;
                }
            }
        }

        /// <summary>The AI's shots (yours come from <see cref="Gunshot"/>; recorded ones from the recording).</summary>
        private void ReportShots()
        {
            for (int i = 0; i < Sim.Agents.Count; i++)
            {
                var a = Sim.Agents[i];
                if (a.LastShotTime <= _lastShot[i]) continue;
                _lastShot[i] = a.LastShotTime;
                if (i == _player || a.Mode == AgentMode.Replay) continue;
                Knowledge.ReportShot(i, SimNow, U(a.Eye), _guns[i]);
                if (!GameSettings.Current.showRecordedShots) continue;
                var from = U(a.Eye);
                var to = a.Engaging >= 0 ? U(Sim.Agents[a.Engaging].Eye) : from + Quaternion.Euler(0f, a.Yaw, 0f) * Vector3.forward * 30f;
                Tracer(from, to, Color.Lerp(_ghosts.TeamColor(a.Team), Color.yellow, 0.6f));
            }
        }

        /// <summary>Your bullet hit an enemy (Unity decided it): damage before armor. Returns whether it killed.</summary>
        public bool Hit(int target, float damage, bool headshot, string weaponClass)
        {
            if (!Started) return false;
            _lastHitWeapon = weaponClass ?? "";
            _lastHitHead = headshot;
            return Sim.ExternalHit(_player, target, damage);
        }

        /// <summary>You fired (enemies in range hear it, and which gun it was).</summary>
        public void Gunshot(string weaponClass, Vector3 eye)
        {
            if (!Started) return;
            Sim.ExternalGunshot(_player);
            _lastShot[_player] = You.LastShotTime;
            Knowledge.ReportShot(_player, SimNow, eye, weaponClass);
        }

        private void CollectKills()
        {
            for (; _killsSeen < Sim.Kills.Count; _killsSeen++)
            {
                var k = Sim.Kills[_killsSeen];
                float t = SourceStart + k.Time;
                string weapon = "";
                bool head = false;
                if (!k.Simulated)
                {
                    // replayed from the recording: take its weapon and headshot
                    foreach (var rk in _replay.Kills)
                        if (rk.Victim == k.Victim && Mathf.Abs(rk.Time - t) < 0.3f) { weapon = rk.Weapon; head = rk.Headshot; break; }
                }
                else if (k.Killer >= 0 && k.Killer == _player) { weapon = _lastHitWeapon; head = _lastHitHead; }
                else if (k.Killer >= 0) weapon = _guns[k.Killer];
                _kills.Add(new AiKill { Time = t, Killer = k.Killer, Victim = k.Victim, Weapon = weapon, Headshot = head });
                Knowledge.ReportKill(k.Killer, k.Victim, t, weapon);
                _deaths.Add(new GhostDeath { Victim = k.Victim, Position = U(Sim.Agents[k.Victim].Position) });
            }
        }

        // ------------------------------------------------------------------ line of sight

        private void RefreshSmokes()
        {
            _smokes.Clear();
            foreach (var e in _replay.Entities)
            {
                if (e.Agent.Length == 0 || Now < e.SpawnTime || (e.DespawnTime >= 0 && Now >= e.DespawnTime) || !ReplayViewer.IsSmoke(e)) continue;
                if (RecordingHides(e.Owner, e.SpawnTime)) continue;
                e.PositionAt(Now, out float x, out float y, out float z);
                _smokes.Add((_replay.UnityPoint(x, y, z), ReplayViewer.SmokeRadius(e)));
            }
        }

        /// <summary>The sim's eye-to-eye test: smokes, then the built map's colliders (or the sim's own grid without one).</summary>
        private bool Sight(SVector3 from, SVector3 to)
        {
            var a = U(from);
            var b = U(to);
            foreach (var (c, r) in _smokes)
                if (SegmentHitsSphere(a, b, c, r)) return false;
            return _ghosts.MapGeometryReady ? _mapSight.Clear(a, b) : Sim.Map.Grid.LineOfSight(from, to);
        }

        private static bool SegmentHitsSphere(Vector3 a, Vector3 b, Vector3 c, float r)
        {
            var ab = b - a;
            float t = Mathf.Clamp01(Vector3.Dot(c - a, ab) / Mathf.Max(1e-6f, ab.sqrMagnitude));
            return (a + ab * t - c).sqrMagnitude < r * r;
        }

        // ------------------------------------------------------------------ IGhostDriver

        public bool TryGetPose(int player, out GhostPose pose)
        {
            pose = default;
            if (player == _player)
            {
                // the gold ghost of your original run: as recorded, but not into the next round
                if (Now < _recordedEnd) return false;
                return true;
            }
            if (!Started || player >= Sim.Agents.Count) return false;
            var a = Sim.Agents[player];
            if (!a.Alive) return true;
            if (a.Mode == AgentMode.Replay) return false;      // still on the recording: draw it smoothly from there
            // the sim moves in 20 Hz steps: draw one step behind, interpolated
            float dt = 1f / Sim.Options.TickRate;
            float alpha = Mathf.Clamp01((Now - SourceStart - Sim.Time) / dt);
            pose.Visible = true;
            pose.Position = Vector3.Lerp(U(_prev[player]), U(_cur[player]), alpha);
            pose.Yaw = Mathf.LerpAngle(_prevYaw[player], _curYaw[player], alpha);
            pose.Crouching = a.Crouching;
            return true;
        }

        public bool RecordingHides(int player, float time)
        {
            if (time >= _recordedEnd) return true;             // the next round of the recording never comes
            if (player == _player) return time >= _takeover;
            if (Sim == null || player < 0 || player >= Sim.Agents.Count) return false;
            if (_allAi) return time >= _takeover;
            float left = Sim.Agents[player].DivergedAt;
            return left >= 0f && time >= SourceStart + left;
        }

        public bool TryGetSpike(out Vector3 position, out bool visible)
        {
            position = default;
            visible = false;
            if (!Started) return false;
            switch (Sim.SpikeState)
            {
                case SpikeStatus.Carried:
                    if (Sim.SpikeCarrier == _player) return true;      // in your hands
                    if (TryGetPose(Sim.SpikeCarrier, out var pose))
                    {
                        visible = pose.Visible;
                        position = pose.Position + Vector3.up * 1.3f;
                    }
                    else
                    {
                        var f = _replay.Sample(Sim.SpikeCarrier, Now);
                        visible = f.Alive;
                        position = _replay.UnityPosition(f) + Vector3.up * 1.3f;
                    }
                    return true;
                case SpikeStatus.Dropped:
                case SpikeStatus.Planted:
                case SpikeStatus.Defused:
                    visible = true;
                    position = U(Sim.SpikePosition) + Vector3.down * (NavGrid.ActorHeight - 0.15f);
                    return true;
                default:
                    return true;
            }
        }

        // ------------------------------------------------------------------ ILiveWorld: the blackboards' live input

        float ILiveWorld.Start => SourceStart;
        int ILiveWorld.Round => _roundIndex;
        bool ILiveWorld.Hides(int player, float time) => RecordingHides(player, time);

        /// <summary>You (from the first-person controller) and everyone the AI plays, as they are now; players still on
        /// the recording keep their recorded frames.</summary>
        bool ILiveWorld.TryFrame(int player, float time, out Frame frame)
        {
            frame = default;
            if (player == _player)
            {
                if (time < _takeover) return false;
                var fps = _round.Fps;
                var you = Sim.Agents[_player];
                if (fps == null) return false;
                var gun = _round.Weapons != null ? _round.Weapons.Equipped : null;
                frame = MakeFrame(fps.transform.position, fps.transform.eulerAngles.y, you.Alive && !_round.Dead,
                    fps.Crouching, fps.Walking, !fps.Grounded, you.Health, you.Armor,
                    gun != null ? EquippableIndex(gun.Stats.Class) : (byte)0,
                    gun == null || gun.Stats.Melee ? (byte)255 : (byte)Mathf.Clamp(gun.Magazine, 0, 254));
                return true;
            }
            if (player >= Sim.Agents.Count) return false;
            var a = Sim.Agents[player];
            if (a.Alive && a.Mode == AgentMode.Replay) return false;
            frame = MakeFrame(U(a.Position), a.Yaw, a.Alive, a.Crouching, a.Walking, false, a.Health, a.Armor, EquippableIndex(_guns[player]), 255);
            return true;
        }

        private byte EquippableIndex(string cls) => !string.IsNullOrEmpty(cls) && _equippable.TryGetValue(cls, out byte i) ? i : (byte)0;

        private Frame MakeFrame(Vector3 unity, float yaw, bool alive, bool crouching, bool walking, bool airborne, float health, float armor, byte weapon, byte ammo)
        {
            var f = new Frame
            {
                YawCenti = (short)Mathf.Clamp(Mathf.RoundToInt(Mathf.DeltaAngle(0f, yaw) * 100f), -18000, 17999),
                Flags = FrameFlags.HasData | (alive ? FrameFlags.Alive : 0) | (crouching ? FrameFlags.Crouching : 0)
                        | (walking ? FrameFlags.Walking : 0) | (airborne ? FrameFlags.Airborne : 0),
                Health = (byte)Mathf.Clamp(Mathf.CeilToInt(alive ? health : 0f), 0, 255),
                Armor = (byte)Mathf.Clamp(Mathf.CeilToInt(armor), 0, 255),
                Weapon = weapon, Ammo = ammo, ReserveAmmo = 255,
            };
            if (_replay.Space == CoordinateSpace.Unity) { f.X = unity.x; f.Y = unity.y; f.Z = unity.z; }
            else { f.X = unity.z * 100f; f.Y = unity.x * 100f; f.Z = unity.y * 100f; }   // inverse of VrpReplay.ToUnity
            return f;
        }

        // ------------------------------------------------------------------ IBeliefSource: decisions read the blackboard

        /// <summary>The team's blackboard on one enemy: a live sighting is certain; otherwise the last position (seen,
        /// revealed, heard, hit by) decayed by age, or the most likely callout area when that says more.</summary>
        public bool Believes(int team, int enemy, out SVector3 position, out float certainty)
        {
            position = default;
            certainty = 0f;
            var ei = Intel(team, enemy);
            if (ei == null || ei.Dead) return false;
            float c = ei.Visible ? 1f : ei.CertaintyAt(Knowledge.Time, _knowledgeSettings);
            var p = ei.Position;
            if (Knowledge.Areas != null)
            {
                int area = ei.MostLikelyArea(out float prob);
                if (area >= 0 && prob * areaCertainty > c) { c = prob * areaCertainty; p = Knowledge.Areas.Areas[area].Center; }
            }
            if (c <= 0.01f) return false;
            position = S(p);
            certainty = c;
            return true;
        }

        private EnemyIntel Intel(int team, int enemy) =>
            Knowledge == null || team < 0 || team > 1 || enemy < 0 || enemy >= Knowledge.Teams[team].Enemies.Length ? null : Knowledge.Teams[team].Enemies[enemy];

        // ------------------------------------------------------------------ views for the controller and HUD

        /// <summary>Health, armor and gun of a player the AI drives; false = it plays as recorded.</summary>
        public bool TryGetStatus(int player, out int health, out int armor, out string gun)
        {
            health = armor = 0;
            gun = "";
            if (!Started || player == _player || player >= Sim.Agents.Count) return false;
            var a = Sim.Agents[player];
            if (a.Alive && a.Mode == AgentMode.Replay) return false;
            health = Mathf.CeilToInt(a.Health);
            armor = Mathf.CeilToInt(a.Armor);
            gun = _guns[player];
            return true;
        }

        private void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.f3Key.wasPressedThisFrame)
            {
                ShowMatrix = !ShowMatrix;
                GameSettings.Current.showAiMatrix = ShowMatrix;
                GameSettings.Save();
            }
        }

        // ------------------------------------------------------------------ tracers

        private void UpdateTracers()
        {
            for (int i = 0; i < _tracers.Count; i++)
                if (_tracers[i].Line.gameObject.activeSelf && Time.time > _tracers[i].Until) _tracers[i].Line.gameObject.SetActive(false);
        }

        private void Tracer(Vector3 from, Vector3 to, Color color)
        {
            int slot = _tracers.FindIndex(t => !t.Line.gameObject.activeSelf);
            if (slot < 0)
            {
                var line = new GameObject("AI Tracer").AddComponent<LineRenderer>();
                line.transform.SetParent(transform, false);
                line.sharedMaterial = _ghosts.LineMaterial;
                line.positionCount = 2;
                line.widthMultiplier = 0.04f;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _tracers.Add((line, 0f));
                slot = _tracers.Count - 1;
            }
            var l = _tracers[slot].Line;
            l.gameObject.SetActive(true);
            l.startColor = color;
            l.endColor = new Color(color.r, color.g, color.b, 0f);
            l.SetPosition(0, from);
            l.SetPosition(1, to);
            _tracers[slot] = (l, Time.time + TracerSeconds);
        }

        // ------------------------------------------------------------------ behaviour matrix overlay (F3)

        private static readonly string[] ChoiceShort = { "HOLD", "ADV", "PEEK", "BACK", "TRADE" };
        private GUIStyle _label, _table;

        private void OnGUI()
        {
            if (!ShowMatrix || Sim == null || (_round != null && _round.Paused)) return;
            _label ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.LowerCenter, fontSize = 12, richText = true };
            _table ??= new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 12, richText = true, padding = new RectOffset(8, 8, 6, 6) };
            var cam = Camera.main;

            var rows = new System.Text.StringBuilder();
            rows.Append("<b>AI BEHAVIOUR MATRIX</b>   (F3)\n");
            rows.Append(Started ? $"sim {Sim.Time:0.0}s   director: atk {DirectorText(Sim.AttackingTeam)}\n" : "waiting for the barrier drop\n");
            for (int team = 0; team < 2; team++)
            {
                rows.Append($"<color={(team == Team ? "#7CFFB2" : "#FF6B6B")}>blackboard</color> ");
                bool any = false;
                foreach (var ei in Knowledge.Teams[team].EachEnemy())
                {
                    string name = _replay.Players[ei.Player].AgentName;
                    if (ei.Dead) { rows.Append($"<color=#888>{name} dead</color>  "); any = true; continue; }
                    if (!Believes(team, ei.Player, out _, out float c)) continue;
                    string how = ei.Visible ? "seen" : ei.PositionSource == IntelSource.None || ei.CertaintyAt(Knowledge.Time, _knowledgeSettings) < c ? "area" : ei.PositionSource.ToString().ToLowerInvariant();
                    rows.Append($"{name} {c:0.00} {how}  ");
                    any = true;
                }
                rows.Append(any ? "\n" : "nothing\n");
            }
            for (int i = 0; i < Sim.Agents.Count; i++)
            {
                var a = Sim.Agents[i];
                string who = _replay.Players[i].AgentName;
                string line = $"<color={(a.Team == Team ? "#7CFFB2" : "#FF6B6B")}>{who,-9}</color> {State(a)}";
                rows.Append(line).Append('\n');

                if (cam == null || !a.Alive || i == _player || !TryGetPose(i, out var pose) || !pose.Visible) continue;
                var sp = cam.WorldToScreenPoint(pose.Position + Vector3.up * 1.3f);
                if (sp.z <= 0f) continue;
                var r = new Rect(sp.x - 110f, Screen.height - sp.y - 54f, 220f, 54f);
                GUI.Label(r, $"<b>{who}</b>  {State(a)}", _label);
            }
            var size = _table.CalcSize(new GUIContent(rows.ToString()));
            GUI.Box(new Rect(12f, 12f, Mathf.Max(360f, size.x), size.y), rows.ToString(), _table);
        }

        private string DirectorText(int team)
        {
            var d = Sim.Director(team);
            if (d == null) return "";
            return $"{d.Plan} → {d.TargetSite}{(d.Executing ? " (executing)" : "")}{(d.Rotations > 0 ? " rotated" : "")}";
        }

        /// <summary>Mode and, for the AI, the chosen action and every score (the chosen one bold).</summary>
        private string State(SimAgent a)
        {
            if (!a.Alive) return "<color=#888>dead</color>";
            if (a.Index == _player) return "<color=#FFC233>YOU</color>";
            if (a.Mode == AgentMode.Replay) return "<color=#AAA>recording</color>";
            var sb = new System.Text.StringBuilder();
            sb.Append(a.Action.ToString().ToUpperInvariant());
            if (a.Engaging >= 0) sb.Append(" → ").Append(_replay.Players[a.Engaging].AgentName);
            sb.Append("   ");
            for (int c = 0; c < a.Scores.Length; c++)
            {
                bool chosen = UtilityBrain.Choices[c] == a.Action;
                string s = $"{ChoiceShort[c]} {a.Scores[c]:0.00}";
                sb.Append(chosen ? $"<b>{s}</b>" : s).Append("  ");
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ helpers

        private static Vector3 U(SVector3 v) => new Vector3(v.X, v.Y, v.Z);
        private static SVector3 S(Vector3 v) => new SVector3(v.x, v.y, v.z);
    }
}
