// WeaponStats.cs - what each gun does: damage by range and hit region, fire rate, accuracy, the range it wins fights at,
// and what guns and armor cost (a team's buy, RoundSim.Economy).
using System;
using System.Collections.Generic;
using ValorantReplay.Playback;

namespace ValorantReplay.Sim
{
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
        /// <summary>Metres up to which this gun holds its own against a rifle; RangeFit falls off beyond it.</summary>
        public float Range = 50f;
        /// <summary>Closer than this the gun is awkward (scoped snipers); 0 = no minimum.</summary>
        public float MinRange;
        /// <summary>Typical price of the category (credits); Price(class) has each gun's own.</summary>
        public int Cost;

        public float DamageAt(float distance, float baseDamage)
        {
            if (distance <= FalloffStart) return baseDamage;
            float t = Math.Min(1f, (distance - FalloffStart) / Math.Max(0.01f, FalloffEnd - FalloffStart));
            return baseDamage * (1f + (FalloffMin - 1f) * t);
        }

        /// <summary>How well the gun suits a fight at distance metres, 0..1: 1 within Range (and past MinRange), falling to
        /// 0.3 at twice Range, 0.6 well inside MinRange.</summary>
        public float Effectiveness(float distance)
        {
            if (distance < MinRange) return Lerp(0.6f, 1f, distance / MinRange);
            if (distance <= Range) return 1f;
            return Lerp(1f, 0.3f, (distance - Range) / Range);
        }

        static float Lerp(float a, float b, float t) => a + (b - a) * Math.Max(0f, Math.Min(1f, t));

        public static readonly WeaponStats Rifle = new WeaponStats { Name = "rifle", FireInterval = 0.1f, Head = 156f, Body = 39f, Legs = 33f, Cost = 2900 };
        public static readonly WeaponStats Vandal = new WeaponStats { Name = "vandal", FireInterval = 0.103f, Head = 160f, Body = 40f, Legs = 34f, Cost = 2900 };
        public static readonly WeaponStats Burst = new WeaponStats { Name = "burst rifle", FireInterval = 0.11f, Head = 115f, Body = 35f, Legs = 29f, Accuracy = 0.9f, Range = 30f, Cost = 2050 };
        public static readonly WeaponStats Marksman = new WeaponStats { Name = "marksman", FireInterval = 0.21f, Head = 195f, Body = 65f, Legs = 49f, Accuracy = 1.15f, Cost = 2250 };
        public static readonly WeaponStats Smg = new WeaponStats { Name = "smg", FireInterval = 0.075f, Head = 78f, Body = 26f, Legs = 22f, Accuracy = 0.85f, FalloffStart = 15f, FalloffEnd = 30f, FalloffMin = 0.85f, Range = 15f, Cost = 1600 };
        public static readonly WeaponStats Sniper = new WeaponStats { Name = "sniper", FireInterval = 1.6f, Head = 255f, Body = 150f, Legs = 127f, Accuracy = 1.25f, Range = 80f, MinRange = 8f, Cost = 4700 };
        public static readonly WeaponStats Outlaw = new WeaponStats { Name = "outlaw", FireInterval = 0.36f, Head = 238f, Body = 140f, Legs = 119f, Accuracy = 1.2f, Range = 70f, MinRange = 8f, Cost = 2400 };
        public static readonly WeaponStats Marshal = new WeaponStats { Name = "marshal", FireInterval = 0.67f, Head = 202f, Body = 101f, Legs = 85f, Accuracy = 1.2f, Range = 60f, MinRange = 6f, Cost = 950 };
        public static readonly WeaponStats MachineGun = new WeaponStats { Name = "machine gun", FireInterval = 0.08f, Head = 95f, Body = 38f, Legs = 32f, Accuracy = 0.75f, Range = 25f, Cost = 3200 };
        public static readonly WeaponStats Shotgun = new WeaponStats { Name = "shotgun", FireInterval = 0.4f, Head = 150f, Body = 120f, Legs = 100f, Accuracy = 1.1f, FalloffStart = 8f, FalloffEnd = 15f, FalloffMin = 0.25f, Range = 8f, Cost = 850 };
        public static readonly WeaponStats Revolver = new WeaponStats { Name = "revolver", FireInterval = 0.25f, Head = 159f, Body = 55f, Legs = 47f, Accuracy = 0.85f, Range = 30f, Cost = 800 };
        public static readonly WeaponStats Pistol = new WeaponStats { Name = "pistol", FireInterval = 0.15f, Head = 78f, Body = 26f, Legs = 22f, Accuracy = 0.85f, FalloffStart = 30f, FalloffEnd = 50f, FalloffMin = 0.85f, Range = 15f, Cost = 0 };
        public static readonly WeaponStats Ghost = new WeaponStats { Name = "ghost", FireInterval = 0.15f, Head = 105f, Body = 30f, Legs = 25f, Accuracy = 0.9f, FalloffStart = 30f, FalloffEnd = 50f, FalloffMin = 0.83f, Range = 25f, Cost = 500 };

        // ---------------------------------------------------------------- prices
        // Approximate and patch-dependent; what matters is telling a full buy from a force and an eco.
        static readonly Dictionary<string, int> Prices = new Dictionary<string, int>
        {
            ["BasePistol"] = 0, ["SawedOffShotgun"] = 300, ["AutomaticPistol"] = 450, ["LugerPistol"] = 500, ["CompactPistol"] = 600,
            ["RevolverPistol"] = 800, ["Vector"] = 1100, ["SubMachineGun_MP5"] = 1600, ["PumpShotgun"] = 850, ["AutomaticShotgun"] = 1850,
            ["AssaultRifle_Burst"] = 2050, ["DMR"] = 2250, ["AssaultRifle_ACR"] = 2900, ["AssaultRifle_AK"] = 2900, ["BattleRifle"] = 2900,
            ["LeverSniperRifle"] = 950, ["DS_Gun"] = 2400, ["BoltSniper"] = 4700, ["LightMachineGun"] = 1600, ["HeavyMachineGun"] = 3200,
        };

        /// <summary>Price of a gun class (e.g. "AssaultRifle_AK"); -1 when unknown (new guns).</summary>
        public static int Price(string weaponClass) => weaponClass != null && Prices.TryGetValue(weaponClass, out int p) ? p : -1;

        /// <summary>Price of armor by type.</summary>
        public static int ArmorPrice(ArmorType armor) => armor == ArmorType.Heavy ? 1000 : armor == ArmorType.Regen ? 650 : armor == ArmorType.Light ? 400 : 0;

        /// <summary>Price of armor by how much is left (heavy 50, light 25): what a setup without a loadout can tell.</summary>
        public static int ArmorPrice(float armor) => armor > 25.5f ? 1000 : armor > 0f ? 400 : 0;

        /// <summary>What a recorded loadout is worth: guns and armor (-1 when there is no loadout).</summary>
        public static int LoadoutValue(PlayerLoadout l)
        {
            if (l == null) return -1;
            int Gun(string c)
            {
                if (string.IsNullOrEmpty(c)) return 0;
                int p = Price(c);
                return p >= 0 ? p : For(c).Cost;
            }
            return Gun(l.Primary) + Gun(l.Sidearm) + ArmorPrice(l.Armor);
        }

        /// <summary>Best guess for an internal weapon class; unknown or empty classes count as a rifle.</summary>
        public static WeaponStats For(string weaponClass)
        {
            string c = weaponClass ?? "";
            bool Has(string k) => c.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0;
            // the replays' classes: Phantom AssaultRifle_ACR, Vandal _AK, Bulldog _Burst, Guardian DMR, Spectre
            // SubMachineGun_MP5, Stinger Vector, Operator BoltSniper, Marshal LeverSniperRifle, Outlaw DS_Gun, Odin
            // HeavyMachineGun, Ares LightMachineGun, Ghost LugerPistol, Classic BasePistol, Sheriff RevolverPistol
            if (Has("LeverSniper")) return Marshal;
            if (Has("DS_Gun")) return Outlaw;
            if (Has("Sniper") || Has("Operator")) return Sniper;
            if (Has("Shotgun")) return Shotgun;
            if (Has("SubMachine") || Has("SMG") || Has("Vector")) return Smg;     // before MachineGun: "SubMachineGun" contains it
            if (Has("MachineGun") || Has("LMG")) return MachineGun;
            if (Has("Revolver")) return Revolver;
            if (Has("Luger")) return Ghost;
            if (Has("Pistol")) return Pistol;
            if (Has("DMR")) return Marksman;
            if (Has("Burst")) return Burst;
            if (Has("_AK")) return Vandal;
            return Rifle;
        }
    }
}
