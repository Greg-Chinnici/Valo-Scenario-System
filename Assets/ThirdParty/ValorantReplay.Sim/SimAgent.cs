// SimAgent.cs - one player in a RoundSim. Mode decides who drives it: the recording (until something it perceives
// no longer matches what happened), the AI, or the user (Unity).
using System.Collections.Generic;
using System.Numerics;

namespace ValorantReplay.Sim
{
    public enum AgentMode : byte
    {
        Ai = 0,         // UtilityBrain + director
        Replay = 1,     // follows the recording until it diverges, then switches to Ai
        External = 2,   // driven from outside (RoundSim.SetExternalState / ExternalFire), e.g. the user in Unity
    }

    /// <summary>What a commander told an AI agent to do (RoundSim.Command). While it lasts the agent does that instead of
    /// what the brain would pick, still stopping to fight whoever it sees; once it is done, or cannot be done any more,
    /// the brain decides again.</summary>
    public enum OrderType : byte
    {
        None = 0,
        Move = 1,       // go to Target, then the brain decides
        Hold = 2,       // stand at Target looking along Yaw until the order is cleared or replaced
        Plant = 3,      // carry the spike to Target (on a site) and plant it there
        Defuse = 4,     // go to the planted spike and defuse it
        PickUp = 5,     // go to the dropped spike and pick it up
    }

    public struct AgentOrder
    {
        public OrderType Type;
        public Vector3 Target;
        public float Yaw;               // Hold: where to look
    }

    public sealed class SimAgent
    {
        public int Index;               // into RoundSim.Agents; equals the replay's player index for FromReplay
        public int Team;
        public bool Attacking;
        public string Name = "";
        public Profile Profile = Profile.Default;
        public AgentMode Mode;
        public WeaponStats Weapon = WeaponStats.Rifle;
        public Beliefs Beliefs;

        public Vector3 Position;
        public float Yaw;               // degrees, Unity Y euler
        public bool Alive = true;
        public float Health = 100f, Armor;
        public bool Moving, Walking, Crouching;

        public AgentAction Action;
        public Vector3 Goal;
        public string AssignedSite = "";
        /// <summary>The commander's order this agent is carrying out (Type None = the brain decides).</summary>
        public AgentOrder Order;
        /// <summary>Last decision's scores, in UtilityBrain.Choices order (for debugging overlays).</summary>
        public readonly float[] Scores = new float[UtilityBrain.Choices.Length];
        /// <summary>Sim time this agent left the recording; -1 = never.</summary>
        public float DivergedAt = -1f;
        public int Kills;
        /// <summary>Shooting counters with Measurements' definitions (accuracy, hit regions, notices, reactions).</summary>
        public readonly PlayerTally Shooting = new PlayerTally();

        /// <summary>The enemy this agent is engaging, or -1.</summary>
        public int Engaging => Target;
        /// <summary>Sim time of the last shot fired (-100 = none yet), for tracers and sounds.</summary>
        public float LastShotTime => LastShot;
        /// <summary>Seconds into the current plant or defuse (0 when doing neither).</summary>
        public float TaskSeconds => TaskProgress;

        public Vector3 Eye => Position + new Vector3(0f, Crouching ? 0.35f : 0.65f, 0f);
        public Vector3 Forward => SimSpace.Forward(Yaw);
        public float Durability => Profile.Clamp01((Health + Armor) / 150f);

        // ------------------------------------------------ RoundSim state
        internal float NextDecision;
        internal readonly List<int> Path = new List<int>();
        internal int PathIndex;
        internal Vector3 PathGoal = new Vector3(float.NaN);
        internal int Target = -1;           // enemy being engaged
        internal float FireReadyAt, NextShotAt, TargetLastSeen;
        internal float LastShot = -100f;
        internal float TaskProgress;        // plant/defuse seconds
        internal float HoldYaw;             // where to look while holding
        internal float LookQuantile = 0.5f; // which of the angles players check here (LookModel), drawn per decision
        internal float LookOffset;          // this decision's crosshair placement error, degrees
        internal bool WatchesThreat;        // this decision believes in a threat at ThreatPosition (looked at while moving)
        internal Vector3 ThreatPosition;
        internal float[] LastCallout, LastHeard;
        internal Vector3 PrevPosition;
        internal bool WantsDecision;

        public override string ToString() => $"{Index} {Name} team {Team} {(Attacking ? "atk" : "def")} {Mode} {(Alive ? Action.ToString() : "dead")}";
    }

    public struct KillRecord
    {
        public float Time;
        public int Killer, Victim;          // agent / player indices; Killer -1 = no player (spike, fall)
        public int KillerTeam, VictimTeam;
        public bool Simulated;              // false = taken from the recording
    }

    /// <summary>Hit: Agent was damaged by Other (for a host's own senses: damage indicators, hit sounds). PlantStarted /
    /// DefuseStarted: Agent began planting / defusing (the spike makes noise from then on). OrderDone: Agent's order is
    /// over, Other 1 = carried out, 0 = could not be (the spike moved on, no way there); not raised for a cleared order
    /// or a death.</summary>
    public enum SimEventType : byte { Kill = 1, Diverged = 2, Planted = 3, Defused = 4, SpikeDropped = 5, SpikePickedUp = 6, Detonated = 7, Hit = 8, PlantStarted = 9, DefuseStarted = 10, OrderDone = 11 }

    public struct SimEvent
    {
        public float Time;
        public SimEventType Type;
        public int Agent, Other;            // Kill: victim, killer; Diverged/Spike*: agent; Other -1 if unused
        public override string ToString() => $"{Time,6:0.00} {Type} {Agent}{(Other >= 0 ? " <- " + Other : "")}";
    }
}
