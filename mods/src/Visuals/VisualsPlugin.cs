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
    [BepInPlugin("tabginstaller.visuals", "TABG Visuals", "1.5.0")]
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
        internal ConfigEntry<bool> RealisticFlashbang;
        internal ConfigEntry<bool> VolumetricSmoke;
        internal ConfigEntry<int> SmokeSteps;
        internal ConfigEntry<bool> GrenadeEffects;
        internal ConfigEntry<float> VolumetricLight;
        internal ConfigEntry<int> VolumetricSteps;
        internal ConfigEntry<bool> Water;
        internal ConfigEntry<bool> Clouds;
        internal ConfigEntry<float> CloudCoverage;
        internal ConfigEntry<float> CloudHeight;
        internal ConfigEntry<int> CloudSteps;
        internal ConfigEntry<bool> WeatherEnabled;
        internal ConfigEntry<string> WeatherForce;
        internal ConfigEntry<float> WeatherFog;
        internal ConfigEntry<float> WeatherParticles;
        internal ConfigEntry<bool> WeatherLens;
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
        internal Material SmokeMaterial;
        internal Material WeatherMaterial;
        private float _nextCameraCheck;

        private static ConfigDescription Range(string text, float min, float max) =>
            new ConfigDescription(text, new AcceptableValueRange<float>(min, max));

        private void Awake()
        {
            Instance = this;
            Enabled = Config.Bind("General", "Enabled", true, "Apply the visuals effect");
            ToggleKey = Config.Bind("General", "ToggleKey", KeyCode.F8, "Key to switch the effect on/off (to compare)");

            Warmth = Config.Bind("Grade", "Warmth", 0.3f, Range("Warm sunlit highlights, cool blue shadows", 0f, 1.5f));
            Saturation = Config.Bind("Grade", "Saturation", -0.08f, Range("Richer colours everywhere", -0.5f, 0.6f));
            Vibrance = Config.Bind("Grade", "Vibrance", 0f, Range("Lifts dull colours more than bright ones", -0.5f, 0.8f));
            Contrast = Config.Bind("Grade", "Contrast", 0.2f, Range("Soft S-curve: deeper shadows, brighter highlights", 0f, 1f));
            Haze = Config.Bind("Atmosphere", "Haze", 0.45f, Range("Blue-lavender haze over distant terrain, warm toward the sun", 0f, 1f));
            HazeStart = Config.Bind("Atmosphere", "HazeStart", 25f, Range("Distance (m) where the haze begins", 0f, 300f));
            HazeDistance = Config.Bind("Atmosphere", "HazeDistance", 450f, Range("How far (m) until the haze is two-thirds thick", 50f, 3000f));
            SkyDepth = Config.Bind("Atmosphere", "SkyDepth", 0.35f, Range("Deeper blue sky overhead, light at the horizon", 0f, 1f));
            SunRays = Config.Bind("Atmosphere", "SunRays", 0.8f, Range("Light shafts and glow around the sun (screen space)", 0f, 2f));
            VolumetricLight = Config.Bind("Atmosphere", "VolumetricLight", 0.4f, Range("Sunlight in the air, shadowed by trees and buildings (0 = off)", 0f, 2f));
            VolumetricSteps = Config.Bind("Atmosphere", "VolumetricSteps", 24, new ConfigDescription(
                "Samples per pixel for volumetric light (quality vs speed)", new AcceptableValueRange<int>(8, 48)));
            Water = Config.Bind("Water", "Enabled", true, "Better sea: wave foam on, richer colour, more sun glowing through the waves (tunes TABG's Crest ocean)");
            Clouds = Config.Bind("Clouds", "Enabled", true, "Soft 3D cumulus clouds lit by the sun; replaces the game's painted and mesh clouds");
            CloudCoverage = Config.Bind("Clouds", "Coverage", 0.45f, Range("How much of the sky the clouds cover", 0.05f, 0.95f));
            CloudHeight = Config.Bind("Clouds", "Height", 650f, Range("Cloud base height (m)", 200f, 2000f));
            CloudSteps = Config.Bind("Clouds", "Steps", 24, new ConfigDescription(
                "Samples per pixel for clouds (quality vs speed)", new AcceptableValueRange<int>(8, 48)));
            Vignette = Config.Bind("Atmosphere", "Vignette", 0.25f, Range("Darker screen corners", 0f, 1f));
            Sharpen = Config.Bind("Detail", "Sharpen", 0.25f, Range("Contrast-adaptive sharpening (0 = off)", 0f, 1f));
            ExplosionGlowStrength = Config.Bind("Explosions", "Glow", 1f, Range("Light flash, bloom and glow for explosions (0 = off)", 0f, 2f));
            RealisticFlashbang = Config.Bind("Explosions", "RealisticFlashbang", true, "Flashbangs burn the view into your eyes and vision returns blurred (same blind time as the game)");
            VolumetricSmoke = Config.Bind("Explosions", "VolumetricSmoke", true, "Smoke grenades become real 3D smoke, lit by the sun (same duration as the game's, 2-4x as big)");
            SmokeSteps = Config.Bind("Explosions", "SmokeSteps", 32, new ConfigDescription(
                "Samples per pixel for smoke (quality vs speed)", new AcceptableValueRange<int>(12, 64)));
            GrenadeEffects = Config.Bind("Explosions", "GrenadeEffects", true, "Soft lingering smoke and dust after blasts and a faint haze in fields, on top of the game's own effects");
            WeatherEnabled = Config.Bind("Weather", "Enabled", true, "Weather that depends on the biome: snow and fog in the snow, rain and storms on grass, sandstorms in the desert");
            WeatherForce = Config.Bind("Weather", "Force", "Auto", new ConfigDescription("Always this weather instead of following the biome",
                new AcceptableValueList<string>("Auto", "Clear", "Overcast", "Mist", "Rain", "Storm", "Flurries", "Snowfall", "Blizzard", "Haze", "Sandstorm")));
            WeatherFog = Config.Bind("Weather", "Fog", 1f, Range("How thick weather fog gets", 0f, 2f));
            WeatherParticles = Config.Bind("Weather", "Particles", 1f, Range("Amount of snow, rain and dust (quality vs speed)", 0.25f, 1.5f));
            WeatherLens = Config.Bind("Weather", "Lens", true, "Rain drops and snowflakes stick to the screen");
            BetterShadows = Config.Bind("Quality", "BetterShadows", true,
                "Sharper soft shadows (4096 shadow map, 4 cascades) when the game's shadow quality is High");
            BetterAO = Config.Bind("Quality", "BetterAO", true,
                "Stronger, smoother ambient occlusion when the game's AO option is on");
            AOStrength = Config.Bind("Quality", "AOStrength", 0.7f, Range("Ambient occlusion strength (game default 0.3)", 0f, 1.5f));
            InkStrength = Config.Bind("Ink", "Strength", 0f, Range("Outlines around objects (0 = off)", 0f, 1f));
            InkWidth = Config.Bind("Ink", "Width", 1.5f, Range("Line width in pixels at 1080p", 1f, 4f));
            InkDarken = Config.Bind("Ink", "Darkness", 0.45f, Range("Line colour: 0 = black, 1 = invisible", 0f, 1f));
            InkSensitivity = Config.Bind("Ink", "Sensitivity", 30f, Range("How small an edge or crease still gets a line", 1f, 200f));
            InkFadeStart = Config.Bind("Ink", "FadeStart", 40f, Range("Distance (m) where lines start to fade", 0f, 500f));
            InkFadeEnd = Config.Bind("Ink", "FadeEnd", 150f, Range("Distance (m) where lines are gone", 1f, 1000f));
            DebugView = Config.Bind("Debug", "View", 0, new ConfigDescription(
                "0 normal, 1 outline mask, 2 depth, 3 passthrough, 4 sun rays, 5 distance (red = sky), 6 volumetric light, 7 clouds, 8 explosion glow, 9 smoke",
                new AcceptableValueRange<int>(0, 9)));

            MigrateDefaults();

            const string cat = "Visuals";
            ModSettings.ModSettingsUI.Register(cat, "Enabled", "Apply the visuals effect", Enabled);
            ModSettings.ModSettingsUI.Register(cat, "Toggle Key", "Switch the effect on/off to compare", ToggleKey);
            ModSettings.ModSettingsUI.Register(cat, "Better Shadows", "Sharper soft shadows (game shadow quality High)", BetterShadows);
            ModSettings.ModSettingsUI.Register(cat, "Better AO", "Stronger ambient occlusion (game AO on)", BetterAO);
            ModSettings.ModSettingsUI.Register(cat, "AO Strength", "Ambient occlusion strength", AOStrength);
            ModSettings.ModSettingsUI.Register(cat, "Volumetric Light", "Sunlight in the air, shadowed by the world", VolumetricLight);
            ModSettings.ModSettingsUI.Register(cat, "Volumetric Quality", "Samples per pixel", VolumetricSteps);
            ModSettings.ModSettingsUI.Register(cat, "Better Water", "Foam, richer colour, sun through waves", Water);
            ModSettings.ModSettingsUI.Register(cat, "Clouds", "Soft 3D clouds in place of the painted ones", Clouds);
            ModSettings.ModSettingsUI.Register(cat, "Cloud Coverage", "How much sky the clouds cover", CloudCoverage);
            ModSettings.ModSettingsUI.Register(cat, "Cloud Height", "Cloud base height (m)", CloudHeight);
            ModSettings.ModSettingsUI.Register(cat, "Cloud Quality", "Samples per pixel", CloudSteps);
            ModSettings.ModSettingsUI.Register(cat, "Weather", "Snow, fog, rain, storms by biome", WeatherEnabled);
            ModSettings.ModSettingsUI.Register(cat, "Weather Force", "Auto, or Clear, Overcast, Mist, Rain, Storm, Flurries, Snowfall, Blizzard, Haze, Sandstorm", WeatherForce);
            ModSettings.ModSettingsUI.Register(cat, "Weather Fog", "How thick weather fog gets", WeatherFog);
            ModSettings.ModSettingsUI.Register(cat, "Weather Particles", "Amount of snow, rain and dust", WeatherParticles);
            ModSettings.ModSettingsUI.Register(cat, "Weather On Screen", "Rain drops and snowflakes stick to the screen", WeatherLens);
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
            ModSettings.ModSettingsUI.Register(cat, "Realistic Flashbang", "Burned-in view and blurry recovery", RealisticFlashbang);
            ModSettings.ModSettingsUI.Register(cat, "Grenade Effects", "Lingering smoke, dust and field haze", GrenadeEffects);
            ModSettings.ModSettingsUI.Register(cat, "Volumetric Smoke", "Real 3D smoke from smoke grenades", VolumetricSmoke);
            ModSettings.ModSettingsUI.Register(cat, "Smoke Quality", "Samples per pixel", SmokeSteps);
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
            Enabled.SettingChanged += (_, __) => { Quality.Apply(); Ocean.Apply(Enabled.Value && Water.Value); ApplyClouds(); };
            Clouds.SettingChanged += (_, __) => ApplyClouds();
            Water.SettingChanged += (_, __) => Ocean.Apply(Enabled.Value && Water.Value);
        }

        /// <summary>
        /// Moves settings still at an old default to the new one (a config file keeps every value it was
        /// written with, so changed defaults would otherwise never reach existing installs).
        /// </summary>
        private void MigrateDefaults()
        {
            var version = Config.Bind("General", "ConfigVersion", 1, "Settings format (do not edit)");
            if (version.Value >= 3) return;
            void Move(ConfigEntry<float> e, float old)
            {
                if (Mathf.Abs(e.Value - old) < 1e-4f) e.Value = (float)e.DefaultValue;
            }
            if (version.Value < 2)
            {
                // 2: less saturated grade.
                Move(Warmth, 0.5f);
                Move(Saturation, 0f);
                Move(Vibrance, 0.1f);
                Move(Contrast, 0.25f);
                Move(SkyDepth, 0.45f);
            }
            // 3: no outlines by default.
            Move(InkStrength, 0.3f);
            version.Value = 3;
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
                SmokeMaterial = MaterialFrom(bundle, "Assets/Bundles/visuals/Smoke.shader");
                if (SmokeMaterial != null) SmokeNoise.Build();
                WeatherMaterial = MaterialFrom(bundle, "Assets/Bundles/visuals/WeatherParticle.shader");
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

        internal void Log(string message) => Logger.LogInfo(message);

        private void LateUpdate() => Weather.LateUpdate();

        private void ApplyClouds() => PaintedClouds.Apply(Enabled.Value && Clouds.Value && CloudMaterial != null);

        private void Update()
        {
            ExplosionGlow.Update();
            TabgInstaller.Visuals.VolumetricSmoke.Update();
            GrenadeFx.Update();
            if (Input.GetKeyDown(ToggleKey.Value))
            {
                Enabled.Value = !Enabled.Value;
                Logger.LogInfo($"[Visuals] {(Enabled.Value ? "on" : "off")}");
            }

            // The player camera is created per match; attach to whatever the main camera is now.
            if (Material == null || Time.unscaledTime < _nextCameraCheck) return;
            _nextCameraCheck = Time.unscaledTime + 1f;
            Ocean.Apply(Enabled.Value && Water.Value);
            ApplyClouds();
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
