// RoundSetup.cs - what a RoundSim starts from (AgentSetup per player, RoundSetup for the round) and what RunToEnd
// returns (RoundResult).
using System.Collections.Generic;
using System.Numerics;
using ValorantReplay.Playback;

namespace ValorantReplay.Sim
{
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
        /// <summary>The teams' plans (templates: the round plays a copy). Null = drawn from SimOptions.AttackPlans /
        /// DefensePlans, else DefaultAttack / DefaultDefense.</summary>
        public AttackPlan Attack;
        public DefensePlan Defense;
        public string TargetSite;               // null = the attack plan picks
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
        public string PlantSite = "";           // the site the spike went down on ("" = no plant)
        public int Diverged;                    // agents that left the recording
        /// <summary>The plans the teams played (Name) and how often each rotated.</summary>
        public string AttackPlan = "", DefensePlan = "";
        public int AttackRotations, DefenseRotations;
        public List<KillRecord> Kills = new List<KillRecord>();
        public List<int> Players = new List<int>();   // agents alive at the start
        /// <summary>Each agent's shooting counters (accuracy, hit regions, notices, reactions), indexed by agent, with
        /// Measurements' definitions.</summary>
        public List<PlayerTally> Shooting = new List<PlayerTally>();
        /// <summary>Execute and plant times and movement speed, with the same sampler as recorded rounds.</summary>
        public RoundTempo Tempo;
    }
}
