using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace TabgInstaller.PerformanceClient
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInIncompatibility("tabginstaller.enhancedclient")]
    public sealed class PerformanceClientPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "tabginstaller.performanceclient";
        public const string PluginName = "TABG Performance Client";
        public const string PluginVersion = "2.0.0";

        internal static PerformanceClientPlugin Instance;

        private ConfigEntry<bool> _blackMainMenu;
        private ConfigEntry<bool> _restorePreviewScreens;
        private ConfigEntry<bool> _showFps;
        private ConfigEntry<KeyCode> _fpsToggleKey;
        private ConfigEntry<KeyCode> _shootingRangeKey;
        private ConfigEntry<bool> _autoOpenRangeForBenchmark;
        private ConfigEntry<int> _menuFpsLimit;
        private ConfigEntry<int> _gameFpsLimit;
        private ConfigEntry<int> _renderDistance;
        private ConfigEntry<int> _shadowDistance;
        private ConfigEntry<int> _pickupDrawDistance;
        private ConfigEntry<bool> _disableAmbientOcclusion;
        private ConfigEntry<bool> _disablePlanarReflections;
        private ConfigEntry<bool> _disableAtmosphericHaze;
        private ConfigEntry<bool> _disableCameraHdr;
        private ConfigEntry<bool> _disablePostProcessing;
        private ConfigEntry<float> _gameplayRenderScale;
        private ConfigEntry<bool> _optimizeRuntimeHotPaths;
        private ConfigEntry<int> _reflectionUpdateInterval;
        private ConfigEntry<float> _reflectionMaxDistance;
        private ConfigEntry<int> _reflectionTextureSize;
        private ConfigEntry<float> _remoteFullPhysicsDistance;
        private ConfigEntry<float> _physicsObjectSimulationDistance;
        private ConfigEntry<int> _interactionRefreshRate;
        private ConfigEntry<int> _pickupRefreshRate;
        private ConfigEntry<bool> _distanceCullStaticVisuals;
        private ConfigEntry<float> _smallVisualDistance;
        private ConfigEntry<float> _mediumVisualDistance;
        private ConfigEntry<float> _largeVisualDistance;

        private readonly List<CameraSnapshot> _cameraSnapshots = new List<CameraSnapshot>();
        private readonly List<CanvasSnapshot> _canvasSnapshots = new List<CanvasSnapshot>();
        private readonly List<BehaviourSnapshot> _effectSnapshots = new List<BehaviourSnapshot>();
        private readonly List<BehaviourSnapshot> _lightSnapshots = new List<BehaviourSnapshot>();
        private readonly List<BehaviourSnapshot> _animatorSnapshots = new List<BehaviourSnapshot>();
        private readonly List<ParticleSnapshot> _particleSnapshots = new List<ParticleSnapshot>();

        private Harmony _harmony;
        private GameObject _blackBackdrop;
        private MenuCamSequence _menuSequence;
        private bool _menuSequenceWasEnabled;
        private bool _menuIsBlack;
        private bool _inMainMenu;
        private float _nextSettingsRefresh;
        private float _fpsElapsed;
        private int _fpsFrames;
        private float _displayFps;
        private bool _rangeAutoOpened;
        private bool _loggedGameplayInventory;

        private static readonly string[] ExpensiveCameraEffectNames =
        {
            "AmplifyOcclusionEffect",
            "PostProcessingHandler",
            "MenuPostProcessing",
            "PostProcessLayer",
            "DS_HazeView",
            "ScreenSpaceReflection",
            "TemporalReprojection",
            "DepthOfField",
            "MotionBlur",
            "Bloom",
            "SunShafts",
            "Antialiasing"
        };

        private void Awake()
        {
            Instance = this;
            BindConfiguration();

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(PerformanceClientPlugin).Assembly);
            SceneManager.sceneLoaded += OnSceneLoaded;

            gameObject.AddComponent<CentralPhysicsCullingManager>();

            ApplyPersistentPreferences();
            ApplyRuntimeSettings();
            OnSceneLoaded(SceneManager.GetActiveScene(), LoadSceneMode.Single);

            Logger.LogInfo("[PerformanceClient] Loaded: performance rendering, hot-path allocation fixes, physics LOD, and scene-aware menu optimization are active.");
        }

        private void BindConfiguration()
        {
            _blackMainMenu = Config.Bind("Menu", "BlackBackground", true,
                "Replace the live-rendered Main Menu world with a black background while keeping the UI.");
            _restorePreviewScreens = Config.Bind("Menu", "Restore3DForDripStoreAndResults", true,
                "Temporarily restore the 3D menu scene on screens that use character/item previews.");
            _menuFpsLimit = Config.Bind("Frame pacing", "MenuFpsLimit", 144,
                new ConfigDescription("Main Menu frame cap. VSync stays disabled.", new AcceptableValueRange<int>(60, 360)));
            _gameFpsLimit = Config.Bind("Frame pacing", "GameplayFpsLimit", 120,
                new ConfigDescription("Gameplay frame cap. VSync stays disabled.", new AcceptableValueRange<int>(60, 360)));
            _showFps = Config.Bind("Overlay", "ShowFps", false, "Show Unity's update-loop FPS counter. Use an external present-time tool for authoritative display FPS.");
            _fpsToggleKey = Config.Bind("Overlay", "ToggleFpsKey", KeyCode.F10, "Toggle the FPS counter.");
            _shootingRangeKey = Config.Bind("Shortcuts", "OpenShootingRangeKey", KeyCode.F8,
                "Open the offline Shooting Range from the Main Menu using its original scene action.");
            _autoOpenRangeForBenchmark = Config.Bind("Diagnostics", "AutoOpenShootingRangeOnce", false,
                "Automatically open the offline Shooting Range once after startup. Intended only for repeatable benchmarks.");

            _renderDistance = Config.Bind("Gameplay", "RenderDistance", 1200,
                new ConfigDescription("Player camera far distance in metres.", new AcceptableValueRange<int>(500, 5000)));
            _shadowDistance = Config.Bind("Gameplay", "ShadowDistance", 100,
                new ConfigDescription("Hard-shadow distance in metres.", new AcceptableValueRange<int>(0, 500)));
            _pickupDrawDistance = Config.Bind("Gameplay", "PickupDrawDistance", 120,
                new ConfigDescription("Maximum pickup/item draw distance in metres.", new AcceptableValueRange<int>(25, 1000)));
            _disableAmbientOcclusion = Config.Bind("Gameplay", "DisableAmbientOcclusion", true,
                "Disable TABG's expensive screen-space ambient occlusion effect.");
            _disablePlanarReflections = Config.Bind("Gameplay", "DisablePlanarReflections", true,
                "Disable extra planar reflection cameras while keeping normal lighting and textures.");
            _disableAtmosphericHaze = Config.Bind("Gameplay", "DisableAtmosphericHaze", true,
                "Disable the costly DeepSky haze and its temporal buffers for a clearer image and substantially lower GPU load.");
            _disableCameraHdr = Config.Bind("Gameplay", "DisableCameraHDR", false,
                "Use a lower-bandwidth camera buffer while retaining normal textures and the post-processing stack.");
            _disablePostProcessing = Config.Bind("Gameplay", "DisablePostProcessing", false,
                "Disable TABG's final post-processing layer. This removes the costly washed-out bloom/color pass but keeps normal lighting and textures.");
            _gameplayRenderScale = Config.Bind("Gameplay", "WorldRenderScale", 1f,
                new ConfigDescription("Scale only the 3D world through a dedicated framebuffer; overlay UI remains native resolution.",
                    new AcceptableValueRange<float>(0.5f, 1f)));
            _optimizeRuntimeHotPaths = Config.Bind("Gameplay", "OptimizeRuntimeHotPaths", true,
                "Throttle wasteful decompiled per-frame terrain, physics-culling, building-LOD, and streaming checks without changing gameplay simulation.");
            _reflectionUpdateInterval = Config.Bind("Gameplay", "ReflectionUpdateInterval", 3,
                new ConfigDescription("When planar reflections are enabled, render each surface once every N frames.",
                    new AcceptableValueRange<int>(1, 8)));
            _reflectionMaxDistance = Config.Bind("Gameplay", "ReflectionMaxDistance", 250f,
                new ConfigDescription("Skip planar reflection surfaces farther than this distance and cap their camera far plane.",
                    new AcceptableValueRange<float>(50f, 1500f)));
            _reflectionTextureSize = Config.Bind("Gameplay", "ReflectionTextureSize", 128,
                new ConfigDescription("Maximum planar-reflection texture dimension.",
                    new AcceptableValueRange<int>(64, 512)));
            _remoteFullPhysicsDistance = Config.Bind("Simulation", "RemoteFullPhysicsDistance", 90f,
                new ConfigDescription("Use TABG's simplified pose/physics path for remote players beyond this distance.",
                    new AcceptableValueRange<float>(25f, 500f)));
            _physicsObjectSimulationDistance = Config.Bind("Simulation", "PhysicsObjectSimulationDistance", 300f,
                new ConfigDescription("Centralized distance at which loose physics objects are put into their kinematic state.",
                    new AcceptableValueRange<float>(75f, 2000f)));
            _interactionRefreshRate = Config.Bind("Simulation", "InteractionRefreshRate", 20,
                new ConfigDescription("Maximum expensive interaction scans per second; input frames are never skipped.",
                    new AcceptableValueRange<int>(10, 60)));
            _pickupRefreshRate = Config.Bind("Simulation", "NearbyPickupRefreshRate", 8,
                new ConfigDescription("Nearby-inventory pickup scans per second.",
                    new AcceptableValueRange<int>(2, 30)));
            _distanceCullStaticVisuals = Config.Bind("Gameplay", "DistanceCullStaticVisuals", true,
                "Disable only distant static renderers; colliders, players, vehicles, projectiles, and simulation stay active.");
            _smallVisualDistance = Config.Bind("Gameplay", "SmallVisualDistance", 160f,
                new ConfigDescription("Distance for small static props.", new AcceptableValueRange<float>(75f, 1000f)));
            _mediumVisualDistance = Config.Bind("Gameplay", "MediumVisualDistance", 300f,
                new ConfigDescription("Distance for medium static props.", new AcceptableValueRange<float>(100f, 1500f)));
            _largeVisualDistance = Config.Bind("Gameplay", "LargeVisualDistance", 600f,
                new ConfigDescription("Distance for large buildings/background meshes.", new AcceptableValueRange<float>(200f, 3000f)));
        }

        private void Update()
        {
            UpdateFpsCounter();

            if (Input.GetKeyDown(_fpsToggleKey.Value))
                _showFps.Value = !_showFps.Value;

            if (_inMainMenu && Input.GetKeyDown(_shootingRangeKey.Value))
                OpenShootingRange();

            if (_inMainMenu && _blackMainMenu.Value)
            {
                var shouldBeBlack = !_restorePreviewScreens.Value || !MenuNeeds3DPreview(MenuState.CurrentMenuState);
                if (shouldBeBlack != _menuIsBlack)
                {
                    if (shouldBeBlack)
                        ApplyBlackMenu();
                    else
                        RestoreMenuVisuals();
                }
            }

            if (Time.unscaledTime >= _nextSettingsRefresh)
            {
                _nextSettingsRefresh = Time.unscaledTime + 5f;
                ApplyRuntimeSettings();
            }

            RemotePhysicsLod.UpdateIfDue(_remoteFullPhysicsDistance.Value);
        }

        private void OnGUI()
        {
            if (!_showFps.Value || _displayFps <= 0f)
                return;

            var frameMs = 1000f / _displayFps;
            var label = string.Format("{0:0} FPS  {1:0.0} ms", _displayFps, frameMs);
            var rect = new Rect(Screen.width - 185f, 8f, 175f, 24f);
            var oldColor = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.8f);
            GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), label);
            GUI.color = _displayFps >= 60f ? new Color(0.45f, 1f, 0.55f, 1f) : new Color(1f, 0.45f, 0.35f, 1f);
            GUI.Label(rect, label);
            GUI.color = oldColor;
        }

        private void UpdateFpsCounter()
        {
            _fpsElapsed += Time.unscaledDeltaTime;
            _fpsFrames++;
            if (_fpsElapsed < 0.5f)
                return;

            _displayFps = _fpsFrames / Mathf.Max(0.0001f, _fpsElapsed);
            _fpsElapsed = 0f;
            _fpsFrames = 0;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!scene.IsValid())
                return;

            if (!string.Equals(scene.name, "MainMenu", StringComparison.OrdinalIgnoreCase))
            {
                if (_inMainMenu)
                    RestoreMenuVisuals();
                _inMainMenu = false;
                _loggedGameplayInventory = false;
                Application.targetFrameRate = _gameFpsLimit.Value;
                ApplyRuntimeSettings();
                Logger.LogInfo("[PerformanceClient] Gameplay profile applied for scene " + scene.name + ".");
                StartCoroutine(PrepareGameplayScene(scene.name));
                return;
            }

            _inMainMenu = true;
            ScalableBufferManager.ResizeBuffers(1f, 1f);
            Application.targetFrameRate = _menuFpsLimit.Value;
            StartCoroutine(PrepareMainMenu());
        }

        private IEnumerator PrepareGameplayScene(string sceneName)
        {
            yield return null;
            yield return new WaitForSecondsRealtime(3f);
            if (_inMainMenu)
                yield break;

            ApplyRuntimeSettings();
            ApplyGameplayCameraSettings();
            ConfigurePostProcessing();
            DisableGameplayReflectionCameras();
            TuneTerrain();
            InstallWorldRenderScaler();
            InstallDistanceCuller();
            if (_autoOpenRangeForBenchmark.Value)
                LogGameplayComponentInventory(sceneName);
        }

        private IEnumerator PrepareMainMenu()
        {
            yield return null;
            yield return new WaitForEndOfFrame();

            if (!_inMainMenu || !_blackMainMenu.Value)
                yield break;

            LogMenuRenderLayout();
            ApplyBlackMenu();

            if (_autoOpenRangeForBenchmark.Value && !_rangeAutoOpened)
            {
                _rangeAutoOpened = true;
                yield return new WaitForSecondsRealtime(3f);
                OpenShootingRange();
            }
        }

        private void ApplyBlackMenu()
        {
            if (_menuIsBlack || !_inMainMenu)
                return;

            ClearMenuSnapshots();
            _menuSequence = FindSceneObjects<MenuCamSequence>().FirstOrDefault();
            if (_menuSequence != null)
            {
                _menuSequenceWasEnabled = _menuSequence.enabled;
                _menuSequence.SkipIntro();
                _menuSequence.introCounter = 20f;
                if (_menuSequence.groups != null)
                {
                    foreach (var group in _menuSequence.groups)
                    {
                        if (group != null)
                            group.alpha = 1f;
                    }
                }
                _menuSequence.enabled = false;
            }

            foreach (var canvas in FindSceneObjects<Canvas>())
            {
                if (!canvas.isRootCanvas || canvas.renderMode != RenderMode.ScreenSpaceCamera)
                    continue;

                _canvasSnapshots.Add(new CanvasSnapshot(canvas));
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.worldCamera = null;
            }

            var cameras = FindSceneObjects<Camera>().ToList();
            var primary = cameras.FirstOrDefault(camera => camera != null && camera.isActiveAndEnabled && camera.CompareTag("MainCamera"))
                          ?? cameras.FirstOrDefault(camera => camera != null && camera.isActiveAndEnabled)
                          ?? cameras.FirstOrDefault();

            foreach (var camera in cameras)
            {
                if (camera == null)
                    continue;

                _cameraSnapshots.Add(new CameraSnapshot(camera));
                if (camera == primary)
                {
                    camera.enabled = true;
                    camera.clearFlags = CameraClearFlags.SolidColor;
                    camera.backgroundColor = Color.black;
                    camera.cullingMask = 0;
                    camera.depthTextureMode = DepthTextureMode.None;
                    camera.allowHDR = false;
                    camera.allowMSAA = false;
                    camera.useOcclusionCulling = false;
                }
                else
                {
                    camera.enabled = false;
                }

                DisableExpensiveEffects(camera.gameObject);
            }

            foreach (var light in FindSceneObjects<Light>())
            {
                _lightSnapshots.Add(new BehaviourSnapshot(light));
                light.enabled = false;
            }

            foreach (var animator in FindSceneObjects<Animator>())
            {
                if (animator.GetComponentInParent<Canvas>() != null)
                    continue;
                _animatorSnapshots.Add(new BehaviourSnapshot(animator));
                animator.enabled = false;
            }

            foreach (var particle in FindSceneObjects<ParticleSystem>())
            {
                _particleSnapshots.Add(new ParticleSnapshot(particle));
                particle.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }

            CreateBlackBackdrop();
            Time.timeScale = 1f;
            _menuIsBlack = true;
            Logger.LogInfo(string.Format(
                "[PerformanceClient] Black menu active: {0} cameras, {1} camera canvases, {2} lights, {3} animators, {4} particle systems suspended.",
                _cameraSnapshots.Count, _canvasSnapshots.Count, _lightSnapshots.Count, _animatorSnapshots.Count, _particleSnapshots.Count));
        }

        private void RestoreMenuVisuals()
        {
            if (!_menuIsBlack)
                return;

            if (_blackBackdrop != null)
                Destroy(_blackBackdrop);
            _blackBackdrop = null;

            foreach (var snapshot in _canvasSnapshots)
                snapshot.Restore();
            foreach (var snapshot in _cameraSnapshots)
                snapshot.Restore();
            foreach (var snapshot in _effectSnapshots)
                snapshot.Restore();
            foreach (var snapshot in _lightSnapshots)
                snapshot.Restore();
            foreach (var snapshot in _animatorSnapshots)
                snapshot.Restore();
            foreach (var snapshot in _particleSnapshots)
                snapshot.Restore();

            if (_menuSequence != null)
                _menuSequence.enabled = _menuSequenceWasEnabled;

            ClearMenuSnapshots();
            _menuIsBlack = false;
            Logger.LogInfo("[PerformanceClient] Restored the 3D menu scene for a preview screen.");
        }

        private void CreateBlackBackdrop()
        {
            if (_blackBackdrop != null)
                return;

            _blackBackdrop = new GameObject("TABG Performance - Black Menu Background");
            _blackBackdrop.layer = LayerMask.NameToLayer("UI");

            var canvas = _blackBackdrop.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = short.MinValue;

            var imageObject = new GameObject("Black");
            imageObject.layer = _blackBackdrop.layer;
            imageObject.transform.SetParent(_blackBackdrop.transform, false);
            var image = imageObject.AddComponent<Image>();
            image.color = Color.black;
            image.raycastTarget = false;

            var rect = image.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private void DisableExpensiveEffects(GameObject cameraObject)
        {
            foreach (var behaviour in cameraObject.GetComponents<Behaviour>())
            {
                if (behaviour == null || behaviour is Camera)
                    continue;
                if (!ExpensiveCameraEffectNames.Contains(behaviour.GetType().Name))
                    continue;

                _effectSnapshots.Add(new BehaviourSnapshot(behaviour));
                behaviour.enabled = false;
            }
        }

        private void ApplyPersistentPreferences()
        {
            PlayerPrefs.SetInt("Item_VSync", 0);
            PlayerPrefs.SetInt("Item_AO", _disableAmbientOcclusion.Value ? 1 : 0);
            PlayerPrefs.SetInt("Item_RenderDistance", _renderDistance.Value);
            PlayerPrefs.SetInt("Item_ShadowQuality", 2);
            PlayerPrefs.SetInt("Item_ShadowDistance", _shadowDistance.Value);
            PlayerPrefs.Save();
        }

        internal void ApplyRuntimeSettings()
        {
            QualitySettings.vSyncCount = 0;
            QualitySettings.masterTextureLimit = 0;
            QualitySettings.anisotropicFiltering = AnisotropicFiltering.Enable;
            QualitySettings.shadowResolution = ShadowResolution.Low;
            QualitySettings.shadows = _shadowDistance.Value > 0 ? ShadowQuality.HardOnly : ShadowQuality.Disable;
            QualitySettings.shadowmaskMode = ShadowmaskMode.Shadowmask;
            QualitySettings.shadowCascades = 0;
            QualitySettings.shadowDistance = _shadowDistance.Value;
            QualitySettings.pixelLightCount = 1;
            QualitySettings.softParticles = false;
            QualitySettings.realtimeReflectionProbes = false;
            QualitySettings.lodBias = 0.75f;
            QualitySettings.particleRaycastBudget = 32;

            OptionsHolder.vSync = 0;
            OptionsHolder.AO = _disableAmbientOcclusion.Value ? 1 : 0;
            OptionsHolder.renderDistance = _renderDistance.Value;
            OptionsHolder.shadowQuality = 2;
            OptionsHolder.shadowDistance = _shadowDistance.Value;

            Application.targetFrameRate = _inMainMenu ? _menuFpsLimit.Value : _gameFpsLimit.Value;

            if (PickupManager.instance != null)
                PickupManager.instance.m_DistanceThreshold = _pickupDrawDistance.Value;
        }

        internal bool DisableAtmosphericHazeEnabled => _disableAtmosphericHaze != null && _disableAtmosphericHaze.Value;
        internal bool DisablePlanarReflectionsEnabled => _disablePlanarReflections != null && _disablePlanarReflections.Value;
        internal bool OptimizeRuntimeHotPathsEnabled => _optimizeRuntimeHotPaths != null && _optimizeRuntimeHotPaths.Value;
        internal int ReflectionUpdateInterval => _reflectionUpdateInterval != null ? _reflectionUpdateInterval.Value : 3;
        internal float ReflectionMaxDistance => _reflectionMaxDistance != null ? _reflectionMaxDistance.Value : 250f;
        internal int ReflectionTextureSize => _reflectionTextureSize != null ? _reflectionTextureSize.Value : 128;
        internal float PhysicsObjectSimulationDistance => _physicsObjectSimulationDistance != null ? _physicsObjectSimulationDistance.Value : 300f;
        internal int InteractionRefreshRate => _interactionRefreshRate != null ? _interactionRefreshRate.Value : 20;
        internal int PickupRefreshRate => _pickupRefreshRate != null ? _pickupRefreshRate.Value : 8;
        internal float TerrainCullingDistance => _renderDistance != null ? _renderDistance.Value : 1200f;

        internal void LogDiagnostic(string message)
        {
            Logger.LogInfo(message);
        }

        private void ApplyGameplayCameraSettings()
        {
            var scale = Mathf.Clamp(_gameplayRenderScale.Value, 0.5f, 1f);
            ScalableBufferManager.ResizeBuffers(scale, scale);

            foreach (var camera in FindSceneObjects<Camera>())
            {
                if (camera == null || !camera.isActiveAndEnabled)
                    continue;

                camera.allowDynamicResolution = scale < 0.999f;
                if (_disableCameraHdr.Value)
                    camera.allowHDR = false;

                foreach (var behaviour in camera.GetComponents<Behaviour>())
                {
                    if (behaviour == null || !behaviour.enabled)
                        continue;

                    var effectName = behaviour.GetType().Name;
                    if ((_disableAmbientOcclusion.Value && effectName == "AmplifyOcclusionEffect")
                        || (_disablePostProcessing.Value &&
                            (effectName == "PostProcessLayer" || effectName == "PostProcessingHandler"))
                        || (_disableAtmosphericHaze.Value &&
                            (effectName == "DS_HazeView"
                             || effectName == "TemporalReprojection"
                             || effectName == "VelocityBuffer"
                             || effectName == "FrustumJitter")))
                    {
                        behaviour.enabled = false;
                    }

                    if (effectName == "DS_HazeView" && !_disableAtmosphericHaze.Value)
                        ConfigureHazeForPerformance(behaviour);
                }
            }
        }

        private static void ConfigureHazeForPerformance(Behaviour haze)
        {
            var type = haze.GetType();
            var downsample = AccessTools.Field(type, "m_DownsampleFactor");
            if (downsample != null && downsample.FieldType.IsEnum)
                downsample.SetValue(haze, Enum.ToObject(downsample.FieldType, 4));

            var samples = AccessTools.Field(type, "m_VolumeSamples");
            if (samples != null && samples.FieldType.IsEnum)
                samples.SetValue(haze, Enum.ToObject(samples.FieldType, 0));

            var temporal = AccessTools.Field(type, "m_TemporalReprojection");
            if (temporal != null)
                temporal.SetValue(haze, false);
        }

        private void ConfigurePostProcessing()
        {
            foreach (var behaviour in FindSceneObjects<Behaviour>())
            {
                if (behaviour == null)
                    continue;

                var type = behaviour.GetType();
                if (type.Name == "PostProcessLayer")
                {
                    if (_disablePostProcessing.Value)
                    {
                        behaviour.enabled = false;
                        continue;
                    }

                    var antialiasingField = AccessTools.Field(type, "antialiasingMode");
                    if (antialiasingField != null && antialiasingField.FieldType.IsEnum)
                    {
                        try
                        {
                            antialiasingField.SetValue(behaviour,
                                Enum.Parse(antialiasingField.FieldType, "FastApproximateAntialiasing"));
                        }
                        catch
                        {
                            // Retain the game's current AA mode if this PP stack uses a different enum.
                        }
                    }
                    continue;
                }

                if (type.Name != "PostProcessVolume")
                    continue;

                const BindingFlags profileFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var profileProperty = type.GetProperty("sharedProfile", profileFlags)
                                      ?? type.GetProperty("profile", profileFlags);
                var profile = profileProperty?.GetValue(behaviour, null);
                if (profile == null)
                    continue;

                var settingsField = AccessTools.Field(profile.GetType(), "settings");
                if (!(settingsField?.GetValue(profile) is IEnumerable settings))
                    continue;

                foreach (var setting in settings)
                {
                    if (setting == null)
                        continue;
                    var settingName = setting.GetType().Name;
                    if (settingName != "AmbientOcclusion"
                        && settingName != "MotionBlur"
                        && settingName != "DepthOfField"
                        && settingName != "ScreenSpaceReflections")
                        continue;

                    DisablePostProcessSetting(setting);
                }
            }
        }

        private static void DisablePostProcessSetting(object setting)
        {
            var type = setting.GetType();
            var activeField = AccessTools.Field(type, "active");
            if (activeField != null && activeField.FieldType == typeof(bool))
                activeField.SetValue(setting, false);

            var enabledField = AccessTools.Field(type, "enabled");
            var enabledParameter = enabledField?.GetValue(setting);
            if (enabledParameter == null)
                return;

            var valueProperty = AccessTools.Property(enabledParameter.GetType(), "value");
            if (valueProperty != null && valueProperty.CanWrite && valueProperty.PropertyType == typeof(bool))
                valueProperty.SetValue(enabledParameter, false, null);
        }

        private static void TuneTerrain()
        {
            foreach (var terrain in FindSceneObjects<Terrain>())
            {
                terrain.heightmapPixelError = Mathf.Max(terrain.heightmapPixelError, 12f);
                terrain.basemapDistance = Mathf.Min(terrain.basemapDistance, 500f);
                terrain.treeDistance = Mathf.Min(terrain.treeDistance, 450f);
                terrain.treeBillboardDistance = Mathf.Min(terrain.treeBillboardDistance, 90f);
                terrain.detailObjectDistance = Mathf.Min(terrain.detailObjectDistance, 60f);
                terrain.detailObjectDensity = Mathf.Min(terrain.detailObjectDensity, 0.7f);
                terrain.drawInstanced = true;
            }

            foreach (var light in FindSceneObjects<Light>())
                if (light.type == LightType.Directional)
                    light.shadows = Instance != null && Instance._shadowDistance.Value > 0
                        ? LightShadows.Hard
                        : LightShadows.None;
        }

        private void LogGameplayComponentInventory(string sceneName)
        {
            if (_loggedGameplayInventory)
                return;
            _loggedGameplayInventory = true;

            var cameras = FindSceneObjects<Camera>().Where(camera => camera.isActiveAndEnabled).ToList();
            var terrains = FindSceneObjects<Terrain>().ToList();
            var renderers = FindSceneObjects<Renderer>().Count();
            var particles = FindSceneObjects<ParticleSystem>().Count();
            Logger.LogInfo(string.Format(
                "[PerformanceClient] Scene {0} after tuning: {1} active cameras, {2} terrains, {3} renderers, {4} particle systems, render scale {5:0.00}.",
                sceneName, cameras.Count, terrains.Count, renderers, particles, ScalableBufferManager.widthScaleFactor));

            foreach (var camera in cameras)
            {
                var effects = string.Join(",", camera.GetComponents<Behaviour>()
                    .Where(component => component != null)
                    .Select(component => component.GetType().Name + "=" + component.enabled)
                    .ToArray());
                Logger.LogInfo("[PerformanceClient] Gameplay camera '" + camera.name + "': " + effects);
            }

            var commonBehaviours = FindSceneObjects<Behaviour>()
                .GroupBy(behaviour => behaviour.GetType().Name)
                .Select(group => new { Name = group.Key, Count = group.Count() })
                .OrderByDescending(group => group.Count)
                .ThenBy(group => group.Name)
                .Take(30);
            Logger.LogInfo("[PerformanceClient] Most common scene behaviours: " +
                string.Join(", ", commonBehaviours.Select(group => group.Name + "=" + group.Count).ToArray()));
        }

        private void DisableGameplayReflectionCameras()
        {
            if (!_disablePlanarReflections.Value)
                return;

            foreach (var behaviour in FindSceneObjects<Behaviour>())
            {
                if (behaviour != null && behaviour.enabled && behaviour.GetType().Name == "MirrorReflectionHDR")
                    behaviour.enabled = false;
            }
        }

        private void InstallWorldRenderScaler()
        {
            var scale = Mathf.Clamp(_gameplayRenderScale.Value, 0.5f, 1f);
            if (scale >= 0.999f)
                return;

            var camera = FindSceneObjects<Camera>().FirstOrDefault(item => item.isActiveAndEnabled && item.CompareTag("MainCamera"))
                         ?? FindSceneObjects<Camera>().FirstOrDefault(item => item.isActiveAndEnabled);
            if (camera == null || camera.GetComponent<WorldRenderScaler>() != null)
                return;

            var scaler = camera.gameObject.AddComponent<WorldRenderScaler>();
            scaler.Initialize(scale);
        }

        private void InstallDistanceCuller()
        {
            if (!_distanceCullStaticVisuals.Value || FindSceneObjects<StaticVisualDistanceCuller>().Any())
                return;

            var camera = FindSceneObjects<Camera>().FirstOrDefault(item => item.isActiveAndEnabled && item.CompareTag("MainCamera"))
                         ?? FindSceneObjects<Camera>().FirstOrDefault(item => item.isActiveAndEnabled);
            if (camera == null)
                return;

            var host = new GameObject("TABG Performance - Static Visual Culler");
            SceneManager.MoveGameObjectToScene(host, SceneManager.GetActiveScene());
            var culler = host.AddComponent<StaticVisualDistanceCuller>();
            culler.Initialize(camera.transform, _smallVisualDistance.Value, _mediumVisualDistance.Value, _largeVisualDistance.Value);
        }

        private void LogMenuRenderLayout()
        {
            var cameras = FindSceneObjects<Camera>().ToList();
            var canvases = FindSceneObjects<Canvas>().Where(canvas => canvas.isRootCanvas).ToList();
            Logger.LogInfo(string.Format("[PerformanceClient] MainMenu render layout before optimization: {0} cameras, {1} root canvases.", cameras.Count, canvases.Count));

            foreach (var camera in cameras)
            {
                var effects = string.Join(",", camera.GetComponents<Behaviour>()
                    .Where(component => component != null && !(component is Camera))
                    .Select(component => component.GetType().Name)
                    .ToArray());
                Logger.LogInfo(string.Format("[PerformanceClient] Camera '{0}': enabled={1}, tag={2}, mask=0x{3:X8}, effects=[{4}]",
                    camera.name, camera.enabled, camera.tag, camera.cullingMask, effects));
            }

            foreach (var canvas in canvases)
            {
                Logger.LogInfo(string.Format("[PerformanceClient] Canvas '{0}': mode={1}, order={2}, camera={3}",
                    canvas.name, canvas.renderMode, canvas.sortingOrder,
                    canvas.worldCamera != null ? canvas.worldCamera.name : "none"));
            }

            foreach (var sceneLink in FindSceneObjects<GoToScene>())
            {
                Logger.LogInfo(string.Format("[PerformanceClient] Scene button '{0}': scene={1}, offline={2}",
                    GetHierarchyPath(sceneLink.transform), sceneLink.sceneName, sceneLink.goOffline));
            }
        }

        private void OpenShootingRange()
        {
            var sceneLink = FindSceneObjects<GoToScene>().FirstOrDefault(link =>
                string.Equals(link.sceneName, "WilhelmTest", StringComparison.OrdinalIgnoreCase));
            if (sceneLink == null)
            {
                Logger.LogWarning("[PerformanceClient] The Shooting Range scene action was not found.");
                return;
            }

            Logger.LogInfo("[PerformanceClient] Opening offline Shooting Range through " + GetHierarchyPath(sceneLink.transform) + ".");
            sceneLink.GoToSceneByName();
        }

        private static string GetHierarchyPath(Transform transform)
        {
            if (transform == null)
                return "<missing>";

            var names = new Stack<string>();
            while (transform != null)
            {
                names.Push(transform.name);
                transform = transform.parent;
            }
            return string.Join("/", names.ToArray());
        }

        private static bool MenuNeeds3DPreview(MenuState.TABGMenuState state)
        {
            return state == MenuState.TABGMenuState.Drip
                   || state == MenuState.TABGMenuState.Shop
                   || state == MenuState.TABGMenuState.Battlepass
                   || state == MenuState.TABGMenuState.BuyBattlepass
                   || state == MenuState.TABGMenuState.ResultScreen;
        }

        private static IEnumerable<T> FindSceneObjects<T>() where T : Component
        {
            return Resources.FindObjectsOfTypeAll<T>()
                .Where(component => component != null && component.gameObject.scene.IsValid() && component.gameObject.scene.isLoaded);
        }

        private void ClearMenuSnapshots()
        {
            _cameraSnapshots.Clear();
            _canvasSnapshots.Clear();
            _effectSnapshots.Clear();
            _lightSnapshots.Clear();
            _animatorSnapshots.Clear();
            _particleSnapshots.Clear();
            _menuSequence = null;
        }

        private void OnDestroy()
        {
            RestoreMenuVisuals();
            RemotePhysicsLod.RestoreAll();
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _harmony?.UnpatchSelf();
            Instance = null;
        }

        private sealed class CameraSnapshot
        {
            private readonly Camera _camera;
            private readonly bool _enabled;
            private readonly CameraClearFlags _clearFlags;
            private readonly Color _backgroundColor;
            private readonly int _cullingMask;
            private readonly DepthTextureMode _depthTextureMode;
            private readonly bool _allowHdr;
            private readonly bool _allowMsaa;
            private readonly bool _useOcclusionCulling;

            internal CameraSnapshot(Camera camera)
            {
                _camera = camera;
                _enabled = camera.enabled;
                _clearFlags = camera.clearFlags;
                _backgroundColor = camera.backgroundColor;
                _cullingMask = camera.cullingMask;
                _depthTextureMode = camera.depthTextureMode;
                _allowHdr = camera.allowHDR;
                _allowMsaa = camera.allowMSAA;
                _useOcclusionCulling = camera.useOcclusionCulling;
            }

            internal void Restore()
            {
                if (_camera == null)
                    return;
                _camera.enabled = _enabled;
                _camera.clearFlags = _clearFlags;
                _camera.backgroundColor = _backgroundColor;
                _camera.cullingMask = _cullingMask;
                _camera.depthTextureMode = _depthTextureMode;
                _camera.allowHDR = _allowHdr;
                _camera.allowMSAA = _allowMsaa;
                _camera.useOcclusionCulling = _useOcclusionCulling;
            }
        }

        private sealed class CanvasSnapshot
        {
            private readonly Canvas _canvas;
            private readonly RenderMode _renderMode;
            private readonly Camera _worldCamera;
            private readonly float _planeDistance;

            internal CanvasSnapshot(Canvas canvas)
            {
                _canvas = canvas;
                _renderMode = canvas.renderMode;
                _worldCamera = canvas.worldCamera;
                _planeDistance = canvas.planeDistance;
            }

            internal void Restore()
            {
                if (_canvas == null)
                    return;
                _canvas.renderMode = _renderMode;
                _canvas.worldCamera = _worldCamera;
                _canvas.planeDistance = _planeDistance;
            }
        }

        private sealed class BehaviourSnapshot
        {
            private readonly Behaviour _behaviour;
            private readonly bool _enabled;

            internal BehaviourSnapshot(Behaviour behaviour)
            {
                _behaviour = behaviour;
                _enabled = behaviour.enabled;
            }

            internal void Restore()
            {
                if (_behaviour != null)
                    _behaviour.enabled = _enabled;
            }
        }

        private sealed class ParticleSnapshot
        {
            private readonly ParticleSystem _particle;
            private readonly bool _wasPlaying;
            private readonly bool _wasPaused;

            internal ParticleSnapshot(ParticleSystem particle)
            {
                _particle = particle;
                _wasPlaying = particle.isPlaying;
                _wasPaused = particle.isPaused;
            }

            internal void Restore()
            {
                if (_particle == null)
                    return;
                if (_wasPlaying)
                    _particle.Play(true);
                else if (_wasPaused)
                    _particle.Pause(true);
            }
        }
    }

    internal sealed class WorldRenderScaler : MonoBehaviour
    {
        private Camera _camera;
        private RenderTexture _renderTexture;
        private GameObject _displayObject;
        private RenderTexture _originalTarget;
        private bool _originalDynamicResolution;
        private float _scale;
        private int _screenWidth;
        private int _screenHeight;

        internal void Initialize(float scale)
        {
            _camera = GetComponent<Camera>();
            _scale = Mathf.Clamp(scale, 0.5f, 1f);
            _originalTarget = _camera.targetTexture;
            _originalDynamicResolution = _camera.allowDynamicResolution;
            RecreateTarget();
        }

        private void LateUpdate()
        {
            if (_camera != null && (_screenWidth != Screen.width || _screenHeight != Screen.height))
                RecreateTarget();
        }

        private void RecreateTarget()
        {
            ReleaseTarget();
            _screenWidth = Screen.width;
            _screenHeight = Screen.height;
            if (_screenWidth <= 0 || _screenHeight <= 0 || _camera == null)
                return;

            var width = Mathf.Max(640, Mathf.RoundToInt(_screenWidth * _scale));
            var height = Mathf.Max(360, Mathf.RoundToInt(_screenHeight * _scale));
            width -= width % 2;
            height -= height % 2;

            _renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.Default)
            {
                name = "TABG Performance World " + width + "x" + height,
                filterMode = FilterMode.Bilinear,
                useMipMap = false,
                autoGenerateMips = false,
                antiAliasing = 1
            };
            _renderTexture.Create();

            _displayObject = new GameObject("TABG Performance - Native UI World Display");
            _displayObject.transform.SetParent(transform, false);
            var canvas = _displayObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = -32000;

            var imageObject = new GameObject("Scaled 3D World");
            imageObject.transform.SetParent(_displayObject.transform, false);
            var image = imageObject.AddComponent<RawImage>();
            image.texture = _renderTexture;
            image.color = Color.white;
            image.raycastTarget = false;
            var rect = image.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            _camera.allowDynamicResolution = false;
            _camera.targetTexture = _renderTexture;
            Debug.Log(string.Format("[PerformanceClient] 3D world framebuffer: {0}x{1} -> {2}x{3}; overlay UI remains native.",
                width, height, _screenWidth, _screenHeight));
        }

        private void ReleaseTarget()
        {
            if (_camera != null && _camera.targetTexture == _renderTexture)
                _camera.targetTexture = _originalTarget;
            if (_displayObject != null)
                Destroy(_displayObject);
            _displayObject = null;
            if (_renderTexture != null)
            {
                _renderTexture.Release();
                Destroy(_renderTexture);
            }
            _renderTexture = null;
        }

        private void OnDestroy()
        {
            ReleaseTarget();
            if (_camera != null)
                _camera.allowDynamicResolution = _originalDynamicResolution;
        }
    }

    internal sealed class StaticVisualDistanceCuller : MonoBehaviour
    {
        private sealed class Entry
        {
            internal Renderer Renderer;
            internal Vector3 Center;
            internal float DistanceSquared;
            internal bool Culled;
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private Transform _camera;
        private int _cursor;
        private float _nextReport;
        private Vector3 _lastCullPosition;
        private bool _cullCyclePending;

        internal void Initialize(Transform cameraTransform, float smallDistance, float mediumDistance, float largeDistance)
        {
            _camera = cameraTransform;
            foreach (var renderer in Object.FindObjectsOfType<Renderer>())
            {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    continue;
                if (renderer.GetComponentInParent<Player>() != null || renderer.GetComponentInParent<Car>() != null)
                    continue;
                if (renderer is TrailRenderer || renderer is LineRenderer || renderer is SkinnedMeshRenderer)
                    continue;
                if (renderer.GetComponentInParent<Rigidbody>() != null || renderer.GetComponentInParent<Animator>() != null)
                    continue;

                var extent = renderer.bounds.extents.magnitude;
                var distance = renderer is ParticleSystemRenderer
                    ? Mathf.Min(smallDistance, 110f)
                    : (extent <= 2f ? smallDistance : (extent <= 12f ? mediumDistance : largeDistance));
                var center = renderer is ParticleSystemRenderer ? renderer.transform.position : renderer.bounds.center;
                var limit = distance + extent;

                _entries.Add(new Entry { Renderer = renderer, Center = center, DistanceSquared = limit * limit });
                if (extent <= 1.25f)
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }

            _lastCullPosition = _camera != null ? _camera.position : Vector3.zero;
            CullBatch(_entries.Count);
            _cullCyclePending = false;
            PerformanceClientPlugin.Instance?.LogDiagnostic("[PerformanceClient] Distance culler tracks " + _entries.Count + " active static visual renderers; rigidbodies, animated meshes, gameplay objects, and physics are excluded.");
        }

        private void Update()
        {
            if (_camera == null)
                return;

            if (!_cullCyclePending)
            {
                if ((_camera.position - _lastCullPosition).sqrMagnitude < 16f)
                {
                    ReportIfDue();
                    return;
                }

                _lastCullPosition = _camera.position;
                _cullCyclePending = true;
            }

            CullBatch(256);
            if (_cursor == 0)
                _cullCyclePending = false;

            ReportIfDue();
        }

        private void ReportIfDue()
        {
            if (Time.unscaledTime < _nextReport)
                return;
            _nextReport = Time.unscaledTime + 10f;
            PerformanceClientPlugin.Instance?.LogDiagnostic("[PerformanceClient] Distance culler currently hides " + _entries.Count(entry => entry.Culled) + " / " + _entries.Count + " static renderers.");
        }

        private void CullBatch(int count)
        {
            if (_camera == null || _entries.Count == 0)
                return;

            var cameraPosition = _camera.position;
            var end = Mathf.Min(_cursor + count, _entries.Count);
            for (var index = _cursor; index < end; index++)
            {
                var entry = _entries[index];
                var renderer = entry.Renderer;
                if (renderer == null)
                    continue;

                var far = (entry.Center - cameraPosition).sqrMagnitude > entry.DistanceSquared;
                if (far && !entry.Culled && renderer.enabled)
                {
                    renderer.enabled = false;
                    entry.Culled = true;
                }
                else if (!far && entry.Culled)
                {
                    renderer.enabled = true;
                    entry.Culled = false;
                }
            }

            _cursor = end >= _entries.Count ? 0 : end;
        }

        private void OnDestroy()
        {
            foreach (var entry in _entries)
            {
                if (entry.Culled && entry.Renderer != null)
                    entry.Renderer.enabled = true;
            }
        }
    }

    [HarmonyPatch(typeof(OptionsHolder), nameof(OptionsHolder.ApplyGameClientOptions))]
    internal static class OptionsHolderApplyPatch
    {
        private static void Postfix()
        {
            PerformanceClientPlugin.Instance?.ApplyRuntimeSettings();
        }
    }

    [HarmonyPatch(typeof(ShadowDistance), "Awake")]
    internal static class ShadowDistanceAwakePatch
    {
        private static void Postfix()
        {
            PerformanceClientPlugin.Instance?.ApplyRuntimeSettings();
        }
    }

    // TABG's water/mirror component renders the scene through a second camera
    // every time the surface is visible. Skip that entire duplicate render in
    // the performance profile.
    [HarmonyPatch(typeof(MirrorReflectionHDR), "OnWillRenderObject")]
    internal static class MirrorReflectionRenderPatch
    {
        private static bool Prefix(MirrorReflectionHDR __instance)
        {
            var plugin = PerformanceClientPlugin.Instance;
            if (plugin == null)
                return true;
            if (plugin.DisablePlanarReflectionsEnabled)
                return false;

            var current = Camera.current;
            if (current == null)
                return false;

            var maximumDistance = plugin.ReflectionMaxDistance;
            if ((__instance.transform.position - current.transform.position).sqrMagnitude > maximumDistance * maximumDistance)
                return false;

            __instance.m_TextureSize = Mathf.Min(__instance.m_TextureSize, plugin.ReflectionTextureSize);
            var interval = Mathf.Max(1, plugin.ReflectionUpdateInterval);
            return interval == 1 || (Time.frameCount + Mathf.Abs(__instance.GetInstanceID())) % interval == 0;
        }
    }

    [HarmonyPatch(typeof(MirrorReflectionHDR), "UpdateCameraModes")]
    internal static class MirrorReflectionCameraDistancePatch
    {
        private static void Postfix(Camera dest)
        {
            var plugin = PerformanceClientPlugin.Instance;
            if (plugin != null && dest != null)
                dest.farClipPlane = Mathf.Min(dest.farClipPlane, plugin.ReflectionMaxDistance);
        }
    }

    // The bundled Playdead TAA path allocates and processes multiple full-size
    // frame/velocity buffers. DeepSky haze is disabled by the selected profile,
    // so its temporal support work can be skipped as well.
    [HarmonyPatch(typeof(TemporalReprojection), "OnRenderImage")]
    internal static class TemporalReprojectionPatch
    {
        private static bool Prefix(RenderTexture source, RenderTexture destination)
        {
            var skip = PerformanceClientPlugin.Instance != null
                       && PerformanceClientPlugin.Instance.DisableAtmosphericHazeEnabled;
            if (skip)
                Graphics.Blit(source, destination);
            return !skip;
        }
    }

    [HarmonyPatch(typeof(VelocityBuffer), "OnPostRender")]
    internal static class VelocityBufferPatch
    {
        private static bool Prefix()
        {
            return PerformanceClientPlugin.Instance == null
                   || !PerformanceClientPlugin.Instance.DisableAtmosphericHazeEnabled;
        }
    }

    [HarmonyPatch(typeof(FrustumJitter), "OnPreCull")]
    internal static class FrustumJitterPatch
    {
        private static bool Prefix()
        {
            return PerformanceClientPlugin.Instance == null
                   || !PerformanceClientPlugin.Instance.DisableAtmosphericHazeEnabled;
        }
    }

    // These decompiled Update methods repeat static or slow-changing work every
    // rendered frame. Distribute their checks across frames; physics simulation,
    // player movement, weapons, projectiles, and networking remain untouched.
    [HarmonyPatch(typeof(TallWorkLOD), "Update")]
    internal static class TallWorkLodUpdatePatch
    {
        private static bool Prefix(TallWorkLOD __instance)
        {
            return PerformanceClientPlugin.Instance == null
                   || !PerformanceClientPlugin.Instance.OptimizeRuntimeHotPathsEnabled
                   || ((Time.frameCount + __instance.GetInstanceID()) % 12 == 0);
        }
    }

    [HarmonyPatch(typeof(TerrainCullingSystem), "Update")]
    internal static class TerrainCullingUpdatePatch
    {
        private static bool Prefix()
        {
            return PerformanceClientPlugin.Instance == null
                   || !PerformanceClientPlugin.Instance.OptimizeRuntimeHotPathsEnabled;
        }
    }

    [HarmonyPatch(typeof(PhysicCullingSystem), "Update")]
    internal static class PhysicsCullingUpdatePatch
    {
        private static bool Prefix()
        {
            return PerformanceClientPlugin.Instance == null
                   || !PerformanceClientPlugin.Instance.OptimizeRuntimeHotPathsEnabled;
        }
    }

    [HarmonyPatch(typeof(WilhelmStreaming), "Update")]
    internal static class WilhelmStreamingUpdatePatch
    {
        private static float _nextUpdate;

        private static bool Prefix()
        {
            var plugin = PerformanceClientPlugin.Instance;
            if (plugin == null || !plugin.OptimizeRuntimeHotPathsEnabled)
                return true;
            if (Time.unscaledTime < _nextUpdate)
                return false;

            _nextUpdate = Time.unscaledTime + 0.1f;
            return true;
        }
    }

    [HarmonyPatch(typeof(TerrainBlending), "Update")]
    internal static class TerrainBlendingUpdatePatch
    {
        private static bool Prefix(TerrainBlending __instance)
        {
            return PerformanceClientPlugin.Instance == null
                   || !PerformanceClientPlugin.Instance.OptimizeRuntimeHotPathsEnabled
                   || ((Time.frameCount + __instance.GetInstanceID()) % 60 == 0);
        }
    }

    [HarmonyPatch(typeof(DayAndNightCycle), "Update")]
    internal static class DayNightUpdatePatch
    {
        private static bool Prefix()
        {
            return PerformanceClientPlugin.Instance == null
                   || !PerformanceClientPlugin.Instance.OptimizeRuntimeHotPathsEnabled
                   || Time.frameCount % 4 == 0;
        }
    }
}
