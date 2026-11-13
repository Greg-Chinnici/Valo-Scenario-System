using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using ValoReplay.Replay;

namespace ValoReplay.Play
{
    /// <summary>
    /// Esc menu for round play: pauses (time frozen, cursor free), resume / restart / back to replay / back to import,
    /// and live settings. Every change is applied and saved immediately (<see cref="GameSettings"/>).
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
        private Label _info;
        private Slider _sensitivity, _chLength, _chThickness, _chGap, _speed;
        private SliderInt _fov;
        private Toggle _invert, _chDot, _chSpread, _ammo, _shots, _aim, _roundAi, _aiMatrix, _mapTextures, _vsync, _shadows, _fullscreen;
        private DropdownField _chColor, _fps;

        public bool IsOpen { get; private set; }

        private void OnEnable()
        {
            if (round == null) round = FindFirstObjectByType<RoundPlayController>();
            var root = GetComponent<UIDocument>().rootVisualElement;
            _pause = root.Q("pause");
            _info = root.Q<Label>("pause-info");
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
            _mapTextures = Bind<Toggle, bool>(root, "set-map-textures", v => s.mapTextures = v);
            _vsync = Bind<Toggle, bool>(root, "set-vsync", v => { s.vSync = v; _fps.SetEnabled(!v); });
            _fps = Bind<DropdownField, string>(root, "set-fps", v => s.fpsLimit = FpsLimits[Mathf.Max(0, _fps.choices.IndexOf(v))]);
            _shadows = Bind<Toggle, bool>(root, "set-shadows", v => s.shadows = v);
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
            _mapTextures.SetValueWithoutNotify(s.mapTextures);
            _vsync.SetValueWithoutNotify(s.vSync);
            int fi = System.Array.IndexOf(FpsLimits, s.fpsLimit);
            _fps.SetValueWithoutNotify(_fps.choices[fi < 0 ? 0 : fi]);
            _fps.SetEnabled(!s.vSync);
            _shadows.SetValueWithoutNotify(s.shadows);
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
            }
            if (round != null) round.SetPaused(open);
        }

        private void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame) Open(!IsOpen);
        }
    }
}
