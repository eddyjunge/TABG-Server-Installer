using System.IO;
using HarmonyLib;
using TabgInstaller.Vehicles;
using UnityEngine;
namespace TabgInstaller.FlyingControls
{
    internal static class AircraftHealthDisplay
    {
        // Vanilla's smoke curve divides CurrentHealth by this original prefab maximum.
        [HarmonyPatch(typeof(Damagable),"NetworkInit")]
        internal static class InitialMaximum
        {
            static void Postfix(Damagable __instance)
            {
                var car=__instance.GetComponentInParent<Car>();
                if(car && VehicleBalance.IsAircraft(car.name) && !__instance.playerDeath)
                    __instance.health=MissileRules.IsHeli(car.name)?300:30;
            }
        }
        internal static void Receive(BinaryReader reader)
        {
            int id=reader.ReadInt32();float hp=VehicleProtocol.ReadFinite(reader),maximum=VehicleProtocol.ReadFinite(reader);
            if(reader.BaseStream.Position!=reader.BaseStream.Length || (maximum!=30 && maximum!=300) || hp<0 || hp>maximum)return;
            var car=AircraftClient.Cars?.Find(c=>c.CarIndex==id)?.CarReference;
            if(!car || !VehicleBalance.IsAircraft(car.name))return;
            foreach(var part in car.GetComponentsInChildren<Damagable>(true))
            {
                if(part.playerDeath)continue;
                part.health=maximum;
                // The normal damage-event stream owns CurrentHealth. Only its denominator changes.
            }
        }
    }
}
