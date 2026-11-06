using HarmonyLib;
using UnityEngine;

namespace TabgInstaller.Visuals
{
    /// <summary>
    /// Shadow and ambient-occlusion upgrades. Only when the game's own shadow option is High
    /// (OptionsHolder.shadowQuality == 0): PerformanceClient's performance profile sets it to Low, so
    /// the two mods never fight. Re-applied after the game applies its options.
    /// </summary>
    internal static class Quality
    {
        private struct AOState
        {
            public AmplifyOcclusionBase.SampleCountLevel SampleCount;
            public float Intensity, Radius;
            public bool Blur, Fade;
            public int BlurPasses, BlurRadius;
            public float FadeStart, FadeLength;
        }

        private static AmplifyOcclusionEffect _ao;
        private static AOState _aoOriginal;

        internal static void Apply()
        {
            var p = VisualsPlugin.Instance;
            if (p == null) return;
            bool on = p.Enabled.Value;
            ApplyShadows(on && p.BetterShadows.Value);
            ApplyAO(on && p.BetterAO.Value, p.AOStrength.Value);
        }

        private static void ApplyShadows(bool better)
        {
            if (OptionsHolder.shadowQuality != 0)
                return;     // Medium/Low or PerformanceClient's profile: leave alone
            if (better)
            {
                QualitySettings.shadows = ShadowQuality.All;
                QualitySettings.shadowResolution = ShadowResolution.VeryHigh;
                QualitySettings.shadowCascades = 4;
                // More resolution close to the camera, where shadows are looked at.
                QualitySettings.shadowCascade4Split = new Vector3(0.04f, 0.12f, 0.35f);
                foreach (var l in Object.FindObjectsOfType<Light>())
                    if (l.type == LightType.Directional && l.shadows != LightShadows.None)
                        l.shadows = LightShadows.Soft;
            }
            else
            {
                // The game's own High preset (OptionsHolder.ApplyGameClientOptions).
                QualitySettings.shadowResolution = ShadowResolution.High;
                QualitySettings.shadowCascades = 4;
                QualitySettings.shadowCascade4Split = new Vector3(0.067f, 0.2f, 0.467f);
            }
        }

        private static void ApplyAO(bool better, float strength)
        {
            var cam = Camera.main;
            var ao = cam != null ? cam.GetComponent<AmplifyOcclusionEffect>() : null;
            if (ao == null) return;
            if (ao != _ao)
            {
                _ao = ao;
                _aoOriginal = new AOState
                {
                    SampleCount = ao.SampleCount, Intensity = ao.Intensity, Radius = ao.Radius,
                    Blur = ao.BlurEnabled, BlurPasses = ao.BlurPasses, BlurRadius = ao.BlurRadius,
                    Fade = ao.FadeEnabled, FadeStart = ao.FadeStart, FadeLength = ao.FadeLength,
                };
            }
            if (better)
            {
                ao.SampleCount = AmplifyOcclusionBase.SampleCountLevel.High;
                ao.Intensity = strength;
                ao.Radius = 2.2f;
                ao.BlurEnabled = true;
                ao.BlurPasses = 2;
                ao.BlurRadius = 3;
                // Far AO is mostly noise on terrain; fade it out with distance.
                ao.FadeEnabled = true;
                ao.FadeStart = 60f;
                ao.FadeLength = 120f;
                ao.FadeToIntensity = 0f;
                ao.FadeToRadius = ao.Radius;
            }
            else
            {
                var o = _aoOriginal;
                ao.SampleCount = o.SampleCount; ao.Intensity = o.Intensity; ao.Radius = o.Radius;
                ao.BlurEnabled = o.Blur; ao.BlurPasses = o.BlurPasses; ao.BlurRadius = o.BlurRadius;
                ao.FadeEnabled = o.Fade; ao.FadeStart = o.FadeStart; ao.FadeLength = o.FadeLength;
            }
        }
    }

    [HarmonyPatch(typeof(OptionsHolder), nameof(OptionsHolder.ApplyGameClientOptions))]
    internal static class ApplyGameClientOptionsPatch
    {
        // Run after the game (and after PerformanceClient, which sets shadowQuality first).
        [HarmonyPriority(Priority.Last)]
        private static void Postfix() => Quality.Apply();
    }
}
