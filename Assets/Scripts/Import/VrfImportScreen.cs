using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using ValorantReplay.Playback;
using ValoReplay.Replay;

namespace ValoReplay.Import
{
    /// <summary>
    /// UI Toolkit start screen: lists the .vrp replays in a folder, shows the selected one (map, length, score, teams) and
    /// opens it in the replay viewer. Replays are converted from .vrf outside the app (the private converter publishes
    /// anonymized .vrp files into this repo's data/replays). Layout: Assets/UI/VrfImport/VrfImport.uxml.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class VrfImportScreen : MonoBehaviour
    {
        [Tooltip("Folder listed on first start. Empty = the repo's data/replays (in the editor), else <persistentDataPath>/Replays.")]
        [SerializeField] private string defaultFolder = "";
        [Tooltip("Oldest log lines are dropped past this count.")]
        [SerializeField] private int maxLogLines = 2000;

        public VrpReplay LastReplay { get; private set; }
        public string LastPath { get; private set; }

        private enum LogLevel { Info, Success, Warning, Error }
        private readonly struct LogLine
        {
            public readonly LogLevel Level;
            public readonly string Text;
            public LogLine(LogLevel level, string text) { Level = level; Text = text; }
        }

        private const string PrefFolder = "ValoReplay.Import.Folder", PrefFile = "ValoReplay.Import.File";

        private TextField _folderField;
        private Button _browseButton, _refreshButton, _openButton, _copyButton, _clearButton, _showFolderButton, _watchButton;
        private Label _folderLabel, _fileInfo, _titleLabel, _teamsLabel, _statusLabel;
        private Label _statLength, _statScore, _statRounds, _statKills, _statPlants, _statPlayers;
        private ListView _fileList, _logList;
        private Toggle _autoScrollToggle;

        private readonly List<FileInfo> _files = new List<FileInfo>();
        private readonly List<LogLine> _lines = new List<LogLine>();
        private bool _logDirty;
        private string _folder;
        private VrpReplay _selected;
        private string _selectedPath;

        /// <summary>Where replays are looked for, best first: the repo's data/replays (editor, or next to a build), then
        /// the app's own Replays folder.</summary>
        public static IEnumerable<string> DefaultFolders()
        {
            yield return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "data", "replays"));
            yield return Path.Combine(Application.persistentDataPath, "Replays");
        }

        // ------------------------------------------------------------------------------------------ lifecycle
        private void OnEnable()
        {
            var root = GetComponent<UIDocument>().rootVisualElement;
            _folderField = root.Q<TextField>("folder-field");
            _browseButton = root.Q<Button>("browse-button");
            _refreshButton = root.Q<Button>("refresh-button");
            _openButton = root.Q<Button>("open-button");
            _copyButton = root.Q<Button>("copy-button");
            _clearButton = root.Q<Button>("clear-button");
            _showFolderButton = root.Q<Button>("open-output-button");
            _watchButton = root.Q<Button>("watch-button");
            _folderLabel = root.Q<Label>("folder-label");
            _fileInfo = root.Q<Label>("file-info");
            _titleLabel = root.Q<Label>("title-label");
            _teamsLabel = root.Q<Label>("teams-label");
            _statusLabel = root.Q<Label>("status-label");
            _statLength = root.Q<Label>("stat-length");
            _statScore = root.Q<Label>("stat-score");
            _statRounds = root.Q<Label>("stat-rounds");
            _statKills = root.Q<Label>("stat-kills");
            _statPlants = root.Q<Label>("stat-plants");
            _statPlayers = root.Q<Label>("stat-players");
            _fileList = root.Q<ListView>("file-list");
            _logList = root.Q<ListView>("log-list");
            _autoScrollToggle = root.Q<Toggle>("autoscroll-toggle");

            _folderField.isDelayed = true;
            _browseButton.SetEnabled(FileDialog.IsSupported);
            SetupFileList();
            SetupLog();

            _folderField.RegisterValueChangedCallback(e => ListFolder(e.newValue, force: true));
            _browseButton.clicked += Browse;
            _refreshButton.clicked += () => ListFolder(_folder, force: true);
            _openButton.clicked += OpenSelected;
            _watchButton.clicked += Watch;
            _clearButton.clicked += () => { _lines.Clear(); _logDirty = true; };
            _copyButton.clicked += () => GUIUtility.systemCopyBuffer = string.Join("\n", _lines.Select(l => l.Text));
            _showFolderButton.clicked += ShowFolder;

            // coming back from the viewer: the session still holds the replay
            if (ReplaySession.Instance != null && ReplaySession.Instance.Replay != null)
            {
                LastReplay = ReplaySession.Instance.Replay;
                LastPath = ReplaySession.Instance.VrpPath;
                SetStatus($"Loaded {Path.GetFileName(LastPath)}. Watch it again or pick another replay.", null);
            }

            string folder = PlayerPrefs.GetString(PrefFolder, "");
            if (!Directory.Exists(folder)) folder = defaultFolder;
            if (!Directory.Exists(folder)) folder = DefaultFolders().FirstOrDefault(Directory.Exists) ?? DefaultFolders().Last();
            _folderField.SetValueWithoutNotify(folder);
            ListFolder(folder, force: true);
            string last = PlayerPrefs.GetString(PrefFile, "");
            int i = _files.FindIndex(f => PathsEqual(f.FullName, last));
            if (i >= 0) _fileList.SetSelection(i);
            UpdateButtons();
        }

        private void OnDisable()
        {
            if (_folder != null) PlayerPrefs.SetString(PrefFolder, _folder);
            PlayerPrefs.Save();
        }

        private void Update()
        {
            if (!_logDirty) return;
            _logDirty = false;
            _logList.RefreshItems();
            if (_autoScrollToggle.value && _lines.Count > 0)
                _logList.schedule.Execute(() => _logList.ScrollToItem(_lines.Count - 1));
        }

        // ------------------------------------------------------------------------------------------ the folder
        private void SetupFileList()
        {
            _fileList.itemsSource = _files;
            _fileList.makeItem = () =>
            {
                var item = new VisualElement();
                item.AddToClassList("vr-file-item");
                var name = new Label { name = "name" };
                var meta = new Label { name = "meta" };
                name.AddToClassList("vr-file-name");
                meta.AddToClassList("vr-file-meta");
                item.Add(name);
                item.Add(meta);
                return item;
            };
            _fileList.bindItem = (e, i) =>
            {
                var f = _files[i];
                e.Q<Label>("name").text = Path.GetFileNameWithoutExtension(f.Name);
                e.Q<Label>("meta").text = $"{f.LastWriteTime:yyyy-MM-dd HH:mm}   {FormatSize(f.Length)}";
            };
            _fileList.selectionChanged += sel => { if (sel.FirstOrDefault() is FileInfo f) Select(f.FullName); };
            _fileList.itemsChosen += chosen => { if (chosen.Any()) OpenSelected(); };   // double-click / Enter
        }

        private void ListFolder(string folder, bool force = false)
        {
            folder = (folder ?? "").Trim().Trim('"');
            if (folder.Length == 0 || (!force && PathsEqual(folder, _folder))) return;
            _folder = folder;
            _files.Clear();
            try
            {
                if (Directory.Exists(folder))
                    _files.AddRange(new DirectoryInfo(folder).GetFiles("*.vrp").OrderByDescending(f => f.LastWriteTimeUtc));
            }
            catch (Exception e) { Log(LogLevel.Warning, $"Cannot list {folder}: {e.Message}"); }
            _folderLabel.text = Directory.Exists(folder)
                ? $"{_files.Count} replay{(_files.Count == 1 ? "" : "s")}"
                : "Folder not found. Replays are .vrp files converted outside the app.";
            _folderLabel.EnableInClassList("vr-status--error", !Directory.Exists(folder));
            PlayerPrefs.SetString(PrefFolder, folder);
            _fileList.RefreshItems();
            _fileList.ClearSelection();
            Select(null);
        }

        /// <summary>The native dialog picks a .vrp; its folder becomes the listed one and it gets selected.</summary>
        private void Browse()
        {
            string path = FileDialog.OpenVrp(Directory.Exists(_folder) ? _folder : "");
            if (path == null) return;
            string folder = Path.GetDirectoryName(path);
            _folderField.SetValueWithoutNotify(folder);
            ListFolder(folder, force: true);
            int i = _files.FindIndex(f => PathsEqual(f.FullName, path));
            if (i >= 0) _fileList.SetSelection(i);
        }

        // ------------------------------------------------------------------------------------------ the selected replay
        /// <summary>Reads the replay to show what it is (a .vrp loads in well under a second).</summary>
        private void Select(string path)
        {
            _selected = null;
            _selectedPath = path;
            _fileInfo.text = "";
            if (path != null)
            {
                try
                {
                    _selected = VrpReplay.Load(path);
                    PlayerPrefs.SetString(PrefFile, path);
                }
                catch (Exception e)
                {
                    // e.g. a file written by another .vrp layout: it has to be converted again
                    _fileInfo.text = $"Cannot read: {e.Message}";
                    Log(LogLevel.Error, $"Cannot read {path}: {e.Message}");
                }
            }
            ShowDetails(_selected);
            UpdateButtons();
        }

        private void ShowDetails(VrpReplay r)
        {
            if (r == null)
            {
                _titleLabel.text = _selectedPath == null ? "No replay selected" : Path.GetFileNameWithoutExtension(_selectedPath);
                foreach (var l in new[] { _statLength, _statScore, _statRounds, _statKills, _statPlants, _statPlayers }) l.text = "–";
                _teamsLabel.text = "";
                return;
            }
            _titleLabel.text = r.MapName;
            _statLength.text = FormatTime(r.Duration);
            _statScore.text = r.Teams.Count >= 2 ? $"{r.Teams[0].FinalWins} - {r.Teams[1].FinalWins}" : "–";
            _statRounds.text = r.Rounds.Count.ToString();
            _statKills.text = r.Kills.Count.ToString();
            _statPlants.text = r.Rounds.Count(x => x.PlantTime >= 0f).ToString();
            _statPlayers.text = r.Players.Count.ToString();
            var teams = new List<string>();
            for (int t = 0; t < r.Teams.Count; t++)
            {
                var players = r.Players.Where(p => p.Team == t).Select(p => p.Name.Length > 0 ? $"{p.Name} ({p.AgentName})" : p.AgentName);
                teams.Add($"Team {t} ({r.Teams[t].InitialRole}): {string.Join(", ", players)}");
            }
            _teamsLabel.text = string.Join("\n", teams);
        }

        private void OpenSelected()
        {
            if (_selected == null) return;
            LastReplay = _selected;
            LastPath = _selectedPath;
            Log(LogLevel.Success, $"Loaded {Path.GetFileName(LastPath)} ({LastReplay.MapName}, {LastReplay.Players.Count} players, {LastReplay.Rounds.Count} rounds)");
            Watch();
        }

        /// <summary>Hands the replay to the DontDestroyOnLoad session and switches to the viewer scene.</summary>
        private void Watch()
        {
            if (LastReplay == null) return;
            SetStatus("Opening replay viewer…", null);
            ReplaySession.Open(LastReplay, LastPath);
        }

        private void UpdateButtons()
        {
            _openButton.SetEnabled(_selected != null);
            _watchButton.SetEnabled(LastReplay != null);
        }

        private void ShowFolder()
        {
            string folder = Directory.Exists(_folder) ? _folder : DefaultFolders().Last();
#if UNITY_EDITOR
            Directory.CreateDirectory(folder);
            UnityEditor.EditorUtility.RevealInFinder(folder);
#else
            Directory.CreateDirectory(folder);
            Application.OpenURL(new Uri(folder).AbsoluteUri);
#endif
        }

        // ------------------------------------------------------------------------------------------ log
        private void SetupLog()
        {
            _logList.itemsSource = _lines;
            _logList.makeItem = () =>
            {
                var l = new Label();
                l.AddToClassList("vr-log-line");
                return l;
            };
            _logList.bindItem = (e, i) =>
            {
                var line = _lines[i];
                var label = (Label)e;
                label.text = line.Text;
                foreach (LogLevel level in Enum.GetValues(typeof(LogLevel)))
                    label.EnableInClassList("vr-log--" + level.ToString().ToLowerInvariant(), level == line.Level);
            };
        }

        private void Log(LogLevel level, string text)
        {
            if (level == LogLevel.Error) Debug.LogError("[Replays] " + text);
            _lines.Add(new LogLine(level, text));
            if (_lines.Count > maxLogLines) _lines.RemoveRange(0, _lines.Count - maxLogLines);
            _logDirty = true;
        }

        // ------------------------------------------------------------------------------------------ helpers
        private void SetStatus(string text, string cls)
        {
            _statusLabel.text = text;
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
