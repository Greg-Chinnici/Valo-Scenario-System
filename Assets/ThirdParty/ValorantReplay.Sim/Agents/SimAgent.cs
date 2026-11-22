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

    /// <summary>How an agent peeks (AgentAction.Peek) or swings for a trade (AgentAction.Trade).</summary>
    public enum PeekStyle : byte
    {
        None = 0,
        Wide = 1,       // run out wide of the angle: the holder has to turn further, the peeker shoots off a run
        Slice = 2,      // walk round the angle with the crosshair on the believed spot: pre-aimed, slow and silent
        Jiggle = 3,     // step out for a look and straight back: information, rarely a fight
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
        /// <summary>What its guns and armor were worth at the start (credits): the team's buy, RoundSim.Economy.</summary>
        public int Loadout;
        public Beliefs Beliefs;

        public Vector3 Position;
        public float Yaw;               // degrees, Unity Y euler
        public bool Alive = true;
        public float Health = 100f, Armor;
        public bool Moving, Walking, Crouching;

        public AgentAction Action;
        /// <summary>How it peeks or swings while Action is Peek or Trade (None otherwise).</summary>
        public PeekStyle PeekStyle => Action == AgentAction.Peek || Action == AgentAction.Trade ? Swing : PeekStyle.None;
        public Vector3 Goal;
        /// <summary>The enemy its current action is about: the believed threat it holds on, peeks or backs off from, the
        /// killer it trades; -1 = none. Set at each decision.</summary>
        public int Focus = -1;
        /// <summary>Where its team's director wants it at its last decision, and how close counts as there.</summary>
        public Vector3 Objective;
        public float ObjectiveRadius;
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
        internal float PrevShot = -100f;    // last aimed shot (Fire), for aim streaks
        internal int SprayTarget = -1;      // who that shot was at, and whether it hit
        internal bool SprayHit;
        internal float EngageAngle = -1f;   // degrees turned onto the enemy just engaged, until its first bullet
        internal float FlickSign = 1f;      // which way that turn went
        internal float NextGlance, GlanceUntil = -1f, GlanceOffset, GlanceApplied, YawWithGlance;
        internal float Urgency;             // at the last decision (Situation.Urgency)
        internal string HoldSite;           // the site HoldSpot was drawn for (Map.Holds)
        internal Vector3 HoldSpot;
        internal float TaskProgress;        // plant/defuse seconds
        internal float HoldYaw;             // where to look while holding
        internal float LookQuantile = 0.5f; // which of the angles players check here (LookModel), drawn per decision
        internal float LookOffset;          // this decision's crosshair placement error, degrees
        internal bool WatchesThreat;        // this decision believes in a threat at ThreatPosition (looked at while moving)
        internal Vector3 ThreatPosition;
        internal float[] LastCallout, LastHeard;
        internal PeekStyle Swing;           // PeekStyle, kept while the peek or trade lasts
        internal float LastKillTime = -100f;
        internal Vector3 LastKillPosition;  // where its last victim fell
        internal Vector3 PeekOrigin;        // Jiggle: where it stepped out from, and goes back to
        internal bool PeekReturning;
        internal float JiggleEnds;          // a jiggle that has not made it back by then ends where it is
        internal Vector3 PrevPosition;
        internal bool WantsDecision;
        // what the event log last said it decided, and the objective it last logged (SimEventType.Decided, Objective)
        internal AgentAction LoggedAction;
        internal PeekStyle LoggedSwing;
        internal int LoggedFocus = -1;
        internal Vector3 LoggedObjective = new Vector3(float.NaN);

        public override string ToString() => $"{Index} {Name} team {Team} {(Attacking ? "atk" : "def")} {Mode} {(Alive ? Action.ToString() : "dead")}";
    }
}
