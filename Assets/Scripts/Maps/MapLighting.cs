using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ValoReplay.Maps
{
    /// <summary>
    /// Lighting for the built maps. Their grey, flat-shaded surfaces carry no detail of their own, so shape comes from
    /// light. The look uses a strong warm sun with long shadows, a cool sky fill (so the shadow side reads blue
    /// rather than black), a light distance haze, and a filmic grade: ACES, a touch of bloom on the brightest faces,
    /// split toning and a mild vignette. Ambient occlusion lives on the renderer asset (PC_Renderer).
    /// <see cref="Enhanced"/> false puts back the scene's own sun, ambient, fog and camera settings.
    /// One per scene, made by <see cref="Ensure"/>.
    /// </summary>
    public sealed class MapLighting : MonoBehaviour
    {
        // sun: about 40 degrees up, so walls throw shadows a little longer than they are tall
        static readonly Quaternion SunRotation = Quaternion.Euler(40f, 140f, 0f);
        static readonly Color SunColor = new Color(1f, 0.9f, 0.76f);
        const float SunIntensity = 2.6f;
        // ambient gradient: cool sky above, neutral at the horizon, a warm dark bounce from the ground
        static readonly Color SkyColor = new Color(0.34f, 0.42f, 0.56f);
        static readonly Color EquatorColor = new Color(0.24f, 0.25f, 0.27f);
        static readonly Color GroundColor = new Color(0.12f, 0.1f, 0.08f);
        // haze: clear within fighting range, softens the far side of the map and the vista
        static readonly Color FogColor = new Color(0.6f, 0.66f, 0.74f);
        const float FogStart = 70f, FogEnd = 700f;

        static MapLighting _instance;

        /// <summary>The scene's MapLighting, made on first use.</summary>
        public static MapLighting Ensure()
        {
            if (_instance == null) _instance = new GameObject("Map Lighting").AddComponent<MapLighting>();
            return _instance;
        }

        bool _enhanced = true;
        bool _saved;
        Light _sun;
        Volume _volume;
        Camera _camera;
        bool _cameraPost;
        AntialiasingMode _cameraAa;

        // the scene's own settings, restored when Enhanced goes off
        Quaternion _sunRotation;
        Color _sunColor;
        float _sunIntensity, _sunShadowStrength;
        AmbientMode _ambientMode;
        Color _ambientSky, _ambientEquator, _ambientGround;
        float _ambientIntensity;
        bool _fog;
        FogMode _fogMode;
        Color _fogColor;
        float _fogStart, _fogEnd;

        public bool Enhanced
        {
            get => _enhanced;
            set
            {
                if (_enhanced == value) return;
                _enhanced = value;
                Apply();
            }
        }

        void Awake()
        {
            _sun = RenderSettings.sun;
            if (_sun == null)
                foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
                    if (l.type == LightType.Directional) { _sun = l; break; }
            if (_sun == null)
            {
                _sun = new GameObject("Sun").AddComponent<Light>();
                _sun.type = LightType.Directional;
                _sun.shadows = LightShadows.Soft;
            }
            Save();
            _volume = new GameObject("Map Grade").AddComponent<Volume>();
            _volume.transform.SetParent(transform, false);
            _volume.isGlobal = true;
            _volume.priority = 10f;
            _volume.sharedProfile = BuildProfile();
            Apply();
        }

        void OnDestroy()
        {
            if (_instance == this) _instance = null;
            if (_volume != null && _volume.sharedProfile != null) Destroy(_volume.sharedProfile);
        }

        // the camera can change after the map loads (the FPS controller makes its own), so it is checked every frame
        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == _camera) return;
            RestoreCamera();
            _camera = cam;
            if (_camera != null && _camera.TryGetComponent<UniversalAdditionalCameraData>(out var data))
            {
                _cameraPost = data.renderPostProcessing;
                _cameraAa = data.antialiasing;
            }
            ApplyCamera();
        }

        void Save()
        {
            _sunRotation = _sun.transform.rotation;
            _sunColor = _sun.color;
            _sunIntensity = _sun.intensity;
            _sunShadowStrength = _sun.shadowStrength;
            _ambientMode = RenderSettings.ambientMode;
            _ambientSky = RenderSettings.ambientSkyColor;
            _ambientEquator = RenderSettings.ambientEquatorColor;
            _ambientGround = RenderSettings.ambientGroundColor;
            _ambientIntensity = RenderSettings.ambientIntensity;
            _fog = RenderSettings.fog;
            _fogMode = RenderSettings.fogMode;
            _fogColor = RenderSettings.fogColor;
            _fogStart = RenderSettings.fogStartDistance;
            _fogEnd = RenderSettings.fogEndDistance;
            _saved = true;
        }

        void Apply()
        {
            if (!_saved || _sun == null) return;
            if (_enhanced)
            {
                _sun.transform.rotation = SunRotation;
                _sun.color = SunColor;
                _sun.intensity = SunIntensity;
                _sun.shadowStrength = 1f;
                RenderSettings.ambientMode = AmbientMode.Trilight;
                RenderSettings.ambientSkyColor = SkyColor;
                RenderSettings.ambientEquatorColor = EquatorColor;
                RenderSettings.ambientGroundColor = GroundColor;
                RenderSettings.ambientIntensity = 1f;
                RenderSettings.fog = true;
                RenderSettings.fogMode = FogMode.Linear;
                RenderSettings.fogColor = FogColor;
                RenderSettings.fogStartDistance = FogStart;
                RenderSettings.fogEndDistance = FogEnd;
            }
            else
            {
                _sun.transform.rotation = _sunRotation;
                _sun.color = _sunColor;
                _sun.intensity = _sunIntensity;
                _sun.shadowStrength = _sunShadowStrength;
                RenderSettings.ambientMode = _ambientMode;
                RenderSettings.ambientSkyColor = _ambientSky;
                RenderSettings.ambientEquatorColor = _ambientEquator;
                RenderSettings.ambientGroundColor = _ambientGround;
                RenderSettings.ambientIntensity = _ambientIntensity;
                RenderSettings.fog = _fog;
                RenderSettings.fogMode = _fogMode;
                RenderSettings.fogColor = _fogColor;
                RenderSettings.fogStartDistance = _fogStart;
                RenderSettings.fogEndDistance = _fogEnd;
            }
            DynamicGI.UpdateEnvironment();
            if (_volume != null) _volume.enabled = _enhanced;
            ApplyCamera();
        }

        void ApplyCamera()
        {
            if (_camera == null || !_camera.TryGetComponent<UniversalAdditionalCameraData>(out var data)) return;
            data.renderPostProcessing = _enhanced || _cameraPost;
            data.antialiasing = _enhanced ? AntialiasingMode.SubpixelMorphologicalAntiAliasing : _cameraAa;
            if (_enhanced) data.antialiasingQuality = AntialiasingQuality.High;
        }

        void RestoreCamera()
        {
            if (_camera == null || !_camera.TryGetComponent<UniversalAdditionalCameraData>(out var data)) return;
            data.renderPostProcessing = _cameraPost;
            data.antialiasing = _cameraAa;
        }

        static VolumeProfile BuildProfile()
        {
            var p = ScriptableObject.CreateInstance<VolumeProfile>();
            p.name = "Map Grade";

            var tone = p.Add<Tonemapping>(true);
            tone.mode.value = TonemappingMode.ACES;

            var adjust = p.Add<ColorAdjustments>(true);
            adjust.postExposure.value = 0.45f;     // ACES darkens mid greys; this brings the lit side back up
            adjust.contrast.value = 12f;
            adjust.saturation.value = 8f;

            // cool shadows, warm highlights: the sun/sky split, pushed a little further
            var split = p.Add<SplitToning>(true);
            split.shadows.value = new Color(0.42f, 0.47f, 0.58f);
            split.highlights.value = new Color(0.58f, 0.53f, 0.45f);
            split.balance.value = 10f;

            var bloom = p.Add<Bloom>(true);
            bloom.threshold.value = 1.6f;          // only faces square to the sun, and tracers
            bloom.intensity.value = 0.25f;
            bloom.scatter.value = 0.6f;

            var vignette = p.Add<Vignette>(true);
            vignette.intensity.value = 0.18f;
            vignette.smoothness.value = 0.45f;

            // the default profile turns these on; they smear a replay scrubbed frame by frame
            p.Add<MotionBlur>(true).intensity.value = 0f;
            p.Add<FilmGrain>(true).intensity.value = 0f;
            return p;
        }
    }
}
