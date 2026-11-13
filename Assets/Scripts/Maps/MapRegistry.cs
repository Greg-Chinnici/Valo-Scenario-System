using System;
using System.Collections.Generic;
using UnityEngine;
using ValorantReplay.Playback;

namespace ValoReplay.Maps
{
    /// <summary>
    /// Built map prefabs by level path (e.g. "/Game/Maps/Ascent/Ascent"). Filled by ValoReplay > Maps > Build;
    /// lives at Assets/Maps/Resources/MapRegistry.asset so any scene can load it.
    /// Prefabs are already in replay (Unity) space: no offset or rotation is needed.
    /// </summary>
    public sealed class MapRegistry : ScriptableObject
    {
        public const string ResourcePath = "MapRegistry";

        [Serializable]
        public sealed class Entry
        {
            public string displayName;
            public string levelPath;
            public GameObject prefab;
        }

        public List<Entry> maps = new List<Entry>();

        private static MapRegistry _instance;

        public static MapRegistry Instance
        {
            get
            {
                if (_instance == null) _instance = Resources.Load<MapRegistry>(ResourcePath);
                return _instance;
            }
        }

        /// <summary>The built map for a replay's level path, or null.</summary>
        public GameObject Find(string levelPath)
        {
            if (string.IsNullOrEmpty(levelPath)) return null;
            string level = MapCatalog.LevelName(levelPath);
            foreach (var e in maps)
                if (e.prefab != null && (e.levelPath == levelPath || MapCatalog.LevelName(e.levelPath) == level)) return e.prefab;
            return null;
        }

        public static bool Has(string levelPath) => Instance != null && Instance.Find(levelPath) != null;
    }
}
