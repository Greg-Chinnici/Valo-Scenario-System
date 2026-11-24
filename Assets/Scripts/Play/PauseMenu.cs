using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using ValoReplay.Replay;

namespace ValoReplay.Play
{
    /// <summary>
    /// Esc menu for round play: pauses (time frozen, cursor free), resume / restart / share / back to replay / back to
    /// import, and live settings. Share saves the takeover so far as a branched .vrp (<see cref="TakeoverRecorder"/>),
    /// copies its path and shows it in the file browser. Every change is applied and saved immediately (<see cref="GameSettings"/>).
    /// Lives in the round play HUD document (the "pause" element of RoundPlay.uxml).
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class PauseMenu : MonoBehaviour
    {
        [SerializeField] private RoundPlayController round;

        /// <summary>Valorant's yaw constant: degrees per mouse count at in-game sensitivity 1.0.</summary>
        private const float ValorantYaw = 0.07f;

        private static readonly (string Name, string Hex)[] Colors =
        {
            ("Green", "#7CFFB2"), ("White", "#FFFFFF"), ("Yellow", "#F5E663"), ("Cyan", "#5CF0FF"),
            ("Pink", "#FF7AD9"), ("Red", "#FF4655"),
        };
        private static readonly int[] FpsLimits = { 0, 60, 120, 144, 165, 240, 360 };

        private VisualElement _pause;
        private Label _title, _info, _shareStatus;
        private Button _share;
        private Slider _sensitivity, _chLength, _chThickness, _chGap, _speed;
        private SliderInt _fov;
        private Toggle _invert, _chDot, _chSpread, _ammo, _shots, _aim, _roundAi, _aiMatrix, _aiThoughts, _mapTextures, _vsync, _shadows, _lighting, _fullscreen;
        private DropdownField _chColor, _fps;

        public bool IsOpen { get; private set; }

        private void OnEnable()
        {
            if (round == null) round = FindFirstObjectByType<RoundPlayController>();
            var root = GetComponent<UIDocument>().rootVisualElement;
            _pause = root.Q("pause");
            _title = root.Q<Label>("pause-title");
            _info = root.Q<Label>("pause-info");
            _share = root.Q<Button>("share-button");
            _shareStatus = root.Q<Label>("share-status");
            _share.clicked += Share;
            root.Q<Button>("resume-button").clicked += () => Open(false);
            root.Q<Button>("restart-button").clicked += () => { Open(false); round.Restart(); };
            root.Q<Button>("spectate-button").clicked += () => { Open(false); round.Spectator?.Toggle(); };
            root.Q<Button>("viewer-button").clicked += ReplaySession.BackToViewer;
            root.Q<Button>("import-button").clicked += ReplaySession.BackToImport;
            root.Q<Button>("reset-button").clicked += () => { GameSettings.ResetToDefaults(); Refresh(); };

            var s = GameSettings.Current;
            _sensitivity = Bind<Slider, float>(root, "set-sensitivity", v => s.sensitivity = v * ValorantYaw);
            _invert = Bind<Toggle, bool>(root, "set-invert", v => s.invertY = v);
            _fov = Bind<SliderInt, int>(root, "set-fov", v => s.horizontalFov = v);
            _chColor = Bind<DropdownField, string>(root, "set-ch-color", v => s.crosshairColor = Colors.FirstOrDefault(c => c.Name == v).Hex ?? s.crosshairColor);
            _chLength = Bind<Slider, float>(root, "set-ch-length", v => s.crosshairLength = v);
            _chThickness = Bind<Slider, float>(root, "set-ch-thickness", v => s.crosshairThickness = v);
            _chGap = Bind<Slider, float>(root, "set-ch-gap", v => s.crosshairGap = v);
            _chDot = Bind<Toggle, bool>(root, "set-ch-dot", v => s.crosshairDot = v);
            _chSpread = Bind<Toggle, bool>(root, "set-ch-spread", v => s.crosshairShowsSpread = v);
            _speed = Bind<Slider, float>(root, "set-speed", v => s.gameSpeed = v);
            _ammo = Bind<Toggle, bool>(root, "set-ammo", v => s.infiniteAmmo = v);
            _shots = Bind<Toggle, bool>(root, "set-shots", v => s.showRecordedShots = v);
            _aim = Bind<Toggle, bool>(root, "set-aim", v => s.showGhostAim = v);
            _roundAi = Bind<Toggle, bool>(root, "set-ai", v => s.roundAi = v);
            _aiMatrix = Bind<Toggle, bool>(root, "set-ai-matrix", v => { s.showAiMatrix = v; if (round != null && round.Ai != null) round.Ai.ShowMatrix = v; });
            _aiThoughts = Bind<Toggle, bool>(root, "set-ai-thoughts", v => { s.showAiThoughts = v; if (round != null && round.Ai != null) round.Ai.ShowThoughts = v; });
            _mapTextures = Bind<Toggle, bool>(root, "set-map-textures", v => s.mapTextures = v);
            _vsync = Bind<Toggle, bool>(root, "set-vsync", v => { s.vSync = v; _fps.SetEnabled(!v); });
            _fps = Bind<DropdownField, string>(root, "set-fps", v => s.fpsLimit = FpsLimits[Mathf.Max(0, _fps.choices.IndexOf(v))]);
            _shadows = Bind<Toggle, bool>(root, "set-shadows", v => s.shadows = v);
            _lighting = Bind<Toggle, bool>(root, "set-lighting", v => s.enhancedLighting = v);
            _fullscreen = Bind<Toggle, bool>(root, "set-fullscreen", v => s.fullscreen = v);

            _chColor.choices = Colors.Select(c => c.Name).ToList();
            _fps.choices = FpsLimits.Select(f => f == 0 ? "Unlimited" : f.ToString()).ToList();
            Open(false);
        }

        /// <summary>Wires a control: on change, write the setting, save and apply (the setting instance is re-read
        /// so Reset to defaults keeps working).</summary>
        private T Bind<T, TValue>(VisualElement root, string name, System.Action<TValue> write) where T : VisualElement, INotifyValueChanged<TValue>
        {
            var control = root.Q<T>(name);
            control.RegisterValueChangedCallback(e =>
            {
                write(e.newValue);
                GameSettings.Save();
            });
            return control;
        }

        /// <summary>Controls show the saved values (on open and after a reset).</summary>
        private void Refresh()
        {
            var s = GameSettings.Current;
            _sensitivity.SetValueWithoutNotify(s.sensitivity / ValorantYaw);
            _invert.SetValueWithoutNotify(s.invertY);
            _fov.SetValueWithoutNotify(Mathf.RoundToInt(s.horizontalFov));
            var color = Colors.FirstOrDefault(c => string.Equals(c.Hex, s.crosshairColor, System.StringComparison.OrdinalIgnoreCase));
            _chColor.SetValueWithoutNotify(color.Name ?? Colors[0].Name);
            _chLength.SetValueWithoutNotify(s.crosshairLength);
            _chThickness.SetValueWithoutNotify(s.crosshairThickness);
            _chGap.SetValueWithoutNotify(s.crosshairGap);
            _chDot.SetValueWithoutNotify(s.crosshairDot);
            _chSpread.SetValueWithoutNotify(s.crosshairShowsSpread);
            _speed.SetValueWithoutNotify(s.gameSpeed);
            _ammo.SetValueWithoutNotify(s.infiniteAmmo);
            _shots.SetValueWithoutNotify(s.showRecordedShots);
            _aim.SetValueWithoutNotify(s.showGhostAim);
            _roundAi.SetValueWithoutNotify(s.roundAi);
            _aiMatrix.SetValueWithoutNotify(s.showAiMatrix);
            _aiThoughts.SetValueWithoutNotify(s.showAiThoughts);
            _mapTextures.SetValueWithoutNotify(s.mapTextures);
            _vsync.SetValueWithoutNotify(s.vSync);
            int fi = System.Array.IndexOf(FpsLimits, s.fpsLimit);
            _fps.SetValueWithoutNotify(_fps.choices[fi < 0 ? 0 : fi]);
            _fps.SetEnabled(!s.vSync);
            _shadows.SetValueWithoutNotify(s.shadows);
            _lighting.SetValueWithoutNotify(s.enhancedLighting);
            _fullscreen.SetValueWithoutNotify(s.fullscreen);
        }

        public void Open(bool open)
        {
            IsOpen = open;
            _pause.EnableInClassList("rp-pause--open", open);
            if (open)
            {
                Refresh();
                if (round != null && round.Replay != null)
                {
                    var p = round.Replay.Players[round.Player];
                    int ri = round.Replay.RoundAt(round.Now);
                    _info.text = $"{p.AgentName} · {round.Replay.MapName} · round {(ri >= 0 ? round.Replay.Rounds[ri].Number + 1 : 0)}";
                }
                else _info.text = round != null ? round.Error ?? "" : "";
                _title.text = round != null && round.RoundOver ? "ROUND OVER" : "PAUSED";
                RefreshShare(null);
            }
            if (round != null) round.SetPaused(open);
        }

        /// <summary>The share button and the line under it; message replaces the default text.</summary>
        private void RefreshShare(string message)
        {
            var rec = round != null ? round.Recorder : null;
            _share.SetEnabled(rec != null && rec.HasData && !rec.Saving);
            if (message != null) _shareStatus.text = message;
            else if (rec == null) _shareStatus.text = round != null && round.Replay != null ? "Sharing records the round AI's play: turn Round AI on and restart." : "";
            else if (rec.SavedPath != null) _shareStatus.text = $"Saved {Path.GetFileName(rec.SavedPath)}. Share again to update it.";
            else if (!rec.HasData) _shareStatus.text = "Nothing to share yet: play from the takeover first.";
            else _shareStatus.text = round.RoundOver ? "Saves this round as its own replay." : "Saves the round so far as its own replay.";
            _shareStatus.style.display = string.IsNullOrEmpty(_shareStatus.text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        /// <summary>Saves the branch, copies its path and shows it in the file browser.</summary>
        private async void Share()
        {
            var rec = round != null ? round.Recorder : null;
            if (rec == null || rec.Saving || !rec.HasData) return;
            RefreshShare("Saving…");
            _share.SetEnabled(false);
            string message;
            try
            {
                string path = await rec.SaveAsync();
                GUIUtility.systemCopyBuffer = path;
                Reveal(path);
                message = $"Saved {Path.GetFileName(path)} (path copied). Send the file: it opens from the replay library.";
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
                message = $"Could not save: {e.Message}";
            }
            if (this != null) RefreshShare(message);     // the scene may have changed while it saved
        }

        /// <summary>Selects the file in Finder / Explorer (elsewhere opens its folder).</summary>
        private static void Reveal(string path)
        {
            try
            {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
                System.Diagnostics.Process.Start("open", $"-R \"{path}\"");
#elif UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
#else
                Application.OpenURL(new System.Uri(Path.GetDirectoryName(path)).AbsoluteUri);
#endif
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[PauseMenu] Could not show {path}: {e.Message}");
            }
        }

        private void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame) Open(!IsOpen);
        }
    }
}
