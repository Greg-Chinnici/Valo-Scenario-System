using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using ValorantReplay.Playback;

namespace ValoReplay.Replay
{
    /// <summary>A saved moment in a replay: the time and what the viewer showed (who it followed, the intel team, the
    /// camera), so opening it lands on the same view. Made in the viewer (K), listed on the import screen.</summary>
    [Serializable]
    public sealed class ReplayBookmark
    {
        public string id;
        public string vrpPath;
        public string map;
        public string label;
        public long created;             // UTC ticks
        public float time;
        public int round = -1;           // index into Rounds
        public float roundClock = -1f;   // seconds since the barrier dropped; negative in the buy phase
        public int follow = -1;
        public string followAgent;
        public int intelTeam = -1;
        public bool hideEnemies;
        public bool builtMap = true;
        public bool hasCamera;
        public Vector3 cameraPivot;
        public float cameraDistance, cameraYaw, cameraPitch;

        public string FileName => string.IsNullOrEmpty(vrpPath) ? "" : Path.GetFileName(vrpPath);
        public DateTime Created => new DateTime(created, DateTimeKind.Utc);

        /// <summary>"R7 · 0:42" (42 s after the barrier dropped) or "R7 · buy phase".</summary>
        public string Moment
        {
            get
            {
                string r = round >= 0 ? $"R{round + 1}" : "–";
                if (roundClock < 0f) return round >= 0 ? $"{r} · buy phase" : r;
                int s = Mathf.FloorToInt(roundClock);
                return $"{r} · {s / 60}:{s % 60:00}";
            }
        }
    }

    /// <summary>
    /// Every bookmark, newest first, in &lt;persistentDataPath&gt;/bookmarks.json. A bookmark keeps the full path of its .vrp;
    /// <see cref="Resolve"/> also finds the file by name in the replay folders if it moved.
    /// </summary>
    public static class ReplayBookmarks
    {
        [Serializable]
        private sealed class Store
        {
            public List<ReplayBookmark> items = new List<ReplayBookmark>();
        }

        private static Store _store;

        public static string FilePath => Path.Combine(Application.persistentDataPath, "bookmarks.json");

        public static event Action Changed;

        public static IReadOnlyList<ReplayBookmark> All => Load().items;

        public static ReplayBookmark Add(ReplayBookmark b)
        {
            if (string.IsNullOrEmpty(b.id)) b.id = Guid.NewGuid().ToString("N");
            if (b.created == 0) b.created = DateTime.UtcNow.Ticks;
            Load().items.Insert(0, b);
            Save();
            return b;
        }

        public static void Remove(string id)
        {
            if (Load().items.RemoveAll(b => b.id == id) > 0) Save();
        }

        public static void Rename(string id, string label)
        {
            var b = Load().items.Find(x => x.id == id);
            if (b == null || b.label == label) return;
            b.label = label;
            Save();
        }

        /// <summary>A bookmark of the replay at <paramref name="time"/>, with a label naming the round, the clock and who
        /// is followed. The caller fills in the view (intel, map, camera).</summary>
        public static ReplayBookmark At(VrpReplay replay, string vrpPath, float time, int follow)
        {
            var b = new ReplayBookmark
            {
                vrpPath = vrpPath,
                map = replay.MapName ?? "",
                time = time,
                round = replay.RoundAt(time),
                follow = follow >= 0 && follow < replay.Players.Count ? follow : -1,
            };
            if (b.round >= 0)
            {
                var rd = replay.Rounds[b.round];
                float start = rd.BarrierTime >= 0f ? rd.BarrierTime : rd.StartTime;
                b.roundClock = time >= start ? time - start : -1f;
            }
            if (b.follow >= 0) b.followAgent = replay.Players[b.follow].AgentName;
            b.label = b.follow >= 0 ? $"{b.Moment} · {b.followAgent}" : b.Moment;
            return b;
        }

        /// <summary>The bookmark's .vrp: its saved path, else a file of the same name in <paramref name="folders"/>.</summary>
        public static string Resolve(ReplayBookmark b, IEnumerable<string> folders)
        {
            if (!string.IsNullOrEmpty(b.vrpPath) && File.Exists(b.vrpPath)) return b.vrpPath;
            string name = b.FileName;
            if (name.Length == 0) return null;
            foreach (var f in folders)
            {
                if (string.IsNullOrEmpty(f)) continue;
                string p = Path.Combine(f, name);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        private static Store Load()
        {
            if (_store != null) return _store;
            _store = new Store();
            try
            {
                if (File.Exists(FilePath)) JsonUtility.FromJsonOverwrite(File.ReadAllText(FilePath), _store);
                if (_store.items == null) _store.items = new List<ReplayBookmark>();
            }
            catch (Exception e) { Debug.LogWarning($"[Bookmarks] Ignoring {FilePath}: {e.Message}"); }
            return _store;
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonUtility.ToJson(Load(), true));
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch (Exception e) { Debug.LogWarning($"[Bookmarks] Cannot save {FilePath}: {e.Message}"); }
            Changed?.Invoke();
        }
    }
}
