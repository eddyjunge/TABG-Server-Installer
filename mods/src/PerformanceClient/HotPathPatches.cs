using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Landfall.Network;
using TABGInput;
using UnityEngine;
using UnityStandardAssets.Cameras;

namespace TabgInstaller.PerformanceClient
{
    internal static class HotPathEnabled
    {
        internal static bool Value
        {
            get
            {
                var plugin = PerformanceClientPlugin.Instance;
                return plugin != null && plugin.OptimizeRuntimeHotPathsEnabled;
            }
        }
    }

    [HarmonyPatch(typeof(NetworkPlayer))]
    internal static class NetworkPlayerRigidbodyCachePatch
    {
        private static readonly FieldInfo RigidbodiesField = AccessTools.Field(typeof(NetworkPlayer), "m_Rigidbodies");
        private static readonly MethodInfo Replacement = AccessTools.Method(typeof(NetworkPlayerRigidbodyCachePatch), nameof(GetCachedRigidbodies));
        private static readonly Rigidbody[] Empty = new Rigidbody[0];

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(NetworkPlayer), "ApplyRemoteUpdates");
            yield return AccessTools.Method(typeof(NetworkPlayer), "LateUpdate");
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                var method = instruction.operand as MethodInfo;
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                    && method != null
                    && method.Name == "GetComponentsInChildren"
                    && method.IsGenericMethod
                    && method.GetGenericArguments()[0] == typeof(Rigidbody)
                    && method.GetParameters().Length == 0)
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = Replacement;
                }

                yield return instruction;
            }
        }

        private static Rigidbody[] GetCachedRigidbodies(NetworkPlayer player)
        {
            if (!HotPathEnabled.Value)
                return player.GetComponentsInChildren<Rigidbody>();

            var cached = RigidbodiesField.GetValue(player) as Rigidbody[];
            if (cached != null)
                return cached;

            cached = player.GetComponentsInChildren<Rigidbody>();
            RigidbodiesField.SetValue(player, cached);
            return cached ?? Empty;
        }
    }

    [HarmonyPatch(typeof(RaycastAllTrail), "LateUpdate")]
    internal static class RaycastAllTrailNonAllocPatch
    {
        private static readonly RaycastHit[] Hits = new RaycastHit[128];

        private static bool Prefix(
            RaycastAllTrail __instance,
            ProjectileHit ___projectileHit,
            SpawnerHolder ___holdable,
            ref Vector3 ___lastPosition)
        {
            if (!HotPathEnabled.Value || ___projectileHit == null)
                return true;

            var currentPosition = __instance.transform.position;
            var delta = currentPosition - ___lastPosition;
            var distance = delta.magnitude;
            if (distance <= 0.0001f)
            {
                ___lastPosition = currentPosition;
                return false;
            }

            var count = Physics.RaycastNonAlloc(new Ray(___lastPosition, delta), Hits, distance, __instance.mask);
            SortByDistance(Hits, count);
            for (var index = 0; index < count; index++)
            {
                var hit = Hits[index];
                if (hit.collider == null)
                    continue;

                if (___holdable == null || ___holdable.spawner == null || ___holdable.spawner != hit.transform.root)
                {
                    ___projectileHit.Hit(hit);
                    ___projectileHit.damage *= 0.5f;
                }
                else
                {
                    LandLog.LogError(__instance.transform.name + " hit itself. This should never happen. Contact your closest Wilhelm");
                }
            }

            ___lastPosition = currentPosition;
            return false;
        }

        internal static void SortByDistance(RaycastHit[] hits, int count)
        {
            for (var index = 1; index < count; index++)
            {
                var value = hits[index];
                var cursor = index - 1;
                while (cursor >= 0 && hits[cursor].distance > value.distance)
                {
                    hits[cursor + 1] = hits[cursor];
                    cursor--;
                }
                hits[cursor + 1] = value;
            }
        }
    }

    [HarmonyPatch(typeof(BouncyRayCastTrail), "LateUpdate")]
    internal static class BouncyRaycastTrailNonAllocPatch
    {
        private static readonly RaycastHit[] Hits = new RaycastHit[64];

        private static bool Prefix(
            BouncyRayCastTrail __instance,
            ProjectileHit ___hit,
            SpawnerHolder ___spawnerHolder,
            MoveTransform ___moveTrans,
            BulletHitSfx ___sfx,
            ref Vector3 ___lastPosition)
        {
            if (!HotPathEnabled.Value || ___hit == null || ___moveTrans == null)
                return true;

            var currentPosition = __instance.transform.position;
            var delta = currentPosition - ___lastPosition;
            var distance = delta.magnitude;
            if (distance <= 0.0001f)
            {
                ___lastPosition = currentPosition;
                return false;
            }

            var count = Physics.SphereCastNonAlloc(new Ray(___lastPosition, delta), 0.4f, Hits, distance, __instance.mask);
            var nearestIndex = -1;
            var nearestDistance = float.PositiveInfinity;
            for (var index = 0; index < count; index++)
            {
                var candidate = Hits[index];
                if (candidate.collider == null)
                    continue;
                if (___spawnerHolder != null && ___spawnerHolder.spawner != null
                    && ___spawnerHolder.spawner == candidate.transform.root)
                    continue;
                if (candidate.distance >= nearestDistance)
                    continue;

                nearestDistance = candidate.distance;
                nearestIndex = index;
            }

            if (nearestIndex < 0)
            {
                ___lastPosition = currentPosition;
                return false;
            }

            var nearest = Hits[nearestIndex];
            if (nearest.rigidbody != null || __instance.bouncesAllowed <= 0)
            {
                ___hit.Hit(nearest);
                return false;
            }

            ___moveTrans.velocity = Vector3.Reflect(___moveTrans.velocity, nearest.normal);
            __instance.transform.position = nearest.point;
            __instance.bouncesAllowed--;
            ParticlePlayer.PlayEffect(__instance.bounceEffectID, nearest.point, nearest.normal);
            if (___sfx != null)
                ___sfx.PlaySound(TagToGroundID.GetGroundID(nearest.collider.transform.tag), nearest.point);

            ___lastPosition = __instance.transform.position;
            return false;
        }
    }

    [HarmonyPatch(typeof(GroundPickup), "FixedUpdate")]
    internal static class GroundPickupNonAllocPatch
    {
        private static readonly Collider[] Colliders = new Collider[96];
        private static readonly List<Pickup> Pickups = new List<Pickup>(32);
        private static float _nextRefresh;

        private static bool Prefix(GroundPickup __instance, float ___m_radius, LayerMask ___layerMask, Player ___m_player)
        {
            if (!HotPathEnabled.Value)
                return true;
            if (___m_player != Player.localPlayer || !Player.IsInterface())
                return false;
            if (Time.unscaledTime < _nextRefresh)
                return false;

            var plugin = PerformanceClientPlugin.Instance;
            _nextRefresh = Time.unscaledTime + 1f / Mathf.Max(1, plugin.PickupRefreshRate);
            Pickups.Clear();
            var count = Physics.OverlapSphereNonAlloc(
                __instance.transform.position - Vector3.up,
                ___m_radius,
                Colliders,
                ___layerMask,
                QueryTriggerInteraction.UseGlobal);

            for (var index = 0; index < count; index++)
            {
                var collider = Colliders[index];
                if (collider == null)
                    continue;
                var pickup = collider.GetComponentInParent<Pickup>();
                if (pickup != null && pickup.canInteract && !Pickups.Contains(pickup))
                    Pickups.Add(pickup);
            }

            InventoryUI.RepaintNearby(Pickups);
            return false;
        }
    }

    [HarmonyPatch(typeof(InteractionHandler), "LateUpdate")]
    internal static class InteractionScanThrottlePatch
    {
        private static float _nextScan;

        private static bool Prefix(InteractionHandler __instance, PlayerActions ___m_PlayerActions, ref float ___sinceThrow)
        {
            if (!HotPathEnabled.Value || __instance.player != Player.localPlayer)
                return true;

            var interact = ___m_PlayerActions != null ? ___m_PlayerActions.Interact : null;
            if (interact != null && (interact.WasPressed || interact.WasReleased))
                return true;
            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + 1f / Mathf.Max(1, PerformanceClientPlugin.Instance.InteractionRefreshRate);
                return true;
            }

            // The original increments this every LateUpdate. Keep throwable timing
            // exact even on frames where the expensive overlap scan is skipped.
            ___sinceThrow += Time.deltaTime;
            return false;
        }
    }

    [HarmonyPatch(typeof(ProtectCameraFromWallClip), "LateUpdate")]
    internal static class CameraWallClipNonAllocPatch
    {
        private static readonly Collider[] Overlaps = new Collider[32];
        private static readonly RaycastHit[] Hits = new RaycastHit[64];
        private static readonly Action<ProtectCameraFromWallClip, bool> SetProtecting = CreateProtectingSetter();

        private static bool Prefix(
            ProtectCameraFromWallClip __instance,
            Transform ___m_Cam,
            Transform ___m_Pivot,
            float ___m_OriginalDist,
            ref float ___m_MoveVelocity,
            ref float ___m_CurrentDist)
        {
            if (!HotPathEnabled.Value || ___m_Cam == null || ___m_Pivot == null)
                return true;

            var targetDistance = ___m_OriginalDist;
            var ray = new Ray(
                ___m_Pivot.position + ___m_Pivot.forward * __instance.sphereCastRadius,
                -___m_Pivot.forward);
            var overlapCount = Physics.OverlapSphereNonAlloc(ray.origin, __instance.sphereCastRadius, Overlaps);
            var startsInsideGeometry = false;
            for (var index = 0; index < overlapCount; index++)
            {
                if (IsValid(Overlaps[index], __instance.dontClipTag))
                {
                    startsInsideGeometry = true;
                    break;
                }
            }

            int hitCount;
            if (startsInsideGeometry)
            {
                ray.origin += ___m_Pivot.forward * __instance.sphereCastRadius;
                hitCount = Physics.RaycastNonAlloc(ray, Hits, ___m_OriginalDist - __instance.sphereCastRadius);
            }
            else
            {
                hitCount = Physics.SphereCastNonAlloc(
                    ray,
                    __instance.sphereCastRadius,
                    Hits,
                    ___m_OriginalDist + __instance.sphereCastRadius);
            }

            var nearestDistance = float.PositiveInfinity;
            var nearestIndex = -1;
            for (var index = 0; index < hitCount; index++)
            {
                if (Hits[index].distance < nearestDistance && IsValid(Hits[index].collider, __instance.dontClipTag))
                {
                    nearestDistance = Hits[index].distance;
                    nearestIndex = index;
                }
            }

            var protecting = nearestIndex >= 0;
            if (protecting)
            {
                var hit = Hits[nearestIndex];
                targetDistance = -___m_Pivot.InverseTransformPoint(hit.point).z;
                Debug.DrawRay(ray.origin, -___m_Pivot.forward * (targetDistance + __instance.sphereCastRadius), Color.red);
            }

            SetProtecting?.Invoke(__instance, protecting);
            ___m_CurrentDist = Mathf.SmoothDamp(
                ___m_CurrentDist,
                targetDistance,
                ref ___m_MoveVelocity,
                ___m_CurrentDist > targetDistance ? __instance.clipMoveTime : __instance.returnTime);
            ___m_CurrentDist = Mathf.Clamp(___m_CurrentDist, __instance.closestDistance, ___m_OriginalDist);
            ___m_Cam.localPosition = -Vector3.forward * ___m_CurrentDist;
            return false;
        }

        private static bool IsValid(Collider collider, string dontClipTag)
        {
            return collider != null
                   && !collider.isTrigger
                   && (collider.attachedRigidbody == null || !collider.attachedRigidbody.CompareTag(dontClipTag));
        }

        private static Action<ProtectCameraFromWallClip, bool> CreateProtectingSetter()
        {
            var setter = AccessTools.PropertySetter(typeof(ProtectCameraFromWallClip), "protecting");
            return setter == null
                ? null
                : (Action<ProtectCameraFromWallClip, bool>)Delegate.CreateDelegate(
                    typeof(Action<ProtectCameraFromWallClip, bool>), setter);
        }
    }

    [HarmonyPatch(typeof(Car), "FixedUpdate")]
    internal static class FrozenCarEarlyOutPatch
    {
        private static readonly HashSet<int> StoppedCars = new HashSet<int>();
        private static readonly MethodInfo FrictionSetter = AccessTools.PropertySetter(typeof(PhysicMaterial), nameof(PhysicMaterial.dynamicFriction));
        private static readonly MethodInfo FrictionReplacement = AccessTools.Method(typeof(FrozenCarEarlyOutPatch), nameof(SetFrictionIfChanged));

        private static bool Prefix(Car __instance, VehicleSoundHandler ___sounds)
        {
            if (!HotPathEnabled.Value)
                return true;
            if (__instance.isFrozen)
            {
                if (StoppedCars.Add(__instance.GetInstanceID()) && ___sounds != null)
                {
                    ___sounds.stopBrake();
                    ___sounds.StopDrive();
                    ___sounds.StopSkid();
                }
                return false;
            }
            StoppedCars.Remove(__instance.GetInstanceID());

            // The owning car remains full-rate. Remote cars already receive
            // interpolation in Update, so far-away wheel/force work can run at
            // one third of the local physics rate without delaying packets.
            if (PhotonServerConnector.IsNetworkMatch && Player.localPlayer != null && !__instance.isSimulatedByMe)
            {
                var localRoot = Player.localPlayer.transform.root;
                var driverSeat = __instance.driverSeat;
                var localOccupant = driverSeat != null && driverSeat.occupant != null
                                    && driverSeat.occupant.transform.root == localRoot;
                var plugin = PerformanceClientPlugin.Instance;
                var distance = plugin != null ? plugin.PhysicsObjectSimulationDistance : 300f;
                var position = __instance.mainRig != null ? __instance.mainRig.position : __instance.transform.position;
                if (!localOccupant
                    && (position - localRoot.position).sqrMagnitude > distance * distance
                    && (Time.frameCount + __instance.GetInstanceID()) % 3 != 0)
                    return false;
            }
            return true;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                if (FrictionSetter != null && instruction.Calls(FrictionSetter))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = FrictionReplacement;
                }
                yield return instruction;
            }
        }

        private static void SetFrictionIfChanged(PhysicMaterial material, float value)
        {
            if (material != null && !Mathf.Approximately(material.dynamicFriction, value))
                material.dynamicFriction = value;
        }
    }

    [HarmonyPatch(typeof(PostProcessingHandler), "Update")]
    internal static class PostProcessingChangeOnlyPatch
    {
        private static readonly Dictionary<int, int> LastAoValue = new Dictionary<int, int>();

        private static bool Prefix(PostProcessingHandler __instance)
        {
            if (!HotPathEnabled.Value)
                return true;

            var id = __instance.GetInstanceID();
            var value = OptionsHolder.AO;
            int previous;
            if (LastAoValue.TryGetValue(id, out previous) && previous == value)
                return false;
            LastAoValue[id] = value;
            return true;
        }
    }

    [HarmonyPatch(typeof(PhotonServerHandler), nameof(PhotonServerHandler.SendPlayerUpdate))]
    internal static class PlayerPacketWriterPatch
    {
        private static readonly byte[] Buffer = new byte[26];

        private static bool Prefix(
            PhotonServerHandler __instance,
            Vector3 pos,
            Vector2 rot,
            Vector3 movementDir,
            byte movementFlags,
            bool ads)
        {
            if (!HotPathEnabled.Value || __instance.LocalPlayer == null)
                return true;

            var offset = 0;
            Buffer[offset++] = __instance.LocalPlayer.PlayerIndex;
            PacketWriter.WriteFloat(Buffer, ref offset, pos.x);
            PacketWriter.WriteFloat(Buffer, ref offset, pos.y);
            PacketWriter.WriteFloat(Buffer, ref offset, pos.z);
            PacketWriter.WriteFloat(Buffer, ref offset, rot.x);
            PacketWriter.WriteFloat(Buffer, ref offset, rot.y);
            Buffer[offset++] = ads ? (byte)1 : (byte)0;
            PacketWriter.WriteDirection(Buffer, ref offset, movementDir);
            Buffer[offset] = movementFlags;
            ServerConnector.Instance.SendMessageToServer(EventCode.PlayerUpdate, Buffer, false);
            return false;
        }
    }

    internal static class PacketWriter
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private struct FloatInt
        {
            [System.Runtime.InteropServices.FieldOffset(0)] internal float Float;
            [System.Runtime.InteropServices.FieldOffset(0)] internal int Int;
        }

        internal static void WriteFloat(byte[] destination, ref int offset, float value)
        {
            var converter = new FloatInt { Float = value };
            WriteInt(destination, ref offset, converter.Int);
        }

        internal static void WriteInt(byte[] destination, ref int offset, int value)
        {
            destination[offset++] = (byte)value;
            destination[offset++] = (byte)(value >> 8);
            destination[offset++] = (byte)(value >> 16);
            destination[offset++] = (byte)(value >> 24);
        }

        internal static void WriteDirection(byte[] destination, ref int offset, Vector3 direction)
        {
            destination[offset++] = (byte)(direction.x * 100f + 100f);
            destination[offset++] = (byte)(direction.y * 100f + 100f);
            destination[offset++] = (byte)(direction.z * 100f + 100f);
        }
    }
}
