using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace TabgInstaller.Visuals
{
    /// <summary>
    /// Adds the "Hidden/TabgVisuals/Stylize" image effect to the main camera, after the game's own
    /// post-processing: a warm cinematic grade (split toning, saturation, S-curve), aerial haze,
    /// sun rays and glow that follow the game's sun, a vignette, sharpening, and optional subtle ink
    /// outlines. Purely client-side and cosmetic; does not touch shadows or quality settings (that
    /// is PerformanceClient's job).
    /// </summary>
    [BepInPlugin("tabginstaller.visuals", "TABG Visuals", "1.1.0")]
    [BepInDependency("tabginstaller.modsettings", BepInDependency.DependencyFlags.HardDependency)]
    public class VisualsPlugin : BaseUnityPlugin
    {
        internal static VisualsPlugin Instance;

        internal ConfigEntry<bool> Enabled;
        internal ConfigEntry<KeyCode> ToggleKey;
        internal ConfigEntry<float> Warmth;
        internal ConfigEntry<float> Saturation;
        internal ConfigEntry<float> Vibrance;
        internal ConfigEntry<float> Contrast;
        internal ConfigEntry<float> Haze;
        internal ConfigEntry<float> HazeStart;
        internal ConfigEntry<float> HazeDistance;
        internal ConfigEntry<float> SunRays;
        internal ConfigEntry<float> SkyDepth;
        internal ConfigEntry<float> Vignette;
        internal ConfigEntry<float> Sharpen;
        internal ConfigEntry<float> InkStrength;
        internal ConfigEntry<float> InkWidth;
        internal ConfigEntry<float> InkDarken;
        internal ConfigEntry<float> InkSensitivity;
        internal ConfigEntry<float> InkFadeStart;
        internal ConfigEntry<float> InkFadeEnd;
        internal ConfigEntry<int> DebugView;

        internal Material Material;
        private float _nextCameraCheck;

        private static ConfigDescription Range(string text, float min, float max) =>
            new ConfigDescription(text, new AcceptableValueRange<float>(min, max));

        private void Awake()
        {
            Instance = this;
            Enabled = Config.Bind("General", "Enabled", true, "Apply the visuals effect");
            ToggleKey = Config.Bind("General", "ToggleKey", KeyCode.F8, "Key to switch the effect on/off (to compare)");

            Warmth = Config.Bind("Grade", "Warmth", 0.8f, Range("Warm sunlit highlights, cool blue shadows", 0f, 1.5f));
            Saturation = Config.Bind("Grade", "Saturation", 0.25f, Range("Richer colours everywhere", -0.5f, 0.6f));
            Vibrance = Config.Bind("Grade", "Vibrance", 0.25f, Range("Lifts dull colours more than bright ones", -0.5f, 0.8f));
            Contrast = Config.Bind("Grade", "Contrast", 0.35f, Range("Soft S-curve: deeper shadows, brighter highlights", 0f, 1f));
            Haze = Config.Bind("Atmosphere", "Haze", 0.45f, Range("Blue-lavender haze over distant terrain, warm toward the sun", 0f, 1f));
            HazeStart = Config.Bind("Atmosphere", "HazeStart", 25f, Range("Distance (m) where the haze begins", 0f, 300f));
            HazeDistance = Config.Bind("Atmosphere", "HazeDistance", 450f, Range("How far (m) until the haze is two-thirds thick", 50f, 3000f));
            SkyDepth = Config.Bind("Atmosphere", "SkyDepth", 0.85f, Range("Deeper blue sky overhead, light at the horizon", 0f, 1f));
            SunRays = Config.Bind("Atmosphere", "SunRays", 0.8f, Range("Light shafts and glow around the sun", 0f, 2f));
            Vignette = Config.Bind("Atmosphere", "Vignette", 0.25f, Range("Darker screen corners", 0f, 1f));
            Sharpen = Config.Bind("Detail", "Sharpen", 0.25f, Range("Contrast-adaptive sharpening (0 = off)", 0f, 1f));
            InkStrength = Config.Bind("Ink", "Strength", 0.3f, Range("Outlines around objects (0 = off)", 0f, 1f));
            InkWidth = Config.Bind("Ink", "Width", 1.5f, Range("Line width in pixels at 1080p", 1f, 4f));
            InkDarken = Config.Bind("Ink", "Darkness", 0.45f, Range("Line colour: 0 = black, 1 = invisible", 0f, 1f));
            InkSensitivity = Config.Bind("Ink", "Sensitivity", 30f, Range("How small an edge or crease still gets a line", 1f, 200f));
            InkFadeStart = Config.Bind("Ink", "FadeStart", 40f, Range("Distance (m) where lines start to fade", 0f, 500f));
            InkFadeEnd = Config.Bind("Ink", "FadeEnd", 150f, Range("Distance (m) where lines are gone", 1f, 1000f));
            DebugView = Config.Bind("Debug", "View", 0, new ConfigDescription(
                "0 = normal, 1 = outline mask, 2 = depth, 3 = passthrough, 4 = sun rays, 5 = distance (red = sky)", new AcceptableValueRange<int>(0, 5)));

            const string cat = "Visuals";
            ModSettings.ModSettingsUI.Register(cat, "Enabled", "Apply the visuals effect", Enabled);
            ModSettings.ModSettingsUI.Register(cat, "Toggle Key", "Switch the effect on/off to compare", ToggleKey);
            ModSettings.ModSettingsUI.Register(cat, "Warmth", "Warm highlights, cool shadows", Warmth);
            ModSettings.ModSettingsUI.Register(cat, "Saturation", "Richer colours", Saturation);
            ModSettings.ModSettingsUI.Register(cat, "Vibrance", "Lifts dull colours", Vibrance);
            ModSettings.ModSettingsUI.Register(cat, "Contrast", "Soft S-curve", Contrast);
            ModSettings.ModSettingsUI.Register(cat, "Haze", "Distant haze", Haze);
            ModSettings.ModSettingsUI.Register(cat, "Haze Distance", "How far until the haze is thick", HazeDistance);
            ModSettings.ModSettingsUI.Register(cat, "Sky Depth", "Deeper blue sky overhead", SkyDepth);
            ModSettings.ModSettingsUI.Register(cat, "Sun Rays", "Light shafts and sun glow", SunRays);
            ModSettings.ModSettingsUI.Register(cat, "Vignette", "Darker corners", Vignette);
            ModSettings.ModSettingsUI.Register(cat, "Sharpen", "Contrast-adaptive sharpening", Sharpen);
            ModSettings.ModSettingsUI.Register(cat, "Outline Strength", "Outlines around objects (0 = off)", InkStrength);
            ModSettings.ModSettingsUI.Register(cat, "Outline Darkness", "0 = black lines, 1 = invisible", InkDarken);

            Material = LoadMaterial();
            Logger.LogInfo(Material != null
                ? $"[Visuals] Loaded. Press {ToggleKey.Value} to compare on/off, F9 for settings."
                : "[Visuals] Shader could not be loaded; effect disabled.");
        }

        private Material LoadMaterial()
        {
            using (var stream = typeof(VisualsPlugin).Assembly.GetManifestResourceStream("visuals.bundle"))
            {
                if (stream == null)
                {
                    Logger.LogError("[Visuals] embedded bundle missing");
                    return null;
                }
                var bytes = new byte[stream.Length];
                int read = 0;
                while (read < bytes.Length)
                {
                    int n = stream.Read(bytes, read, bytes.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                var bundle = AssetBundle.LoadFromMemory(bytes);
                if (bundle == null)
                {
                    Logger.LogError("[Visuals] bundle failed to load (built for another Unity version?)");
                    return null;
                }
                var shader = bundle.LoadAsset<Shader>("Assets/Bundles/visuals/Stylize.shader");
                bundle.Unload(false);
                if (shader == null || !shader.isSupported)
                {
                    Logger.LogError($"[Visuals] shader {(shader == null ? "not in bundle" : "not supported by this GPU/API")}");
                    return null;
                }
                return new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(ToggleKey.Value))
            {
                Enabled.Value = !Enabled.Value;
                Logger.LogInfo($"[Visuals] {(Enabled.Value ? "on" : "off")}");
            }

            // The player camera is created per match; attach to whatever the main camera is now.
            if (Material == null || Time.unscaledTime < _nextCameraCheck) return;
            _nextCameraCheck = Time.unscaledTime + 1f;
            var cam = Camera.main;
            if (cam != null && cam.GetComponent<StylizeEffect>() == null)
            {
                cam.gameObject.AddComponent<StylizeEffect>();
                Logger.LogInfo($"[Visuals] attached to camera '{cam.name}'");
            }
        }
    }

    /// <summary>Runs after every other image effect on the camera (it is added last).</summary>
    [RequireComponent(typeof(Camera))]
    public class StylizeEffect : MonoBehaviour
    {
        private static readonly int InkId = Shader.PropertyToID("_Ink");
        private static readonly int InkShapeId = Shader.PropertyToID("_InkShape");
        private static readonly int ToneId = Shader.PropertyToID("_Tone");
        private static readonly int GradeId = Shader.PropertyToID("_Grade");
        private static readonly int SunId = Shader.PropertyToID("_Sun");
        private static readonly int SunColorId = Shader.PropertyToID("_SunColor");
        private static readonly int HazeColorId = Shader.PropertyToID("_HazeColor");
        private static readonly int RayParamsId = Shader.PropertyToID("_RayParams");
        private static readonly int RaysTexId = Shader.PropertyToID("_RaysTex");
        private static readonly int AtmosId = Shader.PropertyToID("_Atmos");

        // Distant haze: a cool, slightly lavender sky blue.
        private static readonly Color HazeColor = new Color(0.70f, 0.77f, 0.93f);

        private Camera _cam;
        private Light _sun;
        private float _nextSunSearch;
        private float _nextSunLog;

        private void OnEnable()
        {
            _cam = GetComponent<Camera>();
        }

        private void OnPreCull()
        {
            // Haze, rays and lines all need depth; TABG's AO and haze usually request it already.
            var p = VisualsPlugin.Instance;
            if (p != null && p.Enabled.Value)
                _cam.depthTextureMode |= DepthTextureMode.Depth;
        }

        private Light FindSun()
        {
            if (_sun != null && _sun.isActiveAndEnabled) return _sun;
            if (Time.unscaledTime < _nextSunSearch) return null;
            _nextSunSearch = Time.unscaledTime + 2f;
            // TABG's DayAndNightCycle drives the sun; fall back to the scene's sun or brightest
            // directional light.
            var cycle = FindObjectOfType<DayAndNightCycle>();
            if (cycle != null && cycle.light != null) return _sun = cycle.light;
            if (RenderSettings.sun != null) return _sun = RenderSettings.sun;
            Light best = null;
            foreach (var l in FindObjectsOfType<Light>())
                if (l.type == LightType.Directional && l.isActiveAndEnabled && (best == null || l.intensity > best.intensity))
                    best = l;
            return _sun = best;
        }

        private void OnRenderImage(RenderTexture src, RenderTexture dst)
        {
            var p = VisualsPlugin.Instance;
            if (p == null || p.Material == null || !p.Enabled.Value)
            {
                Graphics.Blit(src, dst);
                return;
            }
            var m = p.Material;

            // Sun position on screen; visibility fades as it leaves the view or sets.
            var sun = FindSun();
            Vector4 sunVec = Vector4.zero;
            Color sunColor = new Color(1f, 0.85f, 0.65f);
            if (sun != null)
            {
                Vector3 vp = _cam.WorldToViewportPoint(_cam.transform.position - sun.transform.forward * 1000f);
                float onScreen = 1f - Mathf.Clamp01((Mathf.Max(Mathf.Abs(vp.x - 0.5f), Mathf.Abs(vp.y - 0.5f)) - 0.5f) / 0.4f);
                float aboveHorizon = Mathf.Clamp01(-sun.transform.forward.y * 4f + 0.2f);
                float visible = vp.z > 0f ? onScreen * aboveHorizon : 0f;
                sunVec = new Vector4(vp.x, vp.y, visible, p.SunRays.Value);
                if (p.DebugView.Value != 0 && Time.unscaledTime >= _nextSunLog)
                {
                    _nextSunLog = Time.unscaledTime + 5f;
                    Debug.Log($"[Visuals] sun '{sun.name}' viewport {vp} visible {visible:0.00} color {sun.color} intensity {sun.intensity:0.00}");
                }
                sunColor = sun.color * Mathf.Clamp(sun.intensity, 0.3f, 1f);
            }

            m.SetVector(InkId, new Vector4(p.InkStrength.Value, p.InkWidth.Value,
                p.InkFadeStart.Value, Mathf.Max(p.InkFadeEnd.Value, p.InkFadeStart.Value + 1f)));
            m.SetVector(InkShapeId, new Vector4(p.InkDarken.Value, p.InkSensitivity.Value, 0.08f, 0f));
            m.SetVector(ToneId, new Vector4(p.Sharpen.Value, p.Vibrance.Value, p.DebugView.Value, p.Saturation.Value));
            m.SetVector(GradeId, new Vector4(p.Warmth.Value, p.Contrast.Value, p.Vignette.Value, p.Haze.Value));
            m.SetVector(SunId, sunVec);
            m.SetVector(AtmosId, new Vector4(p.SkyDepth.Value, 0f, 0f, 0f));
            m.SetColor(SunColorId, sunColor);
            m.SetColor(HazeColorId, new Color(HazeColor.r, HazeColor.g, HazeColor.b, p.HazeStart.Value));
            m.SetVector(RayParamsId, new Vector4(0f, 0.65f, 1f / Mathf.Max(1f, p.HazeDistance.Value), 0f));

            RenderTexture rays = null;
            if (sunVec.z > 0.01f && p.SunRays.Value > 0f)
            {
                int w = Mathf.Max(1, src.width / 4), h = Mathf.Max(1, src.height / 4);
                rays = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBHalf);
                var tmp = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBHalf);
                Graphics.Blit(src, rays, m, 1);
                // Three radial blur passes with growing reach give long, smooth shafts.
                foreach (float reach in new[] { 0.35f, 0.6f, 0.9f })
                {
                    m.SetVector(RayParamsId, new Vector4(reach, 0.65f, 1f / Mathf.Max(1f, p.HazeDistance.Value), 0f));
                    Graphics.Blit(rays, tmp, m, 2);
                    var t = rays; rays = tmp; tmp = t;
                }
                RenderTexture.ReleaseTemporary(tmp);
                m.SetTexture(RaysTexId, rays);
            }
            else
            {
                m.SetTexture(RaysTexId, Texture2D.blackTexture);
            }

            Graphics.Blit(src, dst, m, 0);
            if (rays != null) RenderTexture.ReleaseTemporary(rays);
        }
    }
}
