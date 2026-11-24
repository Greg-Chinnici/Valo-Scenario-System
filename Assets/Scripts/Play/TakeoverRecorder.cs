using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using ValorantReplay.Playback;
using ValorantReplay.Sim;
using ValoReplay.Knowledge;
using Frame = ValorantReplay.Playback.Frame;

namespace ValoReplay.Play
{
    /// <summary>
    /// Records a takeover as a branch of the replay: a new .vrp that is the recording up to the takeover, then the round
    /// as you and the round AI played it. Everyone still following the recording keeps their recorded frames, shots and
    /// utility until they leave it (<see cref="RoundAi.RecordingHides"/>); from then on their poses, shots, hits and
    /// deaths come from the simulation, yours from the first-person controller. Plants, defuses, the spike and the round
    /// result come from the simulation too. The branch ends where you are (the round result, or the moment you paused)
    /// and has no later rounds. Records into memory while you play; <see cref="SaveAsync"/> writes the file.
    /// Branches are saved under <see cref="BranchFolder"/>, not in the replay folders, and carry
    /// <see cref="BranchMarker"/> in their match id, so the AI never learns a map from its own play.
    /// </summary>
    public sealed class TakeoverRecorder : MonoBehaviour
    {
        /// <summary>In the match id of every branch: "matchid~branch-20261124-153012".</summary>
        public const string BranchMarker = "~branch-";

        public static bool IsBranch(string matchId) => matchId != null && matchId.Contains(BranchMarker);

        public static string BranchFolder => Path.Combine(Application.persistentDataPath, "Branches");

        /// <summary>The file this takeover was last saved to (each save overwrites it with the longer branch); null until saved.</summary>
        public string SavedPath { get; private set; }
        public bool Saving { get; private set; }
        /// <summary>Something happened since the takeover (there is a branch to save).</summary>
        public bool HasData => _round != null && _round.Now > _round.TakeoverTime + 0.05f;

        private RoundPlayController _round;
        private RoundAi _ai;
        private VrpReplay _source;
        private int _you;
        private string _stamp;

        // per player: driven poses as (replay time, frame), in time order
        private List<(float Time, Frame Frame)>[] _poses;
        private readonly List<Shot> _shots = new List<Shot>();
        private readonly List<DamageEvent> _damage = new List<DamageEvent>();
        private readonly List<RoundStateSample> _states = new List<RoundStateSample>();
        private readonly List<SpikeCarrierSample> _carriers = new List<SpikeCarrierSample>();
        private readonly List<DefuseAttempt> _defuses = new List<DefuseAttempt>();
        private DefuseAttempt[] _defusing;
        private float[] _pool;               // health + armor at the last sample, for the AI's hits
        private float[] _yourDamage;         // your damage to each player since the last sample
        private int _eventsSeen;
        private float _lastSimTime = -1f;

        public void Init(RoundPlayController round, RoundAi ai)
        {
            _round = round;
            _ai = ai;
            _source = round.Replay;
            _you = round.Player;
            _stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            int n = _source.Players.Count;
            _poses = new List<(float, Frame)>[n];
            for (int i = 0; i < n; i++) _poses[i] = new List<(float, Frame)>();
            _defusing = new DefuseAttempt[n];
            _pool = new float[n];
            _yourDamage = new float[n];
            round.Weapons.Fired += OnYouFired;
            round.PlayerHit += OnYouHit;
            ai.Shot += OnAiShot;
        }

        private void OnDestroy()
        {
            if (_round != null)
            {
                if (_round.Weapons != null) _round.Weapons.Fired -= OnYouFired;
                _round.PlayerHit -= OnYouHit;
            }
            if (_ai != null) _ai.Shot -= OnAiShot;
        }

        // ------------------------------------------------------------------ recording

        /// <summary>After the controller stepped the AI this frame.</summary>
        private void LateUpdate()
        {
            if (_ai == null || _round.Fps == null) return;
            var live = (ILiveWorld)_ai;
            float now = _round.Now;

            // you, at the frame's time
            var yours = _poses[_you];
            if (now >= _round.TakeoverTime && (yours.Count == 0 || now > yours[yours.Count - 1].Time) && live.TryFrame(_you, now, out var f))
            {
                float pitch = _round.Fps.Camera != null ? Mathf.DeltaAngle(0f, _round.Fps.Camera.transform.localEulerAngles.x) : 0f;
                f.PitchCenti = (short)Mathf.Clamp(Mathf.RoundToInt(pitch * 100f), -9000, 9000);
                var gun = _round.Weapons.Equipped;
                if (gun != null && !gun.Stats.Melee) f.ReserveAmmo = (byte)Mathf.Clamp(gun.Reserve, 0, 253);
                yours.Add((now, f));
            }
            if (!_ai.Started) return;

            // the AI's players, at the sim's time (it steps at its own rate)
            var sim = _ai.Sim;
            float simNow = _ai.SourceStart + sim.Time;
            if (simNow <= _lastSimTime) return;
            if (_lastSimTime < 0f)
                for (int p = 0; p < sim.Agents.Count; p++) _pool[p] = sim.Agents[p].Alive ? sim.Agents[p].Health + sim.Agents[p].Armor : 0f;
            _lastSimTime = simNow;
            for (int p = 0; p < sim.Agents.Count; p++)
                if (p != _you && live.TryFrame(p, simNow, out var af)) _poses[p].Add((simNow, af));

            SampleDamage(sim, simNow);
            SampleSpike(sim, simNow);
            SampleDefuses(sim, simNow);
        }

        /// <summary>The sim's Hit events say who hit whom, not for how much: the victim's health + armor since the last
        /// sample, less your own damage (recorded with its region from <see cref="OnYouHit"/>), shared among its hits.</summary>
        private void SampleDamage(RoundSim sim, float simNow)
        {
            var hits = new List<SimEvent>();
            for (; _eventsSeen < sim.Events.Count; _eventsSeen++)
            {
                var e = sim.Events[_eventsSeen];
                if (e.Type == SimEventType.Hit && e.Other >= 0 && e.Other != _you) hits.Add(e);
            }
            for (int p = 0; p < sim.Agents.Count; p++)
            {
                var a = sim.Agents[p];
                float pool = a.Alive ? a.Health + a.Armor : 0f;
                int count = 0;
                foreach (var h in hits) if (h.Agent == p) count++;
                if (count > 0)
                {
                    float each = Mathf.Max(0f, _pool[p] - pool - _yourDamage[p]) / count;
                    foreach (var h in hits)
                        if (h.Agent == p)
                            _damage.Add(new DamageEvent
                            {
                                Time = _ai.SourceStart + h.Time, Attacker = (byte)h.Other, Victim = (byte)p, Damage = each,
                                Weapon = _ai.GunOf(h.Other), Region = HitRegion.Body, Killed = !a.Alive,
                            });
                }
                _pool[p] = pool;
                _yourDamage[p] = 0f;
            }
        }

        private void SampleSpike(RoundSim sim, float simNow)
        {
            byte holder = sim.SpikeState == SpikeStatus.Carried && sim.SpikeCarrier >= 0 ? (byte)sim.SpikeCarrier : VrpReplay.HolderNone;
            if (_carriers.Count == 0 || _carriers[_carriers.Count - 1].Holder != holder)
                _carriers.Add(new SpikeCarrierSample { Time = simNow, Holder = holder });

            var spike = ToReplay(sim.SpikeState);
            var phase = sim.Over ? RoundPhase.RoundEnd : RoundPhase.Combat;
            var last = _states.Count > 0 ? _states[_states.Count - 1] : default;
            if (_states.Count == 0 || last.Spike != spike || last.Phase != phase)
                _states.Add(new RoundStateSample
                {
                    Time = simNow, Phase = phase, Spike = spike,
                    Remaining = sim.SpikeState == SpikeStatus.Planted ? sim.SpikeTimer : sim.Remaining,
                });
        }

        /// <summary>Defuses by players off the recording (theirs on it come from the recording).</summary>
        private void SampleDefuses(RoundSim sim, float simNow)
        {
            for (int p = 0; p < sim.Agents.Count; p++)
            {
                var a = sim.Agents[p];
                bool defusing = a.Alive && a.Mode != AgentMode.Replay && a.Action == AgentAction.Defuse;
                float progress = Mathf.Clamp01(a.TaskSeconds / Mathf.Max(0.01f, sim.Options.DefuseDuration));
                var d = _defusing[p];
                if (defusing && d == null)
                {
                    d = _defusing[p] = new DefuseAttempt { Player = (byte)p, StartTime = simNow - a.TaskSeconds, EndTime = simNow, ProgressStart = 0f };
                    _defuses.Add(d);
                }
                if (d == null) continue;
                d.EndTime = simNow;
                if (defusing) d.ProgressEnd = progress;
                else
                {
                    d.Defused = sim.SpikeState == SpikeStatus.Defused;
                    if (d.Defused) d.ProgressEnd = 1f;
                    _defusing[p] = null;
                }
            }
        }

        private void OnYouFired()
        {
            if (!_ai.Started || _round.Fps == null || _round.Fps.Camera == null) return;
            var gun = _round.Weapons.Equipped;
            if (gun == null || gun.Stats.Melee) return;
            var cam = _round.Fps.Camera.transform;
            _shots.Add(MakeShot(_you, _round.Now, cam.position, cam.forward, gun.Stats.Class, gun.Magazine));
        }

        private void OnYouHit(int victim, Region region, float damage, bool killed)
        {
            if (!_ai.Started || victim < 0 || victim >= _yourDamage.Length) return;
            _yourDamage[victim] += damage;
            _damage.Add(new DamageEvent
            {
                Time = _round.Now, Attacker = (byte)_you, Victim = (byte)victim, Damage = damage,
                Weapon = _round.Weapons.Equipped?.Stats.Class ?? "", Killed = killed,
                Region = region == Region.Head ? HitRegion.Head : region == Region.Legs ? HitRegion.Legs : HitRegion.Body,
            });
        }

        private void OnAiShot(int player, float time, Vector3 from, Vector3 to) =>
            _shots.Add(MakeShot(player, time, from, to - from, _ai.GunOf(player), -1));

        private Shot MakeShot(int player, float time, Vector3 origin, Vector3 direction, string weapon, int ammo)
        {
            var d = direction.sqrMagnitude > 1e-6f ? direction.normalized : Vector3.forward;
            var o = ToReplay(origin);
            var dir = ToReplayDirection(d);
            var s = new Shot
            {
                Time = time, Player = (byte)player, Weapon = weapon ?? "", X = o.x, Y = o.y, Z = o.z,
                Yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, Pitch = -Mathf.Asin(Mathf.Clamp(d.y, -1f, 1f)) * Mathf.Rad2Deg,
                AmmoRemaining = (short)ammo,
            };
            s.Directions.Add((dir.x, dir.y, dir.z));
            return s;
        }

        // ------------------------------------------------------------------ saving

        /// <summary>
        /// Writes the branch so far to <see cref="BranchFolder"/> (the first save picks the file name, later saves of this
        /// takeover overwrite it). The branch is put together on a worker thread from what was captured here.
        /// </summary>
        public async Task<string> SaveAsync()
        {
            if (Saving) throw new InvalidOperationException("already saving");
            if (!HasData) throw new InvalidOperationException("nothing to save yet");
            Saving = true;
            try
            {
                var capture = Capture();
                string path = SavedPath ?? Path.Combine(BranchFolder, FileName());
                await Task.Run(() =>
                {
                    var branch = Build(capture);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    string tmp = path + ".tmp";
                    branch.Save(tmp);
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                });
                SavedPath = path;
                return path;
            }
            finally { Saving = false; }
        }

        /// <summary>"Haven-0a1b2c3d~branch-Raze-r7-20261124-153012.vrp" (map first, as the library reads it).</summary>
        private string FileName()
        {
            string map = _source.MapName.Replace(' ', '_');
            string match = string.IsNullOrEmpty(_source.MatchId) ? "match" : _source.MatchId.Split('~')[0];
            if (match.Length > 8) match = match.Substring(0, 8);
            int ri = _source.RoundAt(_round.TakeoverTime);
            string agent = _source.Players[_you].AgentName ?? $"p{_you}";
            string name = $"{map}-{match}{BranchMarker}{agent}-r{(ri >= 0 ? _source.Rounds[ri].Number + 1 : 0)}-{_stamp}.vrp";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        /// <summary>Everything the branch needs, copied on the main thread (the sim and the lists keep changing).</summary>
        private sealed class Snapshot
        {
            public VrpReplay Source;
            public int You, Round;
            public float Takeover, SimStart, End, RecordedEnd;
            public float[] HiddenFrom;
            public List<(float Time, Frame Frame)>[] Poses;
            public List<Shot> Shots;
            public List<DamageEvent> Damage;
            public List<RoundStateSample> States;
            public List<SpikeCarrierSample> Carriers;
            public List<DefuseAttempt> Defuses;
            public List<RoundAi.AiKill> Kills;
            public List<SimEvent> Events;
            public bool Over;
            public int Winner;
            public RoundEndReason EndReason;
            public Vector3 SpikePosition;
            public string PlantSite;
            public string MatchId;
        }

        private Snapshot Capture()
        {
            var sim = _ai.Sim;
            int n = _source.Players.Count;
            int ri = _source.RoundAt(_round.TakeoverTime);
            var s = new Snapshot
            {
                Source = _source, You = _you, Round = ri, Takeover = _round.TakeoverTime, SimStart = _ai.SourceStart,
                RecordedEnd = ri >= 0 ? _source.Rounds[ri].EndTime : float.PositiveInfinity,
                HiddenFrom = new float[n], Poses = new List<(float, Frame)>[n],
                Shots = new List<Shot>(_shots), Damage = new List<DamageEvent>(_damage), States = new List<RoundStateSample>(_states),
                Carriers = new List<SpikeCarrierSample>(_carriers), Kills = new List<RoundAi.AiKill>(_ai.Kills),
                Events = new List<SimEvent>(sim.Events), Over = sim.Over, Winner = sim.Winner, EndReason = sim.EndReason,
                SpikePosition = new Vector3(sim.SpikePosition.X, sim.SpikePosition.Y - NavGrid.ActorHeight, sim.SpikePosition.Z),
                MatchId = (string.IsNullOrEmpty(_source.MatchId) ? "match" : _source.MatchId.Split('~')[0]) + BranchMarker + _stamp,
            };
            s.Defuses = new List<DefuseAttempt>();
            foreach (var d in _defuses)
                s.Defuses.Add(new DefuseAttempt
                {
                    Player = d.Player, StartTime = d.StartTime, EndTime = d.EndTime, ProgressStart = d.ProgressStart,
                    ProgressEnd = d.ProgressEnd, Defused = d.Defused,
                });
            foreach (var e in sim.Events)
                if (e.Type == SimEventType.Planted)
                {
                    var p = sim.SpikePosition;
                    s.PlantSite = sim.Map.NearestSite(p)?.Name ?? "";
                    break;
                }
            // the branch ends where play is: the round result, or now
            s.End = Mathf.Max(s.Takeover, sim.Over ? Mathf.Min(_round.Now, s.SimStart + sim.Time) : _round.Now);
            for (int p = 0; p < n; p++)
            {
                s.Poses[p] = new List<(float, Frame)>(_poses[p]);
                if (p == _you) s.HiddenFrom[p] = s.Takeover;
                else
                {
                    float left = p < sim.Agents.Count ? sim.Agents[p].DivergedAt : -1f;
                    s.HiddenFrom[p] = left >= 0f ? s.SimStart + left : float.PositiveInfinity;
                }
            }
            return s;
        }

        /// <summary>The branch as a new replay: a deep copy of the source cut at the end, with this takeover spliced in.</summary>
        private static VrpReplay Build(Snapshot s)
        {
            VrpReplay b;
            using (var ms = new MemoryStream())
            {
                s.Source.Save(ms);
                ms.Position = 0;
                b = VrpReplay.Load(ms);
            }
            int n = b.Players.Count;
            float end = s.End, takeover = s.Takeover, simStart = s.SimStart;
            bool Hides(int p, float t) => t >= s.RecordedEnd || (p < n && t >= s.HiddenFrom[p]);
            // what the recording has a player do at t still happened in the branch
            bool Keeps(int p, float t) => t <= end && (t < takeover || (p < n && !Hides(p, t)));

            b.MatchId = s.MatchId;
            b.RecordedAtTicks = DateTime.UtcNow.Ticks;

            // frames: recorded up to the takeover and for everyone still on the recording, then the driven poses
            int ticks = Math.Max(1, (int)Math.Floor(end * b.TickRate) + 1);
            var frames = new Frame[ticks * n];
            for (int t = 0; t < ticks; t++)
            {
                float time = t / b.TickRate;
                for (int p = 0; p < n; p++)
                {
                    Frame f;
                    if (time < takeover || !Hides(p, time)) f = s.Source.GetFrame(Math.Min(t, s.Source.TickCount - 1), p);
                    else if (!PoseAt(s.Poses[p], time, out f)) f = t > 0 ? frames[(t - 1) * n + p] : s.Source.GetFrame(0, p);
                    frames[t * n + p] = f;
                }
            }
            b.Frames = frames;
            b.TickCount = ticks;
            b.Duration = end;

            // the round: cut at the end, with the branch's plant, defuse and result
            b.Rounds.RemoveRange(s.Round + 1, b.Rounds.Count - s.Round - 1);
            var rd = b.Rounds[s.Round];
            rd.EndTime = end;
            rd.WinningTeam = s.Over && s.Winner >= 0 ? (byte)s.Winner : (byte)255;
            rd.EndReason = s.Over ? s.EndReason : RoundEndReason.Unknown;
            if (rd.PlantTime >= simStart) { rd.PlantTime = -1f; rd.Site = ""; rd.Planter = 255; }
            if (rd.DefuseTime >= simStart) { rd.DefuseTime = -1f; rd.Defuser = 255; }
            var events = new List<MatchEvent>();
            foreach (var e in s.Events)
            {
                float t = simStart + e.Time;
                if (t > end) continue;
                switch (e.Type)
                {
                    case SimEventType.Planted:
                        rd.PlantTime = t;
                        rd.Planter = (byte)e.Agent;
                        // the recorded plant keeps its recorded spot; the AI's or yours is on the floor under the planter
                        var recorded = s.Source.Rounds[s.Round];
                        if (Mathf.Abs(recorded.PlantTime - t) < 0.2f && recorded.Planter == e.Agent)
                        {
                            rd.PlantX = recorded.PlantX; rd.PlantY = recorded.PlantY; rd.PlantZ = recorded.PlantZ; rd.Site = recorded.Site;
                        }
                        else
                        {
                            var at = ToReplay(b, s.SpikePosition);
                            rd.PlantX = at.x; rd.PlantY = at.y; rd.PlantZ = at.z; rd.Site = s.PlantSite ?? "";
                        }
                        break;
                    case SimEventType.Defused:
                        rd.DefuseTime = t;
                        rd.Defuser = (byte)e.Agent;
                        events.Add(new MatchEvent { Time = t, Type = MatchEventType.SpikeDefused });
                        break;
                    case SimEventType.Detonated:
                        events.Add(new MatchEvent { Time = t, Type = MatchEventType.SpikeDetonated });
                        break;
                }
            }
            for (int i = 0; i < b.Teams.Count; i++)
            {
                int wins = 0;
                foreach (var r in b.Rounds) if (r.WinningTeam == i) wins++;
                b.Teams[i].FinalWins = wins;
            }

            // kills: recorded until the sim starts, then every kill of the sim (it replays the recorded ones that still happen)
            b.Kills.RemoveAll(k => k.Time > simStart || k.Time > end);
            foreach (var k in s.Kills)
                if (k.Time <= end)
                    b.Kills.Add(new Kill
                    {
                        Time = k.Time, Killer = k.Killer >= 0 ? (byte)k.Killer : (byte)255, Victim = (byte)k.Victim,
                        Round = (short)s.Round, Weapon = k.Weapon ?? "", Headshot = k.Headshot,
                    });
            b.Kills.Sort((x, y) => x.Time.CompareTo(y.Time));

            // recorded damage between two players still on the recording; the sim's for the rest
            b.Damage.RemoveAll(d => d.Time > end || (d.Time >= takeover &&
                                    (Hides(d.Victim, d.Time) || (d.Attacker < n && Hides(d.Attacker, d.Time)) || d.Victim >= n)));
            foreach (var d in s.Damage)
                if (d.Time <= end && (Hides(d.Victim, d.Time) || Hides(d.Attacker, d.Time))) b.Damage.Add(d);
            b.Damage.Sort((x, y) => x.Time.CompareTo(y.Time));

            b.Shots.RemoveAll(x => !Keeps(x.Player, x.Time));
            foreach (var x in s.Shots) if (x.Time <= end) b.Shots.Add(x);
            b.Shots.Sort((x, y) => x.Time.CompareTo(y.Time));

            // utility: what was thrown while its owner was still on the recording, gone at the end of the branch
            var keepEntity = new int[b.Entities.Count];
            var entities = new List<Entity>();
            for (int i = 0; i < b.Entities.Count; i++)
            {
                var e = b.Entities[i];
                bool keep = Keeps(e.Owner, e.SpawnTime);
                keepEntity[i] = keep ? entities.Count : -1;
                if (!keep) continue;
                if (e.DespawnTime < 0f || e.DespawnTime > end) e.DespawnTime = end;
                entities.Add(e);
            }
            b.Entities = entities;
            b.StatusEffects.RemoveAll(x => !Keeps(x.Player, x.Time) || (x.SourcePlayer < n && !Keeps(x.SourcePlayer, x.Time)));
            foreach (var x in b.StatusEffects)
            {
                if (x.Source >= 0) x.Source = x.Source < keepEntity.Length ? keepEntity[x.Source] : -1;
                if (x.Duration > 0f && x.Time + x.Duration > end) x.Duration = end - x.Time;
            }
            b.AbilityCasts.RemoveAll(x => !Keeps(x.Player, x.Time));
            b.AbilityCharges.RemoveAll(x => !Keeps(x.Player, x.Time));
            b.PlayerStats.RemoveAll(x => !Keeps(x.Player, x.Time));
            b.Events.RemoveAll(x => x.Player < n ? !Keeps(x.Player, x.Time) : x.Time >= takeover);
            b.Events.AddRange(events);
            b.Events.Sort((x, y) => x.Time.CompareTo(y.Time));

            // the spike and the round clock: recorded until the sim starts, then the sim's
            b.RoundStates.RemoveAll(x => x.Time >= simStart || x.Time > end);
            b.RoundStates.AddRange(s.States.FindAll(x => x.Time <= end));
            b.SpikeCarriers.RemoveAll(x => x.Time >= simStart || x.Time > end);
            b.SpikeCarriers.AddRange(s.Carriers.FindAll(x => x.Time <= end));
            b.DefuseAttempts.RemoveAll(x => x.StartTime > end || (x.EndTime >= simStart && Hides(x.Player, x.EndTime)));
            foreach (var d in s.Defuses) if (d.StartTime <= end) b.DefuseAttempts.Add(d);
            b.DefuseAttempts.Sort((x, y) => x.StartTime.CompareTo(y.StartTime));

            // bookmarks: the recorded highlights of what no longer happens go, the takeover gets one
            b.Tags.RemoveAll(x => x.Time >= takeover || (x.Source == TagSource.Auto && x.Time + x.Duration >= takeover));
            b.AddTag(new ReplayTag
            {
                Time = takeover, Round = (short)s.Round, Player = (byte)s.You, Source = TagSource.User,
                Label = $"Takeover: {b.Players[s.You].AgentName}", Note = s.Over ? "" : "Saved mid-round",
            });
            return b;
        }

        /// <summary>A driven player's pose at a time, between the samples around it (positions and view interpolated, the
        /// rest from the earlier one). False when there are none.</summary>
        private static bool PoseAt(List<(float Time, Frame Frame)> poses, float time, out Frame f)
        {
            f = default;
            if (poses.Count == 0) return false;
            int lo = 0, hi = poses.Count - 1;
            if (time <= poses[0].Time) { f = poses[0].Frame; return true; }
            if (time >= poses[hi].Time) { f = poses[hi].Frame; return true; }
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (poses[mid].Time <= time) lo = mid; else hi = mid;
            }
            var a = poses[lo];
            var b = poses[hi];
            f = a.Frame;
            if (!a.Frame.Alive || !b.Frame.Alive) return true;
            float u = Mathf.Clamp01((time - a.Time) / Mathf.Max(1e-4f, b.Time - a.Time));
            f.X = Mathf.Lerp(a.Frame.X, b.Frame.X, u);
            f.Y = Mathf.Lerp(a.Frame.Y, b.Frame.Y, u);
            f.Z = Mathf.Lerp(a.Frame.Z, b.Frame.Z, u);
            float yaw = Mathf.LerpAngle(a.Frame.Yaw, b.Frame.Yaw, u);
            f.YawCenti = (short)Mathf.Clamp(Mathf.RoundToInt(Mathf.DeltaAngle(0f, yaw) * 100f), -18000, 17999);
            f.PitchCenti = (short)Mathf.RoundToInt(Mathf.Lerp(a.Frame.PitchCenti, b.Frame.PitchCenti, u));
            return true;
        }

        private static SpikeState ToReplay(SpikeStatus s)
        {
            switch (s)
            {
                case SpikeStatus.Carried: return SpikeState.Carried;
                case SpikeStatus.Dropped: return SpikeState.Dropped;
                case SpikeStatus.Planted: return SpikeState.Planted;
                case SpikeStatus.Defused: return SpikeState.Defused;
                case SpikeStatus.Detonated: return SpikeState.Detonated;
                default: return SpikeState.None;
            }
        }

        private Vector3 ToReplay(Vector3 unity) => ToReplay(_source, unity);

        /// <summary>A Unity position in the replay's space (the inverse of <see cref="VrpReplay.ToUnity"/>).</summary>
        private static Vector3 ToReplay(VrpReplay r, Vector3 unity) =>
            r.Space == CoordinateSpace.Unity ? unity : new Vector3(unity.z * 100f, unity.x * 100f, unity.y * 100f);

        private Vector3 ToReplayDirection(Vector3 unity) =>
            _source.Space == CoordinateSpace.Unity ? unity : new Vector3(unity.z, unity.x, unity.y);
    }
}
