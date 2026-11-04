using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TabgInstaller.Vehicles;
using UnityEngine;

namespace TabgInstaller.FlyingControls
{
    // Linked into the client and dedicated-server plugin: identical ID and pickup metadata.
    internal static class FpvItem
    {
        private static readonly FieldInfo Items = AccessTools.Field(typeof(LootDatabase), "items");
        private static GameObject _holder;
        private static bool _registering;
        internal static bool Register(LootDatabase database)
        {
            if(!database || _registering)return false;
            var items=Items.GetValue(database) as Dictionary<int,ItemDataEntry>;
            if(items==null)return false;
            if(items.TryGetValue(DroneRules.ItemId,out var existing))return existing.pickup && existing.pickup.itemName=="FPV Drone";
            ItemDataEntry source=default(ItemDataEntry);
            foreach(var item in items.Values)
                if(item.pickup && string.Equals(item.pickup.itemName,"Dynamite",StringComparison.OrdinalIgnoreCase)){source=item;break;}
            if(!source.prefab)return false;
            _registering=true;
            try
            {
                if(!_holder)
                {
                    _holder=new GameObject("FPV item templates");_holder.SetActive(false);
                    UnityEngine.Object.DontDestroyOnLoad(_holder);
                }
                var clone=UnityEngine.Object.Instantiate(source.prefab,_holder.transform);
                clone.name="FPV Drone";
                // Remove the old fuse and explosion events from our own clone, never from Dynamite.
                foreach(var timer in clone.GetComponentsInChildren<CountEvent>(true))UnityEngine.Object.DestroyImmediate(timer);
                foreach(var spawn in clone.GetComponentsInChildren<SpawnObject>(true))UnityEngine.Object.DestroyImmediate(spawn);
                var pickup=clone.GetComponent<Pickup>();
                pickup.m_itemIndex=DroneRules.ItemId;pickup.itemName="FPV Drone";pickup.NetworkSyncThis=false;
                source.prefab=clone;source.pickup=pickup;
                items.Add(DroneRules.ItemId,source);
                Debug.Log("[FPV] Registered FPV Drone grenade, item="+DroneRules.ItemId);
                return true;
            }
            finally {_registering=false;}
        }
        [HarmonyPatch]
        internal static class LookupPatch
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(LootDatabase),"GetDataEntry");
                yield return AccessTools.Method(typeof(LootDatabase),"HasDataEntry");
                yield return AccessTools.Method(typeof(LootDatabase),"GetPickups");
            }
            static void Prefix(LootDatabase __instance) { Register(__instance); }
        }
    }
}
