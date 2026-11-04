using System.IO;
using System.Reflection;
using HarmonyLib;
using Landfall.Network;
using TabgInstaller.Vehicles;
using UnityEngine;
namespace TabgInstaller.FlyingControls
{
    public sealed class GroundTaxi : MonoBehaviour
    {
        internal Car Car;
        private Transform _driver;
        private string _status="Ziel auf der Karte markieren";
        private float _serverBoostUntil,_serverReadyAt;
        internal static GroundTaxi Local
        {
            get
            {
                var car=Player.localPlayer?.m_sitting?.currentSeat?.GetComponentInParent<Car>();
                return car && AircraftClient.IsLocalDriver(car)?car.GetComponent<GroundTaxi>():null;
            }
        }
        private void Awake(){Car=GetComponent<Car>();}
        internal void SetDestination(Vector3 destination)
        {
            FpvClient.Send(VehicleProtocol.TaxiDestination,w=>{w.Write(true);FpvClient.Write(w,destination);});
            _status="Autopilot: Ziel wird bestaetigt";
        }
        internal void Cancel(){FpvClient.Send(VehicleProtocol.TaxiDestination,w=>w.Write(false));}
        internal static void ReceiveStatus(BinaryReader reader)
        {
            int id=reader.ReadInt32();byte status=reader.ReadByte();float boost=VehicleProtocol.ReadFinite(reader),cooldown=VehicleProtocol.ReadFinite(reader);
            if(reader.BaseStream.Position!=reader.BaseStream.Length)return;
            var car=AircraftClient.Cars?.Find(c=>c.CarIndex==id)?.CarReference;
            if(!car)return;var taxi=car.GetComponent<GroundTaxi>() ?? car.gameObject.AddComponent<GroundTaxi>();
            taxi._serverBoostUntil=Time.unscaledTime+boost;taxi._serverReadyAt=Time.unscaledTime+cooldown;
            taxi._status=status==1?"Autopilot: folgt der Strasse":status==2?"Autopilot: ueberquert Hindernis":status==3?"Weg blockiert – neues Ziel markieren":status==4?"Ziel erreicht":status==5?"Keine verbundene Strasse gefunden":"Ziel auf der Karte markieren";
        }
        private void Update()
        {
            if(Car && AircraftClient.IsLocalDriver(Car))
            {
                var driver=Car.driverSeat.occupant;
                if(driver!=_driver){_driver=driver;var marker=MarkerHandler.Instance?.GetLocalMarker();if(marker!=null && marker.active)SetDestination(marker.position);}
                if(Input.GetKeyDown(KeyCode.W) && !Player.usingInterface && !TABGChat.inChat)FpvClient.Send(VehicleProtocol.TaxiBoost,null);
            }
            else _driver=null;
        }
        internal void Tick(){SyncAuthority();}
        internal bool SyncAuthority()
        {
            if(!Car || !Car.mainRig)return false;
            // The server is the only physics owner. A passenger/driver may never add local lift.
            if(Car.GetComponent<AircraftFallView>() || (Car.driverSeat && Car.driverSeat.occupant))
            {Car.isSimulatedByMe=false;Car.Freeze();}
            return false;
        }
        [HarmonyPatch(typeof(Car),"Update")]
        internal static class RenderAuthorityPatch
        {
            static void Prefix(Car __instance)
            {
                if(!VehicleBalance.IsTaxi(__instance.name) || !__instance.mainRig)return;
                (__instance.GetComponent<GroundTaxi>() ?? __instance.gameObject.AddComponent<GroundTaxi>()).SyncAuthority();
            }
        }
        [HarmonyPatch(typeof(Car),"NetworkUpdate")]
        internal static class ReceivedAuthorityPatch
        {
            static void Postfix(Car __instance)
            {
                if(!VehicleBalance.IsTaxi(__instance.name) || !__instance.mainRig)return;
                (__instance.GetComponent<GroundTaxi>() ?? __instance.gameObject.AddComponent<GroundTaxi>()).SyncAuthority();
            }
        }
        [HarmonyPatch(typeof(NetworkPlayer),"SendUpdate")]
        internal static class DriverSenderPatch
        {
            private static readonly FieldInfo CurrentCar=AccessTools.Field(typeof(NetworkPlayer),"m_CurrentCar"),CurrentSeat=AccessTools.Field(typeof(NetworkPlayer),"m_CurrentSeat"),Driving=AccessTools.Field(typeof(NetworkPlayer),"m_IsDriving");
            static void Prefix(NetworkPlayer __instance)
            {
                if(!Player.localPlayer || __instance.transform.root!=Player.localPlayer.transform.root)return;
                var car=CurrentCar.GetValue(__instance) as Car;
                if(!car || (!VehicleBalance.IsTaxi(car.name) && !MissileRules.IsHeli(car.name)))return;
                bool driver=!VehicleBalance.IsTaxi(car.name) && ReferenceEquals(CurrentSeat.GetValue(__instance),car.driverSeat) && AircraftClient.IsLocalDriver(car);
                Driving.SetValue(__instance,driver);
            }
        }
        private void OnGUI()
        {
            if(!Car || !AircraftClient.IsLocalDriver(Car))return;
            GUI.Label(new Rect(20,Screen.height-85,650,30),_status);
            string boostStatus=Time.unscaledTime<_serverBoostUntil?$"BOOST: {Mathf.Max(0,_serverBoostUntil-Time.unscaledTime):0.0}s":
                Time.unscaledTime<_serverReadyAt?$"Booster laedt: {Mathf.CeilToInt(_serverReadyAt-Time.unscaledTime)}s":"W: Booster bereit (waehrend der Fahrt)";
            GUI.Label(new Rect(20,Screen.height-55,650,30),boostStatus);
        }
        [HarmonyPatch(typeof(MarkerHandler),"AddMarker")]
        internal static class MarkerPatch{static void Postfix(Vector3 __0){Local?.SetDestination(__0);}}
        [HarmonyPatch(typeof(MarkerHandler),"RemoveLocalMarker")]
        internal static class RemoveMarkerPatch{static void Postfix(){Local?.Cancel();}}
    }
}
