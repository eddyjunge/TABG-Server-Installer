using System;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace TabgInstaller.CustomGrenades
{
    /// <summary>
    /// Runs on BOTH client AND server.
    /// Makes all smoke grenades (ID 208) into big purple smoke grenades.
    /// </summary>
    [BepInPlugin("tabginstaller.customgrenades", "TABG Big Smoke Grenade", "1.0.0")]
    public class CustomGrenadesPlugin : BaseUnityPlugin
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> SmokeSizeMultiplier;
        internal static ConfigEntry<float> SmokeLifetimeMultiplier;
        internal static ConfigEntry<float> SmokeEmissionMultiplier;
        internal static ConfigEntry<float> GrenadeScaleMultiplier;

        private void Awake()
        {
            Enabled = Config.Bind("BigSmoke", "Enabled", true, "Enable giant purple smoke behavior for smoke grenades.");
            SmokeSizeMultiplier = Config.Bind("BigSmoke", "SmokeSizeMultiplier", 8f, "Particle size multiplier for Big Smoke grenades.");
            SmokeLifetimeMultiplier = Config.Bind("BigSmoke", "SmokeLifetimeMultiplier", 3f, "Particle lifetime multiplier for Big Smoke grenades.");
            SmokeEmissionMultiplier = Config.Bind("BigSmoke", "SmokeEmissionMultiplier", 3f, "Particle emission multiplier for Big Smoke grenades.");
            GrenadeScaleMultiplier = Config.Bind("BigSmoke", "GrenadeScaleMultiplier", 4f, "GameObject scale multiplier for Big Smoke grenades.");

            var harmony = new Harmony("tabginstaller.customgrenades");
            harmony.PatchAll(typeof(SmokePatch));
            Logger.LogInfo("[BigSmoke] Loaded! All smoke grenades are now big purple smoke.");
        }

        [HarmonyPatch(typeof(Grenade), "Start")]
        internal static class SmokePatch
        {
            static void Postfix(Grenade __instance)
            {
                try
                {
                    if (Enabled != null && !Enabled.Value) return;
                    if (__instance == null) return;

                    var pickup = __instance.GetComponentInParent<Pickup>();
                    if (pickup == null || pickup.m_itemIndex != 208) return;

                    if (__instance.GetComponent<BigSmokeMarker>() != null) return;

                    float sizeMultiplier = Mathf.Max(0.1f, SmokeSizeMultiplier?.Value ?? 8f);
                    float lifetimeMultiplier = Mathf.Max(0.1f, SmokeLifetimeMultiplier?.Value ?? 3f);
                    float emissionMultiplier = Mathf.Max(0.1f, SmokeEmissionMultiplier?.Value ?? 3f);
                    float scaleMultiplier = Mathf.Max(0.1f, GrenadeScaleMultiplier?.Value ?? 4f);

                    foreach (var ps in __instance.GetComponentsInChildren<ParticleSystem>(true))
                    {
                        var main = ps.main;
                        main.startColor = new ParticleSystem.MinMaxGradient(
                            new Color(0.3f, 0.0f, 0.5f, 1f),
                            new Color(0.5f, 0.1f, 0.8f, 0.6f));

                        if (main.startSize.mode == ParticleSystemCurveMode.TwoConstants)
                            main.startSize = new ParticleSystem.MinMaxCurve(
                                main.startSize.constantMin * sizeMultiplier,
                                main.startSize.constantMax * sizeMultiplier);
                        else if (main.startSize.mode == ParticleSystemCurveMode.Constant)
                            main.startSize = main.startSize.constant * sizeMultiplier;

                        if (main.startLifetime.mode == ParticleSystemCurveMode.TwoConstants)
                            main.startLifetime = new ParticleSystem.MinMaxCurve(
                                main.startLifetime.constantMin * lifetimeMultiplier,
                                main.startLifetime.constantMax * lifetimeMultiplier);
                        else if (main.startLifetime.mode == ParticleSystemCurveMode.Constant)
                            main.startLifetime = main.startLifetime.constant * lifetimeMultiplier;

                        var emission = ps.emission;
                        if (emission.rateOverTime.mode == ParticleSystemCurveMode.Constant)
                            emission.rateOverTime = emission.rateOverTime.constant * emissionMultiplier;
                    }

                    __instance.transform.localScale *= scaleMultiplier;
                    __instance.gameObject.AddComponent<BigSmokeMarker>();
                }
                catch (Exception ex) { Debug.LogWarning($"[CustomGrenades] Giant smoke creation failed: {ex.Message}"); }
            }
        }
    }

    public class BigSmokeMarker : MonoBehaviour { }
}
