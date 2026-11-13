using System;
using System.Collections.Generic;
using UnityEngine;
using ValorantReplay.Playback;
using ValoReplay.Replay;

namespace ValoReplay.Knowledge
{
    /// <summary>Line-of-sight test between two points (true = nothing in the way).</summary>
    public interface ISightTest
    {
        bool Clear(Vector3 from, Vector3 to);
    }

    /// <summary>No geometry to test against: only view cones, range and smokes limit sight.</summary>
    public sealed class NoOcclusion : ISightTest
    {
        public bool Clear(Vector3 from, Vector3 to) => true;
    }

    /// <summary>Raycasts against the built map's colliders (other colliders, such as player capsules, are ignored).</summary>
    public sealed class MapSightTest : ISightTest
    {
        private readonly Func<Collider, bool> _isMap;
        private readonly RaycastHit[] _hits = new RaycastHit[32];

        public MapSightTest(Func<Collider, bool> isMap) { _isMap = isMap; }

        public bool Clear(Vector3 from, Vector3 to)
        {
            var d = to - from;
            float len = d.magnitude;
            if (len < 0.01f) return true;
            // map walls can be single-sided planes: hit them from behind too
            bool backfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            int n = Physics.RaycastNonAlloc(from, d / len, _hits, len, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            Physics.queriesHitBackfaces = backfaces;
            for (int i = 0; i < n; i++) if (_isMap(_hits[i].collider)) return false;
            return true;
        }
    }

    /// <summary>
    /// Round play after a takeover: the world is no longer the recording. From <see cref="Start"/> on, players come
    /// from <see cref="TryFrame"/> where it says so, and recorded actions it <see cref="Hides"/> did not happen. What
    /// happens instead is reported with <see cref="KnowledgeSim.ReportShot"/> and the other Report methods.
    /// </summary>
    public interface ILiveWorld
    {
        /// <summary>Replay time from which the world is live.</summary>
        float Start { get; }
        /// <summary>The round being played (live play never moves on to the next recorded round).</summary>
        int Round { get; }
        /// <summary>The player's state now (time is the sensor update's); false = as recorded.</summary>
        bool TryFrame(int player, float time, out Frame frame);
        /// <summary>True = what the recording has this player do at this time did not happen.</summary>
        bool Hides(int player, float time);
    }

    /// <summary>
    /// Runs both teams' senses over a recorded round and fills their <see cref="TeamBlackboard"/>s:
    /// sight (view cone, range, smokes, nearsight, flashes, map geometry), recon reveals, sounds with falloff
    /// (footsteps, landings, gunshots with per-gun identity, reloads, ability casts, spike), damage and debuffs taken,
    /// and the public kill feed. Knowledge starts empty at each round start, and the senses start at the barrier drop
    /// (during the buy phase the teams are walled into their spawns).
    /// The senses run at a fixed rate, so the result does not depend on frame rate; seeking backwards re-runs the
    /// round from its start (sight results are cached, so that is cheap after the first pass).
    /// After <see cref="GoLive"/> the same senses run on the live world instead (see <see cref="ILiveWorld"/>).
    /// </summary>
    public sealed class KnowledgeSim
    {
        public readonly VrpReplay Replay;
        public readonly KnowledgeSettings Settings;
        /// <summary>Index = team (0, 1).</summary>
        public readonly TeamBlackboard[] Teams;
        /// <summary>Time of the last sensor update.</summary>
        public float Time { get; private set; } = float.NegativeInfinity;
        public int Round { get; private set; } = -1;
        /// <summary>False when sight ignores walls (no built map).</summary>
        public bool HasOcclusion => !(_sight is NoOcclusion);
        /// <summary>Callout areas for area beliefs; null when the map has no callouts or they are off.</summary>
        public readonly AreaModel Areas;

        private sealed class Vision
        {
            public bool[] Sees;                 // observer * players + target
            public List<int>[] Util;            // per team: enemy utility objects in sight
            public float[][] Watched;           // per team: share of each area's probes in an ally's sight
        }

        private readonly ISightTest _sight;
        private readonly int _n;
        private readonly int[] _team;
        private readonly AbilitySlotResolver _slots;
        private readonly List<Shot> _shots;
        private readonly List<Kill> _kills;
        private readonly List<DamageEvent> _damage;
        private readonly List<StatusEffect> _effects;
        private readonly List<AbilityCast> _casts;
        private readonly List<DefuseAttempt> _defuses;
        private readonly List<int>[] _castsByPlayer;
        private readonly Dictionary<int, Vision> _visionCache = new Dictionary<int, Vision>();
        private readonly List<(Vector3 Center, float Radius)> _smokes = new List<(Vector3, float)>();
        private Frame[] _frames, _prev;
        private float[] _lastStep;
        private float[,] _lastDamageCue;
        private int _tick, _shot, _kill, _dmg, _effect, _cast, _defuse;
        private float _prevTime;
        private readonly HashSet<int>[] _probeSeen = { new HashSet<int>(), new HashSet<int>() };
        private readonly List<PlayerStatSample>[] _stats;
        /// <summary>Per round, team and enemy: alive at the round's end and guns known then (for the next round).</summary>
        private readonly Dictionary<int, (bool Alive, string[] Guns)> _carry = new Dictionary<int, (bool, string[])>();
        private static readonly string[] KitSlots = { "C", "Q", "E" };

        public KnowledgeSim(VrpReplay replay, KnowledgeSettings settings, ISightTest sight)
        {
            Replay = replay;
            Settings = settings;
            _sight = sight ?? new NoOcclusion();
            _n = replay.Players.Count;
            _team = new int[_n];
            for (int p = 0; p < _n; p++) _team[p] = replay.Players[p].Team == 0 ? 0 : 1;
            Teams = new[] { new TeamBlackboard(0, _team), new TeamBlackboard(1, _team) };
            _slots = new AbilitySlotResolver(replay);

            _shots = Sorted(replay.Shots, s => s.Time);
            _kills = Sorted(replay.Kills, k => k.Time);
            _damage = Sorted(replay.Damage, d => d.Time);
            _effects = Sorted(replay.StatusEffects, s => s.Time);
            _casts = Sorted(replay.AbilityCasts, c => c.Time);
            _defuses = Sorted(replay.DefuseAttempts, d => d.StartTime);
            _castsByPlayer = new List<int>[_n];
            for (int p = 0; p < _n; p++) _castsByPlayer[p] = new List<int>();
            for (int i = 0; i < _casts.Count; i++)
                if (_casts[i].Player < _n) _castsByPlayer[_casts[i].Player].Add(i);

            _frames = new Frame[_n];
            _prev = new Frame[_n];
            _lastStep = new float[_n];
            _lastDamageCue = new float[2, _n];

            _stats = new List<PlayerStatSample>[_n];
            for (int p = 0; p < _n; p++) _stats[p] = new List<PlayerStatSample>();
            foreach (var st in replay.PlayerStats) if (st.Player < _n) _stats[st.Player].Add(st);
            foreach (var list in _stats) list.Sort((a, b) => a.Time.CompareTo(b.Time));
            LearnKits();

            if (settings.areaBeliefs) Areas = AreaModel.Build(replay, settings);
            if (Areas != null)
                foreach (var bb in Teams)
                {
                    bb.Expected = new float[Areas.Areas.Length];
                    foreach (var ei in bb.EachEnemy()) ei.Areas = new float[Areas.Areas.Length];
                }
        }

        private static List<T> Sorted<T>(List<T> source, Func<T, float> time)
        {
            var list = new List<T>(source);
            list.Sort((a, b) => time(a).CompareTo(time(b)));
            return list;
        }

        /// <summary>Settings changed: forget cached sight and re-run the round on the next advance.</summary>
        public void Invalidate()
        {
            if (Live != null) return;          // a live round cannot be re-run
            _visionCache.Clear();
            Round = -1;
        }

        // ------------------------------------------------------------------------------------------ live play
        public ILiveWorld Live { get; private set; }

        private enum LiveKind : byte { Shot, Kill, Damage, Plant, Defuse }

        private struct LiveEvent
        {
            public float Time;
            public LiveKind Kind;
            public int A, B;            // shooter / killer / attacker / planter / defuser; victim
            public Vector3 Position;    // shot origin (eye) / spike
            public string Weapon;
        }

        private readonly List<LiveEvent> _live = new List<LiveEvent>();

        /// <summary>From now on the senses read the live world. Call once caught up to <see cref="ILiveWorld.Start"/>
        /// (everything before it happened as recorded).</summary>
        public void GoLive(ILiveWorld live)
        {
            Live = live;
            Round = live.Round;
        }

        /// <summary>Live: a player fired (from their eyes). Enemies hear it, and which gun it was.</summary>
        public void ReportShot(int player, float time, Vector3 eye, string weapon) =>
            Queue(new LiveEvent { Time = time, Kind = LiveKind.Shot, A = player, Position = eye, Weapon = weapon });

        /// <summary>Live: a death in the kill feed (killer -1 = no player).</summary>
        public void ReportKill(int killer, int victim, float time, string weapon) =>
            Queue(new LiveEvent { Time = time, Kind = LiveKind.Kill, A = killer, B = victim, Weapon = weapon });

        /// <summary>Live: attacker damaged victim (the victim's team learns the gun and roughly where it came from).</summary>
        public void ReportDamage(int attacker, int victim, float time, string weapon) =>
            Queue(new LiveEvent { Time = time, Kind = LiveKind.Damage, A = attacker, B = victim, Weapon = weapon });

        public void ReportPlant(int planter, float time, Vector3 spike) =>
            Queue(new LiveEvent { Time = time, Kind = LiveKind.Plant, A = planter, Position = spike });

        public void ReportDefuseStart(int defuser, float time) =>
            Queue(new LiveEvent { Time = time, Kind = LiveKind.Defuse, A = defuser });

        private void Queue(LiveEvent e)
        {
            int i = _live.Count;
            while (i > 0 && _live[i - 1].Time > e.Time) i--;
            _live.Insert(i, e);
        }

        private bool IsLive(float t) => Live != null && t >= Live.Start;

        /// <summary>A recorded action of this player at this time that did not happen in live play.</summary>
        private bool Hidden(int player, float time) => IsLive(time) && player >= 0 && player < _n && Live.Hides(player, time);

        private Frame FrameOf(int p, float t) => IsLive(t) && Live.TryFrame(p, t, out var f) ? f : Replay.Sample(p, t);

        private void LiveEvents(float t)
        {
            int done = 0;
            for (; done < _live.Count && _live[done].Time <= t; done++)
            {
                var e = _live[done];
                switch (e.Kind)
                {
                    case LiveKind.Shot:
                        if (e.A >= 0 && e.A < _n) OnShot(e.A, e.Time, e.Weapon, e.Position + Vector3.down * 0.6f);
                        break;
                    case LiveKind.Kill:
                        OnKill(e.A, e.B, e.Time, e.Weapon);
                        break;
                    case LiveKind.Damage:
                        OnDamage(e.A, e.B, e.Time, e.Weapon);
                        break;
                    case LiveKind.Plant:
                        if (e.A >= 0 && e.A < _n) Hear(EnemiesOf(e.A), CueKind.Plant, e.A, e.Position + Vector3.up, Settings.spikeRange, e.Time, "");
                        break;
                    case LiveKind.Defuse:
                        if (e.A >= 0 && e.A < _n && OnMap(_frames[e.A])) Hear(EnemiesOf(e.A), CueKind.Defuse, e.A, Pos(e.A), Settings.spikeRange, e.Time, "");
                        break;
                }
            }
            if (done > 0) _live.RemoveRange(0, done);
        }

        /// <summary>Runs the senses up to <paramref name="time"/>, at most <paramref name="maxSteps"/> updates this
        /// call. Returns true when caught up.</summary>
        public bool AdvanceTo(float time, int maxSteps = int.MaxValue)
        {
            if (Replay.Rounds.Count == 0) return true;
            int round = Live != null ? Live.Round : Replay.RoundAt(time);
            if (Live == null && (round != Round || time < Time - 1e-4f))
            {
                // moving on to the next round: finish this one so its survivors and their guns carry over
                if (Round >= 0 && round == Round + 1 && time >= Time) FinishRound();
                Reset(round);
            }
            float rate = Mathf.Max(1f, Settings.sensorRate);
            int steps = 0;
            while (_tick / rate <= time + 1e-5f)
            {
                if (steps++ >= maxSteps) return false;
                Step(_tick / rate);
                _tick++;
            }
            return true;
        }

        private void Reset(int round)
        {
            Round = round;
            var rd = Replay.Rounds[round];
            foreach (var t in Teams) t.Reset();
            float rate = Mathf.Max(1f, Settings.sensorRate);
            // Time stays at the round start through the buy phase (so seeking within it does not reset every frame);
            // sensing starts at the barrier drop
            Time = rd.StartTime;
            float start = Mathf.Max(rd.StartTime, rd.BarrierTime);
            _prevTime = start;
            _tick = Mathf.CeilToInt(start * rate - 1e-4f);
            _shot = LowerBound(_shots, s => s.Time, start);
            _kill = LowerBound(_kills, k => k.Time, start);
            _dmg = LowerBound(_damage, d => d.Time, start);
            _effect = LowerBound(_effects, s => s.Time, start);
            _cast = LowerBound(_casts, c => c.Time, start);
            _defuse = LowerBound(_defuses, d => d.StartTime, start);
            for (int p = 0; p < _n; p++)
            {
                _prev[p] = Replay.Sample(p, start);
                _lastStep[p] = float.NegativeInfinity;
                _lastDamageCue[0, p] = _lastDamageCue[1, p] = float.NegativeInfinity;
            }
            if (Areas != null) StartAreas(round);
            StartEconomy(round);
        }

        private static int LowerBound<T>(List<T> list, Func<T, float> time, float value)
        {
            int lo = 0, hi = list.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (time(list[mid]) < value) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        // ------------------------------------------------------------------------------------------ one sensor update
        private void Step(float t)
        {
            float tp = _prevTime;
            _prevTime = Time = t;
            for (int p = 0; p < _n; p++) _frames[p] = FrameOf(p, t);

            if (Areas != null) foreach (var bb in Teams) SpreadAreas(bb, t - tp);
            var vision = VisionAt(_tick, t);
            foreach (var bb in Teams) ApplyVision(bb, vision, t, tp);
            if (Areas != null) foreach (var bb in Teams) ClearWatchedAreas(bb, vision, t - tp);

            FrameSounds(t, tp);
            ShotSounds(t);
            CastSounds(t);
            SpikeSounds(t, tp);
            KillFeed(t);
            DamageTaken(t);
            EffectsTaken(t);
            if (Live != null) LiveEvents(t);
            if (Areas != null) foreach (var bb in Teams) SumAreas(bb);
            if (Settings.ultPointsPublic)
                foreach (var bb in Teams)
                    foreach (var ei in bb.EachEnemy()) ei.UltPoints = StatAt(ei.Player, t)?.UltPoints ?? -1;

            var swap = _prev;
            _prev = _frames;
            _frames = swap;
        }

        private static bool OnMap(in Frame f) => f.Alive && (f.Flags & FrameFlags.OffMap) == 0;
        private Vector3 Pos(int p) => Replay.UnityPosition(_frames[p]);
        private Vector3 Eye(in Frame f) => Replay.UnityPosition(f) + Vector3.up * (f.Crouching ? 0.07f : 0.62f);
        private TeamBlackboard EnemiesOf(int player) => Teams[1 - _team[player]];

        private static bool Active(Entity e, float t) => t >= e.SpawnTime && (e.DespawnTime < 0 || t < e.DespawnTime);

        private static readonly string[] NotGuns = { "Ability", "Gun_", "Equippable", "Projectile", "GameObject", "Gameobject" };

        /// <summary>A buyable gun (not an ability, ability gun, projectile or the knife).</summary>
        public static bool IsGun(string cls)
        {
            if (string.IsNullOrEmpty(cls)) return false;
            foreach (var prefix in NotGuns) if (cls.StartsWith(prefix, StringComparison.Ordinal)) return false;
            return true;
        }

        private static bool Suppressed(string gun) => gun == "LugerPistol" || gun == "SubMachineGun_MP5";

        // ------------------------------------------------------------------------------------------ sight
        private Vision VisionAt(int tick, float t)
        {
            bool live = IsLive(t);      // the live world never repeats: no caching
            if (!live && _visionCache.TryGetValue(tick, out var v)) return v;
            v = new Vision { Sees = new bool[_n * _n], Util = new[] { new List<int>(), new List<int>() } };
            if (Areas != null)
            {
                v.Watched = new[] { new float[Areas.Areas.Length], new float[Areas.Areas.Length] };
                _probeSeen[0].Clear();
                _probeSeen[1].Clear();
            }

            _smokes.Clear();
            foreach (var e in Replay.Entities)
            {
                if (e.Agent.Length == 0 || !Active(e, t) || !ReplayViewer.IsSmoke(e) || Hidden(e.Owner, e.SpawnTime)) continue;
                e.PositionAt(t, out float x, out float y, out float z);
                _smokes.Add((Replay.UnityPoint(x, y, z), ReplayViewer.SmokeRadius(e)));
            }

            float halfFov = Settings.fieldOfView * 0.5f;
            for (int a = 0; a < _n; a++)
            {
                var fa = _frames[a];
                if (!OnMap(fa) || ((fa.Flags & FrameFlags.Blinded) != 0 && !Hidden(a, t))) continue;
                float range = Nearsighted(a, t) ? Settings.nearsightRange : Settings.sightRange;
                var eye = Eye(fa);
                float yaw = fa.Yaw * Mathf.Deg2Rad;
                var forward = new Vector3(Mathf.Sin(yaw), 0f, Mathf.Cos(yaw));

                for (int e = 0; e < _n; e++)
                {
                    if (_team[e] == _team[a] || !OnMap(_frames[e])) continue;
                    var head = Eye(_frames[e]);
                    var body = Replay.UnityPosition(_frames[e]);
                    if (!InView(eye, forward, head, range, halfFov)) continue;
                    if (Clear(eye, head) || Clear(eye, body)) v.Sees[a * _n + e] = true;
                }

                if (Areas != null) WatchAreas(v, _team[a], eye, forward, range, halfFov);

                var seen = v.Util[_team[a]];
                for (int i = 0; i < Replay.Entities.Count; i++)
                {
                    var ent = Replay.Entities[i];
                    if (ent.Owner >= _n || _team[ent.Owner] == _team[a] || ent.Agent.Length == 0 || !Active(ent, t) || seen.Contains(i)) continue;
                    if (Hidden(ent.Owner, ent.SpawnTime)) continue;
                    ent.PositionAt(t, out float x, out float y, out float z);
                    var point = Replay.UnityPoint(x, y, z) + Vector3.up * 0.3f;
                    // a smoke is visible itself, so only map geometry blocks utility
                    if (InView(eye, forward, point, range, halfFov) && _sight.Clear(eye, point)) seen.Add(i);
                }
            }
            if (!live) _visionCache[tick] = v;
            return v;
        }

        private bool Nearsighted(int player, float t)
        {
            if (Hidden(player, t)) return false;            // the recorded debuff hit where they no longer are
            foreach (var s in Replay.StatusEffectsAt(player, t))
                if (s.Type == StatusEffectType.Nearsight && !Hidden(s.SourcePlayer, s.Time)) return true;
            return false;
        }

        private static bool InView(Vector3 eye, Vector3 forward, Vector3 point, float range, float halfFov)
        {
            var to = point - eye;
            if (to.sqrMagnitude > range * range) return false;
            var flat = new Vector3(to.x, 0f, to.z);
            return flat.sqrMagnitude < 0.25f || Vector3.Angle(forward, flat) <= halfFov;
        }

        private bool Clear(Vector3 from, Vector3 to) => !ThroughSmoke(from, to) && _sight.Clear(from, to);

        private bool ThroughSmoke(Vector3 from, Vector3 to)
        {
            var d = to - from;
            float len2 = d.sqrMagnitude;
            foreach (var (c, r) in _smokes)
            {
                float u = len2 > 1e-6f ? Mathf.Clamp01(Vector3.Dot(c - from, d) / len2) : 0f;
                if ((from + d * u - c).sqrMagnitude < r * r) return true;
            }
            return false;
        }

        private void ApplyVision(TeamBlackboard bb, Vision v, float t, float tp)
        {
            foreach (var ei in bb.EachEnemy())
            {
                ei.Visible = false;
                ei.Spotter = -1;
                if (ei.Dead) continue;
                for (int a = 0; a < _n; a++)
                    if (_team[a] == bb.Team && v.Sees[a * _n + ei.Player]) { ei.Visible = true; ei.Spotter = a; break; }
                if (!ei.Visible) continue;
                SetPosition(ei, Pos(ei.Player), t, 0.5f, 1f, IntelSource.Seen, default);
                PinArea(ei, Pos(ei.Player));
                var gun = Replay.WeaponOf(_frames[ei.Player]);
                if (IsGun(gun)) KnowGun(ei, gun, IntelSource.Seen, t);
            }

            // recon: an ally's reveal on an enemy (dart, prowler, trapwire, ...)
            foreach (var s in Replay.StatusEffects)
            {
                if (s.Type != StatusEffectType.Reveal || s.Player >= _n || _team[s.Player] == bb.Team) continue;
                if (s.SourcePlayer < _n && _team[s.SourcePlayer] != bb.Team) continue;
                if (Hidden(s.SourcePlayer, s.Time) || Hidden(s.Player, s.Time)) continue;   // not cast, or they are elsewhere
                bool on = s.ActiveAt(t) || (s.IsPing && s.Time > tp && s.Time <= t);
                var ei = bb.Enemies[s.Player];
                if (!on || ei.Dead || ei.Visible || !OnMap(_frames[s.Player])) continue;
                SetPosition(ei, Pos(s.Player), t, 1.5f, 0.95f, IntelSource.Revealed, default);
                PinArea(ei, Pos(s.Player));
            }

            foreach (int i in v.Util[bb.Team])
            {
                var ent = Replay.Entities[i];
                ent.PositionAt(t, out float x, out float y, out float z);
                var u = bb.Utility.Find(k => k.Entity == i);
                if (u == null)
                {
                    u = new UtilIntel { Entity = i, Owner = ent.Owner, Slot = _slots.OfEntity(ent), FirstSeen = t };
                    bb.Utility.Add(u);
                    NoteCast(bb, ent.Owner, u.Slot, ent.SpawnTime, IntelSource.Seen, 0.9f, window: 5f, requireRecorded: true);
                }
                u.Position = Replay.UnityPoint(x, y, z);
                u.LastSeen = t;
            }
            // vanished while in sight: it is gone
            float step = 1f / Mathf.Max(1f, Settings.sensorRate);
            foreach (var u in bb.Utility)
            {
                if (u.Gone) continue;
                float end = Replay.Entities[u.Entity].DespawnTime;
                if (end >= 0 && end <= t && u.LastSeen >= end - step - 1e-4f) u.Gone = true;
            }
        }

        // ------------------------------------------------------------------------------------------ hearing
        /// <summary>Best certainty with which a team hears a sound (0 = out of everyone's range). Heard sounds are
        /// logged, and update the source's position when they beat the current belief.</summary>
        private float Hear(TeamBlackboard bb, CueKind kind, int player, Vector3 source, float range, float time, string detail)
        {
            if (range <= 0f) return 0f;
            float best = 0f, bestDistance = 0f;
            int listener = -1;
            for (int a = 0; a < _n; a++)
            {
                if (_team[a] != bb.Team || !OnMap(_frames[a])) continue;
                float d = Vector3.Distance(Pos(a), source);
                if (d > range) continue;
                float c = 1f - (d / range) * (d / range);
                if (c > best) { best = c; bestDistance = d; listener = a; }
            }
            if (listener < 0) return 0f;

            float radius = Mathf.Lerp(Settings.soundBlurNear, Settings.soundBlurFar, bestDistance / range);
            AddCue(bb, kind, player, listener, source, radius, best, bestDistance, time, detail);
            return best;
        }

        private void AddCue(TeamBlackboard bb, CueKind kind, int player, int listener, Vector3 source, float radius, float certainty,
                            float distance, float time, string detail)
        {
            var blurred = source + Jitter(time, player, kind) * (radius * 0.6f);
            bb.AddSound(new SoundCue
            {
                Time = time, Kind = kind, Player = player, Listener = listener, Position = blurred, Radius = radius,
                Certainty = certainty, Distance = distance, Detail = detail ?? "",
            });
            var ei = bb.Enemies[player];
            if (ei == null) return;
            Offer(ei, blurred, time, radius, certainty * 0.9f, kind == CueKind.Damage ? IntelSource.HitBy : IntelSource.Heard, kind);
            WeighAreas(ei, blurred, radius, certainty * 0.9f);
        }

        /// <summary>Deterministic offset in the unit disc: where in its blur a sound is believed to be.</summary>
        private static Vector3 Jitter(float time, int player, CueKind kind)
        {
            unchecked
            {
                uint h = (uint)Mathf.RoundToInt(time * 1000f) * 73856093u ^ (uint)player * 19349663u ^ (uint)kind * 83492791u;
                h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
                float angle = (h & 0xFFFF) / 65535f * Mathf.PI * 2f;
                float mag = Mathf.Sqrt(((h >> 16) & 0xFFFF) / 65535f);
                return new Vector3(Mathf.Cos(angle) * mag, 0f, Mathf.Sin(angle) * mag);
            }
        }

        /// <summary>Footsteps (running, not walking or crouching), landings and reloads, from movement.</summary>
        private void FrameSounds(float t, float tp)
        {
            float dt = t - tp;
            if (dt <= 1e-4f) return;
            for (int p = 0; p < _n; p++)
            {
                var f = _frames[p];
                var pf = _prev[p];
                if (!OnMap(f) || !OnMap(pf) || (f.Flags & FrameFlags.Snapped) != 0) continue;
                var bb = EnemiesOf(p);
                var pos = Pos(p);

                if (!f.Airborne && !f.Walking && !f.Crouching && t - _lastStep[p] >= Settings.footstepInterval)
                {
                    var move = pos - Replay.UnityPosition(pf);
                    move.y = 0f;
                    if (move.magnitude / dt >= Settings.footstepSpeed)
                    {
                        _lastStep[p] = t;
                        Hear(bb, CueKind.Footstep, p, pos, Settings.footstepRange, t, "");
                    }
                }
                if (pf.Airborne && !f.Airborne) Hear(bb, CueKind.Landing, p, pos, Settings.landingRange, t, "");

                if (f.Weapon == pf.Weapon && f.Ammo != 255 && pf.Ammo != 255 && f.Ammo > pf.Ammo)
                {
                    var gun = Replay.WeaponOf(f);
                    if (Hear(bb, CueKind.Reload, p, pos, Settings.reloadRange, t, gun) > 0f && IsGun(gun))
                        KnowGun(bb.Enemies[p], gun, IntelSource.Heard, t);
                }
            }
        }

        /// <summary>Every gun sounds different, so a heard shot tells which gun it was.</summary>
        private void ShotSounds(float t)
        {
            for (; _shot < _shots.Count && _shots[_shot].Time <= t; _shot++)
            {
                var s = _shots[_shot];
                if (s.Player >= _n || Hidden(s.Player, s.Time)) continue;
                OnShot(s.Player, s.Time, s.Weapon, Replay.UnityPoint(s.X, s.Y, s.Z) + Vector3.down * 0.6f);   // shots fire from the eye
            }
        }

        private void OnShot(int player, float time, string weapon, Vector3 body)
        {
            var bb = EnemiesOf(player);
            bool gun = IsGun(weapon);
            float range = !gun ? Settings.abilityRange : Suppressed(weapon) ? Settings.suppressedGunshotRange : Settings.gunshotRange;
            float c = Hear(bb, CueKind.Gunshot, player, body, range, time, weapon);
            if (c <= 0f) return;
            if (gun) KnowGun(bb.Enemies[player], weapon, IntelSource.Heard, time);
            else NoteCast(bb, player, _slots.OfClass(player, weapon), time, IntelSource.Heard, c);
        }

        private void CastSounds(float t)
        {
            for (; _cast < _casts.Count && _casts[_cast].Time <= t; _cast++)
            {
                var c = _casts[_cast];
                if (c.Player >= _n || Hidden(c.Player, c.Time)) continue;
                var bb = EnemiesOf(c.Player);
                if (bb.Enemies[c.Player].Visible) NoteCastIndex(bb, _cast, IntelSource.Seen, 1f);
                float h = Hear(bb, CueKind.Ability, c.Player, Replay.UnityPoint(c.X, c.Y, c.Z), Settings.abilityRange, c.Time, c.Slot);
                if (h > 0f) NoteCastIndex(bb, _cast, IntelSource.Heard, h);
            }
        }

        private void SpikeSounds(float t, float tp)
        {
            var rd = Replay.Rounds[Round];
            if (rd.PlantTime > tp && rd.PlantTime <= t && rd.Planter < _n && !IsLive(rd.PlantTime))   // live plants are reported
                Hear(EnemiesOf(rd.Planter), CueKind.Plant, rd.Planter, Replay.UnityPoint(rd.PlantX, rd.PlantY, rd.PlantZ) + Vector3.up,
                     Settings.spikeRange, rd.PlantTime, "");
            for (; _defuse < _defuses.Count && _defuses[_defuse].StartTime <= t; _defuse++)
            {
                var d = _defuses[_defuse];
                if (d.Player < _n && OnMap(_frames[d.Player]) && !Hidden(d.Player, d.StartTime))
                    Hear(EnemiesOf(d.Player), CueKind.Defuse, d.Player, Pos(d.Player), Settings.spikeRange, d.StartTime, "");
            }
        }

        // ------------------------------------------------------------------------------------------ feed and hits
        /// <summary>Public: every death, and the weapon or ability that made the kill.</summary>
        private void KillFeed(float t)
        {
            for (; _kill < _kills.Count && _kills[_kill].Time <= t; _kill++)
            {
                var k = _kills[_kill];
                if (IsLive(k.Time)) continue;           // live kills (recorded ones the sim replays too) are reported
                OnKill(k.Killer, k.Victim, k.Time, k.Weapon);
            }
        }

        private void OnKill(int killer, int victim, float time, string weapon)
        {
            if (victim >= 0 && victim < _n)
            {
                var ei = EnemiesOf(victim).Enemies[victim];
                ei.Dead = true;
                ei.DeathTime = time;
                ei.Visible = false;
                if (ei.Areas != null) Array.Clear(ei.Areas, 0, ei.Areas.Length);
            }
            if (killer < 0 || killer >= _n || victim < 0 || victim >= _n || _team[killer] == _team[victim]) return;
            var bb = EnemiesOf(killer);
            if (IsGun(weapon)) KnowGun(bb.Enemies[killer], weapon, IntelSource.KillFeed, time);
            else if (!string.IsNullOrEmpty(weapon) && weapon != "Ability_Melee_Base")
                NoteCast(bb, killer, _slots.OfClass(killer, weapon), time, IntelSource.KillFeed, 1f);
        }

        /// <summary>Damage to an ally: the gun (or ability) that hit them, and a rough direction to the shooter.</summary>
        private void DamageTaken(float t)
        {
            for (; _dmg < _damage.Count && _damage[_dmg].Time <= t; _dmg++)
            {
                var d = _damage[_dmg];
                if (Hidden(d.Attacker, d.Time) || Hidden(d.Victim, d.Time)) continue;
                OnDamage(d.Attacker, d.Victim, d.Time, d.Weapon);
            }
        }

        private void OnDamage(int attacker, int victim, float time, string weapon)
        {
            if (attacker < 0 || attacker >= _n || victim < 0 || victim >= _n || _team[attacker] == _team[victim]) return;
            var bb = EnemiesOf(attacker);
            if (IsGun(weapon))
            {
                KnowGun(bb.Enemies[attacker], weapon, IntelSource.HitBy, time);
                int team = bb.Team;
                if (time - _lastDamageCue[team, attacker] < 0.5f || !OnMap(_frames[attacker])) return;
                _lastDamageCue[team, attacker] = time;
                var from = Pos(attacker);
                float dist = Vector3.Distance(Pos(victim), from);
                AddCue(bb, CueKind.Damage, attacker, victim, from, Mathf.Max(3f, dist * 0.3f), 0.6f, dist, time, weapon);
            }
            else if (!string.IsNullOrEmpty(weapon) && weapon != "Ability_Melee_Base")
                NoteCast(bb, attacker, _slots.OfClass(attacker, weapon), time, IntelSource.HitBy, 1f);
        }

        /// <summary>Debuffs (flash, leer, suppress, slow, reveal, ...) put on an ally by an enemy.</summary>
        private void EffectsTaken(float t)
        {
            for (; _effect < _effects.Count && _effects[_effect].Time <= t; _effect++)
            {
                var s = _effects[_effect];
                if (s.Player >= _n || s.SourcePlayer >= _n || _team[s.Player] == _team[s.SourcePlayer]) continue;
                if (Hidden(s.SourcePlayer, s.Time) || Hidden(s.Player, s.Time)) continue;
                NoteCast(EnemiesOf(s.SourcePlayer), s.SourcePlayer, _slots.OfEffect(s), s.Time, IntelSource.HitBy, 1f);
            }
        }

        // ------------------------------------------------------------------------------------------ economy
        /// <summary>Kit sizes (most charges of each ability seen) and ult costs (points just before an ult was used):
        /// agent knowledge, the same for every round.</summary>
        private void LearnKits()
        {
            var kits = new int[_n, 3];
            foreach (var c in Replay.AbilityCharges)
            {
                int slot = Array.IndexOf(KitSlots, c.Slot);
                if (c.Player >= _n || slot < 0) continue;
                kits[c.Player, slot] = Math.Max(kits[c.Player, slot], c.Charges - c.TemporaryCharges);
            }
            // ult points only go down when the ult is used (they carry over deaths and halftime): the value before a
            // drop is the cost
            var ultCost = new int[_n];
            for (int p = 0; p < _n; p++)
                for (int i = 1; i < _stats[p].Count; i++)
                    if (_stats[p][i].UltPoints < _stats[p][i - 1].UltPoints)
                        ultCost[p] = Math.Max(ultCost[p], _stats[p][i - 1].UltPoints);
            foreach (var bb in Teams)
                foreach (var ei in bb.EachEnemy())
                {
                    for (int k = 0; k < 3; k++) ei.Kit[k] = kits[ei.Player, k];
                    ei.UltCost = ultCost[ei.Player];
                }
        }

        private PlayerStatSample? StatAt(int player, float time)
        {
            var list = _stats[player];
            int lo = 0, hi = list.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (list[mid].Time <= time) { found = mid; lo = mid + 1; } else hi = mid - 1;
            }
            return found >= 0 ? list[found] : (PlayerStatSample?)null;
        }

        /// <summary>Credits at the start of the round (public), as recorded for the buy phase.</summary>
        private int RoundStartCredits(int round, int player)
        {
            var l = Replay.LoadoutOf(round, player);
            if (l != null && l.RoundStartCredits >= 0) return l.RoundStartCredits;     // only this public field of the loadout
            var rd = Replay.Rounds[round];
            return StatAt(player, Mathf.Max(rd.StartTime, rd.BarrierTime))?.RoundStartCredits ?? -1;
        }

        private bool SidesSwitched(int round) =>
            round == 0 || Replay.RoleOf(0, round) != Replay.RoleOf(0, round - 1);

        /// <summary>Round start: everyone's credits and predicted buy, and what survivors of last round still carry.</summary>
        private void StartEconomy(int round)
        {
            bool switched = SidesSwitched(round);
            foreach (var bb in Teams)
            {
                int sum = 0, count = 0;
                bool pistol = false;
                foreach (var ei in bb.EachEnemy())
                {
                    int credits = RoundStartCredits(round, ei.Player);
                    bool isPistol = switched && credits >= 0 && credits <= 1000;
                    pistol |= isPistol;
                    ei.RoundStartCredits = credits;
                    ei.Buy = Economy.Classify(credits, isPistol);
                    if (credits >= 0) { sum += credits; count++; }
                    if (!switched) CarryOver(bb, ei, round - 1);
                }
                bb.EnemyAverageCredits = count > 0 ? sum / count : -1;
                bb.EnemyBuy = Economy.Classify(bb.EnemyAverageCredits, pistol);
            }
        }

        /// <summary>Survivors keep their guns: what the team knew of them at the end of last round. Without that
        /// (jumped straight here), the kill feed still says who survived and which guns they killed with.</summary>
        private void CarryOver(TeamBlackboard bb, EnemyIntel ei, int previous)
        {
            if (previous < 0) return;
            if (_carry.TryGetValue(CarryKey(previous, bb.Team, ei.Player), out var c))
            {
                ei.Survived = c.Alive;
                if (c.Alive)
                    foreach (var g in c.Guns) KnowGun(ei, g, IntelSource.LastRound, Replay.Rounds[previous + 1].StartTime);
                return;
            }
            var rd = Replay.Rounds[previous];
            float end = Replay.Rounds[previous + 1].StartTime;
            bool died = false;
            string gun = null;
            foreach (var k in _kills)
            {
                if (k.Time < rd.StartTime || k.Time >= end) continue;
                if (k.Victim == ei.Player) died = true;
                if (k.Killer == ei.Player && IsGun(k.Weapon)) gun = k.Weapon;
            }
            ei.Survived = !died;
            if (!died && gun != null) KnowGun(ei, gun, IntelSource.LastRound, end);
        }

        private static int CarryKey(int round, int team, int player) => round * 1000 + team * 100 + player;

        private void FinishRound()
        {
            float end = Replay.Rounds[Round + 1].StartTime - 1e-3f;
            float rate = Mathf.Max(1f, Settings.sensorRate);
            while (_tick / rate <= end)
            {
                Step(_tick / rate);
                _tick++;
            }
            foreach (var bb in Teams)
                foreach (var ei in bb.EachEnemy())
                    _carry[CarryKey(Round, bb.Team, ei.Player)] = (!ei.Dead, ei.Guns.ToArray());
        }

        // ------------------------------------------------------------------------------------------ area beliefs
        /// <summary>Round start: every enemy is in their side's spawn (both teams are walled in during the buy phase).</summary>
        private void StartAreas(int round)
        {
            foreach (var bb in Teams)
                foreach (var ei in bb.EachEnemy())
                {
                    int spawn = Areas.SpawnOf(Replay.RoleOf(_team[ei.Player], round));
                    if (spawn >= 0) ei.Areas[spawn] = 1f;
                    else Uniform(ei.Areas);
                }
            foreach (var bb in Teams) SumAreas(bb);
        }

        private void Uniform(float[] p)
        {
            int count = 0;
            foreach (var a in Areas.Areas) if (a.CellCount > 0) count++;
            for (int i = 0; i < p.Length; i++) p[i] = Areas.Areas[i].CellCount > 0 && count > 0 ? 1f / count : 0f;
        }

        /// <summary>Unseen enemies drift into connected areas, at a rate set by how long the walk takes.</summary>
        private void SpreadAreas(TeamBlackboard bb, float dt)
        {
            if (dt <= 0f) return;
            float mobility = Mathf.Clamp01(Settings.areaMobility);
            foreach (var ei in bb.EachEnemy())
            {
                if (ei.Dead || ei.Visible) continue;
                var p = ei.Areas;
                var q = _scratch.Length == p.Length ? _scratch : (_scratch = new float[p.Length]);
                Array.Copy(p, q, p.Length);
                for (int a = 0; a < p.Length; a++)
                {
                    if (p[a] <= 0f) continue;
                    var area = Areas.Areas[a];
                    float rate = 0f;
                    for (int k = 0; k < area.Neighbors.Count; k++) rate += 1f / area.TravelTimes[k];
                    if (rate <= 0f) continue;
                    float outflow = p[a] * Mathf.Min(0.9f, mobility * rate * dt);
                    q[a] -= outflow;
                    for (int k = 0; k < area.Neighbors.Count; k++)
                        q[area.Neighbors[k]] += outflow * (1f / area.TravelTimes[k]) / rate;
                }
                Array.Copy(q, p, p.Length);
            }
        }

        private float[] _scratch = new float[0];

        /// <summary>Probes of every area in this ally's view and sight (cached with the rest of the tick's sight).</summary>
        private void WatchAreas(Vision v, int team, Vector3 eye, Vector3 forward, float range, float halfFov)
        {
            var seen = _probeSeen[team];
            var watched = v.Watched[team];
            int id = 0;
            foreach (var area in Areas.Areas)
            {
                int hits = 0;
                for (int k = 0; k < area.Probes.Length; k++, id++)
                {
                    if (seen.Contains(id)) { hits++; continue; }
                    var probe = area.Probes[k];
                    if (!InView(eye, forward, probe, range, halfFov) || !Clear(eye, probe)) continue;
                    seen.Add(id);
                    hits++;
                }
                if (area.Probes.Length > 0) watched[area.Index] = Mathf.Max(watched[area.Index], (float)hits / area.Probes.Length);
            }
        }

        /// <summary>Allies looking at an area without seeing an enemy there: that enemy is probably somewhere else.</summary>
        private void ClearWatchedAreas(TeamBlackboard bb, Vision v, float dt)
        {
            if (dt <= 0f) return;
            var watched = v.Watched[bb.Team];
            foreach (var ei in bb.EachEnemy())
            {
                if (ei.Dead || ei.Visible) continue;
                for (int a = 0; a < watched.Length; a++)
                    if (watched[a] > 0f) ei.Areas[a] *= Mathf.Exp(-Settings.areaClearRate * watched[a] * dt);
                Normalize(ei.Areas);
            }
        }

        /// <summary>Seen or revealed: certainly in that area.</summary>
        private void PinArea(EnemyIntel ei, Vector3 pos)
        {
            if (ei.Areas == null) return;
            Array.Clear(ei.Areas, 0, ei.Areas.Length);
            ei.Areas[Areas.AreaAt(pos)] = 1f;
        }

        /// <summary>A sound (or hit) from around <paramref name="pos"/>: areas near it become likelier, by certainty.</summary>
        private void WeighAreas(EnemyIntel ei, Vector3 pos, float radius, float certainty)
        {
            if (ei.Areas == null || ei.Dead || ei.Visible) return;
            float sigma = radius + 1.5f;
            for (int a = 0; a < ei.Areas.Length; a++)
            {
                var area = Areas.Areas[a];
                float d = (area.Center - pos).magnitude;
                foreach (var probe in area.Probes) d = Mathf.Min(d, (probe - pos).magnitude);
                float near = Mathf.Exp(-0.5f * (d / sigma) * (d / sigma));
                ei.Areas[a] *= (1f - certainty) + certainty * near;
            }
            Normalize(ei.Areas);
        }

        private void Normalize(float[] p)
        {
            float sum = 0f;
            foreach (var x in p) sum += x;
            if (sum < 1e-6f) { Uniform(p); return; }    // evidence ruled out everything: start over
            for (int i = 0; i < p.Length; i++) p[i] /= sum;
        }

        private void SumAreas(TeamBlackboard bb)
        {
            Array.Clear(bb.Expected, 0, bb.Expected.Length);
            foreach (var ei in bb.EachEnemy())
                if (!ei.Dead)
                    for (int a = 0; a < ei.Areas.Length; a++) bb.Expected[a] += ei.Areas[a];
        }

        // ------------------------------------------------------------------------------------------ blackboard writes
        private static void SetPosition(EnemyIntel ei, Vector3 pos, float t, float radius, float certainty, IntelSource source, CueKind cue)
        {
            ei.HasPosition = true;
            ei.Position = pos;
            ei.PositionTime = t;
            ei.PositionRadius = radius;
            ei.PositionCertainty = certainty;
            ei.PositionSource = source;
            ei.PositionCue = cue;
        }

        /// <summary>Takes a weaker observation only when it beats what the team still believes (fresh sound vs old sighting).</summary>
        private void Offer(EnemyIntel ei, Vector3 pos, float t, float radius, float certainty, IntelSource source, CueKind cue)
        {
            if (ei.Dead || ei.Visible) return;
            if (certainty >= ei.CertaintyAt(t, Settings)) SetPosition(ei, pos, t, radius, certainty, source, cue);
        }

        private static void KnowGun(EnemyIntel ei, string gun, IntelSource how, float t)
        {
            if (ei == null) return;
            ei.Gun = gun;
            ei.GunTime = t;
            ei.GunSource = how;
            ei.Guns.Remove(gun);
            ei.Guns.Add(gun);
        }

        private void NoteCastIndex(TeamBlackboard bb, int castIndex, IntelSource how, float certainty)
        {
            var c = _casts[castIndex];
            Record(bb.Enemies[c.Player], c.Player, c.Slot, castIndex, c.Time, how, certainty);
        }

        /// <summary>An ability use learned from its effect: tied to the caster's latest recorded cast of that slot
        /// (or of any slot when the slot is unknown).</summary>
        private void NoteCast(TeamBlackboard bb, int player, string slot, float time, IntelSource how, float certainty,
                              float window = 40f, bool requireRecorded = false)
        {
            var ei = bb.Enemies[player];
            if (ei == null) return;
            int index = FindCast(player, slot ?? "", time, window);
            if (index >= 0)
            {
                Record(ei, player, _casts[index].Slot, index, _casts[index].Time, how, certainty);
                return;
            }
            if (!requireRecorded) Record(ei, player, slot ?? "", -1, time, how, certainty);
        }

        /// <summary>The player's latest recorded cast of a slot (any slot when "") at most <paramref name="window"/>
        /// seconds before <paramref name="time"/>, within this round; -1 if none.</summary>
        private int FindCast(int player, string slot, float time, float window)
        {
            float roundStart = Replay.Rounds[Round].StartTime;
            var list = _castsByPlayer[player];
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var c = _casts[list[i]];
                if (c.Time > time + 0.05f) continue;
                if (c.Time < time - window || c.Time < roundStart) break;
                if (slot.Length == 0 || c.Slot == slot) return list[i];
            }
            return -1;
        }

        private static void Record(EnemyIntel ei, int player, string slot, int castIndex, float time, IntelSource how, float certainty)
        {
            foreach (var k in ei.Casts)
            {
                bool same = castIndex >= 0 ? k.CastIndex == castIndex : k.CastIndex < 0 && k.Slot == slot && Mathf.Abs(k.Time - time) < 15f;
                if (!same) continue;
                if (certainty > k.Certainty) { k.Certainty = certainty; k.How = how; }
                return;
            }
            ei.Casts.Add(new KnownCast { Time = time, Player = player, Slot = slot, CastIndex = castIndex, How = how, Certainty = certainty });
        }
    }
}
