using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using ValorantReplay.Playback;

namespace ValoReplay.Replay
{
    /// <summary>
    /// Carries the loaded replay between scenes (DontDestroyOnLoad singleton): import -> viewer -> round play and back.
    /// </summary>
    public sealed class ReplaySession : MonoBehaviour
    {
        public const string ImportScene = "VrfImport";
        public const string ViewerScene = "ReplayViewer";
        public const string PlayScene = "ScenarioPlayer";

        public static ReplaySession Instance { get; private set; }

        /// <summary>Always in Unity space (converted on <see cref="Open"/>).</summary>
        public VrpReplay Replay { get; private set; }
        /// <summary>The .vrp the replay was saved to / loaded from.</summary>
        public string VrpPath { get; private set; }

        /// <summary>Round play: the player index taken over and the replay time it happened at.</summary>
        public int TakeoverPlayer { get; private set; } = -1;
        public float TakeoverTime { get; private set; }
        /// <summary>Where the viewer should resume (and its follow target) when coming back; -1 = start normally.</summary>
        public float ViewerResumeTime { get; set; } = -1f;
        public int ViewerResumeFollow { get; set; } = -1;
        /// <summary>A bookmark being opened: the viewer HUD restores its view (intel, map, camera) once loaded.</summary>
        public ReplayBookmark PendingView { get; set; }

        /// <summary>Hands a replay to the viewer scene and loads it.</summary>
        public static void Open(VrpReplay replay, string vrpPath)
        {
            Ensure();
            replay.ConvertToUnitySpace();
            Instance.Replay = replay;
            Instance.VrpPath = vrpPath;
            Instance.ViewerResumeTime = -1f;
            Instance.ViewerResumeFollow = -1;
            Instance.PendingView = null;
            SceneManager.LoadScene(ViewerScene);
        }

        /// <summary>Opens the replay in the viewer at a bookmark, paused, with the bookmark's view.</summary>
        public static void OpenAt(VrpReplay replay, string vrpPath, ReplayBookmark bookmark)
        {
            Open(replay, vrpPath);
            Instance.ViewerResumeTime = Mathf.Clamp(bookmark.time, 0f, replay.Duration);
            Instance.ViewerResumeFollow = bookmark.follow;
            Instance.PendingView = bookmark;
        }

        /// <summary>
        /// Play the round in first person as <paramref name="player"/> from <paramref name="time"/>: shows the loading
        /// screen and loads the scenario scene asynchronously (the scene hides the loading screen once you are placed).
        /// </summary>
        public static void PlayAs(int player, float time)
        {
            if (Instance == null || Instance.Replay == null || Instance._loading) return;
            var r = Instance.Replay;
            Instance.TakeoverPlayer = player;
            Instance.TakeoverTime = time;
            Instance.ViewerResumeTime = time;
            Instance.ViewerResumeFollow = player;

            int round = r.RoundAt(time);
            var panel = FindFirstObjectByType<UIDocument>()?.panelSettings;
            LoadingScreen.Show(panel, $"LOADING {r.MapName.ToUpperInvariant()}",
                $"Playing as {r.Players[player].AgentName}{(round >= 0 ? $" · round {r.Rounds[round].Number + 1}" : "")}");
            Instance.StartCoroutine(Instance.LoadAsync(PlayScene));
        }

        private bool _loading;

        private IEnumerator LoadAsync(string scene)
        {
            _loading = true;
            Time.timeScale = 1f;
            var op = SceneManager.LoadSceneAsync(scene);
            while (op != null && !op.isDone)
            {
                LoadingScreen.SetStatus($"Loading scene  {op.progress / 0.9f * 100f:0}%");
                yield return null;
            }
            _loading = false;
            if (op == null) LoadingScreen.Hide();     // scene missing from Build Settings: Unity logged why
        }

        /// <summary>Back from round play to the viewer, at the moment the takeover started.</summary>
        public static void BackToViewer()
        {
            if (Instance != null) Instance.TakeoverPlayer = -1;
            SceneManager.LoadScene(ViewerScene);
        }

        /// <summary>Back to the import screen; the session (and its replay) stays alive.</summary>
        public static void BackToImport() => SceneManager.LoadScene(ImportScene);

        private static void Ensure()
        {
            if (Instance == null) new GameObject("[ReplaySession]").AddComponent<ReplaySession>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }
    }
}
