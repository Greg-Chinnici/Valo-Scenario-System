using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using ValorantReplay.Playback;
using ValorantReplay.Sim;
using ValoReplay.Maps;

namespace ValoReplay.EditorTools.AI
{
    /// <summary>
    /// ValoReplay > AI: the round AI playing whole rounds by itself on a map's geometry (no replay needed), headless.
    /// The map is a built map (Assets/Maps, instantiated into a temporary scene) or whatever is open: for a map of your
    /// own, open its scene and put "Site A" / "Site B" (optionally a trigger SphereCollider for the radius) and
    /// "Attacker Spawn" / "Defender Spawn" GameObjects on the floor.
    /// The agents play like round play's AI (SelfPlaySetup): attack timing learned from replays (of this map when a
    /// replay or folder is given, else from every replay in the library), presets with measured execution. The report
    /// says which tempo and profiles a run used.
    /// Batch: Unity.exe -batchmode -projectPath . -executeMethod ValoReplay.EditorTools.AI.SelfPlayWindow.RunBatch
    ///   -simMap Ascent [-simRounds 200] [-simReplay file.vrp|folder] [-simProfiles population|replay|knobs] [-simSweep]
    ///   [-simReport report.txt]
    /// </summary>
    public sealed class SelfPlayWindow : EditorWindow
    {
        const string OpenScene = "(open scene)";

        [SerializeField] int mapIndex;
        [SerializeField] int rounds = 200;
        [SerializeField] string replayPath = "";
        [SerializeField] SelfPlayProfiles profiles = SelfPlayProfiles.Population;
        [SerializeField] Vector2 scroll;
        [SerializeField] string output = "";

        [MenuItem("ValoReplay/AI/Self-Play...")]
        static void Open() => GetWindow<SelfPlayWindow>("AI Self-Play");

        [MenuItem("ValoReplay/AI/Self-Play 200 Rounds (Open Scene)")]
        static void RunOpenScene()
        {
            string text = Run(OpenScene, 200, null, false, SelfPlayProfiles.Population);
            Debug.Log(text);
        }

        void OnGUI()
        {
            var names = MapNames();
            mapIndex = EditorGUILayout.Popup("Map", Mathf.Clamp(mapIndex, 0, names.Count - 1), names.ToArray());
            rounds = EditorGUILayout.IntSlider("Rounds", rounds, 1, 2000);
            using (new EditorGUILayout.HorizontalScope())
            {
                replayPath = EditorGUILayout.TextField(new GUIContent("Replay (optional)",
                    "A .vrp of the same map, or a folder of them: the AI learns when attacks go in and where players look from this map's replays, " +
                    "and the geometry grid is compared with the one players' positions give. Without one, attack timing is learned from every replay in the library."), replayPath);
                if (GUILayout.Button("…", GUILayout.Width(24)))
                {
                    string p = EditorUtility.OpenFilePanel("Replay", Path.GetDirectoryName(replayPath) ?? "", "vrp");
                    if (!string.IsNullOrEmpty(p)) replayPath = p;
                }
            }
            profiles = (SelfPlayProfiles)EditorGUILayout.EnumPopup(new GUIContent("Execution",
                "Population: the presets with the population's measured execution (round play's perception). ReplayPlayers: execution fitted from " +
                "the replay's players. Knobs: unmeasured presets (not what round play runs)."), profiles);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Run self-play")) output = Run(names[mapIndex], rounds, replayPath, false, profiles);
                if (GUILayout.Button(new GUIContent("Run + director sensitivity check",
                        "Also plays with single TeamDirector constants overridden, to see how much the result depends on them. " +
                        "Not values for TeamDirector: those are tuned upstream against real rounds (vrpsim calibrate).")))
                    output = Run(names[mapIndex], rounds, replayPath, true, profiles);
                if (GUILayout.Button("Copy", GUILayout.Width(50))) EditorGUIUtility.systemCopyBuffer = output;
            }
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.TextArea(output, EditorStyles.textArea, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }

        static List<string> MapNames()
        {
            var l = new List<string> { OpenScene };
            var reg = MapRegistry.Instance;
            if (reg != null) l.AddRange(reg.maps.Where(e => e.prefab != null).Select(e => e.displayName));
            return l;
        }

        // ------------------------------------------------------------------ batch mode

        public static void RunBatch()
        {
            int code = 0;
            try
            {
                string map = Arg("-simMap") ?? OpenScene;
                int n = int.TryParse(Arg("-simRounds"), out int k) ? k : 200;
                string text = Run(map, n, Arg("-simReplay"), Environment.GetCommandLineArgs().Contains("-simSweep"), ParseProfiles(Arg("-simProfiles")));
                Debug.Log(text);
                string report = Arg("-simReport");
                if (!string.IsNullOrEmpty(report)) File.WriteAllText(report, text);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                code = 1;
            }
            EditorApplication.Exit(code);
        }

        static SelfPlayProfiles ParseProfiles(string s)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "": case "population": return SelfPlayProfiles.Population;
                case "replay": case "replayplayers": return SelfPlayProfiles.ReplayPlayers;
                case "knobs": case "unmeasured": return SelfPlayProfiles.Knobs;
                default: throw new ArgumentException($"-simProfiles {s}: expected population, replay or knobs");
            }
        }

        static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        // ------------------------------------------------------------------ the run

        /// <summary>Builds the geometry SimMap, optionally compares it with a replay's, plays the rounds with the AI round
        /// play runs (SelfPlaySetup; with sweep, also with single director constants overridden, as a sensitivity check)
        /// and returns the report.</summary>
        public static string Run(string mapName, int rounds, string replayPath, bool sweep, SelfPlayProfiles profiles)
        {
            var sb = new StringBuilder();
            bool batch = Application.isBatchMode;
            using var ctx = MapContext.Create(mapName);
            try
            {
                Progress(batch, "Sampling map geometry", 0f);
                var settings = new GeometrySimMap.Settings();
                ctx.Added = GeometrySimMap.AddMissingColliders(ctx.MarkerRoots);
                var map = GeometrySimMap.Build(ctx.IsMap, ctx.MarkerRoots, ctx.LevelPath, settings, out var report);
                sb.AppendLine($"{ctx.Name}: {report}");
                if (ctx.Added.Count > 0)
                    sb.AppendLine($"  added temporary colliders to {ctx.Added.Count} opaque meshes that had none: " +
                                  string.Join(", ", ctx.Added.OrderByDescending(c => c.bounds.size.x * c.bounds.size.z).Take(8).Select(c => $"{c.transform.parent?.name}/{c.name}")) + (ctx.Added.Count > 8 ? ", …" : ""));
                sb.AppendLine($"  sites {string.Join(", ", map.Sites.Select(s => $"{s.Name} r{s.Radius:0}"))}");
                sb.AppendLine();

                SimMap replayMap = null;
                Progress(batch, "Loading replays", 0f);
                var replays = SelfPlaySetup.ReplaySet.Load(replayPath, ctx.LevelPath);
                var replay = replays.First;
                if (replay != null)
                {
                    Progress(batch, "Comparing with the replay grid", 0f);
                    var rep = SimMap.FromReplays(new[] { replay });
                    sb.AppendLine($"replay {Path.GetFileName(replay.MatchId)} ({replay.MapName})");
                    sb.AppendLine(GridComparison.Compare(map, report, rep, ctx.IsMap, settings));
                    replayMap = rep;
                }
                else if (!string.IsNullOrEmpty(replayPath)) sb.AppendLine($"no replay of {ctx.Name} at {replayPath}: grid comparison skipped\n");

                Progress(batch, "Learning tempo and looks, fitting profiles", 0f);
                var setup = SelfPlaySetup.Create(ctx.Name, replays, profiles, replayMap);
                sb.AppendLine(setup.Describe());

                var variants = new List<DirectorTweaks> { new DirectorTweaks() };
                if (sweep)
                {
                    variants.Add(new DirectorTweaks { ExecuteAt = 75f });
                    variants.Add(new DirectorTweaks { ExecuteAt = 35f });
                    variants.Add(new DirectorTweaks { Plan = AttackPlan.Rush });
                    variants.Add(new DirectorTweaks { StackThreshold = 2 });
                    variants.Add(new DirectorTweaks { StackThreshold = 99 });
                }
                var batches = new List<SelfPlay.Batch>();
                for (int v = 0; v < variants.Count; v++)
                {
                    int vi = v;
                    var b = SelfPlay.Play(map, setup, ctx.IsMap, rounds, 0, variants[v], (i, n) =>
                        Progress(batch, $"Self-play {variants[vi]} ({vi + 1}/{variants.Count}): round {i + 1}/{n}", (float)i / n));
                    batches.Add(b);
                    sb.AppendLine(SelfPlay.Summary(b));
                    if (b.Cancelled) break;
                }
                if (replayMap != null && !batches[batches.Count - 1].Cancelled)
                {
                    // the same AI on the grid players' positions give (what round play uses today), for comparison
                    foreach (var gs in map.Sites)
                        if (replayMap.SiteNamed(gs.Name) == null)
                        {
                            int n = replayMap.Grid.Nearest(gs.Center, 12f);
                            if (n >= 0) replayMap.Sites.Add(new SimMap.Site { Name = gs.Name, Center = replayMap.Grid.Position(n), Radius = gs.Radius });
                        }
                    replayMap.Sites.Sort((a, c) => string.CompareOrdinal(a.Name, c.Name));
                    // FromReplays puts each spawn at the mean barrier-drop position of a team, which on a two-lane spawn
                    // (Ascent's attackers split A / B) is between the lanes and off the grid: start from the geometry spawns
                    sb.AppendLine($"Replay grid spawns (barrier-drop centroids) on its grid: attacker {(replayMap.Grid.Nearest(replayMap.AttackerSpawn, 3f) >= 0 ? "yes" : "no")}, defender {(replayMap.Grid.Nearest(replayMap.DefenderSpawn, 3f) >= 0 ? "yes" : "no")}; using the geometry spawns snapped onto it");
                    int sa = replayMap.Grid.Nearest(map.AttackerSpawn, 40f), sd = replayMap.Grid.Nearest(map.DefenderSpawn, 40f);
                    if (sa >= 0) replayMap.AttackerSpawn = replayMap.Grid.Position(sa);
                    if (sd >= 0) replayMap.DefenderSpawn = replayMap.Grid.Position(sd);
                    // the same learned behaviour and profiles (Play replaces the replay map's own), so only the grid differs
                    var rb = SelfPlay.Play(replayMap, setup, ctx.IsMap, rounds, 0, new DirectorTweaks(), (i, n) =>
                        Progress(batch, $"Self-play on the replay grid: round {i + 1}/{n}", (float)i / n));
                    sb.AppendLine("Same AI (same tempo, looks and profiles) on the replay grid (sites from plants, geometry spawns):");
                    sb.AppendLine(SelfPlay.Summary(rb));
                }
                if (batches.Count > 1)
                {
                    sb.AppendLine("Director sensitivity check (same seeds, one TeamDirector constant overridden per row). It shows how much the result " +
                                  "depends on a constant on this map's geometry; it is not a set of values to put into TeamDirector: those are tuned " +
                                  "upstream against real rounds (vrpsim calibrate). A fixed execute replaces the learned tempo.");
                    foreach (var b in batches)
                        sb.AppendLine($"  {b.Tweaks,-40} attackers win {100 * b.AttackerWinRate,5:0.0}%, plant {100f * b.Rounds.Count(r => r.Result.Planted) / Math.Max(1, b.Rounds.Count),5:0.0}%, " +
                                      $"duration {b.Rounds.Average(r => r.Result.Duration),5:0.0} s, kills {b.Rounds.Average(r => r.Result.Kills.Count),4:0.0}, rotated {100f * b.Rounds.Count(r => r.AttackerRotations > 0) / Math.Max(1, b.Rounds.Count),5:0.0}%");
                }
            }
            finally
            {
                if (!batch) EditorUtility.ClearProgressBar();
            }
            return sb.ToString();
        }

        static bool Progress(bool batch, string what, float t)
        {
            if (batch) return true;
            return !EditorUtility.DisplayCancelableProgressBar("AI self-play", what, t);
        }

        /// <summary>The map's colliders and marker roots: a built map in a temporary additive scene, or the open scenes.</summary>
        sealed class MapContext : IDisposable
        {
            public string Name = "", LevelPath = "";
            public Func<Collider, bool> IsMap;
            public List<Transform> MarkerRoots = new List<Transform>();
            Scene temp;
            bool haveTemp;
            GameObject instance;
            public List<Collider> Added = new List<Collider>();

            public static MapContext Create(string mapName)
            {
                var ctx = new MapContext();
                if (mapName == OpenScene)
                {
                    ctx.Name = SceneManager.GetActiveScene().name;
                    for (int s = 0; s < SceneManager.sceneCount; s++)
                        foreach (var go in SceneManager.GetSceneAt(s).GetRootGameObjects())
                        {
                            ctx.MarkerRoots.Add(go.transform);
                            // a built map placed in the scene: its catalog entry gives fallback sites and spawns
                            var src = PrefabUtility.GetCorrespondingObjectFromSource(go);
                            var entry = src != null ? MapRegistry.Instance?.maps.Find(e => e.prefab == src) : null;
                            if (entry != null && ctx.LevelPath == "") ctx.LevelPath = entry.levelPath;
                        }
                    ctx.IsMap = c => true;
                    return ctx;
                }
                var reg = MapRegistry.Instance;
                var e = reg?.maps.Find(m => m.prefab != null && m.displayName.Equals(mapName, StringComparison.OrdinalIgnoreCase));
                if (e == null) throw new ArgumentException($"no built map '{mapName}' (run ValoReplay > Maps > Build)");
                ctx.Name = e.displayName;
                ctx.LevelPath = e.levelPath;
                // an untitled scene cannot sit next to an additive one: replace it (after asking, if it has changes)
                bool single = string.IsNullOrEmpty(SceneManager.GetActiveScene().path);
                if (single && !Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                    throw new OperationCanceledException("the open scene was not saved");
                ctx.temp = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, single ? NewSceneMode.Single : NewSceneMode.Additive);
                ctx.haveTemp = !single;
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(e.prefab, ctx.temp);
                ctx.instance = inst;
                var root = inst.transform;
                ctx.MarkerRoots.Add(root);
                ctx.IsMap = c => c.transform.IsChildOf(root);
                Physics.SyncTransforms();
                return ctx;
            }

            public void Dispose()
            {
                foreach (var c in Added) if (c != null) UnityEngine.Object.DestroyImmediate(c);
                if (haveTemp && temp.IsValid()) EditorSceneManager.CloseScene(temp, true);
                else if (instance != null) UnityEngine.Object.DestroyImmediate(instance);
            }
        }
    }
}
