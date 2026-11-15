// Bots.cs - example bots: a skill tier (how well it shoots and reads the round) times an archetype (how it decides and
// plays with its team), and five-player lineups of them. Tiers are set in the corpus's own units: real-game shooting
// stats with Measurements' definitions, turned into simulation parameters through an ExecutionTransfer exactly as
// ProfileFitter does for real players, so Average plays like the replays' population and the other tiers sit around it.
// The Skill knob of a tier is its game sense (memory, hearing, decision rate, reads, flick speed); Bots.Split pairs one
// tier's aim with another's sense.
using System;
using System.Collections.Generic;

namespace ValorantReplay.Sim
{
    public enum BotTier : byte { Novice = 0, Casual = 1, Average = 2, Skilled = 3, Elite = 4, Pro = 5 }

    /// <summary>A tier's execution as a real player's stats (Measurements' definitions) plus its game sense.</summary>
    public sealed class TierStats
    {
        public float Accuracy;      // hits per bullet
        public float HeadHits;      // share of hits on the head
        public float LegHits;       // share of hits on the legs
        public float Reaction;      // seconds from noticing an enemy to the first shot
        public float AimError;      // degrees off the enemy when noticing it (crosshair placement)
        public float Sense;         // Profile.Skill
    }

    /// <summary>How a bot decides: play style, aggression and cohesion (the decision knobs of Profile).</summary>
    public sealed class BotArchetype
    {
        public string Name = "";
        public PlayStyle Style;
        public float Aggression = 0.5f, Cohesion = 0.5f;
        /// <summary>Profile.Trust: how much it acts on teammates' callouts rather than its own eyes and ears.</summary>
        public float Trust = 0.5f;
        public string Description = "";

        public override string ToString() => Name;

        /// <summary>Takes first contact, plays ahead of the team.</summary>
        public static readonly BotArchetype Entry = new BotArchetype { Name = "Entry", Style = PlayStyle.Entry, Aggression = 0.85f, Cohesion = 0.5f, Trust = 0.5f,
            Description = "takes first contact and the space behind it" };
        /// <summary>Plays off the team: close spacing, fast callouts, first to trade.</summary>
        public static readonly BotArchetype Initiator = new BotArchetype { Name = "Initiator", Style = PlayStyle.Support, Aggression = 0.45f, Cohesion = 0.85f, Trust = 0.8f,
            Description = "stays close to the team, calls fast, trades first" };
        /// <summary>No style bias, middle knobs.</summary>
        public static readonly BotArchetype Flex = new BotArchetype { Name = "Flex", Style = PlayStyle.Balanced, Aggression = 0.5f, Cohesion = 0.55f, Trust = 0.5f,
            Description = "no bias, middle of every knob" };
        /// <summary>Plays away from the team, falls back rather than trading.</summary>
        public static readonly BotArchetype Lurker = new BotArchetype { Name = "Lurker", Style = PlayStyle.Lurker, Aggression = 0.55f, Cohesion = 0.15f, Trust = 0.2f,
            Description = "plays apart from the team, rarely trades" };
        /// <summary>Holds an angle on site and does not leave it.</summary>
        public static readonly BotArchetype Anchor = new BotArchetype { Name = "Anchor", Style = PlayStyle.Anchor, Aggression = 0.2f, Cohesion = 0.6f, Trust = 0.6f,
            Description = "holds angles, does not peek" };
        /// <summary>Passive but tight with the team: lets others take contact, then trades them.</summary>
        public static readonly BotArchetype Baiter = new BotArchetype { Name = "Baiter", Style = PlayStyle.Support, Aggression = 0.15f, Cohesion = 0.7f, Trust = 0.8f,
            Description = "lets others take contact, then trades" };
        /// <summary>Solo-queue duelist: all aggression, ignores the team.</summary>
        public static readonly BotArchetype Rusher = new BotArchetype { Name = "Rusher", Style = PlayStyle.Entry, Aggression = 1f, Cohesion = 0.1f, Trust = 0.1f,
            Description = "full aggression, ignores the team" };

        public static readonly BotArchetype[] All = { Entry, Initiator, Flex, Lurker, Anchor, Baiter, Rusher };
    }

    /// <summary>One bot: a name for reports and the Profile the simulation plays.</summary>
    public sealed class Bot
    {
        public string Name = "";
        public BotArchetype Archetype;
        public Profile Profile;
        public override string ToString() => Name;
    }

    /// <summary>Five bots that play together. The order is only a label: BotMatch rotates which recorded player each one
    /// replaces.</summary>
    public sealed class BotLineup
    {
        public string Name = "";
        public Bot[] Bots = Array.Empty<Bot>();
        public override string ToString() => Name;
    }

    public static class Bots
    {
        /// <summary>Real-game stats per tier: the corpus population (ProfileFitter's means and the transfer's reaction and
        /// aim error) moved by the tier's percentile of the between-player spread. The spreads are measured on the corpus
        /// (170 player-matches) with sampling noise taken out by split-half reliability (odd vs even rounds of each
        /// player-match): one match shows 0.40 of accuracy, 0.63 of head share, 0.39 of leg share, 0.25 of reaction and
        /// 0.21 of aim error, so the between-player spread is much narrower than single matches suggest.
        /// A tier is equally good at everything; Novice and Pro (p5, p98) extrapolate past most of the corpus.</summary>
        public static TierStats Stats(BotTier tier, ExecutionTransfer transfer = null)
        {
            var x = transfer ?? ProfileFitter.Transfer;
            float z = Percentile[(int)tier];
            return new TierStats
            {
                Accuracy = Math.Max(0.03f, ProfileFitter.AccuracyMean + z * AccuracySd),
                HeadHits = Math.Max(0.02f, ProfileFitter.HeadHitMean + z * HeadHitSd),
                LegHits = Math.Max(0.02f, ProfileFitter.LegHitMean - z * LegHitSd),
                Reaction = x.ReactionMean - z * ReactionSd,
                AimError = Math.Max(0f, x.AimErrorMean - z * AimErrorSd),
                Sense = Sense[(int)tier],
            };
        }

        /// <summary>Between-player standard deviations on the corpus: sqrt(split-half reliability) x observed sd (see Stats).</summary>
        public static float AccuracySd = 0.042f, HeadHitSd = 0.086f, LegHitSd = 0.038f, ReactionSd = 0.054f, AimErrorSd = 2.5f;
        /// <summary>Each tier's place in the population, as a standard normal z: p5, p25, p50, p75, p90, p98.</summary>
        static readonly float[] Percentile = { -1.65f, -0.67f, 0f, 0.67f, 1.28f, 2.05f };
        /// <summary>Profile.Skill per tier. Nothing in the replays measures it, and calibration finds it has no effect on
        /// outcomes (vrpsim calibrate --neutral sense), so these are only spread evenly.</summary>
        static readonly float[] Sense = { 0.1f, 0.3f, 0.5f, 0.65f, 0.8f, 0.95f };

        public static readonly BotTier[] Tiers = { BotTier.Novice, BotTier.Casual, BotTier.Average, BotTier.Skilled, BotTier.Elite, BotTier.Pro };

        /// <summary>The profile of a bot of this tier and archetype; transfer as in ProfileFitter (null = its Transfer, which
        /// must match the SimOptions the bots play with).</summary>
        public static Profile Make(BotTier tier, BotArchetype archetype, ExecutionTransfer transfer = null) => Split(tier, tier, archetype, transfer);

        /// <summary>Aim from one tier (accuracy, hit regions, reaction, crosshair placement), game sense from another.</summary>
        public static Profile Split(BotTier aim, BotTier sense, BotArchetype archetype, ExecutionTransfer transfer = null)
        {
            var x = transfer ?? ProfileFitter.Transfer;
            var a = Stats(aim, x);
            return new Profile(Stats(sense, x).Sense, archetype.Aggression, archetype.Cohesion, archetype.Style)
            {
                Trust = archetype.Trust,
                Accuracy = Profile.Clamp01(a.Accuracy / x.Accuracy),
                Headshots = a.HeadHits,
                Legshots = a.LegHits,
                Reaction = Math.Max(0.08f, a.Reaction - x.ReactionOffset),
                Placement = x.PlacementFor(a.AimError),
                Wander = ProfileFitter.WanderFor(a.AimError, x),
            };
        }

        public static Bot Of(BotTier tier, BotArchetype archetype, ExecutionTransfer transfer = null) =>
            new Bot { Name = $"{tier} {archetype.Name}", Archetype = archetype, Profile = Make(tier, archetype, transfer) };

        /// <summary>A lineup of one tier. cohesion, when given, replaces every archetype's (solo queue vs a premade).</summary>
        public static BotLineup Lineup(string name, BotTier tier, BotArchetype[] archetypes, float? cohesion = null, ExecutionTransfer transfer = null) =>
            Lineup(name, tier, tier, archetypes, cohesion, transfer);

        public static BotLineup Lineup(string name, BotTier aim, BotTier sense, BotArchetype[] archetypes, float? cohesion = null, ExecutionTransfer transfer = null)
        {
            var bots = new Bot[archetypes.Length];
            for (int i = 0; i < bots.Length; i++)
            {
                var p = Split(aim, sense, archetypes[i], transfer);
                if (cohesion != null) p = p.With(cohesion: cohesion);
                bots[i] = new Bot { Name = archetypes[i].Name, Archetype = archetypes[i], Profile = p };
            }
            return new BotLineup { Name = name, Bots = bots };
        }

        // ------------------------------------------------ compositions
        /// <summary>One of each role.</summary>
        public static readonly BotArchetype[] Standard = { BotArchetype.Entry, BotArchetype.Initiator, BotArchetype.Flex, BotArchetype.Lurker, BotArchetype.Anchor };
        /// <summary>Everyone wants first contact.</summary>
        public static readonly BotArchetype[] Aggro = { BotArchetype.Rusher, BotArchetype.Entry, BotArchetype.Entry, BotArchetype.Initiator, BotArchetype.Flex };
        /// <summary>Slow, hold and trade.</summary>
        public static readonly BotArchetype[] Passive = { BotArchetype.Anchor, BotArchetype.Anchor, BotArchetype.Baiter, BotArchetype.Initiator, BotArchetype.Lurker };
        /// <summary>Two duelists that ignore the team, a lurker, a baiter.</summary>
        public static readonly BotArchetype[] SoloQueue = { BotArchetype.Rusher, BotArchetype.Rusher, BotArchetype.Lurker, BotArchetype.Baiter, BotArchetype.Flex };

        /// <summary>Lineups of one tier with different compositions and team play, for style matchups.</summary>
        public static List<BotLineup> StyleLineups(BotTier tier = BotTier.Average, ExecutionTransfer transfer = null) => new List<BotLineup>
        {
            Lineup("Standard", tier, Standard, transfer: transfer),
            Lineup("Premade", tier, Standard, cohesion: 0.95f, transfer: transfer),
            Lineup("Aggro", tier, Aggro, transfer: transfer),
            Lineup("Passive", tier, Passive, transfer: transfer),
            Lineup("SoloQueue", tier, SoloQueue, transfer: transfer),
        };
    }
}
