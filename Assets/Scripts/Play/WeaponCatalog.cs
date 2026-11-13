using System.Collections.Generic;

namespace ValoReplay.Play
{
    /// <summary>
    /// Gun behaviour by replay weapon class. Numbers are approximate public Valorant stats (damage at close range, one
    /// falloff step) plus a simple spread/recoil model; tune freely. Unknown classes get <see cref="Fallback"/>.
    /// </summary>
    public sealed class WeaponStats
    {
        public string Class, Name;
        public bool Sidearm, Automatic, Melee;
        public float FireRate;                      // shots per second
        public int Magazine, Reserve;
        public float ReloadTime = 2.5f, EquipTime = 0.75f;
        public float Head, Body, Legs;              // damage per bullet/pellet up close
        public float FalloffDistance = 1000f;       // metres; beyond it damage is multiplied by FalloffMultiplier
        public float FalloffMultiplier = 1f;
        public int Pellets = 1;
        public float Spread = 0.25f;                // degrees, standing still, first shot
        public float SpreadPerShot = 0.35f;         // added per shot while spraying
        public float MaxSpread = 4f;
        public float MoveSpread = 3f;               // added while running
        public float Recoil = 0.45f;                // camera kick per shot, degrees up
        public float Range = 150f;

        public float Damage(Region region, float distance)
        {
            float d = region == Region.Head ? Head : region == Region.Legs ? Legs : Body;
            return distance > FalloffDistance ? d * FalloffMultiplier : d;
        }
    }

    public enum Region { Head, Body, Legs }

    public static class WeaponCatalog
    {
        public const string KnifeClass = "Ability_Melee_Base";

        private static readonly Dictionary<string, WeaponStats> Stats = new Dictionary<string, WeaponStats>();

        public static readonly WeaponStats Fallback = new WeaponStats
        {
            Class = "", Name = "Gun", FireRate = 5f, Magazine = 12, Reserve = 36, Head = 90, Body = 30, Legs = 25, Spread = 0.4f,
        };

        public static readonly WeaponStats Knife = new WeaponStats
        {
            Class = KnifeClass, Name = "Knife", Melee = true, FireRate = 1.5f, Magazine = 0, Reserve = 0, Head = 50, Body = 50, Legs = 50,
            Range = 2.2f, Spread = 0f, SpreadPerShot = 0f, MoveSpread = 0f, Recoil = 0f, EquipTime = 0.4f,
        };

        static WeaponCatalog()
        {
            // sidearms
            Add("BasePistol", "Classic", true, false, 6.75f, 12, 36, 1.75f, 78, 26, 22, 30f, 0.85f, spread: 0.4f, recoil: 0.5f);
            Add("SawedOffShotgun", "Shorty", true, false, 3.33f, 2, 10, 1.75f, 22, 11, 9, 9f, 0.5f, pellets: 12, spread: 4f, recoil: 1.5f);
            Add("AutomaticPistol", "Frenzy", true, true, 10f, 13, 26, 1.5f, 78, 26, 22, 20f, 0.8f, spread: 0.45f, recoil: 0.55f);
            Add("LugerPistol", "Ghost", true, false, 6.75f, 15, 45, 1.5f, 105, 30, 25, 30f, 0.85f, spread: 0.3f, recoil: 0.45f);
            Add("RevolverPistol", "Sheriff", true, false, 4f, 6, 24, 2.25f, 159, 55, 46, 30f, 0.92f, spread: 0.25f, recoil: 1.6f);
            // SMGs / shotguns
            Add("Vector", "Stinger", false, true, 16f, 20, 60, 2.25f, 67, 27, 23, 15f, 0.93f, spread: 0.65f, recoil: 0.35f);
            Add("SubMachineGun_MP5", "Spectre", false, true, 13.33f, 30, 90, 2.25f, 78, 26, 22, 20f, 0.85f, spread: 0.4f, recoil: 0.35f);
            Add("PumpShotgun", "Bucky", false, false, 1.1f, 5, 10, 2.5f, 40, 20, 17, 8f, 0.65f, pellets: 15, spread: 3.2f, recoil: 2f);
            Add("AutomaticShotgun", "Judge", false, true, 3.5f, 7, 21, 2.2f, 34, 17, 14, 10f, 0.6f, pellets: 12, spread: 3.5f, recoil: 1.2f);
            // rifles
            Add("AssaultRifle_Burst", "Bulldog", false, true, 10f, 24, 72, 2.5f, 116, 35, 29, spread: 0.3f, recoil: 0.45f);
            Add("DMR", "Guardian", false, false, 5.25f, 12, 36, 2.5f, 195, 65, 49, spread: 0.1f, recoil: 0.9f);
            Add("AssaultRifle_ACR", "Phantom", false, true, 11f, 30, 60, 2.5f, 156, 39, 33, 15f, 0.9f, spread: 0.2f, recoil: 0.45f);
            Add("AssaultRifle_AK", "Vandal", false, true, 9.75f, 25, 50, 2.5f, 160, 40, 34, spread: 0.25f, recoil: 0.55f);
            // snipers / heavies
            Add("LeverSniperRifle", "Marshal", false, false, 1.5f, 5, 10, 2.5f, 202, 101, 85, spread: 0.05f, recoil: 2.5f, moveSpread: 5f);
            Add("BoltSniper", "Operator", false, false, 0.6f, 5, 10, 3.7f, 255, 150, 127, spread: 0.05f, recoil: 3f, moveSpread: 6f);
            Add("LightMachineGun", "Ares", false, true, 13f, 50, 100, 3.25f, 72, 30, 25, 30f, 0.9f, spread: 0.6f, recoil: 0.3f);
            Add("HeavyMachineGun", "Odin", false, true, 12f, 100, 200, 5f, 95, 38, 32, 30f, 0.9f, spread: 0.7f, recoil: 0.3f);
            Stats[KnifeClass] = Knife;
        }

        private static void Add(string cls, string name, bool sidearm, bool auto, float rate, int mag, int reserve, float reload,
                                float head, float body, float legs, float falloff = 1000f, float falloffMult = 1f,
                                int pellets = 1, float spread = 0.3f, float recoil = 0.45f, float moveSpread = 3f)
        {
            Stats[cls] = new WeaponStats
            {
                Class = cls, Name = name, Sidearm = sidearm, Automatic = auto, FireRate = rate, Magazine = mag, Reserve = reserve,
                ReloadTime = reload, Head = head, Body = body, Legs = legs, FalloffDistance = falloff, FalloffMultiplier = falloffMult,
                Pellets = pellets, Spread = spread, Recoil = recoil, MoveSpread = moveSpread,
                SpreadPerShot = auto ? 0.35f : 0.8f, MaxSpread = pellets > 1 ? spread : 4f,
            };
        }

        public static WeaponStats Get(string cls)
        {
            if (string.IsNullOrEmpty(cls)) return null;
            if (Stats.TryGetValue(cls, out var s)) return s;
            return new WeaponStats
            {
                Class = cls, Name = ValoReplay.Replay.WeaponNames.Pretty(cls), Sidearm = cls.Contains("Pistol"),
                FireRate = Fallback.FireRate, Magazine = Fallback.Magazine, Reserve = Fallback.Reserve,
                Head = Fallback.Head, Body = Fallback.Body, Legs = Fallback.Legs, Spread = Fallback.Spread,
            };
        }
    }
}
