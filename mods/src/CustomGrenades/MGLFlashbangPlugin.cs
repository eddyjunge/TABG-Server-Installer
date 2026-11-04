using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace TabgInstaller.CustomGrenades
{
    /// <summary>
    /// CLIENT-SIDE: MGL (ID 203) explosions flashbang nearby players.
    /// Marks projectiles fired from an MGL and only flashes on explosion/effect
    /// objects spawned by TABG's ProjectileHit.SpawnObjects flow.
    /// </summary>
    [BepInPlugin("tabginstaller.mglflashbang", "TABG MGL Flashbang", "1.0.0")]
    public class MGLFlashbangPlugin : BaseUnityPlugin
    {
        private const int MglItemId = 203;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> TriggerWindowSeconds;
        internal static ConfigEntry<float> ExplosionAssociationRadius;
        internal static ConfigEntry<float> RadiusMultiplier;
        internal static ConfigEntry<float> BlindIntensity;
        internal static ConfigEntry<float> BlindDuration;

        private static readonly List<PendingMglExplosion> PendingExplosions = new List<PendingMglExplosion>();
        [ThreadStatic]
        private static MglSpawnContext _currentMglSpawn;
        private Harmony _harmony;

        private void Awake()
        {
            Enabled = Config.Bind("Flashbang", "Enabled", true, "Enable flashbang behavior for MGL explosions.");
            TriggerWindowSeconds = Config.Bind("Flashbang", "TriggerWindowSeconds", 1.25f, "Seconds after an MGL projectile hit where a nearby spawned explosion can flash.");
            ExplosionAssociationRadius = Config.Bind("Flashbang", "ExplosionAssociationRadius", 3f, "Maximum distance from an MGL projectile hit to associate the resulting explosion.");
            RadiusMultiplier = Config.Bind("Flashbang", "RadiusMultiplier", 2f, "Explosion radius multiplier used for flash range.");
            BlindIntensity = Config.Bind("Flashbang", "BlindIntensity", 60f, "Maximum visual effect intensity.");
            BlindDuration = Config.Bind("Flashbang", "BlindDuration", 60f, "Maximum visual effect duration.");

            if (IsDedicatedOrHeadless())
            {
                Logger.LogInfo("[MGLFlashbang] Client-only flashbang visuals disabled on dedicated/headless server.");
                return;
            }

            _harmony = new Harmony("tabginstaller.mglflashbang");
            _harmony.PatchAll(typeof(MarkMglProjectilePatch));
            _harmony.PatchAll(typeof(RegisterMglSpawnedExplosionPatch));
            _harmony.PatchAll(typeof(FlashOnExplosion));
            Logger.LogInfo("[MGLFlashbang] Loaded client-side MGL flashbang effect with projectile-hit association.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            PendingExplosions.Clear();
        }

        private static bool IsMglGun(Gun gun)
        {
            try
            {
                if (gun == null)
                    return false;

                var pickup = gun.GetComponentInParent<Pickup>();
                return pickup != null && pickup.m_itemIndex == MglItemId;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsDedicatedOrHeadless()
        {
            try
            {
                if (Application.isBatchMode)
                    return true;
            }
            catch
            {
            }

            try
            {
                if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                    return true;
            }
            catch
            {
            }

            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (string.Equals(arg, "-batchmode", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "-nographics", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryConsumeAssociatedExplosion(Vector3 position)
        {
            PrunePendingExplosions();

            float radius = Mathf.Max(0.1f, ExplosionAssociationRadius?.Value ?? 3f);
            float radiusSqr = radius * radius;
            for (int i = PendingExplosions.Count - 1; i >= 0; i--)
            {
                if ((PendingExplosions[i].Position - position).sqrMagnitude > radiusSqr)
                    continue;

                PendingExplosions.RemoveAt(i);
                return true;
            }

            return false;
        }

        private static void RegisterProjectileHit(Vector3 position)
        {
            PrunePendingExplosions();
            PendingExplosions.Add(new PendingMglExplosion
            {
                Position = position,
                ExpiresAt = Time.time + Mathf.Max(0.05f, TriggerWindowSeconds?.Value ?? 1.25f)
            });
        }

        private static void PrunePendingExplosions()
        {
            float now = Time.time;
            for (int i = PendingExplosions.Count - 1; i >= 0; i--)
            {
                if (PendingExplosions[i].ExpiresAt <= now)
                    PendingExplosions.RemoveAt(i);
            }
        }

        private static bool IsMarkedMglProjectile(ProjectileHit projectile)
        {
            return projectile != null && projectile.GetComponent<MglProjectileMarker>() != null;
        }

        private static bool HasExplosionSpawnCandidate(ProjectileHit projectile, bool playerHit)
        {
            ProjectileHitSpawn[] spawns = projectile?.objectsToSpawnOnHit;
            if (spawns == null)
                return false;

            for (int i = 0; i < spawns.Length; i++)
            {
                ProjectileHitSpawn spawn = spawns[i];
                if (spawn == null)
                    continue;

                // Mirrors TABG ProjectileHit.SpawnObjects filtering so we only mark
                // completed hits that actually enter the vanilla effect spawn path.
                if (!spawn.any && (playerHit != spawn.onPlayerHit || spawn.onWaterHit))
                    continue;

                if (spawn.effectObject != null)
                {
                    if (spawn.effectObject.GetComponentInChildren<Explosion>(true) != null)
                        return true;
                }
                else if (spawn.effectID >= 0)
                {
                    // ParticlePlayer.PlayEffect resolves pooled effects internally; the
                    // association is still narrow because this runs only while TABG is
                    // spawning effects for a marked MGL projectile.
                    return true;
                }
            }

            return false;
        }

        private static bool TryAssociateCurrentSpawn(Vector3 explosionPosition)
        {
            if (!_currentMglSpawn.Active)
                return false;

            float radius = Mathf.Max(0.1f, ExplosionAssociationRadius?.Value ?? 3f);
            if ((_currentMglSpawn.Position - explosionPosition).sqrMagnitude > radius * radius)
                return false;

            _currentMglSpawn.ExplodedDuringSpawn = true;
            return true;
        }

        [HarmonyPatch(typeof(ProjectileHit), "Start")]
        internal static class MarkMglProjectilePatch
        {
            static void Postfix(ProjectileHit __instance)
            {
                try
                {
                    if (Enabled != null && !Enabled.Value) return;
                    if (__instance == null || __instance.GetComponent<MglProjectileMarker>() != null) return;

                    var gun = __instance.gunThatShotMe != null
                        ? __instance.gunThatShotMe.GetComponentInParent<Gun>()
                        : null;
                    if (IsMglGun(gun))
                        __instance.gameObject.AddComponent<MglProjectileMarker>();
                }
                catch (Exception ex) { Debug.LogWarning($"[MGLFlashbang] Projectile mark failed: {ex.Message}"); }
            }
        }

        [HarmonyPatch]
        internal static class RegisterMglSpawnedExplosionPatch
        {
            static MethodBase TargetMethod()
            {
                return AccessTools.Method(typeof(ProjectileHit), "SpawnObjects", new[] { typeof(RaycastHit), typeof(bool) });
            }

            static void Prefix(ProjectileHit __instance, RaycastHit hit, bool playerHit, out bool __state)
            {
                __state = false;
                try
                {
                    if (Enabled != null && !Enabled.Value) return;
                    if (!IsMarkedMglProjectile(__instance)) return;
                    if (!HasExplosionSpawnCandidate(__instance, playerHit)) return;

                    _currentMglSpawn = new MglSpawnContext
                    {
                        Active = true,
                        Position = hit.point
                    };
                    __state = true;
                }
                catch (Exception ex) { Debug.LogWarning($"[MGLFlashbang] Projectile spawn association setup failed: {ex.Message}"); }
            }

            static void Postfix(ProjectileHit __instance, RaycastHit hit, bool playerHit, bool __state)
            {
                try
                {
                    if (!__state) return;

                    bool alreadyExploded = _currentMglSpawn.ExplodedDuringSpawn;
                    _currentMglSpawn = default(MglSpawnContext);
                    if (!alreadyExploded)
                        RegisterProjectileHit(hit.point);
                }
                catch (Exception ex) { Debug.LogWarning($"[MGLFlashbang] Projectile spawn association failed: {ex.Message}"); }
            }
        }

        [HarmonyPatch(typeof(Explosion), "Explode", typeof(NetworkOwner))]
        internal static class FlashOnExplosion
        {
            static void Postfix(Explosion __instance)
            {
                try
                {
                    if (Enabled != null && !Enabled.Value) return;
                    if (BossFightHandler.IN_BOSS_FIGHT) return;

                    Vector3 explosionPos = __instance.transform.position;
                    bool associatedWithMgl = __instance.GetComponentInParent<MglProjectileMarker>() != null ||
                        TryAssociateCurrentSpawn(explosionPos) ||
                        TryConsumeAssociatedExplosion(explosionPos);
                    if (!associatedWithMgl)
                        return;

                    if (Player.localPlayer == null)
                        return;

                    float radius = __instance.radius * Mathf.Max(0.1f, RadiusMultiplier?.Value ?? 2f);

                    Vector3 playerPos = Player.localPlayer.m_hip != null
                        ? Player.localPlayer.m_hip.transform.position
                        : Player.localPlayer.transform.position;

                    float dist = Vector3.Distance(explosionPos, playerPos);
                    if (dist > radius) return;

                    float rangeMult = Mathf.Clamp01((radius - dist) / radius);
                    var vis = Player.localPlayer.GetComponentInChildren<VisualEffects>();
                    if (vis != null)
                        vis.AddVisualEffect(
                            1,
                            rangeMult * Mathf.Max(0f, BlindIntensity?.Value ?? 60f),
                            Mathf.Max(0f, BlindDuration?.Value ?? 60f));
                }
                catch (Exception ex) { Debug.LogWarning($"[MGLFlashbang] Operation failed: {ex.Message}"); }
            }
        }

        private struct PendingMglExplosion
        {
            public Vector3 Position;
            public float ExpiresAt;
        }

        private struct MglSpawnContext
        {
            public bool Active;
            public Vector3 Position;
            public bool ExplodedDuringSpawn;
        }
    }

    public class MglProjectileMarker : MonoBehaviour { }
}
