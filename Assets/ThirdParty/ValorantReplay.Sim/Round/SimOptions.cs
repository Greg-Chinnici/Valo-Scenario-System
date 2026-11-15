// SimOptions.cs - the simulation's constants: perception, movement, the spike, gunfights, game sense. Most are fitted
// against recorded rounds (see each one's note); vrpsim's --set overrides any of them by name.
using System;

namespace ValorantReplay.Sim
{
    public sealed class SimOptions
    {
        /// <summary>The plans teams play when the round setup names none: one weighted draw per round (Plans.AttackMix
        /// builds one from text). Null = DefaultAttack / DefaultDefense, which the constants below are calibrated with.</summary>
        public PlanPicker<AttackPlan> AttackPlans;
        public PlanPicker<DefensePlan> DefensePlans;

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
        /// <summary>Profile.Spread multiplier once the spike is planted (the recorded spacing it is set from is measured
        /// before the plant). Swept on the corpus at 200 runs: 0.6 predicts winners best (Brier 0.214 vs 0.219 at 0.3,
        /// 0.218 at 1), though it retakes too often (defused 33% vs 18% recorded; 0.3 gives 27%, 1 gives 36%).</summary>
        public float PostPlantSpread = 0.6f;
        /// <summary>Aim streaks: within a spray at one target (shots no more than SprayGap apart), the chance a bullet hits
        /// after the previous one hit, over the chance after it missed. Real aim is on or off the target, so hits come in
        /// runs: recorded rifles hit 40.8% after a hit and 11.5% after a miss (3.55; 2,296 and 9,128 bullets), first
        /// bullets at the average. A player's average accuracy is kept: AimStreaks only decides how hits cluster.
        /// 1 = every bullet independent.</summary>
        public float AimStreaks = 3.55f;
        public float SprayGap = 0.25f;
        /// <summary>Hit chance with distance: x (10 m / distance)^AccuracyFalloff, distance at least AccuracyNear. Recorded
        /// first shots after a notice hit 43% at 0-10 m, 31% at 10-20, 22% at 20-35, 25% beyond (n 134).</summary>
        public float AccuracyFalloff = 0.4f;
        public float AccuracyNear = 4f;
        /// <summary>Flicks: the first shot at a newly engaged enemy lands off target by a half-normal draw with mean
        /// MissBase + MissPerDegree x the turn it needed (at most MissMax), past the target (overswing) with chance
        /// Overswing, and hits that much less often: x (1 + MissHitSlope (MissMean - miss)). Recorded first shots after a
        /// notice (vrpsim duels): mean miss 2.6 / 4.9 / 8.5 deg after 0-5 / 5-15 / 15-30 deg flicks, overshot 33-47% of
        /// flicks over 5 deg, hit 31 / 26 / 21%; 4.0 deg on average. The miss shows in the shooter's yaw for that shot.</summary>
        public float MissBase = 2.2f;
        public float MissPerDegree = 0.4f;
        public float MissMax = 9f;
        public float MissMean = 4f;
        public float MissHitSlope = 0.055f;
        public float Overswing = 0.4f;
        /// <summary>Reaction times are lognormal around Profile.ReactionTime with this sigma (recorded notice to first
        /// aimed shot: median 0.43 s, middle half 0.25-0.62 s; 0.7 gives 0.45 s, 0.30-0.60 s in vrpsim duels).</summary>
        public float ReactionSpread = 0.7f;
        /// <summary>Seconds to find a shooter one only knows of because it is shooting at you (not in view), on top of
        /// reacting and turning. With GlanceRate 0.15 the first to notice wins 70% / 69% of duels after a 0.25-0.5 / 0.5-1.5 s
        /// head start (recorded 73% / 70%; 56% / 57% before either existed).</summary>
        public float LocateDelay = 0.8f;
        /// <summary>Wandering eyes: an AI agent out of a fight glances GlanceMin..GlanceMax deg away for 0.3-1 s, on average
        /// GlanceRate x 2 (1 - Profile.Skill) x Profile.GlanceWander times a second (GlanceRate at average game sense and
        /// crosshair discipline, almost never at the top),
        /// unless its game sense (chance Profile.Skill) keeps its eyes on a threat it believes in. Being caught looking away makes one-sided duels (recorded 18% of duels, 13% at 0.15, 8% without); more glances
        /// mostly turn same-tick duels (recorded 74%; 68% at 0.15, 61% at 0.3) into short head starts real duels rarely have.</summary>
        public float GlanceRate = 0.15f;
        public float GlanceMin = 60f;
        public float GlanceMax = 150f;
        /// <summary>Game sense in movement: an AI agent's path costs (1 + RouteCare x Skill x exposure) per metre, where
        /// exposure sums, over its most pressing believed enemies (MaxRouteThreats, within ExposureRange), certainty x
        /// nearness for every node that enemy could see (NavGrid line of sight). 0 = everyone takes the shortest path.
        /// Off by default: at 3 it made no difference to who wins (Pro vs Novice sense 49%) and simulations ~2.8x slower.</summary>
        public float RouteCare = 0f;
        /// <summary>Taking corners wide: AI paths cost (1 + CornerCost (1 - clearance / CornerMargin)) per metre on nodes
        /// closer than CornerMargin to a wall (NavGrid.Clearance), so agents swing wide instead of hugging the inside of a
        /// corner and see round it from further away (recorded duels open at 0-10 m 12% of the time, agents on shortest
        /// paths 33%, 14% at CornerCost 6 / margin 3 m). Scaled by 1 - urgency; orders and the spike carrier in an execute
        /// take the direct line. 0 = shortest paths.</summary>
        public float CornerCost = 6f;
        public float CornerMargin = 3f;
        public float ExposureRange = 35f;
        public int MaxRouteThreats = 3;
        /// <summary>Game sense in pacing: advancing or peeking with a believed enemy within StealthRange, an agent walks
        /// (no footsteps) when Skill x (1 - urgency) exceeds StealthSense, whatever its aggression.</summary>
        public float StealthSense = 0.5f;
        public float StealthRange = 35f;
    }
}
