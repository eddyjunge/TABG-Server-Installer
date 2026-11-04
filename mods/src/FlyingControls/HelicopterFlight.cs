using HarmonyLib;
using TabgInstaller.Vehicles;
using UnityEngine;
namespace TabgInstaller.FlyingControls
{
    // Preserve driver authority while using the original stabilized flight controls.
    public sealed class HelicopterFlight : MonoBehaviour
    {
        internal Car Car;
        private Transform _driver;
        private bool _remote;
        internal bool SyncAuthority()
        {
            if(Car.GetComponent<AircraftFallView>()){Car.isSimulatedByMe=false;Car.Freeze();return false;}
            var driver=Car.driverSeat?Car.driverSeat.occupant:null;bool local=AircraftClient.IsLocalDriver(Car);
            if(driver!=_driver){_driver=driver;if(driver)Car.LocalDrive();}
            if(driver){Car.isSimulatedByMe=local;if(local)Car.UnFreeze();else Car.Freeze();_remote=!local;}
            else{if(_remote)Car.UnFreeze();_remote=false;}
            return local;
        }
        [HarmonyPatch(typeof(Car),"Update")]
        internal static class AuthorityPatch
        {
            static void Prefix(Car __instance){if(!MissileRules.IsHeli(__instance.name) || !__instance.mainRig)return;Get(__instance).SyncAuthority();}
        }
        [HarmonyPatch(typeof(Car),"NetworkUpdate")]
        internal static class ReceivedPatch
        {
            static void Postfix(Car __instance){if(!MissileRules.IsHeli(__instance.name) || !__instance.mainRig)return;Get(__instance).SyncAuthority();}
        }
        internal static HelicopterFlight Get(Car car){var f=car.GetComponent<HelicopterFlight>() ?? car.gameObject.AddComponent<HelicopterFlight>();f.Car=car;return f;}
    }
}
