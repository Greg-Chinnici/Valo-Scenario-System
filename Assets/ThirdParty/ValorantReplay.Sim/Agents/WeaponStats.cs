// WeaponStats.cs - what each gun does: damage by range and hit region, fire rate, accuracy.
using System;

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
        public static readonly WeaponStats Outlaw = new WeaponStats { Name = "outlaw", FireInterval = 0.36f, Head = 238f, Body = 140f, Legs = 119f, Accuracy = 1.2f };
        public static readonly WeaponStats Marshal = new WeaponStats { Name = "marshal", FireInterval = 0.67f, Head = 202f, Body = 101f, Legs = 85f, Accuracy = 1.2f };
        public static readonly WeaponStats MachineGun = new WeaponStats { Name = "machine gun", FireInterval = 0.08f, Head = 95f, Body = 38f, Legs = 32f, Accuracy = 0.75f };
        public static readonly WeaponStats Shotgun = new WeaponStats { Name = "shotgun", FireInterval = 0.4f, Head = 150f, Body = 120f, Legs = 100f, Accuracy = 1.1f, FalloffStart = 8f, FalloffEnd = 15f, FalloffMin = 0.25f };
        public static readonly WeaponStats Revolver = new WeaponStats { Name = "revolver", FireInterval = 0.25f, Head = 159f, Body = 55f, Legs = 47f, Accuracy = 0.85f };
        public static readonly WeaponStats Pistol = new WeaponStats { Name = "pistol", FireInterval = 0.15f, Head = 78f, Body = 26f, Legs = 22f, Accuracy = 0.85f, FalloffStart = 30f, FalloffEnd = 50f, FalloffMin = 0.85f };
        public static readonly WeaponStats Ghost = new WeaponStats { Name = "ghost", FireInterval = 0.15f, Head = 105f, Body = 30f, Legs = 25f, Accuracy = 0.9f, FalloffStart = 30f, FalloffEnd = 50f, FalloffMin = 0.83f };

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
