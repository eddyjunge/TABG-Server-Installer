using System.IO;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace TabgInstaller.Visuals
{
    /// <summary>
    /// Adds the "Hidden/TabgVisuals/Stylize" image effect (soft ink outlines, contrast-adaptive
    /// sharpening, light vibrance) to the main camera, after the game's own post-processing.
    /// Purely client-side and cosmetic; does not touch shadows or quality settings (that is
    /// PerformanceClient's job).
    /// </summary>
    [BepInPlugin("tabginstaller.visuals", "TABG Visuals", "1.0.0")]
    [BepInDependency("tabginstaller.modsettings", BepInDependency.DependencyFlags.HardDependency)]
    public class VisualsPlugin : BaseUnityPlugin
    {
        internal static VisualsPlugin Instance;

        internal ConfigEntry<bool> Enabled;
        internal ConfigEntry<KeyCode> ToggleKey;
        internal ConfigEntry<float> InkStrength;
        internal ConfigEntry<float> InkWidth;
        internal ConfigEntry<float> InkDarken;
        internal ConfigEntry<float> InkSensitivity;
        internal ConfigEntry<float> InkFadeStart;
        internal ConfigEntry<float> InkFadeEnd;
        internal ConfigEntry<float> Sharpen;
        internal ConfigEntry<float> Vibrance;
        internal ConfigEntry<int> DebugView;

        internal Material Material;
        private float _nextCameraCheck;

        private void Awake()
        {
            Instance = this;
            Enabled = Config.Bind("General", "Enabled", true, "Apply the visuals effect");
            ToggleKey = Config.Bind("General", "ToggleKey", KeyCode.F8, "Key to switch the effect on/off (to compare)");
            InkStrength = Config.Bind("Ink", "Strength", 0.8f,
                new ConfigDescription("How strongly outlines show (0 = off)", new AcceptableValueRange<float>(0f, 1f)));
            InkWidth = Config.Bind("Ink", "Width", 1.5f,
                new ConfigDescription("Line width in pixels at 1080p", new AcceptableValueRange<float>(1f, 4f)));
            InkDarken = Config.Bind("Ink", "Darkness", 0.35f,
                new ConfigDescription("Line colour: 0 = black, 1 = invisible (lines darken the colour below)", new AcceptableValueRange<float>(0f, 1f)));
            InkSensitivity = Config.Bind("Ink", "Sensitivity", 30f,
                new ConfigDescription("How small an edge or crease still gets a line", new AcceptableValueRange<float>(1f, 200f)));
            InkFadeStart = Config.Bind("Ink", "FadeStart", 50f,
                new ConfigDescription("Distance (m) where lines start to fade", new AcceptableValueRange<float>(0f, 500f)));
            InkFadeEnd = Config.Bind("Ink", "FadeEnd", 200f,
                new ConfigDescription("Distance (m) where lines are gone", new AcceptableValueRange<float>(1f, 1000f)));
            Sharpen = Config.Bind("Tone", "Sharpen", 0.3f,
                new ConfigDescription("Contrast-adaptive sharpening (0 = off)", new AcceptableValueRange<float>(0f, 1f)));
            Vibrance = Config.Bind("Tone", "Vibrance", 0.1f,
                new ConfigDescription("Boosts dull colours more than bright ones (0 = off)", new AcceptableValueRange<float>(-0.5f, 0.5f)));

            DebugView = Config.Bind("Debug", "View", 0,
                new ConfigDescription("0 = normal, 1 = outline mask, 2 = depth (10 m bands), 3 = passthrough", new AcceptableValueRange<int>(0, 3)));

            const string cat = "Visuals";
            ModSettings.ModSettingsUI.Register(cat, "Enabled", "Apply the visuals effect", Enabled);
            ModSettings.ModSettingsUI.Register(cat, "Toggle Key", "Switch the effect on/off to compare", ToggleKey);
            ModSettings.ModSettingsUI.Register(cat, "Outline Strength", "How strongly outlines show", InkStrength);
            ModSettings.ModSettingsUI.Register(cat, "Outline Width", "Line width in pixels at 1080p", InkWidth);
            ModSettings.ModSettingsUI.Register(cat, "Outline Darkness", "0 = black lines, 1 = invisible", InkDarken);
            ModSettings.ModSettingsUI.Register(cat, "Outline Sensitivity", "How small an edge still gets a line", InkSensitivity);
            ModSettings.ModSettingsUI.Register(cat, "Outline Fade Start", "Distance (m) where lines start to fade", InkFadeStart);
            ModSettings.ModSettingsUI.Register(cat, "Outline Fade End", "Distance (m) where lines are gone", InkFadeEnd);
            ModSettings.ModSettingsUI.Register(cat, "Sharpen", "Contrast-adaptive sharpening", Sharpen);
            ModSettings.ModSettingsUI.Register(cat, "Vibrance", "Boosts dull colours", Vibrance);

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

        private Camera _cam;

        private void OnEnable()
        {
            _cam = GetComponent<Camera>();
        }

        private void OnPreCull()
        {
            var p = VisualsPlugin.Instance;
            // Lines need the depth texture; TABG's AO and haze usually request it already.
            if (p != null && p.Enabled.Value && p.InkStrength.Value > 0f)
                _cam.depthTextureMode |= DepthTextureMode.Depth;
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
            m.SetVector(InkId, new Vector4(p.InkStrength.Value, p.InkWidth.Value,
                p.InkFadeStart.Value, Mathf.Max(p.InkFadeEnd.Value, p.InkFadeStart.Value + 1f)));
            m.SetVector(InkShapeId, new Vector4(p.InkDarken.Value, p.InkSensitivity.Value, 0.08f, 0f));
            m.SetVector(ToneId, new Vector4(p.Sharpen.Value, p.Vibrance.Value, p.DebugView.Value, 0f));
            Graphics.Blit(src, dst, m);
        }
    }
}
