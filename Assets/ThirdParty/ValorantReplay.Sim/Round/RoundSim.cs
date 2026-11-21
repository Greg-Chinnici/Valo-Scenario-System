// RoundSim.cs - a headless simulation of the rest of one round. Start it from any moment of a .vrp (FromReplay) or
// from scratch; agents follow the recording until they perceive something that did not happen in it, then the AI
// takes over. Deterministic for a given seed; each agent draws from its own random streams (SimRandom). Unity drives it
// with Step() and renders Agents.
//
// What is modelled: movement on the NavGrid, line of sight, footsteps and gunshots, per-agent beliefs and team
// callouts, gunfights (reaction time, accuracy, damage, armor), the spike (carry, drop, plant, defuse, detonate).
// What is not (yet): abilities, buying (each team's buy is fixed for the round: Economy), recoil, jumping, exact hitboxes.
using System;
using System.Collections.Generic;
using System.Numerics;
using ValorantReplay.Playback;

namespace ValorantReplay.Sim
{
    public enum SpikeStatus : byte { None = 0, Carried = 1, Dropped = 2, Planted = 3, Defused = 4, Detonated = 5 }

    public sealed partial class RoundSim
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
        readonly bool[,] onlyShotAt;        // noticed only because it is shooting at us, not in view
        readonly NoticeMeasure notices;
        readonly CalloutTrust trust;
        // independent streams (SimRandom): round-level draws, and per agent aim / decisions / senses / callouts
        readonly Random roundRng;
        readonly Random[] aimRng, decideRng, sensesRng, commsRng, lookRng;
        readonly List<int> startPlayers = new List<int>();
        float perceptionClock;

        /// <summary>Pacing so far (RoundTempo), sampled every RoundTempo.Interval from the start.</summary>
        public readonly RoundTempo Tempo = new RoundTempo();
        readonly TempoSampler tempoSampler;

        readonly float[] buy = new float[2];    // average loadout of each team's living players at the start

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
            aimRng = SimRandom.CreateAll(seed, n, RandomStream.Aim);
            decideRng = SimRandom.CreateAll(seed, n, RandomStream.Decide);
            sensesRng = SimRandom.CreateAll(seed, n, RandomStream.Senses);
            commsRng = SimRandom.CreateAll(seed, n, RandomStream.Comms);
            lookRng = SimRandom.CreateAll(seed, n, RandomStream.Look);
            agents = new List<SimAgent>(n);
            sees = new bool[n, n];
            onlyShotAt = new bool[n, n];
            notices = new NoticeMeasure(n);
            trust = new CalloutTrust(n);
            tempoSampler = new TempoSampler(Tempo);
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
                    LastCallout = SimMath.Filled(n, -100f), LastHeard = SimMath.Filled(n, -100f), HoldYaw = s.Yaw,
                };
                a.Loadout = s.Loadout >= 0 ? s.Loadout : a.Weapon.Cost + WeaponStats.ArmorPrice(s.Armor);
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
                int alive = 0;
                foreach (var a in agents) if (a.Alive && a.Team == t) { buy[t] += a.Loadout; alive++; }
                if (alive > 0) buy[t] /= alive;
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
            directors[AttackingTeam].Setup(this, round, roundRng);
            directors[1 - AttackingTeam].Setup(this, round, roundRng);
        }

        public TeamBlackboard Board(int team) => boards[team];

        /// <summary>How much better team is armed than the other, -1..1: the difference of the average loadouts of the
        /// players alive at the start over Options.EconomyScale (a full buy against a pistol eco is about +0.9, a force
        /// against a full buy about -0.5). Loadouts are public at the start of a round, so both teams know it.</summary>
        public float Economy(int team) => Math.Max(-1f, Math.Min(1f, (buy[team] - buy[1 - team]) / Options.EconomyScale));

        /// <summary>Average loadout (credits) of team's players alive at the start.</summary>
        public float Buy(int team) => buy[team];
        public TeamDirector Director(int team) => team >= 0 && team < 2 ? directors[team] : null;
        public bool Sees(int agent, int other) => sees[agent, other];

        /// <summary>Replaces the NavGrid's approximate line of sight (eye to eye), e.g. with raycasts against the real map
        /// and smokes in Unity. Null = Map.Grid.LineOfSight.</summary>
        public Func<Vector3, Vector3, bool> SightTest { get; set; }

        bool Clear(Vector3 from, Vector3 to) => SightTest != null ? SightTest(from, to) : Map.Grid.LineOfSight(from, to);

        /// <summary>An outside team knowledge model for every decision (e.g. Unity's knowledge blackboard), replacing or
        /// supplementing the agents' own beliefs and the callout board (BeliefSourceMode). Null = the simulation's own.</summary>
        public IBeliefSource BeliefSource { get; set; }
        /// <summary>How BeliefSource combines with the agents' own knowledge. Replace: decisions read only it (every agent
        /// on a team knows the same). Supplement: each agent keeps its own beliefs (its memory, its hearing) and the
        /// callouts it trusts, as without a source, and the source adds what it knows on top, weighted by the agent's
        /// trust like a callout.</summary>
        public BeliefSourceMode BeliefSourceMode { get; set; } = BeliefSourceMode.Replace;

        /// <summary>How much reader believes caller's callouts, 0..1, overriding reader's Profile.Trust (NaN = back to it).</summary>
        public void SetTrust(int reader, int caller, float trust) => this.trust.Set(reader, caller, trust);

        public RoundResult RunToEnd()
        {
            while (!Over) Step();
            var r = new RoundResult
            {
                Winner = Winner, AttackersWon = Winner == AttackingTeam, Reason = EndReason, Duration = Time,
                Planted = Events.Exists(e => e.Type == SimEventType.Planted) || SpikeState == SpikeStatus.Detonated || SpikeState == SpikeStatus.Defused,
            };
            if (r.Planted) r.PlantSite = Map.NearestSite(SpikePosition)?.Name ?? "";
            var attack = directors[AttackingTeam];
            var defense = directors[1 - AttackingTeam];
            r.AttackPlan = attack.PlanName;
            r.DefensePlan = defense.PlanName;
            r.AttackRotations = attack.Rotations;
            r.DefenseRotations = defense.Rotations;
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
            tempoSampler.Start(agents);
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
            directors[0].Update();
            directors[1].Update();
            foreach (var a in agents)
                if (a.Alive && a.Mode == AgentMode.Ai) { Think(a, dt); Glance(a); }
            foreach (var a in agents)
                if (a.Alive && a.Mode == AgentMode.Ai && a.Action != AgentAction.Plant && a.Action != AgentAction.Defuse) Engage(a);
            ApplyHits();
            Objectives(dt);
            bool planted = SpikeState == SpikeStatus.Planted || SpikeState == SpikeStatus.Defused || SpikeState == SpikeStatus.Detonated;
            tempoSampler.Advance(dt, Time, Map, planted, agents);
            CheckEnd();
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
    }
}
