using System;
using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;

namespace TabgInstaller.FlyingControls
{
    internal static class GrenadeProfiles
    {
        internal static Explosion Hand, Dynamite, Knockback;
        internal static PooledEffect DynamiteVisual, HandVisual;
        private static PooledEffect[] Effects => AccessTools.Field(typeof(ParticlePlayer), "internalEffects").GetValue(null) as PooledEffect[];
        internal static bool Load()
        {
            if (Hand && Dynamite && Knockback) return true;
            foreach (var entry in LootDatabase.Instance.GetPickups())
            {
                string name = entry.pickup?.itemName;
                if (name != null) name = name.ToLowerInvariant();
                if (name == "grenade" && entry.prefab.name != "Grenade") continue;
                if (name != "grenade" && name != "dynamite" && name != "big knockback grenade") continue;
                var explosion = Find(entry.prefab, new HashSet<GameObject>(), 0);
                if (!explosion) continue;
                if (name == "grenade")
                {
                    Hand = explosion;
                    foreach (var spawn in entry.prefab.GetComponentsInChildren<SpawnObject>(true))
                        if (Effects != null && spawn.effectID >= 0 && spawn.effectID < Effects.Length) HandVisual = Effects[spawn.effectID];
                }
                else if (name == "dynamite")
                {
                    Dynamite = explosion;
                    foreach (var spawn in entry.prefab.GetComponentsInChildren<SpawnObject>(true))
                        if (Effects != null && spawn.effectID >= 0 && spawn.effectID < Effects.Length) DynamiteVisual = Effects[spawn.effectID];
                }
                else Knockback = explosion;
            }
            if (!Hand || !Dynamite || !Knockback) return false;
            Debug.Log($"[VehicleBalance] Native grenade profiles: damage={Hand.damage}, radius={Dynamite.radius}, force={Knockback.force}, velocity force={Knockback.unscalingForce}");
            return true;
        }
        private static Explosion Find(GameObject obj, HashSet<GameObject> seen, int depth)
        {
            if (!obj || depth > 8 || !seen.Add(obj)) return null;
            var explosion = obj.GetComponentInChildren<Explosion>(true);
            if (explosion) return explosion;
            foreach (var spawn in obj.GetComponentsInChildren<SpawnObject>(true))
            {
                if (Effects != null && spawn.effectID >= 0 && spawn.effectID < Effects.Length && Effects[spawn.effectID].explosion) return Effects[spawn.effectID].explosion;
                var result = Find(spawn.objectToSpawn, seen, depth + 1);
                if (result) return result;
            }
            return null;
        }
    }
}
