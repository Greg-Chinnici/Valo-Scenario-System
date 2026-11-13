using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using ValorantReplay.Playback;
using ValoReplay.Maps;
using ValoReplay.Replay;

namespace ValoReplay.Play
{
    /// <summary>
    /// Round play: puts you in first person as one agent at the exact replay moment (position, view, HP/armor, guns
    /// and ammo, credits, ability charges) on the built 3D map. With the round AI on (<see cref="GameSettings.roundAi"/>,
    /// the default) everyone else follows the recording until they notice you, then the <see cref="RoundAi"/> plays
    /// them: they move, fight back, plant and defuse, and the round ends when the simulation says so. Hold F to plant or
    /// defuse. With it off, everyone keeps playing as recorded (the <see cref="ReplayViewer"/> in ghost mode) and your
    /// hits are scored against their recorded HP + armor; a "kill" greys them out. Esc opens the pause menu.
    /// </summary>
    public sealed class RoundPlayController : MonoBehaviour
    {
        [SerializeField] private ReplayViewer ghosts;
        [SerializeField] private Material tracerMaterial;
        [Header("Play the scene directly (no ReplaySession)")]
        [SerializeField] private string debugVrpPath = "";
        [SerializeField] private int debugPlayer = 0;
        [SerializeField] private float debugTime = -1f;

        public VrpReplay Replay { get; private set; }
        public int Player { get; private set; } = -1;
        public int Team { get; private set; }
        public float TakeoverTime { get; private set; }
        public float Now => Ai != null ? Ai.Now : ghosts != null ? ghosts.Time : 0f;
        /// <summary>The character AI for everyone else (null = they play as recorded).</summary>
        public RoundAi Ai { get; private set; }
        /// <summary>You were killed (round AI only).</summary>
        public bool Dead { get; private set; }
        /// <summary>"ROUND WON · SPIKE DEFUSED" once the AI round is over, else null.</summary>
        public string ResultText { get; private set; }
        /// <summary>Plant / defuse prompt or progress for the HUD, or null.</summary>
        public string TaskHint { get; private set; }
        public FpsController Fps { get; private set; }
        public WeaponSystem Weapons { get; private set; }
        public int Health { get; private set; }
        public int Armor { get; private set; }
        public int Shield { get; private set; }
        public int Credits { get; private set; }
        public bool RoundOver { get; private set; }
        public bool Spectating { get; private set; }
        public SpectatorController Spectator { get; private set; }
        public string Error { get; private set; }
        public ReplayInsights Insights => ghosts != null ? ghosts.Insights : null;

        /// <summary>A shot landed on a player: who, where, how much, and whether it finished them.</summary>
        public event System.Action<int, Region, float, bool> PlayerHit;
        /// <summary>Hit a teammate (no damage).</summary>
        public event System.Action<int> TeammateHit;
        public event System.Action Ready;

        private readonly Dictionary<int, float> _damage = new Dictionary<int, float>();
        private int _round = -1;

        /// <summary>Loads in steps (the map instantiates over several frames) while the loading screen spins.</summary>
        private IEnumerator Start()
        {
            if (ghosts == null) ghosts = FindFirstObjectByType<ReplayViewer>();
            var session = ReplaySession.Instance;
            string path;
            if (session != null && session.Replay != null && session.TakeoverPlayer >= 0)
            {
                Replay = session.Replay;
                path = session.VrpPath;
                Player = session.TakeoverPlayer;
                TakeoverTime = session.TakeoverTime;
            }
            else if (!string.IsNullOrEmpty(debugVrpPath) && File.Exists(debugVrpPath))
            {
                Replay = VrpReplay.Load(debugVrpPath);
                Replay.ConvertToUnitySpace();
                path = debugVrpPath;
                Player = Mathf.Clamp(debugPlayer, 0, Replay.Players.Count - 1);
                TakeoverTime = debugTime >= 0f ? debugTime : Replay.Rounds.Count > 0 ? Replay.Rounds[0].StartTime + 30f : 0f;
            }
            else
            {
                Fail("No round to play. Pick an agent in the replay viewer, or set Debug Vrp Path on Round Play.");
                yield break;
            }
            Team = Replay.Players[Player].Team;
            _round = Replay.RoundAt(TakeoverTime);

            // everyone keeps playing as recorded (your agent as a gold ghost); walls block you
            LoadingScreen.SetStatus("Placing players");
            ghosts.PossessedPlayer = Player;      // your agent's original run plays on as a gold ghost
            ghosts.Load(Replay, path, loadMap: false);
            ghosts.Playing = false;
            ghosts.Seek(TakeoverTime);
            // the AI's walkable map and fitted profiles build on a worker thread while the 3D map loads
            var aiTask = GameSettings.Current.roundAi ? RoundAi.CreateAsync(Replay, path, TakeoverTime, System.Environment.TickCount) : null;
            yield return null;

            string mapName = Replay.MapName;
            yield return ghosts.LoadBuiltMapAsync(p => LoadingScreen.SetStatus($"Building {mapName}  {p * 100f:0}%"));
            if (!ghosts.ShowBuiltMap)
                Error = $"{mapName} has no built 3D map yet (ValoReplay > Maps > Build). Showing the minimap floor.";

            LoadingScreen.SetStatus("Spawning");
            ghosts.Seek(TakeoverTime);
            ghosts.Speed = 1f;
            SpawnPlayer();
            if (aiTask != null)
            {
                LoadingScreen.SetStatus("Briefing the AI");
                while (!aiTask.IsCompleted) yield return null;
                if (aiTask.IsFaulted || aiTask.IsCanceled)
                {
                    string why = aiTask.Exception?.GetBaseException().Message ?? "cancelled";
                    Debug.LogWarning($"[RoundPlay] Round AI unavailable: {aiTask.Exception?.GetBaseException()}");
                    Error = (Error != null ? Error + "\n" : "") + $"Round AI unavailable ({why}). Everyone plays as recorded.";
                }
                else
                {
                    Ai = gameObject.AddComponent<RoundAi>();
                    Ai.Init(this, ghosts, aiTask.Result);
                    // the blackboards learn the round so far from the recording; the AI decides from them
                    yield return Ai.WarmUp(p => LoadingScreen.SetStatus($"Briefing the AI  {p * 100f:0}%"));
                }
            }
            ApplySettings();
            GameSettings.Changed += ApplySettings;
            if (!ghosts.ShowBuiltMap)
            {
                // no geometry: an invisible floor under the player so they do not fall forever
                var floor = new GameObject("Fallback Floor").AddComponent<BoxCollider>();
                floor.size = new Vector3(2000f, 1f, 2000f);
                floor.transform.position = Fps.transform.position + Vector3.down * 1.5f;
            }

            yield return null;          // one frame with everything in place before the reveal
            ghosts.Playing = true;
            LoadingScreen.Hide();
            if (!Paused)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            Ready?.Invoke();
        }

        private void SpawnPlayer()
        {
            var f = Replay.Sample(Player, TakeoverTime);
            var go = new GameObject($"You ({Replay.Players[Player].AgentName})");
            go.AddComponent<CharacterController>();
            Fps = go.AddComponent<FpsController>();
            Weapons = go.AddComponent<WeaponSystem>();
            Weapons.tracerMaterial = tracerMaterial;
            Fps.Teleport(Replay.UnityPosition(f), f.Yaw, f.Pitch, f.Crouching);
            BuildBody(go.transform);
            Spectator = gameObject.AddComponent<SpectatorController>();
            Spectator.Init(this, ghosts);

            Health = f.Alive ? f.Health : 100;
            Armor = f.Armor;
            Shield = f.Shield;
            _armorAtStart = Armor;
            var stats = Replay.StatsAt(Player, TakeoverTime);
            Credits = stats.Time >= 0 ? stats.Credits : 0;

            ghosts.Insights.GunsAt(Player, TakeoverTime, out string primary, out string sidearm);
            string inHand = Replay.WeaponOf(f);
            if (string.IsNullOrEmpty(sidearm) && string.IsNullOrEmpty(primary)) sidearm = "BasePistol";
            Weapons.Setup(primary, sidearm, inHand, f.Ammo, f.ReserveAmmo);
            Weapons.Hit += OnShotHit;
            Weapons.Fired += OnFired;
        }

        private int _armorAtStart;

        private void OnFired()
        {
            if (Ai != null && !Weapons.Equipped.Stats.Melee) Ai.Gunshot(Weapons.Equipped.Stats.Class, Fps.Camera.transform.position);
        }

        private GameObject _body;
        private IPlayerFigure _figure;

        /// <summary>Your character, in your team colour washed toward white, only shown while spectating (in first person
        /// the camera is inside it). Animated from your own movement.</summary>
        private void BuildBody(Transform player)
        {
            _figure = PlayerFigures.Create(player);
            _figure.Tint(Color.Lerp(ghosts.TeamColor(Team), Color.white, 0.6f));
            _body = _figure.Root.gameObject;
            _body.name = "Your Body";
            _body.SetActive(false);
        }

        private void OnShotHit(WeaponSystem.ShotHit hit)
        {
            int p = ghosts.PlayerOf(hit.Collider);
            if (p < 0 || ghosts.Eliminated.Contains(p)) return;
            if (Replay.Players[p].Team == Team) { TeammateHit?.Invoke(p); return; }

            // region from where on the capsule the bullet landed
            var b = hit.Collider.bounds;
            float u = (hit.Point.y - b.min.y) / Mathf.Max(0.01f, b.size.y);
            var region = u > 0.82f ? Region.Head : u < 0.42f ? Region.Legs : Region.Body;
            float dmg = hit.Weapon.Damage(region, hit.Distance);

            if (Ai != null)
            {
                // the sim owns everyone's health: armor absorbs, the victim reacts, a kill is a real death
                if (!Ai.Started) return;           // buy phase: barriers are up
                bool killed = Ai.Hit(p, dmg, region == Region.Head, hit.Weapon.Class);
                PlayerHit?.Invoke(p, region, dmg, killed);
                return;
            }

            _damage.TryGetValue(p, out float total);
            total += dmg;
            _damage[p] = total;
            var f = Replay.Sample(p, Now);
            bool kill = total >= f.Health + f.Armor + f.Shield;   // armor simplified to extra HP
            if (kill) ghosts.Eliminated.Add(p);
            PlayerHit?.Invoke(p, region, dmg, kill);
        }

        /// <summary>
        /// Is this player alive now? Once the AI round runs, the sim decides. Otherwise the recording, less the players
        /// you eliminated and the recorded deaths your agent dealt after the takeover (those no longer happen).
        /// </summary>
        public bool IsAlive(int player)
        {
            if (Replay == null || player < 0 || player >= Replay.Players.Count) return false;
            if (player == Player) return !Dead;
            if (Ai != null && Ai.Started && player < Ai.Sim.Agents.Count) return Ai.Sim.Agents[player].Alive;
            if (ghosts != null && ghosts.Eliminated.Contains(player)) return false;
            int ri = Replay.RoundAt(TakeoverTime);
            float start = ri >= 0 ? Replay.Rounds[ri].StartTime : 0f;
            float t = Now;
            foreach (var k in Replay.Kills)
            {
                if (k.Victim != player || k.Time < start || k.Time > t) continue;
                if (k.Time >= TakeoverTime && k.Killer == Player) continue;
                return false;
            }
            return true;
        }

        /// <summary>Pause: freeze time and input, free the cursor (the pause menu calls this).</summary>
        public void SetPaused(bool paused)
        {
            Paused = paused;
            Time.timeScale = paused ? 0f : GameSettings.Current.gameSpeed;
            UpdateInput();
            Cursor.lockState = paused || Replay == null ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = paused || Replay == null;
        }

        /// <summary>First-person spectating (the <see cref="SpectatorController"/> calls this): your agent stands still
        /// and shows a body so you can find yourself from others' eyes.</summary>
        public void SetSpectating(bool spectating)
        {
            Spectating = spectating;
            if (_body != null)
            {
                if (spectating && !_body.activeSelf) _figure.ResetMotion();
                _body.SetActive(spectating);
            }
            UpdateInput();
        }

        private void UpdateInput()
        {
            bool enabled = !Paused && !RoundOver && !Spectating && !Dead;
            if (Fps != null) Fps.InputEnabled = enabled;
            if (Weapons != null) Weapons.InputEnabled = enabled;
        }

        public bool Paused { get; private set; }

        /// <summary>Restart from the takeover moment (reloads the scene).</summary>
        public void Restart()
        {
            if (Replay == null) return;
            if (ReplaySession.Instance != null && ReplaySession.Instance.Replay == Replay) ReplaySession.PlayAs(Player, TakeoverTime);
            else UnityEngine.SceneManagement.SceneManager.LoadScene(gameObject.scene.name);
        }

        private void ApplySettings()
        {
            var s = GameSettings.Current;
            s.ApplyVideo();
            ghosts.ShowShots = s.showRecordedShots;
            ghosts.ShowAimLines = s.showGhostAim;
            ghosts.MapTextures = s.mapTextures;
            if (!Paused) Time.timeScale = s.gameSpeed;
        }

        private void OnDestroy()
        {
            GameSettings.Changed -= ApplySettings;
            Time.timeScale = 1f;      // leaving the scene: never carry pause / slow motion over
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void Update()
        {
            if (Replay == null || Fps == null) return;     // still loading
            if (Ai != null) UpdateAi();
            else if (!RoundOver && _round >= 0 && Now >= Replay.Rounds[_round].EndTime)
            {
                RoundOver = true;
                ghosts.Playing = false;
                UpdateInput();
            }
            if (_body != null && _body.activeSelf)
                _figure.Animate(Fps.transform.position, Fps.transform.eulerAngles.y, Time.deltaTime, 0f, Fps.Crouching, !Fps.Grounded);
        }

        private void UpdateAi()
        {
            Ai.Advance(Dead ? null : Fps);
            var sim = Ai.Sim;
            var you = Ai.You;

            // your vitals come from the sim (the AI shoots back); the sim keeps armor and ability shield as one pool
            Health = you.Alive ? Mathf.Max(1, Mathf.CeilToInt(you.Health)) : 0;
            int pool = Mathf.CeilToInt(you.Armor);
            Armor = Mathf.Min(_armorAtStart, pool);
            Shield = Mathf.Max(0, pool - Armor);
            if (!you.Alive && !Dead)
            {
                Dead = true;                                // the sim drops the spike where you fell
                UpdateInput();
            }

            UpdatePlantDefuse(sim, you);

            if (!RoundOver && sim.Over)
            {
                RoundOver = true;
                ghosts.Playing = false;
                bool won = sim.Winner == Team;
                ResultText = $"{(won ? "ROUND WON" : "ROUND LOST")}  ·  {Reason(sim.EndReason, sim.Winner == sim.AttackingTeam)}";
                TaskHint = null;
                UpdateInput();
            }
        }

        internal static string Reason(RoundEndReason reason, bool attackersWon)
        {
            switch (reason)
            {
                case RoundEndReason.Elimination: return attackersWon ? "DEFENDERS ELIMINATED" : "ATTACKERS ELIMINATED";
                case RoundEndReason.Defused: return "SPIKE DEFUSED";
                case RoundEndReason.Detonated: return "SPIKE DETONATED";
                case RoundEndReason.TimeExpired: return "TIME EXPIRED";
                default: return reason.ToString().ToUpperInvariant();
            }
        }

        /// <summary>Hold F: plant with the spike on a site, defuse next to the planted spike. Moving or letting go stops.</summary>
        private void UpdatePlantDefuse(ValorantReplay.Sim.RoundSim sim, ValorantReplay.Sim.SimAgent you)
        {
            TaskHint = null;
            if (!Ai.Started || RoundOver || !you.Alive) return;
            var kb = UnityEngine.InputSystem.Keyboard.current;
            bool held = kb != null && kb.fKey.isPressed && Fps.InputEnabled;
            bool planting = you.Action == ValorantReplay.Sim.AgentAction.Plant, defusing = you.Action == ValorantReplay.Sim.AgentAction.Defuse;
            if (held && !planting && !defusing)
            {
                if (sim.BeginPlant(Player)) planting = true;
                else if (sim.BeginDefuse(Player)) defusing = true;
            }
            else if (!held && (planting || defusing))
            {
                sim.CancelTask(Player);
                planting = defusing = false;
            }

            if (planting) TaskHint = $"PLANTING  {you.TaskSeconds:0.0} / {sim.Options.PlantDuration:0} s";
            else if (defusing) TaskHint = $"DEFUSING  {you.TaskSeconds:0.0} / {sim.Options.DefuseDuration:0} s";
            else if (sim.SpikeState == ValorantReplay.Sim.SpikeStatus.Carried && sim.SpikeCarrier == Player)
            {
                var site = sim.Map.NearestSite(you.Position);
                TaskHint = sim.Map.InSite(site, you.Position) ? $"HOLD F TO PLANT ON {site.Name}" : "YOU HAVE THE SPIKE";
            }
            else if (sim.SpikeState == ValorantReplay.Sim.SpikeStatus.Planted && !you.Attacking &&
                     System.Numerics.Vector3.Distance(you.Position, sim.SpikePosition) <= 1.5f)
                TaskHint = "HOLD F TO DEFUSE";
        }

        /// <summary>Alive and on the map now (as the AI or the recording has it), and not eliminated by you.</summary>
        public bool IsUp(int player)
        {
            if (ghosts.Eliminated.Contains(player)) return false;
            if (Ai != null && Ai.TryGetPose(player, out var pose)) return pose.Visible;
            var f = Replay.Sample(player, Now);
            return f.Alive && (f.Flags & FrameFlags.OffMap) == 0;
        }

        /// <summary>A player's eyes and view now (for spectating). False when they are not up.</summary>
        public bool ViewOf(int player, out Vector3 eye, out Quaternion view)
        {
            if (Ai != null && Ai.TryGetPose(player, out var pose))
            {
                eye = pose.Position + Vector3.up * (pose.Crouching ? 0.07f : 0.62f);
                view = Quaternion.Euler(0f, pose.Yaw, 0f);
                return pose.Visible;
            }
            var f = Replay.Sample(player, Now);
            eye = Replay.UnityPosition(f) + Vector3.up * (f.Crouching ? 0.07f : 0.62f);
            view = Replay.UnityRotation(f);
            return f.Alive && (f.Flags & FrameFlags.OffMap) == 0 && !ghosts.Eliminated.Contains(player);
        }

        private void Fail(string message)
        {
            Error = message;
            LoadingScreen.Hide();
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            Ready?.Invoke();
        }
    }
}
