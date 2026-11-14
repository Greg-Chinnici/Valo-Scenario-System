using UnityEngine;
using UnityEngine.UIElements;

namespace ValoReplay.Replay
{
    /// <summary>
    /// Full-screen loading overlay with a spinner that survives scene loads (DontDestroyOnLoad). Shown before an async
    /// scene switch, updated with <see cref="SetStatus"/>, and faded out with <see cref="Hide"/> by whoever finishes
    /// loading. Built in code on the caller's PanelSettings, so it needs no assets of its own.
    /// </summary>
    public sealed class LoadingScreen : MonoBehaviour
    {
        private static LoadingScreen _instance;

        private VisualElement _overlay, _spinner;
        private Label _title, _status;
        private float _fadeStart = -1f;
        private const float FadeSeconds = 0.25f;

        public static bool IsVisible => _instance != null;

        /// <summary>Shows (or retitles) the overlay. <paramref name="panelSettings"/>: any panel the current scene uses.</summary>
        public static void Show(PanelSettings panelSettings, string title, string status = "")
        {
            if (_instance == null)
            {
                if (panelSettings == null) return;
                var go = new GameObject("[Loading Screen]");
                go.SetActive(false);                     // enable only once the panel is assigned
                DontDestroyOnLoad(go);
                var doc = go.AddComponent<UIDocument>();
                doc.panelSettings = panelSettings;
                doc.sortingOrder = 1000;                 // above every HUD on the same panel
                _instance = go.AddComponent<LoadingScreen>();
                go.SetActive(true);
            }
            _instance.Build();
            _instance._title.text = title;
            _instance._status.text = status;
            _instance._fadeStart = -1f;
            _instance._overlay.style.opacity = 1f;
        }

        public static void SetStatus(string status)
        {
            if (_instance != null && _instance._status != null) _instance._status.text = status;
        }

        /// <summary>Fades out and removes the overlay.</summary>
        public static void Hide()
        {
            if (_instance != null && _instance._fadeStart < 0f) _instance._fadeStart = Time.unscaledTime;
        }

        private void Build()
        {
            if (_overlay != null) return;
            var root = GetComponent<UIDocument>().rootVisualElement;
            _overlay = new VisualElement();
            var s = _overlay.style;
            s.position = Position.Absolute;
            s.left = s.top = s.right = s.bottom = 0;
            s.backgroundColor = (Color)new Color32(0x0F, 0x19, 0x23, 0xFF);     // solid navy: nothing half-built shows through
            s.alignItems = Align.Center;
            s.justifyContent = Justify.Center;

            _spinner = new VisualElement();
            var sp = _spinner.style;
            sp.width = sp.height = 54;
            sp.borderTopWidth = sp.borderBottomWidth = sp.borderLeftWidth = sp.borderRightWidth = 5;
            sp.borderTopLeftRadius = sp.borderTopRightRadius = sp.borderBottomLeftRadius = sp.borderBottomRightRadius = 27;
            sp.borderTopColor = (Color)new Color32(0xFF, 0x46, 0x55, 0xFF);
            sp.borderRightColor = (Color)new Color32(0xFF, 0x46, 0x55, 0x55);
            sp.borderBottomColor = sp.borderLeftColor = new Color(1f, 1f, 1f, 0.08f);
            sp.marginBottom = 22;
            _overlay.Add(_spinner);

            _title = new Label();
            _title.style.fontSize = 20;
            _title.style.unityFontStyleAndWeight = FontStyle.Bold;
            _title.style.letterSpacing = 3;
            _title.style.color = (Color)new Color32(0xEC, 0xE8, 0xE1, 0xFF);
            _overlay.Add(_title);

            _status = new Label();
            _status.style.fontSize = 12;
            _status.style.marginTop = 6;
            _status.style.color = (Color)new Color32(0x8A, 0x99, 0xA6, 0xFF);
            _overlay.Add(_status);

            root.Add(_overlay);
        }

        private void Update()
        {
            if (_spinner == null) return;
            // unscaled: keeps turning while a scene is paused or loading
            _spinner.style.rotate = new Rotate(Time.unscaledTime * 360f % 360f);
            if (_fadeStart < 0f) return;
            float u = (Time.unscaledTime - _fadeStart) / FadeSeconds;
            _overlay.style.opacity = Mathf.Clamp01(1f - u);
            if (u >= 1f) Destroy(gameObject);
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }
    }
}
