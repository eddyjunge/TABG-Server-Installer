using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Landfall.Network;
using TabgInstaller.Vehicles;
using UnityEngine;

namespace TabgInstaller.FlyingControls
{
    public sealed class AircraftImpact : MonoBehaviour
    {
        internal Car Car;
        private float _nextReport;
        private void OnCollisionEnter(Collision collision)
        {
            if (!Car || HelicopterLanding.Protects(Car) || !AircraftClient.IsLocalDriver(Car) || Time.unscaledTime < _nextReport) return;
            // Ragdolls leaving a seat are not an aircraft crash surface.
            if(collision.collider && collision.collider.GetComponentInParent<Player>())return;
            float impact = 0;
            foreach (var contact in collision.contacts)
                impact = Mathf.Max(impact, Mathf.Abs(Vector3.Dot(collision.relativeVelocity, contact.normal)));
            if (impact < VehicleBalance.CrashSpeed) return;
            _nextReport = Time.unscaledTime + .3f;
            int index = (int)AccessTools.Field(typeof(Car), "m_NetworkIndex").GetValue(Car);
            UnityEngine.Object.FindObjectOfType<ServerConnector>()?.SendMessageToServer((EventCode)VehicleProtocol.Event,
                VehicleProtocol.Message(VehicleProtocol.Crash, w => { w.Write(index); w.Write(impact); }), true);
        }
    }

    internal static class AircraftClient
    {
        internal static bool IsLocalDriver(Car car) => car && car.driverSeat && Player.localPlayer
            && car.driverSeat.occupant == Player.localPlayer.transform.root;
        private static TABGPlayerClient FindPlayer(PhotonServerHandler handler, byte id) => (TABGPlayerClient)AccessTools.Method(typeof(PhotonServerHandler), "FindPlayer").Invoke(handler, new object[] { id });
        internal static List<TABGCarClient> Cars => AccessTools.Field(typeof(PhotonServerHandler), "m_Cars").GetValue(PhotonServerHandler.instance) as List<TABGCarClient>;

        [HarmonyPatch(typeof(Car), "Start")]
        internal static class SetupPatch
        {
            static void Postfix(Car __instance)
            {
                if (!VehicleBalance.IsAircraft(__instance.name)) return;
                if(MissileRules.IsHeli(__instance.name) && !__instance.GetComponent<HelicopterAudio>())__instance.gameObject.AddComponent<HelicopterAudio>();
                foreach (var body in __instance.GetComponentsInChildren<Rigidbody>(true))
                {
                    var monitor = body.GetComponent<AircraftImpact>() ?? body.gameObject.AddComponent<AircraftImpact>();
                    monitor.Car = __instance;
                }
            }
        }

        [HarmonyPatch(typeof(Damagable), "TakeDamage")]
        internal static class AircraftDamagePatch
        {
            static bool Prefix(Damagable __instance, Vector3 damage, NetworkOwner netOwner)
            {
                var car = __instance.GetComponentInParent<Car>();
                bool customBlast=netOwner && (netOwner.name=="Heli missile grenade explosion" || netOwner.name=="FPV Dynamite explosion");
                if (!car || (!VehicleBalance.IsAircraft(car.name) && !customBlast) || __instance.playerDeath || __instance.sendDamageTo) return true;
                if (AccessTools.Field(typeof(Damagable), "m_OnDamageAction").GetValue(__instance) == null) return true;
                // Vanilla sends remaining health where the dedicated server expects damage.
                // Send an actual damage amount, once from the attacker's simulation.
                if (netOwner && !netOwner.IsMine()) return false;
                float amount = damage.magnitude * __instance.multiplier * __instance.armorFactor;
                int index = (int)AccessTools.Field(typeof(Damagable), "m_CarIndex").GetValue(__instance);
                byte part = (byte)AccessTools.Field(typeof(Damagable), "m_PartIndex").GetValue(__instance);
                if (amount > 0) PhotonServerHandler.instance.ClientDamageCar(index, part, amount);
                return false;
            }
        }
        [HarmonyPatch(typeof(CollisionChecker), "Collide")]
        internal static class AircraftRamPatch
        {
            private static readonly System.Reflection.FieldInfo PlayerField = AccessTools.Field(typeof(CollisionChecker), "player");
            static bool Prefix(CollisionChecker __instance, Collision __0)
            {
                // This is a PLAYER grounding/ram callback. Unused vehicle prefabs also contain
                // copies without a Player; vanilla dereferences that missing field on every contact.
                if(!(PlayerField.GetValue(__instance) as Player))return false;
                if(__0==null || !__0.collider)return false;
                var car = __0.collider.GetComponentInParent<Car>();
                return !car || !VehicleBalance.IsAircraft(car.name);
            }
        }

        internal static void Detonate(BinaryReader reader)
        {
            int index = reader.ReadInt32(); byte attacker = reader.ReadByte();
            var position = new Vector3(VehicleProtocol.ReadFinite(reader), VehicleProtocol.ReadFinite(reader), VehicleProtocol.ReadFinite(reader));
            int count = reader.ReadByte();
            var occupants = new HashSet<byte>();
            for (int i = 0; i < count; i++) occupants.Add(reader.ReadByte());
            if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Unexpected explosion data");
            var handler = PhotonServerHandler.instance;
            var cars = Cars;
            var entry = cars?.Find(c => c.CarIndex == index);
            // Duplicate reliable messages must never produce a second explosion.
            if (entry == null) return;
            cars.Remove(entry);
            var protectedRigs = new List<Rigidbody>();
            foreach(byte id in AircraftSafetyClient.Shielded(index))
            {
                var root=FindPlayer(handler,id)?.PlayerObject;
                if(root)protectedRigs.AddRange(root.GetComponentsInChildren<Rigidbody>(true));
            }
            foreach (byte id in occupants)
            {
                var playerData = FindPlayer(handler, id);
                var root = playerData?.PlayerObject;
                if (!root) continue;
                protectedRigs.AddRange(root.GetComponentsInChildren<Rigidbody>(true));
                var sitting = root.GetComponentInChildren<Sitting>();
                // The network driver state must be cleared before deleting the vehicle.
                // Temporarily remove velocity inheritance to suppress native eject forces.
                if (sitting && sitting.isSeated)
                {
                    var seat = sitting.currentSeat;
                    var inherited = seat ? seat.inheritVelocityFromRig : null;
                    if (seat) seat.inheritVelocityFromRig = null;
                    try
                    {
                        var network = root.GetComponentInChildren<NetworkPlayer>();
                        if (network) network.Sit(seat, SeatAction.GetOut, Vector3.zero, Quaternion.identity);
                        else sitting.GetOut();
                    }
                    finally { if (seat) seat.inheritVelocityFromRig = inherited; }
                }
                playerData.UpdateDrivingState(false, null);
                foreach (var rig in root.GetComponentsInChildren<Rigidbody>(true))
                    if (!rig.isKinematic) { rig.velocity = Vector3.zero; rig.angularVelocity = Vector3.zero; }
            }
            if (Player.localPlayer && occupants.Contains(handler.LocalPlayer.PlayerIndex))
            {
                var death = Player.localPlayer.m_playerDeath;
                if (!death.dead && death.health > 0)
                {
                    float hp = VehicleBalance.SurvivorHealth(death.health);
                    death.AssignNewHealth(hp, true);
                    death.SyncNetworkHealth(hp);
                    Debug.Log($"[VehicleBalance] Occupant survives with {hp} health; network vehicle state cleared");
                }
            }
            if (entry.CarReference)
            {
                // No native death events: they can cause an extra unprotected explosion.
                entry.CarReference.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(entry.CarReference.gameObject);
            }
            if (!GrenadeProfiles.Load()) { Debug.LogError("[VehicleBalance] Grenade profiles unavailable; explosion skipped"); return; }
            var blastObject = new GameObject("Aircraft explosion");
            blastObject.SetActive(false);
            blastObject.transform.position = position;
            var owner = blastObject.AddComponent<NetworkOwner>();
            owner.Init(attacker, FindPlayer(handler, attacker)?.PlayerObject?.transform);
            var blast = blastObject.AddComponent<Explosion>();
            var hand = GrenadeProfiles.Hand; var knock = GrenadeProfiles.Knockback;
            blast.auto = false; blast.radius = GrenadeProfiles.Dynamite.radius * (GrenadeProfiles.Dynamite.scale ? GrenadeProfiles.Dynamite.transform.localScale.x : 1f); blast.damage = hand.damage;
            blast.force = knock.force; blast.unscalingForce = knock.unscalingForce; blast.knockDownMultiplier = knock.knockDownMultiplier;
            AccessTools.Field(typeof(Explosion), "mask").SetValue(blast, AccessTools.Field(typeof(Explosion), "mask").GetValue(hand)); blast.flatDamage = hand.flatDamage; blast.ignoreWalls = hand.ignoreWalls;
            blast.needWalls = hand.needWalls; blast.scale = false; AccessTools.Field(typeof(Explosion), "effect").SetValue(blast, new PlayerEffect[0]);
            blast.onlyAddForceOncePerTarget = true; AccessTools.Field(typeof(Explosion), "rigs").SetValue(blast, protectedRigs);
            blastObject.SetActive(true);
            blast.Explode(owner);
            // Play the pooled native particles and sound without invoking its damaging explosion.
            var visual = GrenadeProfiles.DynamiteVisual;
            if (visual != null && visual.rootObject)
            {
                visual.rootObject.position = position;
                if (visual.particles != null) foreach (var particle in visual.particles)
                    if (particle) { if (visual.emits > 0) particle.Emit(visual.emits); else particle.Play(); }
                if (visual.sound) visual.sound.Play(position);
            }
            UnityEngine.Object.Destroy(blastObject, 2f);
            Debug.Log($"[VehicleBalance] Explosion #{index}; occupants excluded from grenade forces/damage={occupants.Count}");
        }
    }
}
