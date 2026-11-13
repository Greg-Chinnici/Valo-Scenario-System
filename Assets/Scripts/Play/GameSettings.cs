using System;
using UnityEngine;

namespace ValoReplay.Play
{
    /// <summary>
    /// Player settings, saved as JSON in PlayerPrefs. Read <see cref="Current"/> anywhere; after changing a field,
    /// call <see cref="Save"/> (raises <see cref="Changed"/> so live systems re-apply).
    /// </summary>
    [Serializable]
    public sealed class GameSettings
    {
        private const string PrefKey = "ValoReplay.Settings";

        // mouse / view
        public float sensitivity = 0.07f;          // degrees per mouse count
        public bool invertY;
        public float horizontalFov = 103f;
        // crosshair
        public string crosshairColor = "#7CFFB2";
        public float crosshairLength = 6f;
        public float crosshairThickness = 2f;
        public float crosshairGap = 3f;
        public bool crosshairDot = true;
        public bool crosshairShowsSpread = true;
        // gameplay
        public float gameSpeed = 1f;               // Time.timeScale while playing
        public bool infiniteAmmo;
        public bool showRecordedShots = true;
        public bool showGhostAim = true;
        public bool roundAi = true;                // others turn into AI when they notice you (applies on restart)
        public bool showAiMatrix;                  // F3: each AI agent's action and scores
        public bool mapTextures = true;            // off: the map in plain greys
        // video
        public bool vSync = true;
        public int fpsLimit = 0;                   // 0 = unlimited
        public bool shadows = true;
        public bool fullscreen = true;

        public static event Action Changed;

        private static GameSettings _current;

        public static GameSettings Current
        {
            get
            {
                if (_current != null) return _current;
                _current = new GameSettings();
                try
                {
                    string json = PlayerPrefs.GetString(PrefKey, "");
                    if (json.Length > 0) JsonUtility.FromJsonOverwrite(json, _current);
                }
                catch (Exception e) { Debug.LogWarning($"[GameSettings] Ignoring saved settings: {e.Message}"); }
                return _current;
            }
        }

        public static void Save()
        {
            PlayerPrefs.SetString(PrefKey, JsonUtility.ToJson(Current));
            PlayerPrefs.Save();
            Changed?.Invoke();
        }

        /// <summary>Back to defaults, in place (anything holding <see cref="Current"/> keeps working).</summary>
        public static void ResetToDefaults()
        {
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(new GameSettings()), Current);
            Save();
        }

        public Color CrosshairColor => ColorUtility.TryParseHtmlString(crosshairColor, out var c) ? c : new Color(0.49f, 1f, 0.7f);

        /// <summary>Applies the video settings (the rest is read live by the systems that use it).</summary>
        public void ApplyVideo()
        {
            QualitySettings.vSyncCount = vSync ? 1 : 0;
            Application.targetFrameRate = vSync || fpsLimit <= 0 ? -1 : fpsLimit;
            if (Screen.fullScreen != fullscreen) Screen.fullScreen = fullscreen;
            foreach (var light in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (light.type == LightType.Directional) light.shadows = shadows ? LightShadows.Soft : LightShadows.None;
        }
    }
}
