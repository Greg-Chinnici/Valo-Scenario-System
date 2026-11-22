// TeamDirector.cs - runs a team's plan for the round. The plan (AttackPlan or DefensePlan) decides where the team
// plays; the director adds what every plan shares, which is the spike: the attacker nearest a dropped spike goes for
// it, and after the plant attackers guard it while defenders retake or, out of time, save. The director only hands
// each agent an objective; whether the agent goes there is the agent's own decision (UtilityBrain). It also logs the
// plan's calls (execute, rotate, retake) as they happen, whichever plan makes them.
using System;
using System.Numerics;

namespace ValorantReplay.Sim
{
    public sealed class TeamDirector
    {
        public readonly int Team;
        public readonly bool Attacking;
        /// <summary>The attackers' plan this round (null on defence).</summary>
        public AttackPlan Attack { get; private set; }
        /// <summary>The defenders' plan this round (null on attack).</summary>
        public DefensePlan Defense { get; private set; }

        public string PlanName => Attacking ? Attack?.Name ?? "" : Defense?.Name ?? "";
        /// <summary>Where the attack is going ("" on defence, or before a plan has decided).</summary>
        public string TargetSite => Attack?.TargetSite ?? "";
        public bool Executing => Attack != null && Attack.Executing;
        /// <summary>The attack has shown where it is going, for real or as a fake.</summary>
        public bool Committed => Attack != null && Attack.Committed;
        public int Rotations => Attacking ? Attack?.Rotations ?? 0 : Defense?.Rotations ?? 0;
        public bool Retaking => Defense != null && Defense.Retaking;

        PlanContext context;
        bool loggedExecute, loggedRetake;
        int loggedRotations;

        public TeamDirector(int team, bool attacking)
        {
            Team = team;
            Attacking = attacking;
        }

        /// <summary>Round start: this side's plan from the round setup, else drawn from the options' mix, else the
        /// default one; each round plays its own copy.</summary>
        internal void Setup(RoundSim sim, RoundSetup round, Random rng)
        {
            context = new PlanContext(sim, Team, rng);
            if (Attacking)
            {
                Attack = (round.Attack ?? sim.Options.AttackPlans?.Pick(rng) ?? new DefaultAttack()).Clone();
                Attack.Begin(context, round.TargetSite);
            }
            else
            {
                Defense = (round.Defense ?? sim.Options.DefensePlans?.Pick(rng) ?? new DefaultDefense()).Clone();
                Defense.Begin(context);
            }
            LogCalls();
        }

        internal void Update()
        {
            bool planted = context.Sim.SpikeState == SpikeStatus.Planted;
            if (Attacking) { if (!planted) Attack.Update(context); }
            else if (planted) Defense.UpdateRetake(context);
            else Defense.Update(context);
            LogCalls();
        }

        void LogCalls()
        {
            if (Executing && !loggedExecute)
            {
                loggedExecute = true;
                Log(SimEventType.Executed, TargetSite);
            }
            if (Rotations > loggedRotations)
            {
                loggedRotations = Rotations;
                Log(SimEventType.Rotated, Attacking ? TargetSite : Defense.RotatedTo);
            }
            if (Retaking && !loggedRetake)
            {
                loggedRetake = true;
                Log(SimEventType.Retake, null);
            }
        }

        void Log(SimEventType type, string site) =>
            context.Sim.Log(new SimEvent { Type = type, Agent = -1, Other = -1, Team = Team, Site = site });

        /// <summary>Where this agent should be now, and how close counts as there.</summary>
        internal Vector3 Objective(SimAgent a, out float radius)
        {
            var sim = context.Sim;
            if (sim.SpikeState == SpikeStatus.Planted)
            {
                // a defender who can no longer reach the spike and defuse in time saves instead of dying for nothing.
                // (Saving when outnumbered was tried: at 2 or 3 down it made detonations 48% / 30% vs 8% recorded.)
                if (!Attacking && sim.SpikeTimer < SimSpace.FlatDistance(a.Position, sim.SpikePosition) / sim.Options.RunSpeed + sim.Options.DefuseDuration)
                {
                    radius = 8f;
                    return sim.Map.DefenderSpawn;
                }
                if (Attacking) { radius = 3f; return sim.SpikePosition; }
                return Defense.RetakeObjective(context, a, out radius);
            }
            if (!Attacking) return Defense.Objective(context, a, out radius);
            if (sim.SpikeState == SpikeStatus.Dropped && sim.NearestAiAttackerToSpike() == a.Index) { radius = 1f; return sim.SpikePosition; }
            return Attack.Objective(context, a, out radius);
        }
    }
}
