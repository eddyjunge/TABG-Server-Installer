using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using TabgInstaller.Vehicles;

namespace TabgInstaller.FlyingControls
{
    [BepInPlugin("tabginstaller.flyingcontrols", "TABG Flying Vehicle Controls", "1.7.0")]
    public class FlyingControlsPlugin : BaseUnityPlugin
    {
        internal static ConfigEntry<float> ThrustForce;
        internal static ConfigEntry<float> TurnForce;
        internal static ConfigEntry<float> LiftForce;
        internal static ConfigEntry<float> HoverForce;
        internal static ConfigEntry<float> DescentForce;
        internal static ConfigEntry<float> MaxSpeed;
        internal static ConfigEntry<float> MaxAltitude;
        private static bool _syncLimits;
        internal static ConfigEntry<float> Stabilization;
        internal static ConfigEntry<KeyCode> LandKey;
        internal static ConfigEntry<KeyCode> LiftKey;
        internal static ConfigEntry<KeyCode> DescendKey;

        private static readonly HashSet<string> FlyingVehicleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Heli", "Ufo", "Hover_Bike", "Hover_Car"
        };

        private static readonly Dictionary<int, bool> FlyingCache = new Dictionary<int, bool>();
        private static float _nextUpdateErrorLogTime;

        private void Awake()
        {
            LandKey = Config.Bind("Keybinds", "AutoLand", KeyCode.L, "Automatically land the helicopter; press again or use a flight key to cancel.");
            LiftKey = Config.Bind("Keybinds", "Ascend", KeyCode.Space, "Key to fly upward");
            DescendKey = Config.Bind("Keybinds", "Descend", KeyCode.LeftControl, "Key to fly downward");

            ThrustForce = Config.Bind("Physics", "ThrustForce", 22f, "Forward/backward thrust force");
            TurnForce = Config.Bind("Physics", "TurnForce", 10f, "Turning (yaw) force");
            LiftForce = Config.Bind("Physics", "LiftForce", 22f, "Upward force when pressing ascend key");
            HoverForce = Config.Bind("Physics", "HoverForce", 11f, "Vertical braking strength when neither ascend nor descend is held");
            DescentForce = Config.Bind("Physics", "DescentForce", 8f, "Downward force when pressing descend key");
            MaxSpeed = Config.Bind("Physics", "MaxSpeed", 30f, "Maximum flight speed in m/s; higher speed lowers the altitude limit (15-45 m/s).");
            MaxAltitude = Config.Bind("Physics", "MaxAltitude", 87.5f, "Maximum height above ground in metres; higher altitude lowers the speed limit (25-150 m).");
            MaxSpeed.SettingChanged += (sender, args) => SyncLimits(true);
            MaxAltitude.SettingChanged += (sender, args) => SyncLimits(false);
            SyncLimits(true);
            Stabilization = Config.Bind("Physics", "Stabilization", 8f, "Auto-leveling strength");

            try
            {
                TabgInstaller.ModSettings.ModSettingsUI.Register("Flying Controls", "Auto Land Key", "Helicopter: automatic safe landing", LandKey);
                TabgInstaller.ModSettings.ModSettingsUI.Register("Flying Controls", "Ascend Key", "Key to fly upward", LiftKey);
                TabgInstaller.ModSettings.ModSettingsUI.Register("Flying Controls", "Descend Key", "Key to fly downward", DescendKey);
                TabgInstaller.ModSettings.ModSettingsUI.Register("Flying Controls", "Thrust Force", "Forward/backward power", ThrustForce);
                TabgInstaller.ModSettings.ModSettingsUI.Register("Flying Controls", "Turn Force", "Turning speed", TurnForce);
                TabgInstaller.ModSettings.ModSettingsUI.Register("Flying Controls", "Lift Force", "Ascend power", LiftForce);
                TabgInstaller.ModSettings.ModSettingsUI.Register("Flying Controls", "Hover Force", "Vertical braking", HoverForce);
                TabgInstaller.ModSettings.ModSettingsUI.Register("Flying Controls", "Descent Force", "Descend power", DescentForce);
                TabgInstaller.ModSettings.ModSettingsUI.Register("Flying Controls", "Max Speed", "15-45 m/s; faster flight lowers allowed altitude", MaxSpeed);
                TabgInstaller.ModSettings.ModSettingsUI.Register("Flying Controls", "Max Altitude", "25-150 m above ground; more height lowers max speed", MaxAltitude);
                TabgInstaller.ModSettings.ModSettingsUI.Register("Flying Controls", "Stabilization", "Auto-leveling", Stabilization);
            }
            catch (Exception ex) { Debug.LogWarning($"[FlyingControls] ModSettings registration failed (non-fatal): {ex.Message}"); }

            var harmony = new Harmony("tabginstaller.flyingcontrols");
            harmony.PatchAll(typeof(FlyingPhysicsPatch));
            harmony.PatchAll(typeof(NativeHoverPatch));
            harmony.PatchAll(typeof(AircraftClient.SetupPatch));
            harmony.PatchAll(typeof(AircraftHealthDisplay.InitialMaximum));
            harmony.PatchAll(typeof(AircraftClient.AircraftDamagePatch));
            harmony.PatchAll(typeof(AircraftClient.AircraftRamPatch));
            harmony.PatchAll(typeof(GroundTaxi.MarkerPatch));
            harmony.PatchAll(typeof(GroundTaxi.RemoveMarkerPatch));
            harmony.PatchAll(typeof(GroundTaxi.RenderAuthorityPatch));
            harmony.PatchAll(typeof(GroundTaxi.ReceivedAuthorityPatch));
            harmony.PatchAll(typeof(GroundTaxi.DriverSenderPatch));
            harmony.PatchAll(typeof(VehicleSpawnReceiver));
            gameObject.AddComponent<VehicleSpawnHandshake>();
            harmony.PatchAll(typeof(HelicopterSight.WeaponPatch));
            harmony.PatchAll(typeof(HelicopterSight.AimPatch));
            harmony.PatchAll(typeof(HelicopterSight.BulletPatch));
            harmony.PatchAll(typeof(HelicopterSight.BulletSweepPatch));
            gameObject.AddComponent<HelicopterSight>();
            harmony.PatchAll(typeof(FpvItem.LookupPatch));
            harmony.PatchAll(typeof(FpvClient.ThrowPatch));
            harmony.PatchAll(typeof(FpvClient.SingleDroneThrowPatch));
            harmony.PatchAll(typeof(BlastHitmarker.TagDamage));
            harmony.PatchAll(typeof(AircraftSafetyClient.NormalExitSeparation));
            harmony.PatchAll(typeof(FpvClient.InputPatch));
            harmony.PatchAll(typeof(FpvClient.ActionPatch));
            gameObject.AddComponent<FpvClient>();
            gameObject.AddComponent<BlastHitmarker>();
            gameObject.AddComponent<CombatAudio>();
            harmony.PatchAll(typeof(HelicopterAudio.LegacyRotor));
            gameObject.AddComponent<AircraftSafetyClient>();
            harmony.PatchAll(typeof(HelicopterFlight.AuthorityPatch));
            harmony.PatchAll(typeof(HelicopterFlight.ReceivedPatch));

            Logger.LogInfo("[FlyingControls] Loaded! Ascend=" + LiftKey.Value + ", Descend=" + DescendKey.Value);
        }

        private static void SyncLimits(bool speedChanged)
        {
            if (_syncLimits) return;
            _syncLimits = true;
            try
            {
                if (speedChanged)
                {
                    MaxSpeed.Value = VehicleBalance.Clamp(MaxSpeed.Value, VehicleBalance.MinSpeed, VehicleBalance.MaxSpeed);
                    MaxAltitude.Value = VehicleBalance.HeightForSpeed(MaxSpeed.Value);
                }
                else
                {
                    MaxAltitude.Value = VehicleBalance.Clamp(MaxAltitude.Value, VehicleBalance.MinHeight, VehicleBalance.MaxHeight);
                    MaxSpeed.Value = VehicleBalance.SpeedForHeight(MaxAltitude.Value);
                }
            }
            finally { _syncLimits = false; }
        }

        // These are independent physics callbacks: skipping Car.FixedUpdate alone does
        // not stop terrain-based lift. Patch both so callback order and re-enabling
        // components cannot restore the old hover height after descent.
        [HarmonyPatch]
        internal static class NativeHoverPatch
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(HoverRaycaster), "FixedUpdate");
                yield return AccessTools.Method(typeof(RaycastHover), "FixedUpdate");
            }

            static bool Prefix(Component __instance)
            {
                return !FlyingPhysicsPatch.IsFlyingVehicle(__instance.GetComponentInParent<Car>());
            }
        }

        /// <summary>
        /// PREFIX patch — returns false to SKIP the entire original Car.FixedUpdate
        /// for flying vehicles when occupied. This prevents the original wheel-based
        /// physics, anti-gravity, and HoverRaycaster from fighting our flight controls.
        /// </summary>
        [HarmonyPatch(typeof(Car), "FixedUpdate")]
        internal static class FlyingPhysicsPatch
        {
            static bool Prefix(Car __instance)
            {
                try
                {
                    if (!IsFlyingVehicle(__instance)) return true; // Not flying: run original

                    if (VehicleBalance.IsTaxi(__instance.name))
                    {
                        var taxi = __instance.GetComponent<GroundTaxi>() ?? __instance.gameObject.AddComponent<GroundTaxi>();
                        taxi.Tick();
                        return false;
                    }

                    if(MissileRules.IsHeli(__instance.name))
                    {
                        HelicopterFlight.Get(__instance).SyncAuthority();
                        if(__instance.GetComponent<AircraftFallView>())return false;
                        if(HelicopterLanding.Get(__instance).Tick())return false;
                    }

                    // No driver: kill momentum and let it fall gently
                    if (!__instance.driverSeat || !__instance.driverSeat.occupant)
                    {
                        var r = __instance.mainRig;
                        if (r != null && !r.isKinematic)
                        {
                            // Kill horizontal velocity, allow gravity to pull it down
                            r.velocity = new Vector3(r.velocity.x * 0.95f, r.velocity.y * 0.98f, r.velocity.z * 0.95f);
                            r.angularVelocity *= 0.9f;
                            // Auto-level while falling
                            Vector3 fallCorrection = Vector3.Cross(r.transform.up, Vector3.up);
                            r.AddTorque(fallCorrection * 4f, ForceMode.Acceleration);
                        }
                        return false; // Skip original (which applies antiGrav upward force)
                    }

                    // Only take over if WE are in this vehicle
                    bool isOurs = __instance.isSimulatedByMe;
                    if (!isOurs && Player.localPlayer != null)
                    {
                        if (__instance.driverSeat.occupant == Player.localPlayer.transform.root)
                            isOurs = true;
                    }
                    if (!isOurs) return false; // Remote flying vehicles must not receive vanilla lift.

                    var rig = __instance.mainRig;
                    var input = __instance.input;
                    if (rig == null || input == null) return true;

                    // Mark as simulated by us
                    __instance.isSimulatedByMe = true;

                    float forward = input.inputDirection.z;  // W/S
                    float turn = input.inputDirection.x;     // A/D
                    bool goUp = Input.GetKey(LiftKey.Value);
                    bool goDown = Input.GetKey(DescendKey.Value);

                    float heightLeft = float.PositiveInfinity;
                    foreach (var hit in Physics.RaycastAll(rig.position, Vector3.down, 10000f, ~0, QueryTriggerInteraction.Ignore))
                    {
                        if (hit.collider.transform.IsChildOf(__instance.transform) || hit.collider.GetComponentInParent<Player>()) continue;
                        heightLeft = Mathf.Min(heightLeft, hit.distance);
                    }
                    heightLeft = float.IsPositiveInfinity(heightLeft) ? 0f : MaxAltitude.Value - heightLeft;
                    if (heightLeft <= 0f)
                    {
                        goUp = false;
                        var limited = rig.velocity;
                        limited.y = Mathf.Min(limited.y, -Mathf.Min(6f, -heightLeft * 2f));
                        rig.velocity = limited;
                    }
                    else if (heightLeft < 5f && rig.velocity.y > heightLeft)
                    {
                        var limited = rig.velocity; limited.y = heightLeft; rig.velocity = limited;
                        goUp = false;
                    }

                    // Neutral flight cancels only actual gravity, never adds constant lift.
                    if (rig.useGravity) rig.AddForce(-Physics.gravity, ForceMode.Acceleration);
                    if (goUp != goDown)
                        rig.AddForce(Vector3.up * (goUp ? LiftForce.Value : -DescentForce.Value), ForceMode.Acceleration);
                    else
                    {
                        var velocity = rig.velocity;
                        velocity.y = FlightMotion.BrakeVertical(velocity.y, HoverForce.Value, Time.fixedDeltaTime);
                        rig.velocity = velocity;
                    }

                    // Pitching the model must not make W/S change altitude.
                    Vector3 horizontalForward = Vector3.ProjectOnPlane(rig.transform.forward, Vector3.up).normalized;
                    rig.AddForce(horizontalForward * forward * ThrustForce.Value, ForceMode.Acceleration);

                    // Slight pitch when thrusting (visual feedback)
                    if (Mathf.Abs(forward) > 0.1f)
                        rig.AddTorque(rig.transform.right * forward * -1f, ForceMode.Acceleration);

                    // === YAW (TURNING) ===
                    rig.AddTorque(Vector3.up * turn * TurnForce.Value, ForceMode.Acceleration);

                    // === AUTO-STABILIZATION ===
                    // Strong auto-leveling to prevent flipping
                    Vector3 correction = Vector3.Cross(rig.transform.up, Vector3.up);
                    rig.AddTorque(correction * Stabilization.Value, ForceMode.Acceleration);

                    // === DRAG ===
                    rig.velocity *= (1f - 0.01f);        // Linear drag
                    rig.angularVelocity *= (1f - 0.08f); // Angular drag (stop spinning fast)

                    // === SPEED LIMIT ===
                    if (rig.velocity.magnitude > MaxSpeed.Value)
                        rig.velocity = rig.velocity.normalized * MaxSpeed.Value;

                    // Unfreeze if frozen
                    if (__instance.isFrozen)
                        __instance.UnFreeze();

                    return false; // SKIP original Car.FixedUpdate entirely
                }
                catch (Exception ex)
                {
                    if (Time.unscaledTime >= _nextUpdateErrorLogTime)
                    {
                        _nextUpdateErrorLogTime = Time.unscaledTime + 5f;
                        Debug.LogWarning($"[FlyingControls] Vehicle update error: {ex.Message}");
                    }

                    return true;
                }
            }

            internal static bool IsFlyingVehicle(Car car)
            {
                if (car == null) return false;
                int id = car.GetInstanceID();
                if (FlyingCache.TryGetValue(id, out bool cached)) return cached;

                string name = car.gameObject.name.Replace("(Clone)", "").Trim();
                bool isFlying = false;
                foreach (var flyName in FlyingVehicleNames)
                    if (name.IndexOf(flyName, StringComparison.OrdinalIgnoreCase) >= 0) { isFlying = true; break; }
                if (!isFlying && car.GetComponentInChildren<Rotor>() != null)
                    isFlying = true;

                FlyingCache[id] = isFlying;
                return isFlying;
            }
        }
    }
}
