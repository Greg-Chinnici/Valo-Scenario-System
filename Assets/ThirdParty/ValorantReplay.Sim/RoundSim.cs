// RoundSim.cs - a headless simulation of the rest of one round. Start it from any moment of a .vrp (FromReplay) or
// from scratch; agents follow the recording until they perceive something that did not happen in it, then the AI
// takes over. Deterministic for a given seed; each agent draws from its own random streams (SimRandom). Unity drives it
// with Step() and renders Agents.
//
// What is modelled: movement on the NavGrid, line of sight, footsteps and gunshots, per-agent beliefs and team
// callouts, gunfights (reaction time, accuracy, damage, armor), the spike (carry, drop, plant, defuse, detonate).
// What is not (yet): abilities, economy, recoil, jumping, exact hitboxes.
using System;
using System.Collections.Generic;
using System.Numerics;
using ValorantReplay.Playback;

namespace ValorantReplay.Sim
{
    public enum SpikeStatus : byte { None = 0, Carried = 1, Dropped = 2, Planted = 3, Defused = 4, Detonated = 5 }

    public sealed class SimOptions
    {
        public float TickRate = 20f;
        public float PerceptionInterval = 0.1f;
        public float FovHalfAngle = 52f;        // Valorant's 103 degree horizontal FOV
        public float MaxSightRange = 80f;
        public float FootstepRange = 25f;
        public float GunshotRange = 60f;
        public float RunSpeed = 5.4f;           // rifle out
        public float WalkSpeed = 3f;
        public float PlantDuration = 4f;
        public float DefuseDuration = 7f;
        public float SpikeDuration = 45f;
        public float RoundTime = 100f;
        public float TradeWindow = Metrics.TradeWindow;
        public float ArmorAbsorb = 0.66f;
        public float CalloutRefresh = 1.5f;
        public float MaxDuration = 240f;
        /// <summary>Teammates closer than this push apart (players do not stand inside each other).</summary>
        public float Spacing = 1.5f;
        /// <summary>A peek moves at most this far towards the believed enemy before re-deciding.</summary>
        public float PeekDistance = 10f;
        /// <summary>Holder's advantage: a stationary holder whose view is within this many degrees of an enemy that
        /// appears reacts in PreAimReaction of its reaction time, without turning.</summary>
        public float PreAimAngle = 20f;
        public float PreAimReaction = 0.7f;
        /// <summary>Urgency rises from 0 to 1 over this many seconds as the time left closes on the time an agent needs
        /// (attackers: walk to the site and plant; defenders after a plant: walk to the spike and defuse), plus
        /// UrgencyMargin.</summary>
        public float UrgencyWindow = 20f;
        public float UrgencyMargin = 5f;
        /// <summary>How strongly defenders get urgent after a plant, 0..1 (attackers always fully before one). Swept on the
        /// corpus: 1 predicts winners best (Brier 0.222 vs 0.232 at 0.5, 0.236 without), though it retakes too often
        /// (defused 23% vs 18% recorded; 0.5 matches the split).</summary>
        public float DefenderUrgency = 1f;
    }

    public sealed class AgentSetup
    {
        public int Team;
        public bool Attacking;
        public Vector3 Position;
        public float Yaw;
        public bool Alive = true;
        public float Health = 100f, Armor = 50f;
        public Profile Profile = Profile.Default;
        public string Weapon = "";
        public string Name = "";
        public AgentMode Mode = AgentMode.Ai;
    }

    public sealed class RoundSetup
    {
        public float Remaining = 100f;
        public AttackPlan Plan = AttackPlan.Default;
        public string TargetSite;               // null = director picks
        public int SpikeCarrier = -1;           // -1 = a random living attacker
        public bool SpikeDropped;               // on the ground at SpikePosition
        public bool SpikePlanted;               // planted at SpikePosition with SpikeTimer left
        public Vector3 SpikePosition;
        public float SpikeTimer = 45f;
    }

    public sealed class RoundResult
    {
        public int Winner;
        public bool AttackersWon;
        public RoundEndReason Reason;
        public float Duration;
        public bool Planted;
        public int Diverged;                    // agents that left the recording
        public List<KillRecord> Kills = new List<KillRecord>();
        public List<int> Players = new List<int>();   // agents alive at the start
        /// <summary>Each agent's shooting counters (accuracy, hit regions, notices, reactions), indexed by agent, with
        /// Measurements' definitions.</summary>
        public List<PlayerTally> Shooting = new List<PlayerTally>();
        /// <summary>Execute and plant times and movement speed, with the same sampler as recorded rounds.</summary>
        public RoundTempo Tempo;
    }

    public sealed class RoundSim
    {
        public SimMap Map { get; }
        public SimOptions Options { get; }
        public IReadOnlyList<SimAgent> Agents => agents;
        public float Time { get; private set; }
        /// <summary>Round time left before the plant (the spike timer takes over after it).</summary>
        public float Remaining { get; private set; }
        public SpikeStatus SpikeState { get; private set; }
        public int SpikeCarrier { get; private set; } = -1;
        public Vector3 SpikePosition { get; private set; }
        public float SpikeTimer { get; private set; }
        public bool Over { get; private set; }
        public int Winner { get; private set; } = -1;
        public RoundEndReason EndReason { get; private set; }
        public int AttackingTeam { get; }
        public readonly List<KillRecord> Kills = new List<KillRecord>();
        public readonly List<SimEvent> Events = new List<SimEvent>();

        /// <summary>The recording replay-mode agents follow (null when every agent starts as AI).</summary>
        public VrpReplay Source { get; private set; }
        /// <summary>Replay time that sim time 0 corresponds to (FromReplay; also when every agent starts as AI).</summary>
        public float SourceStart { get; private set; }

        readonly List<SimAgent> agents;
        readonly TeamDirector[] directors = new TeamDirector[2];
        readonly TeamBlackboard[] boards = new TeamBlackboard[2];
        readonly bool[,] sees;
        // measurement (Measurements' definitions): in view without the "shot at by" shortcut, last time in view, and the
        // pending notice a reaction is timed from (-1 = none)
        readonly bool[,] inView;
        readonly float[,] lastInView, noticedAt;
        // independent streams (SimRandom): round-level draws, and per agent aim / decisions / senses / callouts
        readonly Random roundRng;
        readonly Random[] aimRng, decideRng, sensesRng, commsRng, lookRng;
        readonly List<int> startPlayers = new List<int>();
        float perceptionClock;

        /// <summary>Pacing so far (RoundTempo), sampled every RoundTempo.Interval from the start.</summary>
        public readonly RoundTempo Tempo = new RoundTempo();
        float tempoClock;
        Vector3[] tempoPrevious;
        bool[] tempoWasAlive;
        readonly List<TempoPlayer> tempoPlayers = new List<TempoPlayer>();

        struct Death { public float Time; public Vector3 Position; public int Killer; }
        readonly Death[] lastDeath = { new Death { Time = -100f }, new Death { Time = -100f } };

        // recording state
        Round sourceRound;
        int killCursor;
        bool spikeFromRecording;

        public RoundSim(SimMap map, IList<AgentSetup> setup, RoundSetup round = null, SimOptions options = null, int seed = 0)
        {
            Map = map;
            Options = options ?? new SimOptions();
            round = round ?? new RoundSetup();
            int n = setup.Count;
            roundRng = SimRandom.Create(seed, -1, RandomStream.Round);
            aimRng = Streams(seed, n, RandomStream.Aim);
            decideRng = Streams(seed, n, RandomStream.Decide);
            sensesRng = Streams(seed, n, RandomStream.Senses);
            commsRng = Streams(seed, n, RandomStream.Comms);
            lookRng = Streams(seed, n, RandomStream.Look);
            agents = new List<SimAgent>(n);
            sees = new bool[n, n];
            inView = new bool[n, n];
            lastInView = new float[n, n];
            noticedAt = new float[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++) { lastInView[i, j] = float.NegativeInfinity; noticedAt[i, j] = -1f; }
            AttackingTeam = -1;
            for (int i = 0; i < n; i++)
            {
                var s = setup[i];
                if (s.Team < 0 || s.Team > 1) throw new ArgumentException($"agent {i}: team must be 0 or 1");
                var a = new SimAgent
                {
                    Index = i, Team = s.Team, Attacking = s.Attacking, Name = s.Name, Profile = s.Profile ?? Profile.Default, Mode = s.Mode,
                    Weapon = WeaponStats.For(s.Weapon), Position = s.Position, PrevPosition = s.Position, Yaw = s.Yaw, Alive = s.Alive,
                    Health = s.Alive ? s.Health : 0f, Armor = s.Armor, Beliefs = new Beliefs(n),
                    LastCallout = Filled(n, -100f), LastHeard = Filled(n, -100f), HoldYaw = s.Yaw,
                };
                agents.Add(a);
                if (a.Alive) startPlayers.Add(i);
                if (s.Attacking)
                {
                    if (AttackingTeam >= 0 && AttackingTeam != s.Team) throw new ArgumentException("both teams marked attacking");
                    AttackingTeam = s.Team;
                }
            }
            if (AttackingTeam < 0) throw new ArgumentException("no attacking team");
            for (int t = 0; t < 2; t++)
            {
                boards[t] = new TeamBlackboard(t, n);
                directors[t] = new TeamDirector(t, t == AttackingTeam);
            }
            foreach (var a in agents)
                if (a.Team != AttackingTeam && a.Attacking) throw new ArgumentException($"agent {a.Index}: attacking flag does not match its team");

            Remaining = round.Remaining;
            if (round.SpikePlanted)
            {
                SpikeState = SpikeStatus.Planted;
                SpikePosition = round.SpikePosition;
                SpikeTimer = round.SpikeTimer;
            }
            else if (round.SpikeDropped)
            {
                SpikeState = SpikeStatus.Dropped;
                SpikePosition = round.SpikePosition;
            }
            else
            {
                int c = round.SpikeCarrier;
                if (c < 0 || c >= n || !agents[c].Alive || !agents[c].Attacking)
                {
                    var alive = agents.FindAll(a => a.Attacking && a.Alive);
                    c = alive.Count > 0 ? alive[roundRng.Next(alive.Count)].Index : -1;
                }
                SpikeState = c >= 0 ? SpikeStatus.Carried : SpikeStatus.None;
                SpikeCarrier = c;
                if (c >= 0) SpikePosition = agents[c].Position;
            }
            directors[AttackingTeam].Setup(this, round.Plan, round.TargetSite, roundRng);
            directors[1 - AttackingTeam].Setup(this, round.Plan, null, roundRng);
        }

        /// <summary>The round of replay at time (seconds into the replay), as a simulation. Agent i is player i. With
        /// followRecording every agent replays what it really did until it diverges; otherwise all start as AI. Profiles
        /// default to ones fitted from the same replay (ProfileFitter), so a diverged player keeps playing like itself.
        /// A time before the barrier drop starts at the barrier drop.</summary>
        public static RoundSim FromReplay(VrpReplay replay, SimMap map, float time, Func<int, Profile> profiles = null,
            SimOptions options = null, int seed = 0, bool followRecording = true, AttackPlan plan = AttackPlan.Default)
        {
            options = options ?? new SimOptions();
            int ri = replay.RoundAt(time);
            if (ri < 0) throw new ArgumentException("replay has no rounds");
            var round = replay.Rounds[ri];
            if (round.AttackingTeam == 255) throw new ArgumentException($"round {ri + 1}: attacking team unknown");
            if (round.BarrierTime >= 0f && time < round.BarrierTime) time = round.BarrierTime;
            if (time >= round.EndTime) throw new ArgumentException($"round {ri + 1} is already over at {time:0.0}s");
            if (profiles == null)
            {
                var fitted = ProfileFitter.Fit(replay, map: map);
                profiles = p => fitted[p].Profile;
            }

            var setup = new List<AgentSetup>();
            for (int p = 0; p < replay.Players.Count; p++)
            {
                var f = replay.Sample(p, time);
                var lo = replay.LoadoutOf(ri, p);
                string held = replay.WeaponOf(f);
                string gun = !string.IsNullOrEmpty(lo?.Primary) ? lo.Primary : !string.IsNullOrEmpty(lo?.Sidearm) ? lo.Sidearm : held;
                int team = replay.Players[p].Team;
                if (team > 1) throw new ArgumentException($"player {p}: team {team} (only two-team matches are supported)");
                setup.Add(new AgentSetup
                {
                    Team = team, Attacking = team == round.AttackingTeam, Position = SimSpace.FromReplay(replay, f), Yaw = f.Yaw,
                    Alive = f.Alive, Health = f.Health, Armor = f.Armor + f.Shield, Profile = profiles(p), Weapon = gun,
                    Name = replay.Players[p].DisplayName, Mode = followRecording ? AgentMode.Replay : AgentMode.Ai,
                });
            }

            var rs = replay.RoundStateAt(time);
            var rsetup = new RoundSetup
            {
                Plan = plan,
                // the recorded clock is sampled only when it changes: trust a combat-phase value (counted down from when it
                // was sampled), not the buy timer's last value that is still there at the barrier drop
                Remaining = Math.Max(1f, rs.Phase == RoundPhase.Combat && rs.Remaining > 1f ? rs.Remaining - (time - rs.Time)
                    : options.RoundTime - (time - Math.Max(round.StartTime, round.BarrierTime))),
            };
            if (round.PlantTime >= 0f && round.PlantTime <= time)
            {
                rsetup.SpikePlanted = true;
                rsetup.SpikePosition = SimSpace.FromReplay(replay, round.PlantX, round.PlantY, round.PlantZ);
                rsetup.SpikeTimer = Math.Max(0.5f, options.SpikeDuration - (time - round.PlantTime));
            }
            else
            {
                int holder = SpikeHolderAt(replay, time, out float since);
                if (holder < replay.Players.Count) rsetup.SpikeCarrier = holder;
                else if (holder == VrpReplay.HolderNone && since >= 0f)
                {
                    // on the ground: where the last holder was when it left them
                    int last = LastSpikeHolderBefore(replay, since);
                    if (last >= 0)
                    {
                        rsetup.SpikeDropped = true;
                        rsetup.SpikePosition = SimSpace.FromReplay(replay, replay.Sample(last, since));
                    }
                }
            }

            var sim = new RoundSim(map, setup, rsetup, options, seed) { SourceStart = time };
            if (followRecording)
            {
                sim.Source = replay;
                sim.sourceRound = round;
                sim.spikeFromRecording = sim.SpikeState != SpikeStatus.Planted && replay.SpikeCarriers.Count > 0;
                while (sim.killCursor < replay.Kills.Count && replay.Kills[sim.killCursor].Time <= time) sim.killCursor++;
            }
            return sim;
        }

        // ------------------------------------------------------------------ public API

        public TeamBlackboard Board(int team) => boards[team];
        public TeamDirector Director(int team) => team >= 0 && team < 2 ? directors[team] : null;
        public bool Sees(int agent, int other) => sees[agent, other];

        /// <summary>Replaces the NavGrid's approximate line of sight (eye to eye), e.g. with raycasts against the real map
        /// and smokes in Unity. Null = Map.Grid.LineOfSight.</summary>
        public Func<Vector3, Vector3, bool> SightTest { get; set; }

        bool Clear(Vector3 from, Vector3 to) => SightTest != null ? SightTest(from, to) : Map.Grid.LineOfSight(from, to);

        /// <summary>Replaces the agents' own beliefs and the callout board for every decision (e.g. Unity's knowledge
        /// blackboard). Null = the simulation's own.</summary>
        public IBeliefSource BeliefSource { get; set; }

        /// <summary>Hands an agent to the user. Everyone else keeps following the recording until they notice.</summary>
        public void TakeControl(int agent)
        {
            var a = agents[agent];
            if (a.Mode == AgentMode.Replay && a.DivergedAt < 0f) a.DivergedAt = Time;
            a.Mode = AgentMode.External;
            a.Action = AgentAction.None;
            a.Order = default;
            a.Path.Clear();
            if (SpikeCarrier == agent) spikeFromRecording = false;
        }

        /// <summary>Moves an External agent (call once per Step).</summary>
        public void SetExternalState(int agent, Vector3 position, float yaw, bool walking = false, bool crouching = false)
        {
            var a = agents[agent];
            if (a.Mode != AgentMode.External || !a.Alive) return;
            a.Moving = SimSpace.FlatDistance(position, a.Position) > 0.02f;
            a.Position = position;
            a.Yaw = yaw;
            a.Walking = walking;
            a.Crouching = crouching;
            if (a.Moving && (a.Action == AgentAction.Plant || a.Action == AgentAction.Defuse)) { a.Action = AgentAction.None; a.TaskProgress = 0f; }
        }

        /// <summary>An External agent fires one shot from its eye along direction. Returns the agent hit, or -1. The hit
        /// region comes from where the ray passes the target (head sphere, body and legs as one capsule).</summary>
        public int ExternalFire(int agent, Vector3 direction)
        {
            var a = agents[agent];
            if (a.Mode != AgentMode.External || !a.Alive || Time < a.NextShotAt || direction.LengthSquared() < 1e-6f) return -1;
            a.NextShotAt = Time + a.Weapon.FireInterval;
            a.LastShot = Time;
            Gunshot(a);
            var dir = Vector3.Normalize(direction);
            int hit = -1;
            float best = float.MaxValue;
            bool head = false;
            foreach (var t in agents)
            {
                if (!t.Alive || t.Team == a.Team) continue;
                var toHead = t.Eye + new Vector3(0f, 0.1f, 0f) - a.Eye;
                float along = Vector3.Dot(toHead, dir);
                if (along <= 0f || along >= best) continue;
                var closest = a.Eye + dir * along;
                bool isHead = Vector3.Distance(closest, t.Eye + new Vector3(0f, 0.1f, 0f)) < 0.17f;
                float flat = SimSpace.FlatDistance(closest, t.Position);
                bool isBody = flat < 0.38f && closest.Y > t.Position.Y - NavGrid.ActorHeight && closest.Y < t.Eye.Y - 0.1f;
                if ((!isHead && !isBody) || !Clear(a.Eye, closest)) continue;
                best = along; hit = t.Index; head = isHead;
            }
            if (hit < 0) return -1;
            var target = agents[hit];
            float d = Vector3.Distance(a.Eye, target.Eye);
            float raw = head ? a.Weapon.Head : a.Weapon.Body;
            Damage(target, a.Weapon.DamageAt(d, raw), a);
            return hit;
        }

        /// <summary>An External agent fired, with what it hit decided outside the simulation (e.g. Unity raycasts against the
        /// real map and hitboxes): enemies in range hear the shot. Report each bullet that hit someone with ExternalHit.</summary>
        public void ExternalGunshot(int agent)
        {
            var a = agents[agent];
            if (a.Mode != AgentMode.External || !a.Alive) return;
            a.LastShot = Time;
            Gunshot(a);
        }

        /// <summary>A bullet of an External agent hit an enemy for damage (before armor), as decided outside the simulation.
        /// Returns whether it killed. Teammates, the dead and non-External shooters are ignored.</summary>
        public bool ExternalHit(int agent, int target, float damage)
        {
            var a = agents[agent];
            if (a.Mode != AgentMode.External || !a.Alive || target < 0 || target >= agents.Count || damage <= 0f) return false;
            var t = agents[target];
            if (!t.Alive || t.Team == a.Team) return false;
            a.Target = target;          // the victim knows who shot it (See: shotAtBy)
            a.LastShot = Time;
            Damage(t, damage, a);
            return !t.Alive;
        }

        /// <summary>An External agent lets go of the plant / defuse key.</summary>
        public void CancelTask(int agent)
        {
            var a = agents[agent];
            if (a.Mode != AgentMode.External || (a.Action != AgentAction.Plant && a.Action != AgentAction.Defuse)) return;
            a.Action = AgentAction.None;
            a.TaskProgress = 0f;
        }

        /// <summary>An External attacker with the spike starts planting (it continues while the agent stands still).</summary>
        public bool BeginPlant(int agent)
        {
            var a = agents[agent];
            if (a.Mode != AgentMode.External || SpikeState != SpikeStatus.Carried || SpikeCarrier != agent || !Map.InSite(Map.NearestSite(a.Position), a.Position)) return false;
            a.Action = AgentAction.Plant;
            a.TaskProgress = 0f;
            Event(SimEventType.PlantStarted, agent);
            return true;
        }

        /// <summary>An External defender next to the planted spike starts defusing.</summary>
        public bool BeginDefuse(int agent)
        {
            var a = agents[agent];
            if (a.Mode != AgentMode.External || a.Attacking || SpikeState != SpikeStatus.Planted || Vector3.Distance(a.Position, SpikePosition) > 1.5f) return false;
            a.Action = AgentAction.Defuse;
            a.TaskProgress = 0f;
            Event(SimEventType.DefuseStarted, agent);
            return true;
        }

        /// <summary>Orders snap to the walkable cell nearest the target within this many metres.</summary>
        public const float OrderSnap = 6f;
        /// <summary>An order to go somewhere is done this close to the spot (or in its grid cell): teammates keep their
        /// spacing, so two sent to one spot cannot both stand on it.</summary>
        public const float OrderArrival = 1.5f;

        /// <summary>Gives an agent an order (see OrderType), replacing any it has; an agent on the recording leaves it,
        /// one planting or defusing for another reason stops. Plant: Target is where on a site (off a site = the nearest
        /// site's centre). Defuse and PickUp go to the spike, whatever Target says. False when the order cannot be carried
        /// out from here: the agent is dead or External, it is not the side or spike state the order needs, or there is
        /// nowhere walkable near the target.</summary>
        public bool Command(int agent, OrderType type, Vector3 target = default, float yaw = 0f)
        {
            if (agent < 0 || agent >= agents.Count || type == OrderType.None) return false;
            var a = agents[agent];
            if (!a.Alive || a.Mode == AgentMode.External || Over) return false;
            switch (type)
            {
                case OrderType.Plant:
                {
                    if (!a.Attacking || SpikeState != SpikeStatus.Carried || SpikeCarrier != agent) return false;
                    var site = Map.NearestSite(target);
                    if (site == null) return false;
                    if (!Map.InSite(site, target)) target = site.Center;
                    int n = Map.Grid.Nearest(target, OrderSnap);
                    if (n >= 0 && Map.InSite(site, Map.Grid.Position(n))) target = Map.Grid.Position(n);
                    break;
                }
                case OrderType.Defuse:
                    if (a.Attacking || SpikeState != SpikeStatus.Planted) return false;
                    target = SpikePosition;
                    break;
                case OrderType.PickUp:
                    if (!a.Attacking || SpikeState != SpikeStatus.Dropped) return false;
                    target = SpikePosition;
                    break;
                default:
                {
                    int n = Map.Grid.Nearest(target, OrderSnap);
                    if (n < 0) return false;
                    target = Map.Grid.Position(n);
                    break;
                }
            }
            if (a.Mode == AgentMode.Replay) Diverge(a);
            bool sameTask = (a.Action == AgentAction.Plant && type == OrderType.Plant) || (a.Action == AgentAction.Defuse && type == OrderType.Defuse);
            if ((a.Action == AgentAction.Plant || a.Action == AgentAction.Defuse) && !sameTask) { a.Action = AgentAction.None; a.TaskProgress = 0f; }
            a.Order = new AgentOrder { Type = type, Target = target, Yaw = yaw };
            a.WatchesThreat = false;
            a.Path.Clear();
            a.PathGoal = new Vector3(float.NaN);
            return true;
        }

        /// <summary>Ends an agent's order without it being done: the brain decides again from the next tick (a plant or
        /// defuse it started carries on).</summary>
        public void ClearOrder(int agent)
        {
            if (agent < 0 || agent >= agents.Count || agents[agent].Order.Type == OrderType.None) return;
            var a = agents[agent];
            a.Order = default;
            a.WantsDecision = true;
        }

        void EndOrder(SimAgent a, bool done)
        {
            a.Order = default;
            a.WantsDecision = true;
            a.PathGoal = new Vector3(float.NaN);
            Event(SimEventType.OrderDone, a.Index, done ? 1 : 0);
        }

        public RoundResult RunToEnd()
        {
            while (!Over) Step();
            var r = new RoundResult
            {
                Winner = Winner, AttackersWon = Winner == AttackingTeam, Reason = EndReason, Duration = Time,
                Planted = Events.Exists(e => e.Type == SimEventType.Planted) || SpikeState == SpikeStatus.Detonated || SpikeState == SpikeStatus.Defused,
            };
            r.Kills.AddRange(Kills);
            r.Players.AddRange(startPlayers);
            foreach (var a in agents) r.Shooting.Add(a.Shooting);
            r.Tempo = Tempo;
            foreach (var a in agents) if (a.DivergedAt >= 0f) r.Diverged++;
            return r;
        }

        /// <summary>Advances one tick (1 / Options.TickRate seconds).</summary>
        public void Step()
        {
            if (Over) return;
            float dt = 1f / Options.TickRate;
            if (tempoPrevious == null)
            {
                tempoPrevious = new Vector3[agents.Count];
                tempoWasAlive = new bool[agents.Count];
                for (int i = 0; i < agents.Count; i++) { tempoPrevious[i] = agents[i].Position; tempoWasAlive[i] = agents[i].Alive; }
            }
            Time += dt;
            if (SpikeState == SpikeStatus.Planted) SpikeTimer -= dt;
            else Remaining -= dt;

            if (Source != null) FollowRecording(dt);
            perceptionClock += dt;
            if (perceptionClock >= Options.PerceptionInterval - 1e-4f)
            {
                perceptionClock = 0f;
                Perceive();
            }
            boards[0].Update(Time);
            boards[1].Update(Time);
            directors[0].Update(this);
            directors[1].Update(this);
            foreach (var a in agents)
                if (a.Alive && a.Mode == AgentMode.Ai) Think(a, dt);
            foreach (var a in agents)
                if (a.Alive && a.Mode == AgentMode.Ai && a.Action != AgentAction.Plant && a.Action != AgentAction.Defuse) Engage(a);
            ApplyHits();
            Objectives(dt);
            SampleTempo(dt);
            CheckEnd();
        }

        void SampleTempo(float dt)
        {
            tempoClock += dt;
            if (tempoClock < RoundTempo.Interval - 1e-4f) return;
            tempoClock = 0f;
            tempoPlayers.Clear();
            for (int i = 0; i < agents.Count; i++)
            {
                var a = agents[i];
                tempoPlayers.Add(new TempoPlayer { Attacking = a.Attacking, Alive = a.Alive, WasAlive = tempoWasAlive[i], Position = a.Position, Previous = tempoPrevious[i] });
                tempoPrevious[i] = a.Position;
                tempoWasAlive[i] = a.Alive;
            }
            bool planted = SpikeState == SpikeStatus.Planted || SpikeState == SpikeStatus.Defused || SpikeState == SpikeStatus.Detonated;
            Tempo.Sample(Time, Map, planted, tempoPlayers);
        }

        // ------------------------------------------------------------------ recording

        void FollowRecording(float dt)
        {
            float rt0 = SourceStart + Time - dt, rt = SourceStart + Time;
            if (rt >= sourceRound.EndTime)
            {
                // the recording has no more of this round
                foreach (var a in agents) if (a.Alive && a.Mode == AgentMode.Replay) Diverge(a);
                Source = null;
                return;
            }
            foreach (var a in agents)
            {
                if (!a.Alive || a.Mode != AgentMode.Replay) continue;
                var f = Source.Sample(a.Index, rt);
                var pos = SimSpace.FromReplay(Source, f);
                a.Moving = SimSpace.FlatDistance(pos, a.Position) / dt > 1f;
                a.Position = pos;
                a.Yaw = f.Yaw;
                a.Walking = f.Walking;
                a.Crouching = f.Crouching;
                if (f.Alive) { a.Health = f.Health; a.Armor = f.Armor + f.Shield; }
            }

            var kills = Source.Kills;
            for (; killCursor < kills.Count && kills[killCursor].Time <= rt; killCursor++)
            {
                var k = kills[killCursor];
                if (k.Time <= rt0 || k.Victim >= agents.Count) continue;
                var v = agents[k.Victim];
                var killer = k.Killer < agents.Count ? agents[k.Killer] : null;
                if (v.Alive && v.Mode == AgentMode.Replay)
                {
                    if (killer == null || killer == v) Kill(v, null, false);
                    else if (killer.Mode == AgentMode.Replay && killer.Alive) Kill(v, killer, false);
                    else Diverge(v);      // its killer is no longer where the recording had it
                }
                else if (killer != null && killer.Alive && killer.Mode == AgentMode.Replay) Diverge(killer);   // its victim is elsewhere
            }
            // deaths with no kill entry (fall damage, spike). Frames are read at the tick at or before rt, and a death
            // with a kill entry just ahead waits for it, so a kill is never mistaken for a death without a killer.
            foreach (var a in agents)
                if (a.Alive && a.Mode == AgentMode.Replay && !Source.GetFrame(Source.TickAt(rt), a.Index).Alive && !KillAhead(a.Index, rt))
                    Kill(a, null, false);

            if (spikeFromRecording && (SpikeState == SpikeStatus.Carried || SpikeState == SpikeStatus.Dropped))
            {
                int holder = SpikeHolderAt(Source, rt, out _);
                if (holder < agents.Count)
                {
                    var h = agents[holder];
                    if (h.Alive && h.Mode == AgentMode.Replay)
                    {
                        if (SpikeState != SpikeStatus.Carried || SpikeCarrier != holder) Event(SimEventType.SpikePickedUp, holder);
                        SpikeState = SpikeStatus.Carried;
                        SpikeCarrier = holder;
                    }
                    else spikeFromRecording = false;
                }
                else if (holder == VrpReplay.HolderNone && SpikeState == SpikeStatus.Carried && agents[SpikeCarrier].Mode == AgentMode.Replay)
                {
                    Event(SimEventType.SpikeDropped, SpikeCarrier);
                    SpikeState = SpikeStatus.Dropped;
                    SpikePosition = agents[SpikeCarrier].Position;
                    SpikeCarrier = -1;
                }
            }
            if (sourceRound.PlantTime > rt0 && sourceRound.PlantTime <= rt && SpikeState == SpikeStatus.Carried && sourceRound.Planter < agents.Count)
            {
                var p = agents[sourceRound.Planter];
                if (p.Alive && p.Mode == AgentMode.Replay && SpikeCarrier == p.Index)
                    Plant(p, SimSpace.FromReplay(Source, sourceRound.PlantX, sourceRound.PlantY, sourceRound.PlantZ));
            }
            if (sourceRound.DefuseTime > rt0 && sourceRound.DefuseTime <= rt && SpikeState == SpikeStatus.Planted && sourceRound.Defuser < agents.Count)
            {
                var d = agents[sourceRound.Defuser];
                if (d.Alive && d.Mode == AgentMode.Replay) Defuse(d);
            }
        }

        bool KillAhead(int victim, float rt)
        {
            var kills = Source.Kills;
            for (int i = killCursor; i < kills.Count && kills[i].Time <= rt + 0.5f; i++)
                if (kills[i].Victim == victim) return true;
            return false;
        }

        void Diverge(SimAgent a)
        {
            if (a.Mode != AgentMode.Replay) return;
            a.Mode = AgentMode.Ai;
            a.DivergedAt = Time;
            a.WantsDecision = true;
            a.Path.Clear();
            a.PathGoal = new Vector3(float.NaN);
            if (SpikeCarrier == a.Index) spikeFromRecording = false;
            Event(SimEventType.Diverged, a.Index);
        }

        static int SpikeHolderAt(VrpReplay r, float time, out float since)
        {
            int h = VrpReplay.HolderNone;
            since = -1f;
            foreach (var s in r.SpikeCarriers)
            {
                if (s.Time > time) break;
                if (s.Holder != h) since = s.Time;
                h = s.Holder;
            }
            return h;
        }

        static int LastSpikeHolderBefore(VrpReplay r, float time)
        {
            int last = -1;
            foreach (var s in r.SpikeCarriers)
            {
                if (s.Time > time) break;
                if (s.Holder < r.Players.Count) last = s.Holder;
            }
            return last;
        }

        // ------------------------------------------------------------------ perception

        void Perceive()
        {
            int n = agents.Count;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    var a = agents[i];
                    var b = agents[j];
                    if (!a.Alive || !b.Alive || a.Team == b.Team) { sees[i, j] = sees[j, i] = false; continue; }
                    float d = Vector3.Distance(a.Position, b.Position);
                    bool los = d <= Options.MaxSightRange && Clear(a.Eye, b.Eye);
                    See(a, b, los, d);
                    See(b, a, los, d);
                }
            foreach (var a in agents)
            {
                if (!a.Alive) continue;
                foreach (var b in agents)
                {
                    if (!b.Alive || b.Team == a.Team || !b.Moving || b.Walking || sees[a.Index, b.Index]) continue;
                    float d = Vector3.Distance(a.Position, b.Position);
                    if (d <= Options.FootstepRange && Time - a.LastHeard[b.Index] >= 0.5f) Hear(a, b, d, Options.FootstepRange, 0.6f, true);
                }
            }
        }

        void See(SimAgent a, SimAgent b, bool los, float d)
        {
            bool shotAtBy = b.Target == a.Index && Time - b.LastShot < 0.5f;
            bool view = los && (d < 3f || InFov(a, b.Position));
            bool noticed = view || (los && shotAtBy);
            Measure(a, b, view);
            bool was = sees[a.Index, b.Index];
            sees[a.Index, b.Index] = noticed;
            if (!noticed) return;
            var s = new Sighting { Enemy = b.Index, Position = b.Position, Time = Time, Certainty = 1f, Cue = Cue.Seen };
            a.Beliefs.Observe(s, Time, a.Profile.MemoryHalfLife);
            if (!was || Time - a.LastCallout[b.Index] >= Options.CalloutRefresh)
            {
                a.LastCallout[b.Index] = Time;
                boards[a.Team].Callout(s, a.Profile, Time, commsRng[a.Index]);
            }
            if (!was) a.WantsDecision = true;
            if (a.Mode == AgentMode.Replay && b.Mode != AgentMode.Replay) Diverge(a);
        }

        void Measure(SimAgent a, SimAgent b, bool view)
        {
            int i = a.Index, j = b.Index;
            if (view && !inView[i, j] && Time - lastInView[i, j] >= Measurements.NoticeGap)
            {
                a.Shooting.Notices++;
                a.Shooting.AimErrorSum += Math.Abs(DeltaYaw(a.Yaw, SimSpace.YawOf(b.Position - a.Position)));
                noticedAt[i, j] = Time;
            }
            if (view) lastInView[i, j] = Time;
            inView[i, j] = view;
        }

        // forcesDivergence: whether hearing a sound the recording does not have takes a replay agent off it. Footsteps do;
        // gunshots only within footstep range (a fight across the map does not change what a player does next).
        void Hear(SimAgent a, SimAgent b, float d, float range, float certainty, bool forcesDivergence)
        {
            a.LastHeard[b.Index] = Time;
            float err = a.Profile.HearingError * (d / range);
            var senses = sensesRng[a.Index];
            var off = new Vector3((float)(senses.NextDouble() * 2 - 1) * err, 0f, (float)(senses.NextDouble() * 2 - 1) * err);
            var s = new Sighting { Enemy = b.Index, Position = b.Position + off, Time = Time, Certainty = certainty * (1f - 0.5f * d / range), Cue = Cue.Heard };
            if (a.Beliefs.Observe(s, Time, a.Profile.MemoryHalfLife) && Time - a.LastCallout[b.Index] >= Options.CalloutRefresh)
            {
                a.LastCallout[b.Index] = Time;
                boards[a.Team].Callout(s, a.Profile, Time, commsRng[a.Index]);
            }
            if (forcesDivergence && a.Mode == AgentMode.Replay && b.Mode != AgentMode.Replay) Diverge(a);
        }

        void Gunshot(SimAgent shooter)
        {
            foreach (var e in agents)
            {
                if (!e.Alive || e.Team == shooter.Team || sees[e.Index, shooter.Index]) continue;
                float d = Vector3.Distance(e.Position, shooter.Position);
                if (d <= Options.GunshotRange && Time - e.LastHeard[shooter.Index] >= 0.25f) Hear(e, shooter, d, Options.GunshotRange, 0.85f, d <= Options.FootstepRange);
            }
        }

        bool InFov(SimAgent a, Vector3 p)
        {
            var to = p - a.Position;
            to.Y = 0f;
            float len = to.Length();
            if (len < 1e-3f) return true;
            float cos = Vector3.Dot(a.Forward, to / len);
            return cos >= (float)Math.Cos(Options.FovHalfAngle * Math.PI / 180.0);
        }

        /// <summary>The better of the agent's own memory and its team board for one enemy.</summary>
        internal bool Believed(SimAgent a, int enemy, out Sighting s, out float certainty)
        {
            if (BeliefSource != null)
            {
                if (!BeliefSource.Believes(a.Team, enemy, out var at, out certainty) || certainty <= 0f) { s = default; certainty = 0f; return false; }
                certainty = Math.Min(1f, certainty);
                s = new Sighting { Enemy = enemy, Position = at, Time = Time, Certainty = certainty, Cue = Cue.Callout };
                return true;
            }
            float hl = a.Profile.MemoryHalfLife;
            bool own = a.Beliefs.TryGet(enemy, out var mine);
            bool team = boards[a.Team].Board.TryGet(enemy, out var shared);
            float cm = own ? mine.CertaintyAt(Time, hl) : 0f, ct = team ? shared.CertaintyAt(Time, hl) : 0f;
            if (!own && !team) { s = default; certainty = 0f; return false; }
            s = cm >= ct ? mine : shared;
            certainty = Math.Max(cm, ct);
            return true;
        }

        /// <summary>Living enemies of team that anyone on it believes (certainty > 0.4) are within radius of p.</summary>
        internal int BelievedEnemiesNear(int team, Vector3 p, float radius)
        {
            int count = 0;
            foreach (var e in agents)
            {
                if (!e.Alive || e.Team == team) continue;
                foreach (var a in agents)
                {
                    if (a.Team != team || !a.Alive) continue;
                    if (Believed(a, e.Index, out var s, out float c) && c > 0.4f && SimSpace.FlatDistance(s.Position, p) <= radius) { count++; break; }
                }
            }
            return count;
        }

        /// <summary>Enemies of team believed (certainty > 0.4) to be committing to site: nearest to it of all sites and
        /// past 40% of the way from the attacker spawn.</summary>
        internal int BelievedEnemiesCommitting(int team, SimMap.Site site)
        {
            float reach = 0.6f * SimSpace.FlatDistance(Map.AttackerSpawn, site.Center);
            int count = 0;
            foreach (var e in agents)
            {
                if (!e.Alive || e.Team == team) continue;
                foreach (var a in agents)
                {
                    if (a.Team != team || !a.Alive || !Believed(a, e.Index, out var s, out float c) || c <= 0.4f) continue;
                    if (Map.NearestSite(s.Position) == site && SimSpace.FlatDistance(s.Position, site.Center) <= reach) { count++; break; }
                }
            }
            return count;
        }

        internal int NearestAiAttackerToSpike()
        {
            int best = -1;
            float bd = float.MaxValue;
            foreach (var a in agents)
            {
                if (!a.Alive || !a.Attacking || a.Mode != AgentMode.Ai) continue;
                float d = Vector3.Distance(a.Position, SpikePosition);
                if (d < bd) { bd = d; best = a.Index; }
            }
            return best;
        }

        bool AnyVisible(SimAgent a)
        {
            for (int j = 0; j < agents.Count; j++) if (sees[a.Index, j]) return true;
            return false;
        }

        // ------------------------------------------------------------------ decisions and movement

        void Think(SimAgent a, float dt)
        {
            if (a.Action == AgentAction.Plant || a.Action == AgentAction.Defuse) { a.Moving = false; return; }
            if (a.Order.Type != OrderType.None && FollowOrder(a, dt)) return;
            if (a.WantsDecision || Time >= a.NextDecision) Decide(a);
            bool engaged = a.Target >= 0 && sees[a.Index, a.Target];
            if (engaged && a.Action != AgentAction.FallBack) { a.Moving = false; return; }   // stop and shoot
            if (a.Action == AgentAction.Hold || (a.Action == AgentAction.Peek && AnyVisible(a)))
            {
                a.Moving = false;
                a.Yaw = a.HoldYaw;
                KeepSpacing(a, dt);
                return;
            }
            MoveTowards(a, a.Goal, dt);
            KeepSpacing(a, dt);
        }

        /// <summary>Carries out an agent's order for this tick: there at a run, stopping to fight whoever it sees (the
        /// brain is not asked). False when the order just ended: the brain decides this tick.</summary>
        bool FollowOrder(SimAgent a, float dt)
        {
            var o = a.Order;
            bool possible = o.Type == OrderType.Plant ? SpikeState == SpikeStatus.Carried && SpikeCarrier == a.Index
                : o.Type == OrderType.Defuse ? SpikeState == SpikeStatus.Planted
                : o.Type == OrderType.PickUp ? SpikeState == SpikeStatus.Dropped
                : true;
            if (!possible) { EndOrder(a, false); return false; }
            if (o.Type == OrderType.Defuse || o.Type == OrderType.PickUp) o.Target = SpikePosition;
            a.Goal = o.Target;
            if (a.Target >= 0 && sees[a.Index, a.Target]) { a.Moving = false; return true; }   // stop and shoot, then carry on

            float d = SimSpace.FlatDistance(a.Position, o.Target);
            bool there = d <= Math.Max(OrderArrival, Map.Grid.CellSize) && Math.Abs(a.Position.Y - o.Target.Y) < 1.5f;
            switch (o.Type)
            {
                case OrderType.Move when there:
                    EndOrder(a, true);
                    return false;
                case OrderType.Hold when there:
                    a.Action = AgentAction.Hold;
                    a.Moving = false;
                    a.Yaw = o.Yaw;
                    KeepSpacing(a, dt);
                    return true;
                case OrderType.Plant when there && Map.InSite(Map.NearestSite(a.Position), a.Position):
                    a.Action = AgentAction.Plant;
                    a.TaskProgress = 0f;
                    a.Moving = false;
                    a.Path.Clear();
                    Event(SimEventType.PlantStarted, a.Index);
                    return true;
                case OrderType.Defuse when Vector3.Distance(a.Position, SpikePosition) <= 1.5f:
                    a.Action = AgentAction.Defuse;
                    a.TaskProgress = 0f;
                    a.Moving = false;
                    a.Path.Clear();
                    Event(SimEventType.DefuseStarted, a.Index);
                    return true;
            }

            a.Action = AgentAction.Advance;
            a.Walking = false;
            if ((o.Type == OrderType.Defuse || o.Type == OrderType.PickUp) && d <= Map.Grid.CellSize * 1.5f && d > 0.01f)
            {
                // the spike lies anywhere, not on a cell centre: the last steps go straight to it
                var to = o.Target - a.Position;
                to.Y = 0f;
                a.Position += to / d * Math.Min(d, Options.RunSpeed * dt);
                a.Yaw = SimSpace.YawOf(to);
                a.Moving = true;
                return true;
            }
            MoveTowards(a, o.Target, dt);
            if (!a.Moving && a.Path.Count == 0)
            {
                EndOrder(a, false);         // no way there
                return false;
            }
            KeepSpacing(a, dt);
            return true;
        }

        // nudge away from teammates that are too close, staying on walkable cells; the spike carrier holds its line and
        // the others make room
        void KeepSpacing(SimAgent a, float dt)
        {
            if (SpikeState == SpikeStatus.Carried && SpikeCarrier == a.Index) return;
            var push = Vector3.Zero;
            foreach (var m in agents)
            {
                if (m == a || !m.Alive || m.Team != a.Team) continue;
                var away = a.Position - m.Position;
                away.Y = 0f;
                float d = away.Length(), spacing = Math.Max(Options.Spacing, a.Profile.Spread * 0.6f);
                if (d >= spacing) continue;
                if (d < 1e-3f)
                {
                    // exactly on top of each other: split by index
                    float ang = (a.Index * 2.399f) % 6.2832f;
                    away = new Vector3((float)Math.Cos(ang), 0f, (float)Math.Sin(ang));
                    d = 0f;
                }
                else away /= d;
                push += away * (spacing - d);
            }
            // a moving agent is never pushed back along its own path (that would freeze the marching order)
            if (a.Moving)
            {
                var fwd = a.Forward;
                float back = Vector3.Dot(push, fwd);
                if (back < 0f) push -= fwd * back;
            }
            if (push.LengthSquared() < 1e-6f) return;
            float max = Options.WalkSpeed * dt;
            if (push.Length() > max) push = Vector3.Normalize(push) * max;
            var p = a.Position + push;
            foreach (int n in Map.Grid.NodesAt(p.X, p.Z))
                if (Math.Abs(Map.Grid.Nodes[n].Y - a.Position.Y) < 1f) { a.Position = p; return; }
        }

        void Decide(SimAgent a)
        {
            a.WantsDecision = false;
            a.NextDecision = Time + a.Profile.DecisionInterval;
            var dir = directors[a.Team];
            var objective = dir.Objective(this, a, out float objRadius);
            var s = new Situation
            {
                Attacking = a.Attacking,
                SpikePlanted = SpikeState == SpikeStatus.Planted,
                CarriesSpike = SpikeState == SpikeStatus.Carried && SpikeCarrier == a.Index,
                Health = a.Durability,
                NearestThreat = float.PositiveInfinity,
                ObjectiveDistance = SimSpace.FlatDistance(a.Position, objective),
            };
            s.TimePressure = SpikeState == SpikeStatus.Planted ? Profile.Clamp01(1f - SpikeTimer / Options.SpikeDuration) : Profile.Clamp01(1f - Remaining / Options.RoundTime);
            s.Urgency = Urgency(a);
            s.AtObjective = s.ObjectiveDistance <= objRadius;

            Vector3 threatPos = default;
            float bestPriority = 0f;
            foreach (var e in agents)
            {
                if (!e.Alive || e.Team == a.Team) continue;
                if (sees[a.Index, e.Index]) s.EnemiesVisible++;
                if (!Believed(a, e.Index, out var sight, out float c) || c < 0.15f) continue;
                float d = Vector3.Distance(a.Position, sight.Position);
                if (c > 0.3f && d < 20f) s.EnemiesNear++;
                float priority = c * Math.Max(0.05f, 1f - d / 50f);
                if (priority > bestPriority) { bestPriority = priority; s.NearestThreat = d; s.ThreatCertainty = c; threatPos = sight.Position; }
            }

            float teamDist = 0f;
            int mates = 0;
            foreach (var m in agents)
            {
                if (!m.Alive || m.Team != a.Team || m == a) continue;
                if (Vector3.Distance(m.Position, a.Position) <= 15f) s.AlliesNear++;
                teamDist += SimSpace.FlatDistance(m.Position, objective);
                mates++;
            }
            s.AheadOfTeam = mates > 0 ? teamDist / mates - s.ObjectiveDistance : 0f;

            var death = lastDeath[a.Team];
            Vector3 tradePos = death.Position;
            if (Time - death.Time <= Options.TradeWindow + 2f && Vector3.Distance(a.Position, death.Position) <= a.Profile.TradeRange)
            {
                s.TradeAvailable = true;
                if (death.Killer >= 0 && agents[death.Killer].Alive && Believed(a, death.Killer, out var ks, out float kc) && kc > 0.3f) tradePos = ks.Position;
            }

            UtilityBrain.Score(s, a.Profile, a.Scores);
            a.Action = UtilityBrain.Pick(a.Scores, a.Action, a.Profile, decideRng[a.Index]);
            switch (a.Action)
            {
                case AgentAction.Advance: a.Goal = OwnSpot(a, objective); break;
                case AgentAction.Peek:
                {
                    var to = threatPos - a.Position;
                    float d = to.Length();
                    a.Goal = d > Options.PeekDistance ? a.Position + to / d * Options.PeekDistance : threatPos;
                    break;
                }
                case AgentAction.FallBack: a.Goal = Retreat(a, threatPos); break;
                case AgentAction.Trade: a.Goal = tradePos; break;
                default: a.Goal = a.Position; break;
            }
            // passive players walk (silently) near known enemies; aggressive ones (or anyone short of time) run
            a.Walking = UtilityBrain.EffectiveAggression(s, a.Profile) < 0.5f && (a.Action == AgentAction.Peek || (a.Action == AgentAction.Advance && s.NearestThreat < 25f));
            // where to look while holding: at a believed threat; else an angle real players check here (Map.Look); else the
            // objective or the attackers' way in. The angle and this player's placement error are drawn per decision.
            var lookRandom = lookRng[a.Index];
            a.LookQuantile = (float)lookRandom.NextDouble();
            a.WatchesThreat = s.ThreatCertainty > 0.15f;
            a.ThreatPosition = threatPos;
            a.LookOffset = Gaussian(lookRandom) * a.Profile.LookSpread;
            if (s.ThreatCertainty <= 0.15f && Map.Look != null && Map.Look.TryLook(a.Position, a.Attacking, null, a.LookQuantile, out float learned))
                a.HoldYaw = learned + a.LookOffset;
            else
            {
                var watch = s.ThreatCertainty > 0.15f ? threatPos : a.Attacking ? objective : (SpikeState == SpikeStatus.Planted ? SpikePosition : Map.AttackerSpawn);
                var look = watch - a.Position;
                if (look.X * look.X + look.Z * look.Z > 0.01f) a.HoldYaw = SimSpace.YawOf(look) + a.LookOffset;
            }
        }

        /// <summary>0..1: how close the time left is to the time this agent needs for its side's objective.</summary>
        float Urgency(SimAgent a)
        {
            float left, need;
            if (a.Attacking && SpikeState != SpikeStatus.Planted)
            {
                var site = Map.SiteNamed(directors[a.Team].TargetSite) ?? Map.NearestSite(a.Position);
                if (site == null) return 0f;
                left = Remaining;
                need = SimSpace.FlatDistance(a.Position, site.Center) / Options.RunSpeed + Options.PlantDuration;
            }
            else if (!a.Attacking && SpikeState == SpikeStatus.Planted)
            {
                left = SpikeTimer;
                need = SimSpace.FlatDistance(a.Position, SpikePosition) / Options.RunSpeed + Options.DefuseDuration;
                if (left < need) return 0f;   // no defuse possible any more: the director has it save
                return Options.DefenderUrgency * Profile.Clamp01(1f - (left - need - Options.UrgencyMargin) / Options.UrgencyWindow);
            }
            else return 0f;
            return Profile.Clamp01(1f - (left - need - Options.UrgencyMargin) / Options.UrgencyWindow);
        }

        static float Gaussian(Random r)
        {
            double u1 = 1.0 - r.NextDouble(), u2 = r.NextDouble();
            return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }

        // each agent's own spot around the team objective (Profile.Spread away, a fixed direction per agent), so a team
        // does not walk single file into one point. The defender closest to a planted spike goes to the spike itself.
        Vector3 OwnSpot(SimAgent a, Vector3 objective)
        {
            if (SpikeState == SpikeStatus.Planted && !a.Attacking)
            {
                bool closest = true;
                float mine = Vector3.Distance(a.Position, SpikePosition);
                foreach (var m in agents)
                    if (m != a && m.Alive && !m.Attacking && m.Mode == AgentMode.Ai && Vector3.Distance(m.Position, SpikePosition) < mine) { closest = false; break; }
                if (closest) return objective;
            }
            if (SpikeState == SpikeStatus.Dropped && a.Attacking && NearestAiAttackerToSpike() == a.Index) return objective;
            // the spike carrier executing on its site goes to the plant spots themselves, not a spot around them that can
            // lie outside the site
            if (SpikeState == SpikeStatus.Carried && SpikeCarrier == a.Index && directors[a.Team].Executing) return objective;
            double ang = a.Index * 2.399;   // golden angle: neighbours by index end up far apart
            var p = objective + new Vector3((float)Math.Cos(ang), 0f, (float)Math.Sin(ang)) * a.Profile.Spread;
            int n = Map.Grid.Nearest(p, 3f);
            if (n < 0) return objective;
            var q = Map.Grid.Position(n);
            return Math.Abs(q.Y - objective.Y) < 2f ? q : objective;
        }

        Vector3 Retreat(SimAgent a, Vector3 threat)
        {
            float mine = Vector3.Distance(a.Position, threat);
            SimAgent best = null;
            float bd = float.MaxValue;
            foreach (var m in agents)
            {
                if (!m.Alive || m.Team != a.Team || m == a || Vector3.Distance(m.Position, threat) <= mine + 3f) continue;
                float d = Vector3.Distance(m.Position, a.Position);
                if (d < bd) { bd = d; best = m; }
            }
            if (best != null) return best.Position;
            return a.Attacking ? Map.AttackerSpawn : Map.DefenderSpawn;
        }

        void MoveTowards(SimAgent a, Vector3 goal, float dt)
        {
            var g = Map.Grid;
            bool goalChanged = float.IsNaN(a.PathGoal.X) || Vector3.Distance(goal, a.PathGoal) > 2f;
            if (goalChanged || (a.PathIndex >= a.Path.Count && SimSpace.FlatDistance(a.Position, goal) > 1.5f && Time >= a.NextDecision - 0.01f))
            {
                a.PathGoal = goal;
                a.PathIndex = 0;
                if (!g.FindPath(g.Nearest(a.Position), g.Nearest(goal, 6f), a.Path)) a.Path.Clear();
            }
            float speed = a.Walking ? Options.WalkSpeed : Options.RunSpeed;
            if (a.Action == AgentAction.Advance || a.Action == AgentAction.Peek) speed *= a.Profile.Pace;
            float budget = speed * dt;
            var start = a.Position;
            while (budget > 1e-4f && a.PathIndex < a.Path.Count)
            {
                var target = g.Position(a.Path[a.PathIndex]);
                var delta = target - a.Position;
                float dist = delta.Length();
                if (dist <= budget) { a.Position = target; budget -= dist; a.PathIndex++; }
                else { a.Position += delta / dist * budget; budget = 0f; }
            }
            var moved = a.Position - start;
            a.Moving = moved.X * moved.X + moved.Z * moved.Z > 1e-4f;
            // while moving: at a believed threat (players walking towards a sound look at it); else an angle players check
            // here within MovingCone of the way it walks; else where it walks
            if (a.Moving && !(a.Target >= 0 && sees[a.Index, a.Target]) && a.WatchesThreat && SimSpace.FlatDistance(a.ThreatPosition, a.Position) > 1f)
                a.Yaw = SimSpace.YawOf(a.ThreatPosition - a.Position) + a.LookOffset;
            else if (a.Moving && !(a.Target >= 0 && sees[a.Index, a.Target]))
                a.Yaw = (Map.Look != null && Map.Look.TryLook(a.Position, a.Attacking, moved, a.LookQuantile, out float learned) ? learned : SimSpace.YawOf(moved))
                    + a.LookOffset;
        }

        // ------------------------------------------------------------------ combat

        void Engage(SimAgent a)
        {
            if (a.Target >= 0 && (!agents[a.Target].Alive || Time - a.TargetLastSeen > 1f)) a.Target = -1;
            int best = -1;
            float bd = float.MaxValue;
            for (int j = 0; j < agents.Count; j++)
            {
                if (!sees[a.Index, j]) continue;
                float d = Vector3.Distance(a.Position, agents[j].Position);
                if (d < bd) { bd = d; best = j; }
            }
            if (best >= 0 && (a.Target < 0 || !sees[a.Index, a.Target]))
            {
                if (a.Target != best)
                {
                    // holding an angle with the crosshair already there (stationary, enemy within PreAimAngle) is faster
                    // than turning onto someone; a moving shooter also has to stop first
                    var to = agents[best].Position - a.Position;
                    float angle = Math.Abs(DeltaYaw(a.Yaw, SimSpace.YawOf(to)));
                    bool preAimed = !a.Moving && a.Action == AgentAction.Hold && angle <= Options.PreAimAngle;
                    float reaction = a.Profile.ReactionTime * (0.85f + 0.3f * (float)aimRng[a.Index].NextDouble());   // no two reactions are alike
                    a.FireReadyAt = Time + (preAimed ? reaction * Options.PreAimReaction : reaction + angle / a.Profile.TurnSpeed)
                        + (a.Moving ? 0.08f : 0f);
                }
                a.Target = best;
            }
            if (a.Target < 0 || !sees[a.Index, a.Target]) return;
            a.TargetLastSeen = Time;
            if (Time < a.FireReadyAt) return;
            var t = agents[a.Target];
            a.Yaw = SimSpace.YawOf(t.Position - a.Position);
            if (Time >= a.NextShotAt) Fire(a, t);
        }

        void Fire(SimAgent a, SimAgent t)
        {
            a.NextShotAt = Time + a.Weapon.FireInterval;
            a.LastShot = Time;
            Gunshot(a);
            // the first shot after a notice: a reaction if it is at that enemy and in time; any shot ends the wait
            for (int j = 0; j < agents.Count; j++)
            {
                float at = noticedAt[a.Index, j];
                if (at < 0f) continue;
                if (j == t.Index && Time - at <= Measurements.ReactionWindow) { a.Shooting.Reactions++; a.Shooting.ReactionSum += Time - at; }
                noticedAt[a.Index, j] = -1f;
            }
            bool counts = a.Weapon != WeaponStats.Shotgun;
            if (counts) a.Shooting.Bullets++;
            float d = Vector3.Distance(a.Eye, t.Eye);
            float p = a.Profile.BaseAccuracy * a.Weapon.Accuracy / (1f + Math.Max(0f, d - 10f) / 25f);
            if (a.Moving) p *= 0.25f;
            if (t.Moving && !t.Walking) p *= 0.85f;
            if (t.Crouching) p *= 0.9f;
            p = Math.Max(0.02f, Math.Min(0.95f, p));
            var aim = aimRng[a.Index];
            if (aim.NextDouble() >= p) return;
            double region = aim.NextDouble();
            bool head = region < a.Profile.HeadshotShare, legs = !head && region < a.Profile.HeadshotShare + a.Profile.LegShare;
            float raw = head ? a.Weapon.Head : legs ? a.Weapon.Legs : a.Weapon.Body;
            if (counts) { a.Shooting.Hits++; if (head) a.Shooting.HeadHits++; else if (legs) a.Shooting.LegHits++; }
            hits.Add((t, a.Weapon.DamageAt(d, raw), a));
        }

        // shots of one tick land together, so two players who fire on the same tick can trade (no advantage for the
        // lower agent index)
        readonly List<(SimAgent target, float damage, SimAgent attacker)> hits = new List<(SimAgent, float, SimAgent)>();

        void ApplyHits()
        {
            foreach (var (t, dmg, by) in hits)
            {
                if (!t.Alive) continue;
                bool wasAlive = by.Alive;
                by.Alive = true;          // a shooter killed this tick still lands the shot it fired this tick
                Damage(t, dmg, by);
                if (!wasAlive) by.Alive = false;
            }
            hits.Clear();
        }

        void Damage(SimAgent t, float dmg, SimAgent attacker)
        {
            if (!t.Alive) return;
            if (t.Mode == AgentMode.Replay) Diverge(t);   // this hit is not in the recording
            float absorbed = Math.Min(t.Armor, dmg * Options.ArmorAbsorb);
            t.Armor -= absorbed;
            t.Health -= dmg - absorbed;
            t.Beliefs.Observe(new Sighting { Enemy = attacker.Index, Position = attacker.Position, Time = Time, Certainty = 0.9f, Cue = Cue.Heard }, Time, t.Profile.MemoryHalfLife);
            t.WantsDecision = true;
            Event(SimEventType.Hit, t.Index, attacker.Index);
            if (t.Health <= 0f) Kill(t, attacker, true);
        }

        void Kill(SimAgent v, SimAgent killer, bool simulated)
        {
            if (!v.Alive) return;
            v.Alive = false;
            v.Health = 0f;
            v.Moving = false;
            v.Action = AgentAction.None;
            v.Order = default;
            v.Target = -1;
            Kills.Add(new KillRecord { Time = Time, Killer = killer?.Index ?? -1, Victim = v.Index, KillerTeam = killer?.Team ?? -1, VictimTeam = v.Team, Simulated = simulated });
            Event(SimEventType.Kill, v.Index, killer?.Index ?? -1);
            if (killer != null)
            {
                killer.Kills++;
                // the dying player's last callout: where the killer is
                boards[v.Team].Callout(new Sighting { Enemy = killer.Index, Position = killer.Position, Time = Time, Certainty = 1f, Cue = Cue.Seen }, v.Profile, Time, commsRng[v.Index]);
            }
            lastDeath[v.Team] = new Death { Time = Time, Position = v.Position, Killer = killer?.Index ?? -1 };
            for (int j = 0; j < agents.Count; j++) { sees[v.Index, j] = false; sees[j, v.Index] = false; }
            if (SpikeState == SpikeStatus.Carried && SpikeCarrier == v.Index)
            {
                SpikeState = SpikeStatus.Dropped;
                SpikePosition = v.Position;
                SpikeCarrier = -1;
                Event(SimEventType.SpikeDropped, v.Index);
            }
            foreach (var m in agents) if (m.Alive) m.WantsDecision = true;
        }

        static float DeltaYaw(float from, float to)
        {
            float d = (to - from) % 360f;
            if (d > 180f) d -= 360f;
            if (d < -180f) d += 360f;
            return d;
        }

        // ------------------------------------------------------------------ the spike

        void Objectives(float dt)
        {
            switch (SpikeState)
            {
                case SpikeStatus.Carried:
                {
                    var c = agents[SpikeCarrier];
                    SpikePosition = c.Position;
                    if (c.Mode == AgentMode.Replay) break;   // the recording plants (FollowRecording)
                    if (c.Action == AgentAction.Plant)
                    {
                        // keep planting through a fight only when there is no time left to win it first
                        bool mustCommit = Remaining < Options.PlantDuration - c.TaskProgress + 1.5f;
                        if (c.Mode == AgentMode.Ai && c.Order.Type != OrderType.Plant && AnyVisible(c) && !mustCommit) { c.Action = AgentAction.None; c.TaskProgress = 0f; c.WantsDecision = true; break; }
                        c.Moving = false;
                        c.TaskProgress += dt;
                        if (c.TaskProgress >= Options.PlantDuration) Plant(c, c.Position);
                    }
                    else if (c.Mode == AgentMode.Ai && c.Order.Type == OrderType.None && (!AnyVisible(c) || Remaining < Options.PlantDuration + 1.5f) && Map.InSite(Map.NearestSite(c.Position), c.Position))
                    {
                        c.Action = AgentAction.Plant;
                        c.TaskProgress = 0f;
                        c.Path.Clear();
                        Event(SimEventType.PlantStarted, c.Index);
                    }
                    break;
                }
                case SpikeStatus.Dropped:
                    foreach (var a in agents)
                    {
                        if (!a.Alive || !a.Attacking || a.Mode == AgentMode.Replay || Vector3.Distance(a.Position, SpikePosition) > 1.2f) continue;
                        SpikeState = SpikeStatus.Carried;
                        SpikeCarrier = a.Index;
                        spikeFromRecording = false;
                        Event(SimEventType.SpikePickedUp, a.Index);
                        if (a.Order.Type == OrderType.PickUp) EndOrder(a, true);
                        foreach (var m in agents) if (m.Alive && m.Attacking) m.WantsDecision = true;
                        break;
                    }
                    break;
                case SpikeStatus.Planted:
                    if (SpikeTimer <= 0f)
                    {
                        SpikeState = SpikeStatus.Detonated;
                        Event(SimEventType.Detonated, -1);
                        break;
                    }
                    foreach (var a in agents)
                    {
                        if (!a.Alive || a.Attacking || a.Mode == AgentMode.Replay) continue;
                        if (a.Action == AgentAction.Defuse)
                        {
                            // keep defusing through a fight only when there is no time left to win it first
                            bool mustCommit = SpikeTimer < Options.DefuseDuration - a.TaskProgress + 1.5f;
                            if (a.Mode == AgentMode.Ai && a.Order.Type != OrderType.Defuse && AnyVisible(a) && !mustCommit) { a.Action = AgentAction.None; a.TaskProgress = 0f; a.WantsDecision = true; continue; }
                            a.Moving = false;
                            a.TaskProgress += dt;
                            if (a.TaskProgress >= Options.DefuseDuration) { Defuse(a); return; }
                        }
                        else if (a.Mode == AgentMode.Ai && a.Order.Type == OrderType.None && Vector3.Distance(a.Position, SpikePosition) <= 1.5f && !AnyVisible(a))
                        {
                            a.Action = AgentAction.Defuse;
                            a.TaskProgress = 0f;
                            a.Path.Clear();
                            Event(SimEventType.DefuseStarted, a.Index);
                        }
                    }
                    break;
            }
        }

        void Plant(SimAgent c, Vector3 at)
        {
            SpikeState = SpikeStatus.Planted;
            SpikePosition = at;
            SpikeTimer = Options.SpikeDuration;
            SpikeCarrier = -1;
            c.Action = AgentAction.None;
            c.TaskProgress = 0f;
            Event(SimEventType.Planted, c.Index);
            if (c.Order.Type == OrderType.Plant) EndOrder(c, true);
            foreach (var m in agents) if (m.Alive) m.WantsDecision = true;
        }

        void Defuse(SimAgent d)
        {
            SpikeState = SpikeStatus.Defused;
            d.Action = AgentAction.None;
            Event(SimEventType.Defused, d.Index);
            if (d.Order.Type == OrderType.Defuse) EndOrder(d, true);
        }

        void CheckEnd()
        {
            bool atk = false, def = false;
            foreach (var a in agents)
                if (a.Alive) { if (a.Attacking) atk = true; else def = true; }
            int defenders = 1 - AttackingTeam;
            if (!def) End(AttackingTeam, RoundEndReason.Elimination);
            else if (SpikeState == SpikeStatus.Detonated) End(AttackingTeam, RoundEndReason.Detonated);
            else if (SpikeState == SpikeStatus.Defused) End(defenders, RoundEndReason.Defused);
            else if (!atk && SpikeState != SpikeStatus.Planted) End(defenders, RoundEndReason.Elimination);
            else if (SpikeState != SpikeStatus.Planted && Remaining <= 0f) End(defenders, RoundEndReason.TimeExpired);
            else if (Time >= Options.MaxDuration) End(defenders, RoundEndReason.TimeExpired);
        }

        void End(int winner, RoundEndReason reason)
        {
            Over = true;
            Winner = winner;
            EndReason = reason;
        }

        void Event(SimEventType type, int agent, int other = -1) => Events.Add(new SimEvent { Time = Time, Type = type, Agent = agent, Other = other });

        static float[] Filled(int n, float v)
        {
            var a = new float[n];
            for (int i = 0; i < n; i++) a[i] = v;
            return a;
        }

        static Random[] Streams(int seed, int n, RandomStream stream)
        {
            var a = new Random[n];
            for (int i = 0; i < n; i++) a[i] = SimRandom.Create(seed, i, stream);
            return a;
        }
    }
}
