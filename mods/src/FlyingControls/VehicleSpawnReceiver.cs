using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Landfall.Network;
using TabgInstaller.Vehicles;
using UnityEngine;

namespace TabgInstaller.FlyingControls
{
    public sealed class VehicleSpawnHandshake : MonoBehaviour
    {
        private float _nextHello;
        private bool _profilesVerified;
        private void Update()
        {
            if (Time.unscaledTime < _nextHello) return;
            _nextHello = Time.unscaledTime + 3f;
            var handler = PhotonServerHandler.instance;
            if (Player.localPlayer == null || handler?.LocalPlayer == null) return;
            if (!_profilesVerified)
            {
                try { _profilesVerified = GrenadeProfiles.Load(); }
                catch (Exception ex) { Debug.LogWarning("[VehicleBalance] Profile lookup: " + ex.Message); }
            }
            var connector = UnityEngine.Object.FindObjectOfType<ServerConnector>();
            connector?.SendMessageToServer((EventCode)VehicleProtocol.Event, VehicleProtocol.Message(VehicleProtocol.Hello), true);
        }
    }

    [HarmonyPatch(typeof(ServerConnector), "OnEvent")]
    internal static class VehicleSpawnReceiver
    {
        private static readonly FieldInfo CarsField = AccessTools.Field(typeof(PhotonServerHandler), "m_Cars");
        static bool Prefix(ClientPackage clientPackage)
        {
            if ((byte)clientPackage.Code != VehicleProtocol.Event) return true;
            Car visual = null;
            try
            {
                using (var reader = VehicleProtocol.Open(clientPackage.Buffer, out byte kind))
                {
                    if (reader == null) return false;
                    if(kind==VehicleProtocol.AircraftHealth){AircraftHealthDisplay.Receive(reader);return false;}
                    if(kind==VehicleProtocol.TaxiStatus){GroundTaxi.ReceiveStatus(reader);return false;}
                    if(kind==VehicleProtocol.BlastConfirmed){BlastHitmarker.Receive(reader);return false;}
                    if(kind==VehicleProtocol.EjectShield || kind==VehicleProtocol.AircraftFall || kind==VehicleProtocol.EjectLaunch || kind==VehicleProtocol.PilotHandover){AircraftSafetyClient.Receive(kind,reader);return false;}
                    if (kind == VehicleProtocol.DroneState || kind == VehicleProtocol.DroneFinish || kind == VehicleProtocol.DroneDenied)
                    { FpvClient.Receive(kind, reader); return false; }
                    if (kind == VehicleProtocol.MissileState || kind == VehicleProtocol.MissileRemove || kind == VehicleProtocol.MissileReply)
                    { HelicopterSight.Receive(kind, reader); return false; }
                    if (kind == VehicleProtocol.Detonate) { AircraftClient.Detonate(reader); return false; }
                    if (kind != VehicleProtocol.Spawn) return false;
                    var handler = PhotonServerHandler.instance;
                    var cars = handler == null ? null : CarsField.GetValue(handler) as List<TABGCarClient>;
                    if (cars == null) return false;
                    int index = reader.ReadInt32(), type = reader.ReadInt32();
                    byte owner = reader.ReadByte();
                    var position = new Vector3(VehicleProtocol.ReadFinite(reader), VehicleProtocol.ReadFinite(reader), VehicleProtocol.ReadFinite(reader));
                    var rotation = Quaternion.Euler(0, VehicleProtocol.ReadFinite(reader), 0);
                    if (index < 0 || type < 0 || type >= CarDatabase.Instance.ItemCount) throw new InvalidDataException("Invalid vehicle ID");
                    if (cars.Exists(c => c.CarIndex == index)) return false;
                    var prefab = (Car)AccessTools.Method(typeof(PhotonServerHandler), "GetCarPrefab").Invoke(handler, new object[] { type });
                    if (prefab == null) throw new InvalidDataException("Missing vehicle prefab");
                    var seats = prefab.GetComponentsInChildren<Seat>();
                    var entry = new TABGCarClient(null, seats, index, type, handler.ClientDamageCar);
                    int parts = reader.ReadByte();
                    for (int i = 0; i < parts; i++) entry.InitPart(reader.ReadByte(), VehicleProtocol.ReadFinite(reader), reader.ReadString());
                    if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Unexpected spawn data");
                    visual = (Car)AccessTools.Method(typeof(PhotonServerHandler), "SpawnCar").Invoke(handler, new object[] { type, position, rotation });
                    var newSeats = visual.GetComponentsInChildren<Seat>();
                    for (int i = 0; i < newSeats.Length; i++) newSeats[i].SetNetworkIndex(i);
                    entry.UpdateSeats(newSeats);
                    entry.UpdateVisualCar(visual);
                    foreach (var body in visual.GetComponentsInChildren<Rigidbody>())
                    {
                        if (!body.isKinematic) { body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
                    }
                    visual.NetworkUpdate(position, rotation, Vector3.zero, default(CarDrivingState));
                    cars.Add(entry);
                    var connector = UnityEngine.Object.FindObjectOfType<ServerConnector>();
                    if (connector != null && connector.GetPlayerIndex() == owner) visual.AssignLocalTemporaryOwner(handler, 3f);
                    Debug.Log($"[VehicleSpawn] Created {prefab.name} index={index} at {position}, seats={newSeats.Length}");
                    visual = null; // Ownership transferred to the normal game vehicle lifecycle.
                    connector?.SendMessageToServer((EventCode)VehicleProtocol.Event, VehicleProtocol.Message(VehicleProtocol.Ack, w => w.Write(index)), true);
                }
            }
            catch (Exception ex) { Debug.LogError($"[VehicleSpawn] Receive failed: {ex}"); }
            finally { if (visual != null) UnityEngine.Object.Destroy(visual.gameObject); }
            return false;
        }
    }
}
