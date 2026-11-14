using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using ValorantReplay.Playback;
using ValoReplay.Replay;

namespace ValoReplay.Import
{
    /// <summary>
    /// UI Toolkit start screen: the replay library. Lists the .vrp replays in a folder (map, score, date, length; filter by
    /// map), shows the selected match (stats, who won each round and how, both rosters with K/D/A and ACS) and opens it in
    /// the replay viewer. Replays are converted from .vrf outside the app (the private converter publishes anonymized .vrp
    /// files into this repo's data/replays). The bookmarks drawer lists moments saved in the viewer (K) and opens the
    /// replay there. Layout: Assets/UI/VrfImport/VrfImport.uxml.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class VrfImportScreen : MonoBehaviour
    {
        [Tooltip("Folder listed on first start. Empty = the repo's data/replays (in the editor), else <persistentDataPath>/Replays.")]
        [SerializeField] private string defaultFolder = "";

        public VrpReplay LastReplay { get; private set; }
        public string LastPath { get; private set; }

        private const string PrefFolder = "ValoReplay.Import.Folder", PrefFile = "ValoReplay.Import.File";
        private const string AllMaps = "All maps";

        // ------------------------------------------------------------------ what the list shows of a replay
        private sealed class PlayerLine
        {
            public string Agent, Name;
            public int Team, Kills, Deaths, Assists, Acs;
        }

        private sealed class Summary
        {
            public string Path, Error;
            public long Size;
            public DateTime Written;
            public string Map = "";
            public float Duration;
            public int Kills, Plants;
            public readonly int[] Wins = new int[2];
            public readonly TeamRole[] StartRoles = new TeamRole[2];
            public readonly List<(int Winner, RoundEndReason Reason, int Attacking)> Rounds = new List<(int, RoundEndReason, int)>();
            public readonly List<PlayerLine> Players = new List<PlayerLine>();
        }

        /// <summary>Summaries outlive the scene (coming back from the viewer is instant); keyed by path and write time.</summary>
        private static readonly Dictionary<string, Summary> Cache = new Dictionary<string, Summary>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<Summary> _arrived = new ConcurrentQueue<Summary>();

        private TextField _folderField;
        private Button _browseButton, _refreshButton, _watchButton, _showFolderButton;
        private Label _countLabel, _emptyLabel, _titleLabel, _subtitleLabel, _statusLabel;
        private Label _statScore, _statLength, _statRounds, _statKills, _statPlants;
        private DropdownField _mapFilter;
        private ListView _fileList;
        private VisualElement _roundStrip, _team0, _team1;
        private Button _bookmarksButton;
        private VisualElement _drawer, _scrim;
        private ScrollView _bookmarkList;
        private Label _bookmarkCount, _bookmarkEmpty;
        private string _renaming;     // id of the bookmark whose label is being edited

        private readonly List<FileInfo> _files = new List<FileInfo>();
        private readonly List<FileInfo> _visible = new List<FileInfo>();
        private string _folder, _selectedPath;
        private int _loadGeneration;

        /// <summary>Where replays are looked for, best first: the repo's data/replays (editor, or next to a build), then
        /// the app's own Replays folder.</summary>
        public static IEnumerable<string> DefaultFolders()
        {
            yield return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "data", "replays"));
            yield return Path.Combine(Application.persistentDataPath, "Replays");
        }

        // ------------------------------------------------------------------ lifecycle
        private void OnEnable()
        {
            var root = GetComponent<UIDocument>().rootVisualElement;
            _folderField = root.Q<TextField>("folder-field");
            _browseButton = root.Q<Button>("browse-button");
            _refreshButton = root.Q<Button>("refresh-button");
            _watchButton = root.Q<Button>("watch-button");
            _showFolderButton = root.Q<Button>("open-output-button");
            _countLabel = root.Q<Label>("count-label");
            _emptyLabel = root.Q<Label>("empty-label");
            _titleLabel = root.Q<Label>("title-label");
            _subtitleLabel = root.Q<Label>("subtitle-label");
            _statusLabel = root.Q<Label>("status-label");
            _statScore = root.Q<Label>("stat-score");
            _statLength = root.Q<Label>("stat-length");
            _statRounds = root.Q<Label>("stat-rounds");
            _statKills = root.Q<Label>("stat-kills");
            _statPlants = root.Q<Label>("stat-plants");
            _mapFilter = root.Q<DropdownField>("map-filter");
            _fileList = root.Q<ListView>("file-list");
            _roundStrip = root.Q("round-strip");
            _team0 = root.Q("team0");
            _team1 = root.Q("team1");
            _bookmarksButton = root.Q<Button>("bookmarks-button");
            _drawer = root.Q("bookmark-drawer");
            _scrim = root.Q("drawer-scrim");
            _bookmarkList = root.Q<ScrollView>("bookmark-list");
            _bookmarkCount = root.Q<Label>("bookmark-count");
            _bookmarkEmpty = root.Q<Label>("bookmark-empty");

            _folderField.isDelayed = true;
            _browseButton.SetEnabled(FileDialog.IsSupported);
            SetupList();
            _mapFilter.choices = new List<string> { AllMaps };
            _mapFilter.SetValueWithoutNotify(AllMaps);
            _mapFilter.RegisterValueChangedCallback(_ => ApplyFilter());

            _folderField.RegisterValueChangedCallback(e => ListFolder(e.newValue, force: true));
            _browseButton.clicked += Browse;
            _refreshButton.clicked += () => ListFolder(_folder, force: true);
            _watchButton.clicked += Watch;
            _showFolderButton.clicked += ShowFolder;
            _bookmarksButton.clicked += () => OpenDrawer(!DrawerOpen);
            root.Q<Button>("bookmarks-close").clicked += () => OpenDrawer(false);
            _scrim.RegisterCallback<ClickEvent>(_ => OpenDrawer(false));
            ReplayBookmarks.Changed += RefreshBookmarks;
            RefreshBookmarks();

            if (ReplaySession.Instance != null && ReplaySession.Instance.Replay != null)
            {
                LastReplay = ReplaySession.Instance.Replay;
                LastPath = ReplaySession.Instance.VrpPath;
            }

            string folder = PlayerPrefs.GetString(PrefFolder, "");
            if (!Directory.Exists(folder)) folder = defaultFolder;
            if (!Directory.Exists(folder)) folder = DefaultFolders().FirstOrDefault(Directory.Exists) ?? DefaultFolders().Last();
            _folderField.SetValueWithoutNotify(folder);
            ListFolder(folder, force: true);
            SelectPath(LastPath ?? PlayerPrefs.GetString(PrefFile, ""));
        }

        private void OnDisable()
        {
            _loadGeneration++;      // summaries still loading are dropped
            ReplayBookmarks.Changed -= RefreshBookmarks;
            if (_folder != null) PlayerPrefs.SetString(PrefFolder, _folder);
            PlayerPrefs.Save();
        }

        private void Update()
        {
            if (DrawerOpen && _renaming == null && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) OpenDrawer(false);
            bool any = false;
            while (_arrived.TryDequeue(out var s))
            {
                Cache[Key(s.Path, s.Written)] = s;
                any = true;
            }
            if (!any) return;
            RefreshFilterChoices();
            ApplyFilter();
            if (_selectedPath != null) ShowDetails(SummaryOf(_selectedPath));
        }

        // ------------------------------------------------------------------ folder and list
        private void SetupList()
        {
            _fileList.itemsSource = _visible;
            _fileList.makeItem = () =>
            {
                var item = Ui.Box("vr-replay");
                var top = Ui.Box("vr-replay-top");
                top.Add(new Label { name = "map" });
                top.Add(new Label { name = "score" });
                top.Q<Label>("map").AddToClassList("vr-replay-map");
                top.Q<Label>("score").AddToClassList("vr-replay-score");
                item.Add(top);
                var meta = new Label { name = "meta" };
                meta.AddToClassList("vr-replay-meta");
                item.Add(meta);
                return item;
            };
            _fileList.bindItem = (e, i) =>
            {
                var f = _visible[i];
                var s = SummaryOf(f.FullName);
                e.Q<Label>("map").text = s != null && s.Map.Length > 0 ? s.Map : MapFromFileName(f.Name);
                e.Q<Label>("score").text = s == null ? "" : s.Error != null ? "unreadable" : $"{s.Wins[0]} – {s.Wins[1]}";
                string detail = s == null ? "reading…" : s.Error != null ? s.Error : $"{FormatTime(s.Duration)} · {s.Rounds.Count} rounds";
                e.Q<Label>("meta").text = $"{f.LastWriteTime:yyyy-MM-dd}  ·  {detail}  ·  {FormatSize(f.Length)}";
            };
            _fileList.selectionChanged += sel => { if (sel.FirstOrDefault() is FileInfo f) SelectPath(f.FullName, fromList: true); };
            _fileList.itemsChosen += chosen => { if (chosen.Any()) Watch(); };   // double-click / Enter
        }

        private void ListFolder(string folder, bool force = false)
        {
            folder = (folder ?? "").Trim().Trim('"');
            if (folder.Length == 0 || (!force && PathsEqual(folder, _folder))) return;
            _folder = folder;
            _files.Clear();
            string error = null;
            try
            {
                if (Directory.Exists(folder))
                    _files.AddRange(new DirectoryInfo(folder).GetFiles("*.vrp").OrderByDescending(f => f.LastWriteTimeUtc).ThenBy(f => f.Name));
            }
            catch (Exception e) { error = e.Message; }
            PlayerPrefs.SetString(PrefFolder, folder);
            LoadSummaries();
            RefreshFilterChoices();
            ApplyFilter();

            if (!Directory.Exists(folder)) SetStatus("Folder not found. Pick the folder that holds your .vrp replays.", "vr-status--error");
            else if (error != null) SetStatus($"Cannot list {folder}: {error}", "vr-status--error");
            else SetStatus(_files.Count == 0 ? "" : "Double-click a replay, or select it and press Watch.", null);
        }

        /// <summary>Reads each replay once on a worker thread for its summary; the list fills in as they arrive.</summary>
        private void LoadSummaries()
        {
            int generation = ++_loadGeneration;
            var todo = _files.Where(f => !Cache.ContainsKey(Key(f.FullName, f.LastWriteTimeUtc)))
                             .Select(f => (f.FullName, f.LastWriteTimeUtc, f.Length)).ToList();
            if (todo.Count == 0) return;
            Task.Run(() =>
            {
                foreach (var (path, written, size) in todo)
                {
                    if (generation != _loadGeneration) return;
                    _arrived.Enqueue(Summarize(path, written, size));
                }
            });
        }

        private static Summary Summarize(string path, DateTime written, long size)
        {
            var s = new Summary { Path = path, Written = written, Size = size };
            try
            {
                var r = VrpReplay.Load(path);
                s.Map = r.MapName;
                s.Duration = r.Duration;
                s.Kills = r.Kills.Count;
                s.Plants = r.Rounds.Count(x => x.PlantTime >= 0f);
                for (int t = 0; t < 2 && t < r.Teams.Count; t++)
                {
                    s.Wins[t] = r.Teams[t].FinalWins;
                    s.StartRoles[t] = r.Teams[t].InitialRole;
                }
                foreach (var rd in r.Rounds) s.Rounds.Add((rd.WinningTeam, rd.EndReason, rd.AttackingTeam));
                for (int i = 0; i < r.Players.Count; i++)
                {
                    var p = r.Players[i];
                    var st = r.StatsAt(i, r.Duration);
                    s.Players.Add(new PlayerLine
                    {
                        Agent = p.AgentName ?? "", Name = p.Name ?? "", Team = p.Team,
                        Kills = st.Kills, Deaths = st.Deaths, Assists = st.Assists,
                        Acs = Mathf.RoundToInt(r.AverageCombatScore(i, r.Duration)),
                    });
                }
            }
            catch (Exception e)
            {
                // e.g. a file written by another .vrp layout: it has to be converted again
                s.Error = e is InvalidDataException ? "needs converting again" : "unreadable";
                Debug.LogWarning($"[Replays] {Path.GetFileName(path)}: {e.Message}");
            }
            return s;
        }

        private void RefreshFilterChoices()
        {
            var maps = _files.Select(f => SummaryOf(f.FullName)?.Map ?? MapFromFileName(f.Name))
                             .Where(m => m.Length > 0).Distinct().OrderBy(m => m).ToList();
            maps.Insert(0, AllMaps);
            string current = _mapFilter.value;
            _mapFilter.choices = maps;
            _mapFilter.SetValueWithoutNotify(maps.Contains(current) ? current : AllMaps);
        }

        private void ApplyFilter()
        {
            string map = _mapFilter.value;
            _visible.Clear();
            foreach (var f in _files)
                if (map == AllMaps || (SummaryOf(f.FullName)?.Map ?? MapFromFileName(f.Name)) == map) _visible.Add(f);
            _fileList.RefreshItems();
            _countLabel.text = _visible.Count == _files.Count ? $"{_files.Count} REPLAY{(_files.Count == 1 ? "" : "S")}" : $"{_visible.Count} OF {_files.Count} REPLAYS";
            _emptyLabel.text = _files.Count > 0 ? "" : Directory.Exists(_folder)
                ? "No replays in this folder.\nReplays are .vrp files: converted and published outside the app, or copied here."
                : "This folder doesn't exist.";
            Ui.Show(_emptyLabel, _files.Count == 0);
            int i = _visible.FindIndex(f => PathsEqual(f.FullName, _selectedPath));
            if (i >= 0) _fileList.SetSelectionWithoutNotify(new[] { i });
            else _fileList.ClearSelection();
        }

        /// <summary>The native dialog picks a .vrp; its folder becomes the listed one and it gets selected.</summary>
        private void Browse()
        {
            string path = FileDialog.OpenVrp(Directory.Exists(_folder) ? _folder : "");
            if (path == null) return;
            string folder = Path.GetDirectoryName(path);
            _folderField.SetValueWithoutNotify(folder);
            ListFolder(folder, force: true);
            SelectPath(path);
        }

        // ------------------------------------------------------------------ the selected match
        private void SelectPath(string path, bool fromList = false)
        {
            var file = _files.FirstOrDefault(f => PathsEqual(f.FullName, path));
            _selectedPath = file?.FullName;
            if (_selectedPath != null) PlayerPrefs.SetString(PrefFile, _selectedPath);
            if (!fromList)
            {
                int i = _visible.FindIndex(f => PathsEqual(f.FullName, _selectedPath));
                if (i >= 0) { _fileList.SetSelectionWithoutNotify(new[] { i }); _fileList.ScrollToItem(i); }
            }
            ShowDetails(_selectedPath != null ? SummaryOf(_selectedPath) : null);
        }

        private void ShowDetails(Summary s)
        {
            _roundStrip.Clear();
            _team0.Clear();
            _team1.Clear();
            var tiles = new[] { _statScore, _statLength, _statRounds, _statKills, _statPlants };
            if (_selectedPath == null || s == null || s.Error != null)
            {
                _titleLabel.text = _selectedPath == null ? (_files.Count > 0 ? "Pick a replay" : "No replays") : MapFromFileName(Path.GetFileName(_selectedPath));
                _subtitleLabel.text = _selectedPath == null ? "" : s == null ? "Reading…" : $"Can't open this file: {s.Error}.";
                foreach (var l in tiles) l.text = "–";
                _watchButton.SetEnabled(false);
                return;
            }

            _titleLabel.text = s.Map.ToUpperInvariant();
            _subtitleLabel.text = $"{Path.GetFileNameWithoutExtension(s.Path)}  ·  {s.Written.ToLocalTime():yyyy-MM-dd HH:mm}  ·  {FormatSize(s.Size)}";
            _statScore.text = $"{s.Wins[0]} – {s.Wins[1]}";
            _statLength.text = FormatTime(s.Duration);
            _statRounds.text = s.Rounds.Count.ToString();
            _statKills.text = s.Kills.ToString();
            _statPlants.text = s.Plants.ToString();
            _watchButton.SetEnabled(true);

            // one cell per round, coloured by the winner; a gap where the sides switch
            int lastAttacking = -1;
            for (int i = 0; i < s.Rounds.Count; i++)
            {
                var (winner, reason, attacking) = s.Rounds[i];
                if (lastAttacking >= 0 && attacking != lastAttacking && attacking != 255) _roundStrip.Add(Ui.Box("vr-round-gap"));
                if (attacking != 255) lastAttacking = attacking;
                var cell = Ui.Box("vr-round");
                if (winner < 2) cell.AddToClassList(winner == 0 ? "vr-round--team0" : "vr-round--team1");
                cell.Add(Ui.Label((i + 1).ToString(), "vr-round-number"));
                cell.Add(Ui.Label(ReasonLetter(reason), "vr-round-reason"));
                cell.tooltip = $"Round {i + 1}: {(winner < 2 ? $"team {winner}" : "?")} · {reason}";
                _roundStrip.Add(cell);
            }

            for (int t = 0; t < 2; t++)
            {
                var panel = t == 0 ? _team0 : _team1;
                var head = Ui.Box("vr-team-head");
                string role = s.StartRoles[t] == TeamRole.Attacking ? "STARTED ON ATTACK" : s.StartRoles[t] == TeamRole.Defending ? "STARTED ON DEFENSE" : "";
                head.Add(Ui.Label($"TEAM {t}   {role}", "vr-team-name"));
                head.Add(Ui.Label(s.Wins[t].ToString(), "vr-team-score"));
                panel.Add(head);
                var cols = Ui.Box("vr-player");
                cols.AddToClassList("vr-player-head");
                cols.Add(Ui.Label("AGENT", "vr-player-agent"));
                cols.Add(Ui.Label("PLAYER", "vr-player-name"));
                cols.Add(Ui.Label("K / D / A", "vr-player-kda"));
                cols.Add(Ui.Label("ACS", "vr-player-acs"));
                panel.Add(cols);
                foreach (var p in s.Players.Where(p => p.Team == t).OrderByDescending(p => p.Acs))
                {
                    var row = Ui.Box("vr-player");
                    row.Add(Ui.Label(p.Agent, "vr-player-agent"));
                    row.Add(Ui.Label(p.Name.Length > 0 ? p.Name : "–", "vr-player-name"));
                    row.Add(Ui.Label($"{p.Kills} / {p.Deaths} / {p.Assists}", "vr-player-kda"));
                    row.Add(Ui.Label(p.Acs.ToString(), "vr-player-acs"));
                    panel.Add(row);
                }
            }
        }

        private static string ReasonLetter(RoundEndReason r) =>
            r == RoundEndReason.Elimination ? "E" : r == RoundEndReason.Defused ? "D" : r == RoundEndReason.Detonated ? "S" :
            r == RoundEndReason.TimeExpired ? "T" : r == RoundEndReason.Surrender ? "FF" : "";

        /// <summary>Loads the selected replay and hands it to the viewer.</summary>
        private void Watch()
        {
            if (_selectedPath == null) return;
            try
            {
                LastReplay = VrpReplay.Load(_selectedPath);
                LastPath = _selectedPath;
            }
            catch (Exception e)
            {
                SetStatus($"Cannot open {Path.GetFileName(_selectedPath)}: {e.Message}", "vr-status--error");
                return;
            }
            SetStatus("Opening replay viewer…", null);
            ReplaySession.Open(LastReplay, LastPath);
        }

        // ------------------------------------------------------------------ bookmarks drawer
        private bool DrawerOpen => _drawer.ClassListContains("vr-drawer--open");

        private void OpenDrawer(bool open)
        {
            if (open) RefreshBookmarks();
            _drawer.EnableInClassList("vr-drawer--open", open);
            _scrim.EnableInClassList("vr-scrim--open", open);
            _bookmarksButton.EnableInClassList("vr-bookmarks-button--open", open);
        }

        private IEnumerable<string> BookmarkFolders() => new[] { _folder }.Concat(DefaultFolders());

        private void RefreshBookmarks()
        {
            var all = ReplayBookmarks.All;
            _bookmarkList.Clear();
            _bookmarksButton.text = all.Count > 0 ? $"BOOKMARKS  {all.Count}" : "BOOKMARKS";
            _bookmarkCount.text = all.Count == 1 ? "1 BOOKMARK" : $"{all.Count} BOOKMARKS";
            Ui.Show(_bookmarkEmpty, all.Count == 0);
            foreach (var b in all) _bookmarkList.Add(BookmarkRow(b));
        }

        private VisualElement BookmarkRow(ReplayBookmark b)
        {
            string path = ReplayBookmarks.Resolve(b, BookmarkFolders());
            var row = Ui.Box("vr-bookmark");
            if (path == null) row.AddToClassList("vr-bookmark--missing");
            var text = Ui.Box("vr-bookmark-text");
            var top = Ui.Box("vr-bookmark-top");
            top.Add(Ui.Label(b.map.Length > 0 ? b.map : "?", "vr-bookmark-map"));
            top.Add(Ui.Label(b.Moment, "vr-bookmark-moment"));
            text.Add(top);
            if (_renaming == b.id)
            {
                var field = new TextField { value = b.label ?? "", isDelayed = true };
                field.AddToClassList("vr-bookmark-rename");
                field.RegisterValueChangedCallback(e => FinishRename(b.id, e.newValue));
                field.RegisterCallback<FocusOutEvent>(_ => FinishRename(b.id, field.value));
                text.Add(field);
                field.schedule.Execute(() => { field.Focus(); field.SelectAll(); });
            }
            else text.Add(Ui.Label(b.label ?? "", "vr-bookmark-label"));
            var view = new List<string>();
            if (b.intelTeam >= 0) view.Add($"intel {Ui.TeamName(b.intelTeam).ToLowerInvariant()}{(b.hideEnemies ? ", enemies hidden" : "")}");
            if (!b.builtMap) view.Add("minimap");
            string where = path == null ? "replay file not found" : b.FileName;
            text.Add(Ui.Label($"{where}  ·  saved {b.Created.ToLocalTime():yyyy-MM-dd HH:mm}{(view.Count > 0 ? "  ·  " + string.Join(", ", view) : "")}", "vr-bookmark-meta"));
            row.Add(text);

            var rename = new Button(() => { _renaming = b.id; RefreshBookmarks(); }) { text = "Rename", tooltip = "Rename this bookmark" };
            rename.AddToClassList("vr-button");
            rename.AddToClassList("vr-button--small");
            row.Add(rename);
            var delete = new Button(() => ReplayBookmarks.Remove(b.id)) { text = "×", tooltip = "Delete this bookmark" };
            delete.AddToClassList("vr-button");
            delete.AddToClassList("vr-bookmark-delete");
            row.Add(delete);

            row.tooltip = path == null ? $"{b.FileName} is not in {b.vrpPath ?? "?"} or the replay folders" : $"Open {b.FileName} at {b.Moment}";
            row.RegisterCallback<ClickEvent>(e =>
            {
                if (Within<Button>(e.target, row) || Within<TextField>(e.target, row)) return;
                OpenBookmark(b);
            });
            return row;
        }

        private void FinishRename(string id, string label)
        {
            if (_renaming != id) return;
            _renaming = null;
            label = (label ?? "").Trim();
            if (label.Length > 0) ReplayBookmarks.Rename(id, label);     // refreshes through Changed
            _drawer.schedule.Execute(RefreshBookmarks);
        }

        private static bool Within<T>(IEventHandler target, VisualElement stop) where T : VisualElement
        {
            for (var e = target as VisualElement; e != null && e != stop; e = e.parent)
                if (e is T) return true;
            return false;
        }

        /// <summary>Loads the bookmark's replay and opens the viewer there (paused, with the bookmark's view).</summary>
        private void OpenBookmark(ReplayBookmark b)
        {
            string path = ReplayBookmarks.Resolve(b, BookmarkFolders());
            if (path == null)
            {
                SetStatus($"Can't find {b.FileName}: it's not at its saved path or in the replay folders.", "vr-status--error");
                return;
            }
            try
            {
                LastReplay = VrpReplay.Load(path);
                LastPath = path;
            }
            catch (Exception e)
            {
                SetStatus($"Cannot open {Path.GetFileName(path)}: {e.Message}", "vr-status--error");
                return;
            }
            PlayerPrefs.SetString(PrefFile, path);
            SetStatus($"Opening {b.map} at {b.Moment}…", null);
            ReplaySession.OpenAt(LastReplay, LastPath, b);
        }

        private void ShowFolder()
        {
            string folder = Directory.Exists(_folder) ? _folder : DefaultFolders().Last();
            Directory.CreateDirectory(folder);
#if UNITY_EDITOR
            UnityEditor.EditorUtility.RevealInFinder(folder);
#else
            Application.OpenURL(new Uri(folder).AbsoluteUri);
#endif
        }

        // ------------------------------------------------------------------ helpers
        private Summary SummaryOf(string path)
        {
            var f = _files.FirstOrDefault(x => PathsEqual(x.FullName, path));
            return f != null && Cache.TryGetValue(Key(f.FullName, f.LastWriteTimeUtc), out var s) ? s : null;
        }

        private static string Key(string path, DateTime written) => path + "|" + written.Ticks;

        /// <summary>Published replays are named "Map-matchid.vrp".</summary>
        private static string MapFromFileName(string name)
        {
            string stem = Path.GetFileNameWithoutExtension(name);
            int dash = stem.IndexOf('-');
            return dash > 0 ? stem.Substring(0, dash).Replace('_', ' ') : stem;
        }

        private void SetStatus(string text, string cls)
        {
            _statusLabel.text = text ?? "";
            _statusLabel.RemoveFromClassList("vr-status--error");
            _statusLabel.RemoveFromClassList("vr-status--success");
            if (cls != null) _statusLabel.AddToClassList(cls);
        }

        private static bool PathsEqual(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }

        private static string FormatSize(long bytes) =>
            bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.0} MB" : $"{bytes / 1024.0:0} KB";

        private static string FormatTime(float seconds)
        {
            int s = Mathf.Max(0, Mathf.FloorToInt(seconds));
            return $"{s / 60}:{s % 60:00}";
        }
    }
}
