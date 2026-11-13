using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ValorantReplay.Playback;

namespace ValoReplay.Replay
{
    /// <summary>
    /// Internal weapon/ability class -> display name ("AssaultRifle_AK" -> "Vandal", "Ability_Wushu_X_..." -> "Jett X").
    /// Unmapped guns fall back to the class name split into words ("BattleRifle" -> "Battle Rifle").
    /// </summary>
    public static class WeaponNames
    {
        private static readonly Dictionary<string, string> Guns = new Dictionary<string, string>
        {
            ["BasePistol"] = "Classic", ["LugerPistol"] = "Ghost", ["RevolverPistol"] = "Sheriff", ["AutomaticPistol"] = "Frenzy",
            ["SawedOffShotgun"] = "Shorty", ["Vector"] = "Stinger", ["SubMachineGun_MP5"] = "Spectre", ["PumpShotgun"] = "Bucky",
            ["AutomaticShotgun"] = "Judge", ["AssaultRifle_Burst"] = "Bulldog", ["DMR"] = "Guardian", ["AssaultRifle_ACR"] = "Phantom",
            ["AssaultRifle_AK"] = "Vandal", ["LeverSniperRifle"] = "Marshal", ["BoltSniper"] = "Operator", ["LightMachineGun"] = "Ares",
            ["HeavyMachineGun"] = "Odin", ["Ability_Melee_Base"] = "Knife", ["Equippable_Unarmed"] = "Unarmed",
        };

        /// <summary>Guns known to be sidearms / primaries even if no loadout in this replay bought them (pickups).</summary>
        public static readonly string[] KnownSidearms = { "BasePistol", "LugerPistol", "RevolverPistol", "AutomaticPistol", "SawedOffShotgun" };
        public static readonly string[] KnownPrimaries =
        {
            "Vector", "SubMachineGun_MP5", "PumpShotgun", "AutomaticShotgun", "AssaultRifle_Burst", "DMR", "AssaultRifle_ACR",
            "AssaultRifle_AK", "LeverSniperRifle", "BoltSniper", "LightMachineGun", "HeavyMachineGun",
        };

        private static readonly Dictionary<string, string> Cache = new Dictionary<string, string>();

        public static string Pretty(string cls)
        {
            if (string.IsNullOrEmpty(cls)) return "";
            if (Cache.TryGetValue(cls, out var name)) return name;
            if (!Guns.TryGetValue(cls, out name))
            {
                // Ability_<Agent>_<Slot>_... / Gun_<Agent>_<Slot>_... -> "<Agent display name> <Slot>"
                var parts = cls.Split('_');
                var agent = parts.Length >= 3 && (parts[0] == "Ability" || parts[0] == "Gun")
                    ? AgentCatalog.All.FirstOrDefault(a => a.DeveloperName == parts[1]) : null;
                name = agent != null ? $"{agent.DisplayName} {parts[2]}" : Regex.Replace(cls.Replace('_', ' '), "(?<=[a-z])(?=[A-Z])", " ");
            }
            Cache[cls] = name;
            return name;
        }
    }
}
