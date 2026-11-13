using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using ValoReplay.Import;
using ValoReplay.Play;
using ValoReplay.Replay;

namespace ValoReplay.EditorTools
{
    /// <summary>
    /// Builds the scenes and their assets, in this build order:
    /// Assets/Scenes/VrfImport.unity (camera + UIDocument + <see cref="VrfImportScreen"/>),
    /// Assets/Scenes/ReplayViewer.unity (camera + light + <see cref="ReplayViewer"/> + HUD) and
    /// Assets/Scenes/ScenarioPlayer.unity (first person: ghost <see cref="ReplayViewer"/> + <see cref="RoundPlayController"/>
    /// + HUD with <see cref="PauseMenu"/>, added to the existing scene; its camera and light are kept).
    /// Missing pieces are set up once automatically; the ValoReplay menu redoes any of them.
    /// </summary>
    [InitializeOnLoad]
    internal static class VrfImportSceneSetup
    {
        private const string UiDir = "Assets/UI/VrfImport";
        private const string UxmlPath = UiDir + "/VrfImport.uxml";
        private const string ThemePath = UiDir + "/VrfImportTheme.tss";
        private const string PanelPath = UiDir + "/VrfImportPanelSettings.asset";
        private const string ImportScenePath = "Assets/Scenes/VrfImport.unity";

        private const string ViewerUxmlPath = "Assets/UI/ReplayViewer/ReplayViewer.uxml";
        private const string ViewerScenePath = "Assets/Scenes/ReplayViewer.unity";
        private const string MaterialDir = "Assets/Art/Materials";
        private const string MinimapMaterialPath = MaterialDir + "/ReplayMinimap.mat";
        private const string LineMaterialPath = MaterialDir + "/ReplayLine.mat";
        private const string SmokeMaterialPath = MaterialDir + "/ReplaySmoke.mat";
        private const string PlayUxmlPath = "Assets/UI/RoundPlay/RoundPlay.uxml";
        private const string PlayScenePath = "Assets/Scenes/" + ReplaySession.PlayScene + ".unity";

        private static readonly Color Background = new Color32(0x0F, 0x19, 0x23, 0xFF);

        private static string DonePref(string scene) => "ValoReplay.SceneSetupDone." + scene + "." + Application.dataPath;

        static VrfImportSceneSetup()
        {
            bool playPending = !EditorPrefs.GetBool(DonePref(PlayScenePath));
            if (!NeedsAutoSetup(ImportScenePath) && !NeedsAutoSetup(ViewerScenePath) && !playPending) return;
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                if (string.IsNullOrEmpty(SceneManager.GetActiveScene().path))
                {
                    Debug.Log("[ValoReplay] Save the open scene, then use the ValoReplay menu to create the import / viewer scenes.");
                    return;
                }
                bool createdImport = NeedsAutoSetup(ImportScenePath);
                if (createdImport) CreateImportScene();
                if (NeedsAutoSetup(ViewerScenePath)) CreateViewerScene();
                if (!EditorPrefs.GetBool(DonePref(PlayScenePath))) SetUpPlayScene(auto: true);
                if (createdImport) OpenIfClean(ImportScenePath);
            };
        }

        private static bool NeedsAutoSetup(string scenePath) => !File.Exists(scenePath) && !EditorPrefs.GetBool(DonePref(scenePath));

        [MenuItem("ValoReplay/Create VRF Import Scene")]
        private static void CreateImportSceneMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            CreateImportScene();
            OpenIfClean(ImportScenePath);
        }

        [MenuItem("ValoReplay/Create Replay Viewer Scene")]
        private static void CreateViewerSceneMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            CreateViewerScene();
            OpenIfClean(ViewerScenePath);
        }

        [MenuItem("ValoReplay/Set Up Scenario Player Scene")]
        private static void SetUpPlaySceneMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            SetUpPlayScene(auto: false);
        }

        // ------------------------------------------------------------------------------------------ scenes
        private static void CreateImportScene()
        {
            EditorPrefs.SetBool(DonePref(ImportScenePath), true);
            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            var panel = GetPanelSettings();
            if (uxml == null || panel == null) { Debug.LogError($"[ValoReplay] Missing {UxmlPath} or {ThemePath}."); return; }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            AddCamera(scene);
            var ui = new GameObject("VRF Import UI");
            var doc = ui.AddComponent<UIDocument>();
            doc.panelSettings = panel;
            doc.visualTreeAsset = uxml;
            ui.AddComponent<VrfImportScreen>();
            SceneManager.MoveGameObjectToScene(ui, scene);
            SaveAndClose(scene, ImportScenePath);
        }

        private static void CreateViewerScene()
        {
            EditorPrefs.SetBool(DonePref(ViewerScenePath), true);
            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(ViewerUxmlPath);
            var panel = GetPanelSettings();
            if (uxml == null || panel == null) { Debug.LogError($"[ValoReplay] Missing {ViewerUxmlPath} or {ThemePath}."); return; }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);

            var camera = AddCamera(scene);
            camera.farClipPlane = 2000f;
            camera.fieldOfView = 50f;

            var light = new GameObject("Directional Light").AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            light.shadows = LightShadows.Soft;
            light.transform.rotation = Quaternion.Euler(55f, -30f, 0f);
            SceneManager.MoveGameObjectToScene(light.gameObject, scene);

            var viewerGo = new GameObject("Replay Viewer");
            var viewer = viewerGo.AddComponent<ReplayViewer>();
            var so = new SerializedObject(viewer);
            so.FindProperty("minimapMaterial").objectReferenceValue = GetMinimapMaterial();
            so.FindProperty("lineMaterial").objectReferenceValue = GetLineMaterial();
            so.FindProperty("smokeMaterial").objectReferenceValue = GetSmokeMaterial();
            so.ApplyModifiedPropertiesWithoutUndo();
            SceneManager.MoveGameObjectToScene(viewerGo, scene);

            var hudGo = new GameObject("Replay HUD");
            var doc = hudGo.AddComponent<UIDocument>();
            doc.panelSettings = panel;
            doc.visualTreeAsset = uxml;
            var hud = hudGo.AddComponent<ReplayHud>();
            so = new SerializedObject(hud);
            so.FindProperty("viewer").objectReferenceValue = viewer;
            so.FindProperty("worldCamera").objectReferenceValue = camera;
            so.ApplyModifiedPropertiesWithoutUndo();
            SceneManager.MoveGameObjectToScene(hudGo, scene);

            var cam = camera.gameObject.AddComponent<ReplayCameraController>();
            so = new SerializedObject(cam);
            so.FindProperty("viewer").objectReferenceValue = viewer;
            so.FindProperty("hud").objectReferenceValue = doc;
            so.ApplyModifiedPropertiesWithoutUndo();

            SaveAndClose(scene, ViewerScenePath);
        }

        /// <summary>
        /// Adds round play to ScenarioPlayer.unity (creating it with a camera and sun if it does not exist). Existing
        /// objects stay: the player adopts the scene's Main Camera. Skips a scene that already has round play, and
        /// (when automatic) one that is open with unsaved changes.
        /// </summary>
        private static void SetUpPlayScene(bool auto)
        {
            var uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(PlayUxmlPath);
            var panel = GetPanelSettings();
            if (uxml == null || panel == null) { Debug.LogError($"[ValoReplay] Missing {PlayUxmlPath} or {ThemePath}."); return; }

            var scene = SceneManager.GetSceneByPath(PlayScenePath);
            bool wasLoaded = scene.IsValid() && scene.isLoaded;
            if (wasLoaded && scene.isDirty && auto)
            {
                Debug.Log($"[ValoReplay] {PlayScenePath} has unsaved changes; save it, then use ValoReplay > Set Up Scenario Player Scene.");
                return;
            }
            if (!wasLoaded)
            {
                scene = File.Exists(PlayScenePath)
                    ? EditorSceneManager.OpenScene(PlayScenePath, OpenSceneMode.Additive)
                    : EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Additive);
            }
            EditorPrefs.SetBool(DonePref(PlayScenePath), true);

            if (scene.GetRootGameObjects().Any(r => r.GetComponentInChildren<RoundPlayController>(true) != null))
            {
                Debug.Log($"[ValoReplay] {PlayScenePath} already has round play.");
                if (!wasLoaded) EditorSceneManager.CloseScene(scene, true);
                return;
            }

            var playGo = new GameObject("Round Play");
            var ghosts = playGo.AddComponent<ReplayViewer>();
            var so = new SerializedObject(ghosts);
            so.FindProperty("loadOnStart").boolValue = false;
            so.FindProperty("preferBuiltMap").boolValue = true;
            so.FindProperty("utilityColliders").boolValue = true;
            so.FindProperty("minimapMaterial").objectReferenceValue = GetMinimapMaterial();
            so.FindProperty("lineMaterial").objectReferenceValue = GetLineMaterial();
            so.FindProperty("smokeMaterial").objectReferenceValue = GetSmokeMaterial();
            so.ApplyModifiedPropertiesWithoutUndo();
            var controller = playGo.AddComponent<RoundPlayController>();
            so = new SerializedObject(controller);
            so.FindProperty("ghosts").objectReferenceValue = ghosts;
            so.FindProperty("tracerMaterial").objectReferenceValue = GetLineMaterial();
            so.ApplyModifiedPropertiesWithoutUndo();
            SceneManager.MoveGameObjectToScene(playGo, scene);

            var hudGo = new GameObject("Round Play HUD");
            var doc = hudGo.AddComponent<UIDocument>();
            doc.panelSettings = panel;
            doc.visualTreeAsset = uxml;
            so = new SerializedObject(hudGo.AddComponent<RoundPlayHud>());
            so.FindProperty("round").objectReferenceValue = controller;
            so.ApplyModifiedPropertiesWithoutUndo();
            so = new SerializedObject(hudGo.AddComponent<PauseMenu>());
            so.FindProperty("round").objectReferenceValue = controller;
            so.ApplyModifiedPropertiesWithoutUndo();
            SceneManager.MoveGameObjectToScene(hudGo, scene);

            if (wasLoaded)
            {
                EditorSceneManager.SaveScene(scene, PlayScenePath);
                UpdateBuildScenes();
                Debug.Log($"[ValoReplay] Added round play to {PlayScenePath}.");
            }
            else SaveAndClose(scene, PlayScenePath);
        }

        private static Camera AddCamera(Scene scene)
        {
            var go = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            var cam = go.GetComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Background;
            SceneManager.MoveGameObjectToScene(go, scene);
            return cam;
        }

        private static void SaveAndClose(Scene scene, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            EditorSceneManager.SaveScene(scene, path);
            EditorSceneManager.CloseScene(scene, true);
            UpdateBuildScenes();
            Debug.Log($"[ValoReplay] Saved {path}.");
        }

        /// <summary>Import first, viewer second, scenario player third, anything else after.</summary>
        private static void UpdateBuildScenes()
        {
            var ours = new[] { ImportScenePath, ViewerScenePath, PlayScenePath };
            var others = EditorBuildSettings.scenes.Where(s => !ours.Contains(s.path));
            var ordered = ours.Where(File.Exists).Select(p => new EditorBuildSettingsScene(p, true));
            EditorBuildSettings.scenes = ordered.Concat(others).ToArray();
            AssetDatabase.SaveAssets();
        }

        private static void OpenIfClean(string path)
        {
            bool dirty = Enumerable.Range(0, SceneManager.sceneCount).Any(i => SceneManager.GetSceneAt(i).isDirty);
            if (!dirty && File.Exists(path)) EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
        }

        // ------------------------------------------------------------------------------------------ assets
        private static PanelSettings GetPanelSettings()
        {
            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelPath);
            if (panel != null) return panel;
            var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
            if (theme == null) return null;
            panel = ScriptableObject.CreateInstance<PanelSettings>();
            panel.themeStyleSheet = theme;
            panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            panel.referenceResolution = new Vector2Int(1600, 900);
            panel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            panel.match = 0.5f;
            AssetDatabase.CreateAsset(panel, PanelPath);
            return panel;
        }

        /// <summary>URP Unlit, alpha-clipped and double-sided: the minimap PNG is transparent outside the map.</summary>
        private static Material GetMinimapMaterial()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MinimapMaterialPath);
            if (mat != null) return mat;
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) { Debug.LogWarning("[ValoReplay] URP Unlit shader not found; the minimap floor is disabled."); return null; }
            mat = new Material(shader) { name = "ReplayMinimap" };
            mat.SetColor("_BaseColor", new Color(0.62f, 0.66f, 0.7f, 1f));
            mat.SetFloat("_AlphaClip", 1f);
            mat.SetFloat("_Cutoff", 0.35f);
            mat.EnableKeyword("_ALPHATEST_ON");
            mat.SetFloat("_Cull", (float)CullMode.Off);
            mat.renderQueue = (int)RenderQueue.AlphaTest;
            Directory.CreateDirectory(MaterialDir);
            AssetDatabase.CreateAsset(mat, MinimapMaterialPath);
            return mat;
        }

        /// <summary>URP Unlit, transparent and double-sided: smokes and ground effects (colour via property blocks).</summary>
        private static Material GetSmokeMaterial()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(SmokeMaterialPath);
            if (mat != null) return mat;
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) return null;
            mat = new Material(shader) { name = "ReplaySmoke" };
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 0f);
            mat.SetFloat("_Cull", (float)CullMode.Off);
            mat.SetColor("_BaseColor", new Color(0.75f, 0.78f, 0.82f, 0.9f));
            BaseShaderGUI.SetMaterialKeywords(mat);
            Directory.CreateDirectory(MaterialDir);
            AssetDatabase.CreateAsset(mat, SmokeMaterialPath);
            return mat;
        }

        /// <summary>Sprites/Default multiplies vertex colours, so LineRenderer start/end colours work.</summary>
        private static Material GetLineMaterial()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(LineMaterialPath);
            if (mat != null) return mat;
            var shader = Shader.Find("Sprites/Default");
            if (shader == null) return null;
            mat = new Material(shader) { name = "ReplayLine" };
            Directory.CreateDirectory(MaterialDir);
            AssetDatabase.CreateAsset(mat, LineMaterialPath);
            return mat;
        }
    }
}
