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

        // measured execution (null = from Skill); simulation parameters, i.e. already converted from the measured stats
        public float? Accuracy;         // BaseAccuracy
        public float? Headshots;        // HeadshotShare
        public float? Legshots;         // LegShare
        public float? Reaction;         // ReactionTime
        public float? Placement;        // LookSpread

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
                Accuracy = Accuracy, Headshots = Headshots, Legshots = Legshots, Reaction = Reaction, Placement = Placement,
            };

        public Profile Unmeasured() => new Profile(Skill, Aggression, Cohesion, Style);

        public bool Measured => Accuracy.HasValue || Headshots.HasValue || Legshots.HasValue || Reaction.HasValue || Placement.HasValue;

        public override string ToString() => $"{Style} skill {Skill:0.00} aggr {Aggression:0.00} coh {Cohesion:0.00}" +
            (Measured ? $" acc {BaseAccuracy:0.00} hs {HeadshotShare:0.00} rt {ReactionTime:0.00}" : "");

        // ------------------------------------------------ skill: execution
        /// <summary>Seconds from first seeing an enemy to the first shot (before turning).</summary>
        public float ReactionTime => Reaction ?? Lerp(0.32f, 0.17f, Skill);
        /// <summary>Crosshair placement: standard deviation (degrees) of how far off the angle it means to check an agent
        /// looks, drawn per decision. 0 = exactly on the angles players check (Map.Look).</summary>
        public float LookSpread => Placement ?? DefaultLookSpread;
        /// <summary>LookSpread of an unmeasured profile: the population's (ExecutionTransfer.Headless.PlacementFor of its
        /// AimErrorMean).</summary>
        public static float DefaultLookSpread = 8.3f;
        /// <summary>Degrees per second when flicking onto a target outside the crosshair.</summary>
        public float TurnSpeed => Lerp(360f, 900f, Skill);
        /// <summary>Chance a single shot hits a standing target at 10 m with a rifle.</summary>
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

        /// <summary>Tempo while advancing or peeking, as a share of run speed: aggressive players take first contact,
        /// passive ones play second in.</summary>
        public float Pace => Lerp(0.8f, 1f, Aggression);

        // ------------------------------------------------ cohesion: team play
        /// <summary>Seconds before a callout reaches the team board.</summary>
        public float CalloutDelay => Lerp(2.5f, 0.4f, Cohesion);
        /// <summary>Chance a callout is never made.</summary>
        public float CalloutDropChance => Lerp(0.5f, 0.05f, Cohesion);
        /// <summary>How far away a teammate's death still pulls this agent in to trade.</summary>
        public float TradeRange => Lerp(8f, 22f, Cohesion);
        /// <summary>How far ahead of the team's centre this agent is willing to be while advancing.</summary>
        public float MaxLead => Lerp(18f, 5f, Cohesion);
        /// <summary>Distance kept from teammates, and how far from the team's objective its own spot is: tight teams play
        /// close enough to trade, loose ones spread out.</summary>
        public float Spread => Lerp(7f, 2f, Cohesion);

        // ------------------------------------------------ presets
        public static Profile Default => new Profile();
        public static Profile Entry => new Profile(0.6f, 0.85f, 0.5f, PlayStyle.Entry);
        public static Profile Support => new Profile(0.5f, 0.4f, 0.8f, PlayStyle.Support);
        public static Profile Lurker => new Profile(0.55f, 0.55f, 0.2f, PlayStyle.Lurker);
        public static Profile Anchor => new Profile(0.55f, 0.2f, 0.6f, PlayStyle.Anchor);

        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
        public static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    }

    /// <summary>Gun stats by category. Replays name weapons by internal class ("AssaultRifle_AK"), so the category is
    /// guessed from keywords in the class; the numbers are close to the live game but not exact.</summary>
    public sealed class WeaponStats
    {
        public string Name = "";
        public float FireInterval;          // seconds between shots
        public float Head, Body, Legs;      // damage per hit
        public float Accuracy = 1f;         // multiplier on the shooter's base accuracy
        public float FalloffStart = 1000f;  // beyond FalloffEnd damage is multiplied by FalloffMin
        public float FalloffEnd = 1000f;
        public float FalloffMin = 1f;

        public float DamageAt(float distance, float baseDamage)
        {
            if (distance <= FalloffStart) return baseDamage;
            float t = Math.Min(1f, (distance - FalloffStart) / Math.Max(0.01f, FalloffEnd - FalloffStart));
            return baseDamage * (1f + (FalloffMin - 1f) * t);
        }

        public static readonly WeaponStats Rifle = new WeaponStats { Name = "rifle", FireInterval = 0.1f, Head = 156f, Body = 39f, Legs = 33f };
        public static readonly WeaponStats Vandal = new WeaponStats { Name = "vandal", FireInterval = 0.103f, Head = 160f, Body = 40f, Legs = 34f };
        public static readonly WeaponStats Burst = new WeaponStats { Name = "burst rifle", FireInterval = 0.11f, Head = 115f, Body = 35f, Legs = 29f, Accuracy = 0.9f };
        public static readonly WeaponStats Marksman = new WeaponStats { Name = "marksman", FireInterval = 0.21f, Head = 195f, Body = 65f, Legs = 49f, Accuracy = 1.15f };
        public static readonly WeaponStats Smg = new WeaponStats { Name = "smg", FireInterval = 0.075f, Head = 78f, Body = 26f, Legs = 22f, Accuracy = 0.85f, FalloffStart = 15f, FalloffEnd = 30f, FalloffMin = 0.85f };
        public static readonly WeaponStats Sniper = new WeaponStats { Name = "sniper", FireInterval = 1.6f, Head = 255f, Body = 150f, Legs = 127f, Accuracy = 1.25f };
        public static readonly WeaponStats MachineGun = new WeaponStats { Name = "machine gun", FireInterval = 0.08f, Head = 95f, Body = 38f, Legs = 32f, Accuracy = 0.75f };
        public static readonly WeaponStats Shotgun = new WeaponStats { Name = "shotgun", FireInterval = 0.4f, Head = 150f, Body = 120f, Legs = 100f, Accuracy = 1.1f, FalloffStart = 8f, FalloffEnd = 15f, FalloffMin = 0.25f };
        public static readonly WeaponStats Revolver = new WeaponStats { Name = "revolver", FireInterval = 0.25f, Head = 159f, Body = 55f, Legs = 47f, Accuracy = 0.85f };
        public static readonly WeaponStats Pistol = new WeaponStats { Name = "pistol", FireInterval = 0.15f, Head = 78f, Body = 26f, Legs = 22f, Accuracy = 0.85f, FalloffStart = 30f, FalloffEnd = 50f, FalloffMin = 0.85f };

        /// <summary>Best guess for an internal weapon class; unknown or empty classes count as a rifle.</summary>
        public static WeaponStats For(string weaponClass)
        {
            string c = weaponClass ?? "";
            bool Has(string k) => c.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0;
            if (Has("Sniper") || Has("Operator")) return Sniper;
            if (Has("Shotgun")) return Shotgun;
            if (Has("MachineGun") || Has("LMG")) return MachineGun;
            if (Has("SubMachine") || Has("SMG")) return Smg;
            if (Has("Revolver")) return Revolver;
            if (Has("Pistol")) return Pistol;
            if (Has("DMR")) return Marksman;
            if (Has("Burst")) return Burst;
            if (Has("_AK")) return Vandal;
            return Rifle;
        }
    }
}
