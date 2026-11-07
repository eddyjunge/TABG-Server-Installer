using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace TabgInstaller.Visuals
{
    /// <summary>
    /// Client-side look upgrade on top of TABG's own rendering:
    ///  - StylizeEffect: image effect on the main camera after the game's post-processing (grade,
    ///    sky gradient, haze, sun rays/glow, volumetric sunlight, vignette, sharpening, outlines).
    ///  - Quality: sharper soft cascaded shadows and stronger, smoother ambient occlusion, applied only
    ///    when the game's own shadow option is High (PerformanceClient's profile sets it to Low).
    /// Purely cosmetic; nothing is sent to the server.
    /// </summary>
    [BepInPlugin("tabginstaller.visuals", "TABG Visuals", "1.2.0")]
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
        internal ConfigEntry<float> ExplosionGlowStrength;
        internal ConfigEntry<float> VolumetricLight;
        internal ConfigEntry<int> VolumetricSteps;
        internal ConfigEntry<bool> Water;
        internal ConfigEntry<bool> Clouds;
        internal ConfigEntry<float> CloudCoverage;
        internal ConfigEntry<float> CloudHeight;
        internal ConfigEntry<int> CloudSteps;
        internal ConfigEntry<bool> BetterShadows;
        internal ConfigEntry<bool> BetterAO;
        internal ConfigEntry<float> AOStrength;
        internal ConfigEntry<float> InkStrength;
        internal ConfigEntry<float> InkWidth;
        internal ConfigEntry<float> InkDarken;
        internal ConfigEntry<float> InkSensitivity;
        internal ConfigEntry<float> InkFadeStart;
        internal ConfigEntry<float> InkFadeEnd;
        internal ConfigEntry<int> DebugView;

        internal Material Material;
        internal Material VolumetricMaterial;
        internal Material CloudMaterial;
        private float _nextCameraCheck;

        private static ConfigDescription Range(string text, float min, float max) =>
            new ConfigDescription(text, new AcceptableValueRange<float>(min, max));

        private void Awake()
        {
            Instance = this;
            Enabled = Config.Bind("General", "Enabled", true, "Apply the visuals effect");
            ToggleKey = Config.Bind("General", "ToggleKey", KeyCode.F8, "Key to switch the effect on/off (to compare)");

            Warmth = Config.Bind("Grade", "Warmth", 0.5f, Range("Warm sunlit highlights, cool blue shadows", 0f, 1.5f));
            Saturation = Config.Bind("Grade", "Saturation", 0f, Range("Richer colours everywhere", -0.5f, 0.6f));
            Vibrance = Config.Bind("Grade", "Vibrance", 0.1f, Range("Lifts dull colours more than bright ones", -0.5f, 0.8f));
            Contrast = Config.Bind("Grade", "Contrast", 0.25f, Range("Soft S-curve: deeper shadows, brighter highlights", 0f, 1f));
            Haze = Config.Bind("Atmosphere", "Haze", 0.45f, Range("Blue-lavender haze over distant terrain, warm toward the sun", 0f, 1f));
            HazeStart = Config.Bind("Atmosphere", "HazeStart", 25f, Range("Distance (m) where the haze begins", 0f, 300f));
            HazeDistance = Config.Bind("Atmosphere", "HazeDistance", 450f, Range("How far (m) until the haze is two-thirds thick", 50f, 3000f));
            SkyDepth = Config.Bind("Atmosphere", "SkyDepth", 0.45f, Range("Deeper blue sky overhead, light at the horizon", 0f, 1f));
            SunRays = Config.Bind("Atmosphere", "SunRays", 0.8f, Range("Light shafts and glow around the sun (screen space)", 0f, 2f));
            VolumetricLight = Config.Bind("Atmosphere", "VolumetricLight", 0.4f, Range("Sunlight in the air, shadowed by trees and buildings (0 = off)", 0f, 2f));
            VolumetricSteps = Config.Bind("Atmosphere", "VolumetricSteps", 24, new ConfigDescription(
                "Samples per pixel for volumetric light (quality vs speed)", new AcceptableValueRange<int>(8, 48)));
            Water = Config.Bind("Water", "Enabled", true, "Better sea: wave foam on, richer colour, more sun glowing through the waves (tunes TABG's Crest ocean)");
            Clouds = Config.Bind("Clouds", "Enabled", true, "Soft 3D cumulus clouds over the painted sky, lit by the sun");
            CloudCoverage = Config.Bind("Clouds", "Coverage", 0.45f, Range("How much of the sky the clouds cover", 0.05f, 0.95f));
            CloudHeight = Config.Bind("Clouds", "Height", 650f, Range("Cloud base height (m)", 200f, 2000f));
            CloudSteps = Config.Bind("Clouds", "Steps", 24, new ConfigDescription(
                "Samples per pixel for clouds (quality vs speed)", new AcceptableValueRange<int>(8, 48)));
            Vignette = Config.Bind("Atmosphere", "Vignette", 0.25f, Range("Darker screen corners", 0f, 1f));
            Sharpen = Config.Bind("Detail", "Sharpen", 0.25f, Range("Contrast-adaptive sharpening (0 = off)", 0f, 1f));
            ExplosionGlowStrength = Config.Bind("Explosions", "Glow", 1f, Range("Light flash, bloom and glow for explosions (0 = off)", 0f, 2f));
            BetterShadows = Config.Bind("Quality", "BetterShadows", true,
                "Sharper soft shadows (4096 shadow map, 4 cascades) when the game's shadow quality is High");
            BetterAO = Config.Bind("Quality", "BetterAO", true,
                "Stronger, smoother ambient occlusion when the game's AO option is on");
            AOStrength = Config.Bind("Quality", "AOStrength", 0.7f, Range("Ambient occlusion strength (game default 0.3)", 0f, 1.5f));
            InkStrength = Config.Bind("Ink", "Strength", 0.3f, Range("Outlines around objects (0 = off)", 0f, 1f));
            InkWidth = Config.Bind("Ink", "Width", 1.5f, Range("Line width in pixels at 1080p", 1f, 4f));
            InkDarken = Config.Bind("Ink", "Darkness", 0.45f, Range("Line colour: 0 = black, 1 = invisible", 0f, 1f));
            InkSensitivity = Config.Bind("Ink", "Sensitivity", 30f, Range("How small an edge or crease still gets a line", 1f, 200f));
            InkFadeStart = Config.Bind("Ink", "FadeStart", 40f, Range("Distance (m) where lines start to fade", 0f, 500f));
            InkFadeEnd = Config.Bind("Ink", "FadeEnd", 150f, Range("Distance (m) where lines are gone", 1f, 1000f));
            DebugView = Config.Bind("Debug", "View", 0, new ConfigDescription(
                "0 normal, 1 outline mask, 2 depth, 3 passthrough, 4 sun rays, 5 distance (red = sky), 6 volumetric light, 7 clouds, 8 explosion glow",
                new AcceptableValueRange<int>(0, 8)));

            const string cat = "Visuals";
            ModSettings.ModSettingsUI.Register(cat, "Enabled", "Apply the visuals effect", Enabled);
            ModSettings.ModSettingsUI.Register(cat, "Toggle Key", "Switch the effect on/off to compare", ToggleKey);
            ModSettings.ModSettingsUI.Register(cat, "Better Shadows", "Sharper soft shadows (game shadow quality High)", BetterShadows);
            ModSettings.ModSettingsUI.Register(cat, "Better AO", "Stronger ambient occlusion (game AO on)", BetterAO);
            ModSettings.ModSettingsUI.Register(cat, "AO Strength", "Ambient occlusion strength", AOStrength);
            ModSettings.ModSettingsUI.Register(cat, "Volumetric Light", "Sunlight in the air, shadowed by the world", VolumetricLight);
            ModSettings.ModSettingsUI.Register(cat, "Volumetric Quality", "Samples per pixel", VolumetricSteps);
            ModSettings.ModSettingsUI.Register(cat, "Better Water", "Foam, richer colour, sun through waves", Water);
            ModSettings.ModSettingsUI.Register(cat, "Clouds", "Soft 3D clouds over the sky", Clouds);
            ModSettings.ModSettingsUI.Register(cat, "Cloud Coverage", "How much sky the clouds cover", CloudCoverage);
            ModSettings.ModSettingsUI.Register(cat, "Cloud Height", "Cloud base height (m)", CloudHeight);
            ModSettings.ModSettingsUI.Register(cat, "Cloud Quality", "Samples per pixel", CloudSteps);
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
            ModSettings.ModSettingsUI.Register(cat, "Explosion Glow", "Light flash, bloom and glow for explosions", ExplosionGlowStrength);
            ModSettings.ModSettingsUI.Register(cat, "Outline Strength", "Outlines around objects (0 = off)", InkStrength);
            ModSettings.ModSettingsUI.Register(cat, "Outline Darkness", "0 = black lines, 1 = invisible", InkDarken);

            LoadMaterials();
            Logger.LogInfo(Material != null
                ? $"[Visuals] Loaded. Press {ToggleKey.Value} to compare on/off, F9 for settings."
                : "[Visuals] Shader could not be loaded; effect disabled.");

            new Harmony("tabginstaller.visuals").PatchAll(typeof(VisualsPlugin).Assembly);
            BetterShadows.SettingChanged += (_, __) => Quality.Apply();
            BetterAO.SettingChanged += (_, __) => Quality.Apply();
            AOStrength.SettingChanged += (_, __) => Quality.Apply();
            Enabled.SettingChanged += (_, __) => { Quality.Apply(); Ocean.Apply(Enabled.Value && Water.Value); };
            Water.SettingChanged += (_, __) => Ocean.Apply(Enabled.Value && Water.Value);
        }

        private void LoadMaterials()
        {
            using (var stream = typeof(VisualsPlugin).Assembly.GetManifestResourceStream("visuals.bundle"))
            {
                if (stream == null)
                {
                    Logger.LogError("[Visuals] embedded bundle missing");
                    return;
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
                    return;
                }
                Material = MaterialFrom(bundle, "Assets/Bundles/visuals/Stylize.shader");
                VolumetricMaterial = MaterialFrom(bundle, "Assets/Bundles/visuals/Volumetric.shader");
                CloudMaterial = MaterialFrom(bundle, "Assets/Bundles/visuals/Clouds.shader");
                bundle.Unload(false);
            }
        }

        private Material MaterialFrom(AssetBundle bundle, string path)
        {
            var shader = bundle.LoadAsset<Shader>(path);
            if (shader == null || !shader.isSupported)
            {
                Logger.LogError($"[Visuals] {path}: {(shader == null ? "not in bundle" : "not supported by this GPU/API")}");
                return null;
            }
            return new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }

        private void Update()
        {
            ExplosionGlow.Update();
            if (Input.GetKeyDown(ToggleKey.Value))
            {
                Enabled.Value = !Enabled.Value;
                Logger.LogInfo($"[Visuals] {(Enabled.Value ? "on" : "off")}");
            }

            // The player camera is created per match; attach to whatever the main camera is now.
            if (Material == null || Time.unscaledTime < _nextCameraCheck) return;
            _nextCameraCheck = Time.unscaledTime + 1f;
            Ocean.Apply(Enabled.Value && Water.Value);
            var cam = Camera.main;
            if (cam != null && cam.GetComponent<StylizeEffect>() == null)
            {
                cam.gameObject.AddComponent<StylizeEffect>();
                Logger.LogInfo($"[Visuals] attached to camera '{cam.name}'");
                Quality.Apply();
            }
        }
    }
}
