// Profile.cs - who an agent is: three 0..1 knobs and a play style. Every number the simulation derives from them
// lives here, so tuning happens in one file. Skill only changes execution (reaction, aim, memory, hearing);
// aggression and play style only change decisions; cohesion changes team play (callouts, spacing, trading).
// Execution can also be set from measurements (Accuracy, Headshots, Legshots, Reaction, Placement; ProfileFitter fills
// them from a replay): a set value replaces what Skill would give, the rest still follows Skill.
using System;

namespace ValorantReplay.Sim
{
    public enum PlayStyle : byte { Balanced = 0, Entry = 1, Support = 2, Lurker = 3, Anchor = 4 }

    public sealed class Profile
    {
        public float Skill = 0.5f;
        public float Aggression = 0.5f;
        public float Cohesion = 0.5f;
        public PlayStyle Style = PlayStyle.Balanced;
        /// <summary>How much this agent believes teammates' callouts (and an outside team blackboard), 0..1: their certainty
        /// is scaled from 0.5x (0) to 1.5x (1, capped at certain); 0.5 takes them as made. Per teammate:
        /// RoundSim.SetTrust. Nothing in the replays measures it.</summary>
        public float Trust = 0.5f;

        // measured execution (null = from Skill); simulation parameters, i.e. already converted from the measured stats
        public float? Accuracy;         // BaseAccuracy
        public float? Headshots;        // HeadshotShare
        public float? Legshots;         // LegShare
        public float? Reaction;         // ReactionTime
        public float? Placement;        // LookSpread
        public float? Wander;           // GlanceWander: how often it looks away, relative to the population

        public Profile() { }

        public Profile(float skill, float aggression, float cohesion, PlayStyle style = PlayStyle.Balanced)
        {
            Skill = Clamp01(skill); Aggression = Clamp01(aggression); Cohesion = Clamp01(cohesion); Style = style;
        }

        /// <summary>A copy with some knobs changed. Measured execution is kept, so changing skill on a measured profile
        /// only moves what is not measured; Unmeasured() drops it.</summary>
        public Profile With(float? skill = null, float? aggression = null, float? cohesion = null, PlayStyle? style = null) =>
            new Profile(skill ?? Skill, aggression ?? Aggression, cohesion ?? Cohesion, style ?? Style)
            {
                Trust = Trust,
                Accuracy = Accuracy, Headshots = Headshots, Legshots = Legshots, Reaction = Reaction, Placement = Placement, Wander = Wander,
            };

        public Profile Unmeasured() => new Profile(Skill, Aggression, Cohesion, Style) { Trust = Trust };

        public bool Measured => Accuracy.HasValue || Headshots.HasValue || Legshots.HasValue || Reaction.HasValue || Placement.HasValue || Wander.HasValue;

        public override string ToString() => $"{Style} skill {Skill:0.00} aggr {Aggression:0.00} coh {Cohesion:0.00}" +
            (Measured ? $" acc {BaseAccuracy:0.00} hs {HeadshotShare:0.00} rt {ReactionTime:0.00} conf {Confidence:0.00}" : "");

        // ------------------------------------------------ skill: execution
        /// <summary>Seconds from first seeing an enemy to the first shot (before turning).</summary>
        public float ReactionTime => Reaction ?? Lerp(0.32f, 0.17f, Skill);
        /// <summary>Crosshair placement: standard deviation (degrees) of how far off the angle it means to check an agent
        /// looks, drawn per decision. 0 = exactly on the angles players check (Map.Look).</summary>
        public float LookSpread => Placement ?? DefaultLookSpread;
        /// <summary>LookSpread of an unmeasured profile: the population's (ExecutionTransfer.Headless.PlacementFor of its
        /// AimErrorMean).</summary>
        public static float DefaultLookSpread = 4.23f;
        /// <summary>Multiplier on SimOptions.GlanceRate: crosshair discipline. Measured profiles take it from their aim error
        /// at notices against the population's (ProfileFitter), so a player who keeps the crosshair where enemies appear
        /// better than average also looks away less; 1 = the population.</summary>
        public float GlanceWander => Wander ?? 1f;
        /// <summary>Degrees per second when flicking onto a target outside the crosshair.</summary>
        public float TurnSpeed => Lerp(360f, 900f, Skill);
        /// <summary>Chance a single shot hits a standing target at 10 m with a rifle (SimOptions.AccuracyFalloff for other ranges).</summary>
        public float BaseAccuracy => Accuracy ?? Lerp(0.30f, 0.70f, Skill);
        /// <summary>Share of hits that land on the head.</summary>
        public float HeadshotShare => Headshots ?? Lerp(0.12f, 0.35f, Skill);
        /// <summary>Share of hits that land on the legs.</summary>
        public float LegShare => Legshots ?? 0.12f;
        /// <summary>Seconds for a sighting's certainty to halve.</summary>
        public float MemoryHalfLife => Lerp(4f, 10f, Skill);
        /// <summary>Metres of error placing a footstep heard at the edge of hearing range.</summary>
        public float HearingError => Lerp(7f, 2f, Skill);
        /// <summary>Seconds between re-decisions when nothing new happens.</summary>
        public float DecisionInterval => Lerp(0.7f, 0.25f, Skill);

        /// <summary>How much this player trusts their own gunfight, 0..1 (0.5 = the population): from measured accuracy,
        /// headshot share and reaction where there are measurements, else Skill. Confident players swing for trades and
        /// wide-peek; unsure ones hold the angle or jiggle for information.</summary>
        public float Confidence
        {
            get
            {
                float zs = (Skill - 0.5f) / 0.2f;     // the knobs are 0.5 + 0.2 z
                float za = Accuracy.HasValue ? (Accuracy.Value - ConfidenceAccuracy) / ConfidenceAccuracySd : zs;
                float zh = Headshots.HasValue ? (Headshots.Value - ConfidenceHeadshots) / ConfidenceHeadshotsSd : zs;
                float zr = Reaction.HasValue ? (ConfidenceReaction - Reaction.Value) / ConfidenceReactionSd : zs;
                return Clamp01(0.5f + 0.2f * (0.45f * za + 0.25f * zh + 0.3f * zr));
            }
        }
        /// <summary>Population mean and spread of the measured execution parameters Confidence is scored against: 170 fitted
        /// player-matches (vrpsim profiles over the published replays, Nov 2026).</summary>
        public static float ConfidenceAccuracy = 0.289f, ConfidenceAccuracySd = 0.040f;
        public static float ConfidenceHeadshots = 0.194f, ConfidenceHeadshotsSd = 0.057f;
        public static float ConfidenceReaction = 0.412f, ConfidenceReactionSd = 0.026f;

        /// <summary>Tempo while advancing or peeking, as a share of run speed: aggressive players take first contact,
        /// passive ones play second in.</summary>
        public float Pace => Lerp(0.8f, 1f, Aggression);

        // ------------------------------------------------ comms: cohesion (willing to talk) and skill (game sense: good calls)
        // At skill and cohesion 0.5 timing and drops are what cohesion alone gave before sense joined in.
        /// <summary>Seconds before a callout reaches the team board: recognising what you see and saying it, mostly sense.</summary>
        public float CalloutDelay => Lerp(2.5f, 0.4f, 0.4f * Cohesion + 0.6f * Skill);
        /// <summary>Chance a callout is never made: mostly whether the player talks at all (cohesion).</summary>
        public float CalloutDropChance => Lerp(0.5f, 0.05f, 0.7f * Cohesion + 0.3f * Skill);
        /// <summary>Metres a callout's position is off ("he's B main" vs "one behind the box").</summary>
        public float CalloutError => Lerp(5f, 0.5f, Skill);
        /// <summary>Share of the caller's certainty a callout carries.</summary>
        public float CalloutCertainty => Lerp(0.85f, 0.95f, Skill);
        /// <summary>Multiplier on SimOptions.CalloutRefresh: how often a caller updates an enemy it keeps seeing or hearing.</summary>
        public float CalloutRefreshScale => Lerp(1.5f, 0.5f, Skill);

        // ------------------------------------------------ cohesion: team play
        /// <summary>How far away a teammate's death still pulls this agent in to trade.</summary>
        public float TradeRange => Lerp(8f, 22f, Cohesion);
        /// <summary>How far ahead of the team's centre this agent is willing to be while advancing.</summary>
        public float MaxLead => Lerp(18f, 5f, Cohesion);
        /// <summary>Distance kept from teammates, and how far from the team's objective its own spot is: tight teams play
        /// close enough to trade, loose ones spread out.</summary>
        public float Spread => Lerp(SpreadLoose, SpreadTight, Cohesion);
        /// <summary>Spread at cohesion 0 and 1. Set so simulated rounds keep the recorded distance to the nearest teammate
        /// (18.3 m on the corpus, ProfileFitter.TeammateMean; 22..9 gives 18.4 from every barrier drop, 7..2 gave 13.9 and
        /// with it twice the recorded trades, attackers converting first kills 77% vs 64% and winning 66% vs 55%).</summary>
        public static float SpreadLoose = 22f, SpreadTight = 9f;

        // ------------------------------------------------ presets
        public static Profile Default => new Profile();
        public static Profile Entry => new Profile(0.6f, 0.85f, 0.5f, PlayStyle.Entry);
        public static Profile Support => new Profile(0.5f, 0.4f, 0.8f, PlayStyle.Support);
        public static Profile Lurker => new Profile(0.55f, 0.55f, 0.2f, PlayStyle.Lurker);
        public static Profile Anchor => new Profile(0.55f, 0.2f, 0.6f, PlayStyle.Anchor);

        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
        public static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    }
}
