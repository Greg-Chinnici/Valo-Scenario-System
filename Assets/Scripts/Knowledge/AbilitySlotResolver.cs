using System;
using System.Collections.Generic;
using ValorantReplay.Playback;

namespace ValoReplay.Knowledge
{
    /// <summary>
    /// Works out which ability key (C / Q / E / X) an ability class, world object or status effect belongs to.
    /// Internal folders do not match the keys ("Ability_Vampire_4_NearsightAoE" is Reyna's C), so each player's own
    /// abilities (from the charge samples) are matched by folder token, then by a shared word ("Thumper", "Flash").
    /// </summary>
    internal sealed class AbilitySlotResolver
    {
        private static readonly HashSet<string> Generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Ability", "Projectile", "GameObject", "Patch", "Production", "Prototype", "Player", "Global", "Actor", "Equippable",
            "Applied", "Source", "Manager", "Synced", "Basic", "Gun",
        };

        private readonly VrpReplay _replay;
        private readonly List<(string Class, string Slot)>[] _abilities;
        private readonly string[] _agent;

        public AbilitySlotResolver(VrpReplay replay)
        {
            _replay = replay;
            int n = replay.Players.Count;
            _abilities = new List<(string, string)>[n];
            _agent = new string[n];
            for (int p = 0; p < n; p++)
            {
                _abilities[p] = new List<(string, string)>();
                var cls = replay.Players[p].AgentClass ?? "";
                int cut = cls.IndexOf("_PC", StringComparison.Ordinal);
                _agent[p] = cut > 0 ? cls.Substring(0, cut) : cls;
            }
            foreach (var c in replay.AbilityCharges)
                if (c.Player < n && !string.IsNullOrEmpty(c.Ability) && !_abilities[c.Player].Contains((c.Ability, c.Slot)))
                    _abilities[c.Player].Add((c.Ability, c.Slot));
        }

        /// <summary>Slot of an ability or ability-gun class used by a player ("Ability_Grenadier_Q_BasicSemtex" -> "C").</summary>
        public string OfClass(int player, string cls)
        {
            if (player >= _abilities.Length || string.IsNullOrEmpty(cls)) return "";
            foreach (var a in _abilities[player]) if (a.Class == cls) return a.Slot;
            var tokens = cls.Split('_');
            return Match(player, FolderOf(tokens), tokens);
        }

        /// <summary>Slot of a world object (Entity.Slot is its ability folder, e.g. "4").</summary>
        public string OfEntity(Entity e)
        {
            if (e == null || e.Owner >= _abilities.Length) return "";
            return Match(e.Owner, e.Slot, e.Class.Split('_'));
        }

        /// <summary>Slot of a status effect's source ability, from its world object or its effect name.</summary>
        public string OfEffect(StatusEffect s)
        {
            if (s.SourcePlayer >= _abilities.Length) return "";
            if (s.Source >= 0 && s.Source < _replay.Entities.Count)
            {
                var slot = OfEntity(_replay.Entities[s.Source]);
                if (slot.Length > 0) return slot;
            }
            if (string.IsNullOrEmpty(s.Name)) return "";
            var tokens = s.Name.Split('_');
            return Match(s.SourcePlayer, FolderOf(tokens), tokens);
        }

        /// <summary>First one-character token ("4", "Q", ...) in a class name.</summary>
        private static string FolderOf(string[] tokens)
        {
            foreach (var t in tokens) if (t.Length == 1) return t;
            return null;
        }

        private string Match(int player, string folder, string[] words)
        {
            var abilities = _abilities[player];
            var candidates = new List<(string Class, string Slot)>();
            if (!string.IsNullOrEmpty(folder))
                foreach (var a in abilities)
                    if (Array.IndexOf(a.Class.Split('_'), folder) >= 0) candidates.Add(a);
            if (candidates.Count == 1) return candidates[0].Slot;

            var pool = candidates.Count > 1 ? candidates : abilities;
            foreach (var w in words)
            {
                if (w.Length < 4 || Generic.Contains(w) || string.Equals(w, _agent[player], StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var a in pool)
                    if (a.Class.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return a.Slot;
            }
            // ultimates have no charge samples, so nothing above matches them
            if (folder == "X") return "X";
            return candidates.Count > 0 ? candidates[0].Slot : "";
        }
    }
}
