using UnityEngine;
using UnityEngine.Rendering;

namespace TabgInstaller.Visuals
{
    /// <summary>
    /// The image effect on the main camera. Runs after every other image effect (added last).
    /// Also drives the volumetric sunlight: while this camera renders, two command buffers sit on
    /// the sun (AfterShadowMap binds the shadow map, BeforeScreenspaceMask raymarches it while the
    /// cascade matrices are set); they are removed again after the camera, so other cameras never
    /// run them.
    /// </summary>
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
        private static readonly int VolId = Shader.PropertyToID("_Vol");
        private static readonly int VolTexId = Shader.PropertyToID("_VolTex");
        private static readonly int FrustumRaysId = Shader.PropertyToID("_FrustumRays");
        private static readonly int CamPosId = Shader.PropertyToID("_CamPos");
        private static readonly int SunDirId = Shader.PropertyToID("_SunDir");
        private static readonly int VolParamsId = Shader.PropertyToID("_VolParams");
        private static readonly int BlurDirId = Shader.PropertyToID("_BlurDir");
        private static readonly int ShadowMapId = Shader.PropertyToID("_TabgShadowMap");
        private static readonly int CloudsId = Shader.PropertyToID("_Clouds");
        private static readonly int CloudTexId = Shader.PropertyToID("_CloudTex");
        private static readonly int CloudLayerId = Shader.PropertyToID("_CloudLayer");
        private static readonly int CloudShapeId = Shader.PropertyToID("_CloudShape");
        private static readonly int AmbientTopId = Shader.PropertyToID("_AmbientTop");
        private static readonly int AmbientBottomId = Shader.PropertyToID("_AmbientBottom");
        private static readonly int FlashId = Shader.PropertyToID("_Flash");
        private static readonly int FlashColorId = Shader.PropertyToID("_FlashColor");
        private static readonly int FlashInfoId = Shader.PropertyToID("_FlashInfo");
        private static readonly int GlowTexId = Shader.PropertyToID("_GlowTex");
        private static readonly int SmokePosId = Shader.PropertyToID("_SmokePos");
        private static readonly int SmokeInfoId = Shader.PropertyToID("_SmokeInfo");
        private static readonly int SmokeColorId = Shader.PropertyToID("_SmokeColor");
        private static readonly int SmokeParamsId = Shader.PropertyToID("_SmokeParams");
        private static readonly int SmokeNoiseId = Shader.PropertyToID("_SmokeNoise");
        private static readonly int SmokeTexId = Shader.PropertyToID("_SmokeTex");
        private static readonly int SmokeOnId = Shader.PropertyToID("_SmokeOn");
        private static readonly int StunId = Shader.PropertyToID("_Stun");
        private static readonly int StunFrozenId = Shader.PropertyToID("_StunFrozen");
        private static readonly int StunBlurId = Shader.PropertyToID("_StunBlur");
        private readonly Vector4[] _smokePos = new Vector4[VolumetricSmoke.MaxDrawn];
        private readonly Vector4[] _smokeInfo = new Vector4[VolumetricSmoke.MaxDrawn];
        private readonly Vector4[] _smokeColor = new Vector4[VolumetricSmoke.MaxDrawn];
        private readonly Vector4[] _smokeEmit = new Vector4[VolumetricSmoke.MaxDrawn];
        private static readonly int SmokeEmitId = Shader.PropertyToID("_SmokeEmit");
        private RenderTexture _stunFrozen, _stunVeil;
        private static readonly int WeatherId = Shader.PropertyToID("_Weather");
        private static readonly int WeatherColorId = Shader.PropertyToID("_WeatherColor");
        private static readonly int PrecipId = Shader.PropertyToID("_Precip");
        private static readonly int PrecipWindId = Shader.PropertyToID("_PrecipWind");
        private static readonly int PrecipColorId = Shader.PropertyToID("_PrecipColor");
        private static readonly int LensId = Shader.PropertyToID("_Lens");

        /// <summary>The last frame this effect drew; flashbangs need it to replace the game's overlay.</summary>
        internal static int LastRenderFrame = -10;
        private readonly Vector4[] _flash = new Vector4[4];
        private readonly Vector4[] _flashColor = new Vector4[4];

        // Distant haze: a cool, slightly lavender sky blue.
        private static readonly Color HazeColor = new Color(0.70f, 0.77f, 0.93f);

        private Camera _cam;
        private Light _sun;
        private float _nextSunSearch;
        private float _nextSunLog;

        private CommandBuffer _shadowCmd, _volCmd;
        private Light _cmdLight;
        private RenderTexture _vol, _volTmp;
        private bool _volThisFrame;
        private readonly Vector3[] _corners = new Vector3[4];

        private void OnEnable()
        {
            _cam = GetComponent<Camera>();
        }

        private void OnDisable()
        {
            RemoveLightBuffers();
            if (_vol != null) { _vol.Release(); Destroy(_vol); _vol = null; }
            if (_volTmp != null) { _volTmp.Release(); Destroy(_volTmp); _volTmp = null; }
            ReleaseFrozen();
        }

        private void OnPreCull()
        {
            // Haze, rays, lines and volumetrics need depth; TABG's AO usually requests it already.
            var p = VisualsPlugin.Instance;
            if (p != null && p.Enabled.Value)
                _cam.depthTextureMode |= DepthTextureMode.Depth;
            // Light command buffers are collected during culling, so they go on before it.
            SetupVolumetric();
        }

        private Light FindSun()
        {
            if (_sun != null && _sun.isActiveAndEnabled) return _sun;
            if (Time.unscaledTime < _nextSunSearch) return null;
            _nextSunSearch = Time.unscaledTime + 2f;
            // TABG's DayAndNightCycle drives the sun; fall back to the scene's sun or the brightest
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

        // ---------------------------------------------------------------- volumetric light

        private void SetupVolumetric()
        {
            _volThisFrame = false;
            var p = VisualsPlugin.Instance;
            var sun = FindSun();
            if (p == null || !p.Enabled.Value || p.VolumetricMaterial == null || p.VolumetricLight.Value <= 0f
                || sun == null || sun.shadows == LightShadows.None || QualitySettings.shadows == ShadowQuality.Disable
                || p.DebugView.Value == 3)
                return;

            int w = Mathf.Max(1, _cam.pixelWidth / 2), h = Mathf.Max(1, _cam.pixelHeight / 2);
            if (_vol == null || _vol.width != w || _vol.height != h)
            {
                if (_vol != null) { _vol.Release(); Destroy(_vol); }
                if (_volTmp != null) { _volTmp.Release(); Destroy(_volTmp); }
                _vol = new RenderTexture(w, h, 0, RenderTextureFormat.RHalf) { name = "TabgVolumetric", filterMode = FilterMode.Bilinear };
                _volTmp = new RenderTexture(w, h, 0, RenderTextureFormat.RHalf) { name = "TabgVolumetricTmp", filterMode = FilterMode.Bilinear };
                _volCmd?.Release();
                _volCmd = null;
            }

            if (_shadowCmd == null)
            {
                _shadowCmd = new CommandBuffer { name = "TABG Visuals: bind shadow map" };
                _shadowCmd.SetGlobalTexture(ShadowMapId, new RenderTargetIdentifier(BuiltinRenderTextureType.CurrentActive));
            }
            var m = p.VolumetricMaterial;
            if (_volCmd == null)
            {
                _volCmd = new CommandBuffer { name = "TABG Visuals: volumetric light" };
                _volCmd.Blit(Texture2D.blackTexture, _vol, m, 0);
                _volCmd.SetGlobalVector(BlurDirId, new Vector4(1f / w, 0f, 0f, 0f));
                _volCmd.Blit(_vol, _volTmp, m, 1);
                _volCmd.SetGlobalVector(BlurDirId, new Vector4(0f, 1f / h, 0f, 0f));
                _volCmd.Blit(_volTmp, _vol, m, 1);
            }

            // World-space rays through the screen corners at view depth 1 (BL, BR, TL, TR).
            _cam.CalculateFrustumCorners(new Rect(0, 0, 1, 1), 1f, Camera.MonoOrStereoscopicEye.Mono, _corners);
            var t = _cam.transform;
            var rays = new Matrix4x4();
            rays.SetRow(0, t.TransformVector(_corners[0]));     // bottom-left
            rays.SetRow(1, t.TransformVector(_corners[3]));     // bottom-right
            rays.SetRow(2, t.TransformVector(_corners[1]));     // top-left
            rays.SetRow(3, t.TransformVector(_corners[2]));     // top-right
            m.SetMatrix(FrustumRaysId, rays);
            m.SetVector(CamPosId, t.position);
            m.SetVector(SunDirId, -sun.transform.forward);
            // density, max distance (m), anisotropy (forward scattering), steps
            m.SetVector(VolParamsId, new Vector4(0.007f, 150f, 0.55f, p.VolumetricSteps.Value));

            RemoveLightBuffers();
            sun.AddCommandBuffer(LightEvent.AfterShadowMap, _shadowCmd);
            sun.AddCommandBuffer(LightEvent.AfterShadowMap, _volCmd);
            _cmdLight = sun;
            _volThisFrame = true;
        }

        private void OnPostRender()
        {
            RemoveLightBuffers();
        }

        private void RemoveLightBuffers()
        {
            if (_cmdLight == null) return;
            if (_shadowCmd != null) _cmdLight.RemoveCommandBuffer(LightEvent.AfterShadowMap, _shadowCmd);
            if (_volCmd != null) _cmdLight.RemoveCommandBuffer(LightEvent.AfterShadowMap, _volCmd);
            _cmdLight = null;
        }

        // ---------------------------------------------------------------- explosions

        /// <summary>Puts visible explosions into _Flash and renders their bloom; null if none.</summary>
        private RenderTexture RenderExplosionGlow(VisualsPlugin p, RenderTexture src)
        {
            var m = p.Material;
            int n = 0;
            var camPos = _cam.transform.position;
            float tanHalf = Mathf.Tan(_cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            foreach (var f in ExplosionGlow.Active)
            {
                if (n >= 4) break;
                float k = ExplosionGlow.Envelope(f);
                if (k <= 0.001f) continue;
                Vector3 vp = _cam.WorldToViewportPoint(f.Position);
                if (vp.z <= 0.5f) continue;
                // Hidden behind a wall or hill? Ignore hits right at either end (the blast's own
                // debris, the player's body).
                Vector3 to = camPos - f.Position;
                float dist = to.magnitude;
                float start = Mathf.Min(f.Radius * 0.4f, dist * 0.5f);
                float len = dist - start - 1.5f;
                if (len > 0f && Physics.Raycast(f.Position + to / dist * start, to / dist, len, ~0, QueryTriggerInteraction.Ignore))
                    continue;
                float r = Mathf.Clamp(f.Radius * 1.3f / (vp.z * 2f * tanHalf), 0.03f, 0.7f);
                _flash[n] = new Vector4(vp.x, vp.y, r, k * f.Power);
                _flashColor[n] = f.Color;
                n++;
            }
            m.SetVectorArray(FlashId, _flash);
            m.SetVectorArray(FlashColorId, _flashColor);
            m.SetVector(FlashInfoId, new Vector4(n, 0.3f, 1.0f, 0f));
            if (n == 0)
            {
                m.SetTexture(GlowTexId, Texture2D.blackTexture);
                return null;
            }
            int w = Mathf.Max(1, src.width / 4), h = Mathf.Max(1, src.height / 4);
            var a = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBHalf);
            var b = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBHalf);
            Graphics.Blit(src, a, m, 3);
            for (int pass = 0; pass < 2; pass++)
            {
                m.SetVector(BlurDirId, new Vector4((pass + 1f) / w, 0f, 0f, 0f));
                Graphics.Blit(a, b, m, 4);
                m.SetVector(BlurDirId, new Vector4(0f, (pass + 1f) / h, 0f, 0f));
                Graphics.Blit(b, a, m, 4);
            }
            RenderTexture.ReleaseTemporary(b);
            m.SetTexture(GlowTexId, a);
            return a;
        }

        // ---------------------------------------------------------------- clouds

        private RenderTexture RenderClouds(VisualsPlugin p, Light sun, Color sunColor, int w, int h)
        {
            var cm = p.CloudMaterial;
            if (cm == null || !p.Clouds.Value || sun == null) return null;
            _cam.CalculateFrustumCorners(new Rect(0, 0, 1, 1), 1f, Camera.MonoOrStereoscopicEye.Mono, _corners);
            var t = _cam.transform;
            var rays = new Matrix4x4();
            rays.SetRow(0, t.TransformVector(_corners[0]));
            rays.SetRow(1, t.TransformVector(_corners[3]));
            rays.SetRow(2, t.TransformVector(_corners[1]));
            rays.SetRow(3, t.TransformVector(_corners[2]));
            cm.SetMatrix(FrustumRaysId, rays);
            cm.SetVector(CamPosId, t.position);
            cm.SetVector(SunDirId, -sun.transform.forward);
            cm.SetColor(SunColorId, sunColor * 1.2f);
            // Ambient: the game's own sky ambience (changes with time of day), plus a cool shade.
            Color sky = RenderSettings.ambientSkyColor;
            cm.SetColor(AmbientTopId, Color.Lerp(new Color(0.78f, 0.84f, 0.96f), sky, 0.3f) * 0.85f);
            cm.SetColor(AmbientBottomId, Color.Lerp(new Color(0.58f, 0.6f, 0.74f), sky, 0.2f) * 0.75f);
            float baseH = p.CloudHeight.Value;
            cm.SetVector(CloudLayerId, new Vector4(baseH, baseH + 550f, Mathf.Min(0.95f, p.CloudCoverage.Value + Weather.Overcast * 0.35f), 1f));
            float wind = Time.time * 6f;
            cm.SetVector(CloudShapeId, new Vector4(wind, wind * 0.4f, 900f, p.CloudSteps.Value));
            w = Mathf.Max(1, w);
            h = Mathf.Max(1, h);
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBHalf);
            var tmp = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBHalf);
            Graphics.Blit(Texture2D.blackTexture, rt, cm, 0);
            cm.SetVector(BlurDirId, new Vector4(1f / w, 0f, 0f, 0f));
            Graphics.Blit(rt, tmp, cm, 1);
            cm.SetVector(BlurDirId, new Vector4(0f, 1f / h, 0f, 0f));
            Graphics.Blit(tmp, rt, cm, 1);
            RenderTexture.ReleaseTemporary(tmp);
            return rt;
        }

        // ---------------------------------------------------------------- smoke

        private const float SmokeTile = 5f;

        private RenderTexture RenderSmoke(VisualsPlugin p, Light sun, Color sunColor, int w, int h)
        {
            var sm = p.SmokeMaterial;
            var noise = SmokeNoise.Texture;
            if (sm == null || noise == null) return null;
            int n = VolumetricSmoke.Fill(_cam.transform.position, p.VolumetricSmoke.Value, _smokePos, _smokeInfo, _smokeColor, _smokeEmit, out float largest);
            if (n == 0) return null;

            _cam.CalculateFrustumCorners(new Rect(0, 0, 1, 1), 1f, Camera.MonoOrStereoscopicEye.Mono, _corners);
            var t = _cam.transform;
            var rays = new Matrix4x4();
            rays.SetRow(0, t.TransformVector(_corners[0]));
            rays.SetRow(1, t.TransformVector(_corners[3]));
            rays.SetRow(2, t.TransformVector(_corners[1]));
            rays.SetRow(3, t.TransformVector(_corners[2]));
            sm.SetMatrix(FrustumRaysId, rays);
            sm.SetVector(CamPosId, t.position);
            sm.SetVector(SunDirId, sun != null ? -sun.transform.forward : Vector3.up);
            sm.SetColor(SunColorId, sunColor * 0.9f);
            Color sky = RenderSettings.ambientSkyColor;
            sm.SetColor(AmbientTopId, Color.Lerp(new Color(0.55f, 0.6f, 0.7f), sky, 0.3f) * 0.75f);
            sm.SetColor(AmbientBottomId, Color.Lerp(new Color(0.3f, 0.3f, 0.34f), sky, 0.15f) * 0.7f);
            sm.SetVectorArray(SmokePosId, _smokePos);
            sm.SetVectorArray(SmokeInfoId, _smokeInfo);
            sm.SetVectorArray(SmokeColorId, _smokeColor);
            sm.SetVectorArray(SmokeEmitId, _smokeEmit);
            // A fixed noise scale in world space (one tile holds four billows of about 1.3 m): tying it to the
            // smoke's size made the whole pattern slide about every time the fit changed.
            sm.SetVector(SmokeParamsId, new Vector4(n, p.SmokeSteps.Value, SmokeTile, 0f));
            sm.SetTexture(SmokeNoiseId, noise);

            w = Mathf.Max(1, w);
            h = Mathf.Max(1, h);
            var a = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBHalf);
            var b = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBHalf);
            Graphics.Blit(Texture2D.blackTexture, a, sm, 0);
            sm.SetVector(BlurDirId, new Vector4(1f / w, 0f, 0f, 0f));
            Graphics.Blit(a, b, sm, 1);
            sm.SetVector(BlurDirId, new Vector4(0f, 1f / h, 0f, 0f));
            Graphics.Blit(b, a, sm, 1);
            RenderTexture.ReleaseTemporary(b);
            return a;
        }

        // ---------------------------------------------------------------- flashbang

        private void ReleaseFrozen()
        {
            if (_stunFrozen == null) return;
            Flashbang.VeilTexture = null;
            _stunFrozen.Release();
            Destroy(_stunFrozen);
            _stunFrozen = null;
            _stunVeil.Release();
            Destroy(_stunVeil);
            _stunVeil = null;
        }

        /// <summary>Blurs a quarter-res copy of src into a temporary (caller releases it).</summary>
        private static RenderTexture BlurredCopy(Material m, RenderTexture src, int w, int h)
        {
            var a = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            var b = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(src, a);
            for (int pass = 0; pass < 2; pass++)
            {
                m.SetVector(BlurDirId, new Vector4((pass + 1f) / w, 0f, 0f, 0f));
                Graphics.Blit(a, b, m, 4);
                m.SetVector(BlurDirId, new Vector4(0f, (pass + 1f) / h, 0f, 0f));
                Graphics.Blit(b, a, m, 4);
            }
            RenderTexture.ReleaseTemporary(b);
            return a;
        }

        /// <summary>Sets up the flashbang composite; returns the blurred view to release, or null.</summary>
        private RenderTexture PrepareFlashbang(Material m, RenderTexture src)
        {
            Flashbang.Tick();
            if (!Flashbang.Visible)
            {
                ReleaseFrozen();
                m.SetVector(StunId, Vector4.zero);
                return null;
            }
            int w = Mathf.Max(1, src.width / 4), h = Mathf.Max(1, src.height / 4);
            RenderTexture blur = BlurredCopy(m, src, w, h);
            if (Flashbang.CapturePending || _stunFrozen == null)
            {
                Flashbang.CapturePending = false;
                if (_stunFrozen == null || _stunFrozen.width != w || _stunFrozen.height != h)
                {
                    ReleaseFrozen();
                    _stunFrozen = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { name = "TabgFlashFrozen" };
                    _stunVeil = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { name = "TabgFlashVeil" };
                }
                // The moment of the flash: this frame, as it was, burned in.
                Graphics.Blit(blur, _stunFrozen);
            }
            m.SetVector(StunId, new Vector4(Flashbang.Blind, Flashbang.Age, Flashbang.Burn, Flashbang.Tail));
            m.SetTexture(StunFrozenId, _stunFrozen);
            m.SetTexture(StunBlurId, blur);
            Graphics.Blit(_stunFrozen, _stunVeil, m, 5);
            Flashbang.VeilTexture = _stunVeil;
            return blur;
        }

        // ---------------------------------------------------------------- image effect

        private void OnRenderImage(RenderTexture src, RenderTexture dst)
        {
            var p = VisualsPlugin.Instance;
            if (p == null || p.Material == null || !p.Enabled.Value)
            {
                Graphics.Blit(src, dst);
                return;
            }
            LastRenderFrame = Time.frameCount;
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
                sunVec = new Vector4(vp.x, vp.y, visible * (1f - Weather.Overcast), p.SunRays.Value);
                sunColor = sun.color * Mathf.Clamp(sun.intensity, 0.3f, 1f);
                if (p.DebugView.Value != 0 && Time.unscaledTime >= _nextSunLog)
                {
                    _nextSunLog = Time.unscaledTime + 5f;
                    Debug.Log($"[Visuals] sun '{sun.name}' viewport {vp} visible {visible:0.00} color {sun.color} intensity {sun.intensity:0.00} shadows {sun.shadows} volumetric {_volThisFrame} lightBuffers {(sun.GetCommandBuffers(LightEvent.AfterShadowMap).Length)} path {_cam.actualRenderingPath} depthMode {_cam.depthTextureMode} cascades {QualitySettings.shadowCascades} shadowDist {QualitySettings.shadowDistance}");
                }
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
            float hazeDensity = 1f / Mathf.Max(1f, p.HazeDistance.Value);
            m.SetVector(RayParamsId, new Vector4(0f, 0.65f, hazeDensity, 0f));

            bool vol = _volThisFrame && _vol != null;
            m.SetVector(VolId, new Vector4(vol ? p.VolumetricLight.Value * (1f - Weather.Overcast) : 0f, 0f, 0f, 0f));

            _cam.CalculateFrustumCorners(new Rect(0, 0, 1, 1), 1f, Camera.MonoOrStereoscopicEye.Mono, _corners);
            var camT = _cam.transform;
            var worldRays = new Matrix4x4();
            worldRays.SetRow(0, camT.TransformVector(_corners[0]));
            worldRays.SetRow(1, camT.TransformVector(_corners[3]));
            worldRays.SetRow(2, camT.TransformVector(_corners[1]));
            worldRays.SetRow(3, camT.TransformVector(_corners[2]));
            m.SetMatrix(FrustumRaysId, worldRays);
            m.SetVector(WeatherId, new Vector4(Weather.Fog, Weather.Overcast, 8f, Weather.Flash));
            m.SetColor(WeatherColorId, Weather.FogColor);
            m.SetVector(PrecipId, new Vector4(Weather.Snow, Weather.Rain, Weather.Wet, Weather.Sand));
            m.SetVector(PrecipWindId, new Vector4(Weather.WindOffset.x, Weather.WindOffset.y, Weather.Wind, 0f));
            m.SetColor(PrecipColorId, Weather.PrecipColor);
            if (SmokeNoise.Texture != null) m.SetTexture(SmokeNoiseId, SmokeNoise.Texture);
            m.SetVector(CamPosId, camT.position);
            m.SetTexture(VolTexId, vol ? (Texture)_vol : Texture2D.blackTexture);

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
                    m.SetVector(RayParamsId, new Vector4(reach, 0.65f, hazeDensity, 0f));
                    Graphics.Blit(rays, tmp, m, 2);
                    var swap = rays; rays = tmp; tmp = swap;
                }
                RenderTexture.ReleaseTemporary(tmp);
                m.SetTexture(RaysTexId, rays);
            }
            else
            {
                m.SetTexture(RaysTexId, Texture2D.blackTexture);
            }

            RenderTexture glow = RenderExplosionGlow(p, src);
            RenderTexture clouds = RenderClouds(p, sun, sunColor, src.width / 2, src.height / 2);
            m.SetVector(CloudsId, new Vector4(clouds != null ? 1f : 0f, 0f, 0f, 0f));
            m.SetTexture(CloudTexId, clouds != null ? (Texture)clouds : Texture2D.blackTexture);

            RenderTexture smoke = RenderSmoke(p, sun, sunColor, src.width / 2, src.height / 2);
            m.SetVector(SmokeOnId, new Vector4(smoke != null ? 1f : 0f, 0f, 0f, 0f));
            m.SetTexture(SmokeTexId, smoke != null ? (Texture)smoke : Texture2D.blackTexture);
            RenderTexture stunBlur = PrepareFlashbang(m, src);

            bool lens = p.WeatherLens.Value && p.DebugView.Value == 0 && (Weather.LensRain > 0.001f || Weather.LensSnow > 0.001f || Weather.LensSand > 0.001f);
            if (lens)
            {
                // Drops refract the finished picture, so it has to exist first.
                var finished = RenderTexture.GetTemporary(src.width, src.height, 0, src.format);
                Graphics.Blit(src, finished, m, 0);
                m.SetVector(LensId, new Vector4(Weather.LensRain, Weather.LensSnow, Weather.LensSand, Weather.Gust));
                Graphics.Blit(finished, dst, m, 6);
                RenderTexture.ReleaseTemporary(finished);
            }
            else
            {
                Graphics.Blit(src, dst, m, 0);
            }
            if (smoke != null) RenderTexture.ReleaseTemporary(smoke);
            if (stunBlur != null) RenderTexture.ReleaseTemporary(stunBlur);
            if (rays != null) RenderTexture.ReleaseTemporary(rays);
            if (clouds != null) RenderTexture.ReleaseTemporary(clouds);
            if (glow != null) RenderTexture.ReleaseTemporary(glow);
        }
    }
}
