using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TabgInstaller.PerformanceClient
{
    [HarmonyPatch(typeof(Streamer), "LoadLevelAsyncManage")]
    internal static class StreamerLoadQueuePatch
    {
        private static bool Prefix(
            Streamer __instance,
            List<SceneSplit> ___scenesToLoad,
            ref int ___currentlySceneLoading,
            ref int ___sceneLoadFrameNext,
            ref bool ___sceneLoadFramesNextWaited)
        {
            if (!HotPathEnabled.Value)
                return true;
            if (___scenesToLoad.Count == 0 || ___currentlySceneLoading > 0)
                return false;

            if (__instance.LoadingProgress < 1f || (___sceneLoadFramesNextWaited && ___sceneLoadFrameNext <= 0))
            {
                ___sceneLoadFramesNextWaited = false;
                ___sceneLoadFrameNext = __instance.sceneLoadWaitFrames;
                while (___currentlySceneLoading < __instance.maxParallelSceneLoading && ___scenesToLoad.Count > 0)
                {
                    // Pop from the end so the hot path does not shift the entire List.
                    var last = ___scenesToLoad.Count - 1;
                    var scene = ___scenesToLoad[last];
                    ___scenesToLoad.RemoveAt(last);
                    ___currentlySceneLoading++;
                    SceneManager.LoadSceneAsync(scene.sceneName, LoadSceneMode.Additive);
                }
            }
            else
            {
                ___sceneLoadFramesNextWaited = true;
                ___sceneLoadFrameNext--;
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(Streamer), "SceneUnloading")]
    internal static class StreamerUnloadPatch
    {
        private static bool Prefix(Streamer __instance, int ___xPos, int ___yPos, int ___zPos)
        {
            if (!HotPathEnabled.Value)
                return true;

            var unloadedAny = false;
            for (var index = __instance.loadedScenes.Count - 1; index >= 0; index--)
            {
                var split = __instance.loadedScenes[index];
                if (split == null || split.sceneGo == null)
                    continue;

                var outside = Mathf.Abs(split.posX + split.xDeloadLimit - ___xPos) > (int)__instance.deloadingRange.x
                              || Mathf.Abs(split.posY + split.yDeloadLimit - ___yPos) > (int)__instance.deloadingRange.y
                              || Mathf.Abs(split.posZ + split.zDeloadLimit - ___zPos) > (int)__instance.deloadingRange.z;
                var insideRingCutout = __instance.useLoadingRangeMin
                                       && Mathf.Abs(split.posX + split.xDeloadLimit - ___xPos) <= __instance.loadingRangeMin.x
                                       && Mathf.Abs(split.posY + split.yDeloadLimit - ___yPos) <= __instance.loadingRangeMin.y
                                       && Mathf.Abs(split.posZ + split.zDeloadLimit - ___zPos) <= __instance.loadingRangeMin.z;
                if (!outside && !insideRingCutout)
                    continue;

                __instance.loadedScenes.RemoveAt(index);
                UnloadSplit(split);
                unloadedAny = true;
            }

            if (unloadedAny && __instance.terrainNeighbours != null)
                __instance.terrainNeighbours.CreateNeighbours();

            // Resources.UnloadUnusedAssets is intentionally omitted here. It is
            // a global synchronous scan and is inappropriate while the player is
            // crossing a streaming boundary; Unity releases the additive scene.
            return false;
        }

        private static void UnloadSplit(SceneSplit split)
        {
            try
            {
                var scene = split.sceneGo.scene;
                var terrain = split.sceneGo.GetComponentInChildren<Terrain>();
                if (terrain != null)
                    terrain.enabled = false;

                if (scene.IsValid() && scene.isLoaded)
                    SceneManager.UnloadSceneAsync(scene);
                else
                    UnityEngine.Object.Destroy(split.sceneGo);
            }
            catch (Exception exception)
            {
                Debug.LogError("[PerformanceClient] Could not unload streamed scene '" + split.sceneName + "': " + exception.Message);
                if (split.sceneGo != null)
                    UnityEngine.Object.Destroy(split.sceneGo);
            }
            split.sceneGo = null;
            split.loaded = false;
        }
    }

    [HarmonyPatch(typeof(ColliderStreamer), "UnloadScene")]
    internal static class ColliderStreamerUnloadPatch
    {
        private static bool Prefix(ColliderStreamer __instance)
        {
            if (!HotPathEnabled.Value)
                return true;

            var root = __instance.sceneGameObject;
            var scene = root != null ? root.scene : SceneManager.GetSceneByName(__instance.sceneName);
            if (scene.IsValid() && scene.isLoaded)
                SceneManager.UnloadSceneAsync(scene);
            else if (root != null)
                UnityEngine.Object.Destroy(root);
            __instance.sceneGameObject = null;
            return false;
        }
    }

    [HarmonyPatch(typeof(ColliderStreamer), "OnTriggerEnter")]
    internal static class ColliderStreamerCancelPendingUnloadPatch
    {
        private static void Prefix(ColliderStreamer __instance)
        {
            if (HotPathEnabled.Value)
                __instance.CancelInvoke("UnloadScene");
        }
    }

    [HarmonyPatch(typeof(TerrainNeighbours), nameof(TerrainNeighbours.CreateNeighbours))]
    internal static class TerrainNeighboursAllocationPatch
    {
        private sealed class Neighbours
        {
            internal Terrain Left;
            internal Terrain Top;
            internal Terrain Right;
            internal Terrain Bottom;
        }

        private sealed class State
        {
            internal bool HasOrigin;
            internal Vector2 Origin;
            internal int SizeX;
            internal int SizeZ;
            internal readonly Dictionary<Vector2Int, Terrain> Map = new Dictionary<Vector2Int, Terrain>();
            internal readonly Dictionary<Terrain, Neighbours> Previous = new Dictionary<Terrain, Neighbours>();
            internal readonly List<Terrain> Removed = new List<Terrain>();
        }

        private static readonly Dictionary<int, State> States = new Dictionary<int, State>();

        private static bool Prefix(TerrainNeighbours __instance)
        {
            if (!HotPathEnabled.Value)
                return true;

            State state;
            var instanceId = __instance.GetInstanceID();
            if (!States.TryGetValue(instanceId, out state))
            {
                state = new State();
                States.Add(instanceId, state);
            }

            var active = Terrain.activeTerrains;
            Terrain first = null;
            for (var index = 0; index < active.Length; index++)
            {
                var terrain = active[index];
                if (terrain != null && !IsOmitted(__instance, terrain))
                {
                    first = terrain;
                    break;
                }
            }
            if (first == null || first.terrainData == null)
                return false;

            var worldOffset = __instance.worldMover != null ? __instance.worldMover.currentMove : Vector3.zero;
            if (!state.HasOrigin)
            {
                state.HasOrigin = true;
                var firstPosition = first.transform.position - worldOffset;
                state.Origin = new Vector2(firstPosition.x, firstPosition.z);
                state.SizeX = Mathf.Max(1, (int)first.terrainData.size.x);
                state.SizeZ = Mathf.Max(1, (int)first.terrainData.size.z);
            }

            state.Map.Clear();
            __instance._terrains.Clear();
            for (var index = 0; index < active.Length; index++)
            {
                var terrain = active[index];
                if (terrain == null || IsOmitted(__instance, terrain))
                    continue;
                var position = terrain.transform.position - worldOffset;
                var key = new Vector2Int(
                    Mathf.RoundToInt((position.x - state.Origin.x) / state.SizeX),
                    Mathf.RoundToInt((position.z - state.Origin.y) / state.SizeZ));
                state.Map[key] = terrain;
                __instance._terrains.Add(terrain);
            }

            foreach (var pair in state.Map)
            {
                Terrain top;
                Terrain left;
                Terrain right;
                Terrain bottom;
                state.Map.TryGetValue(pair.Key + Vector2Int.up, out top);
                state.Map.TryGetValue(pair.Key + Vector2Int.left, out left);
                state.Map.TryGetValue(pair.Key + Vector2Int.right, out right);
                state.Map.TryGetValue(pair.Key + Vector2Int.down, out bottom);

                Neighbours previous;
                if (!state.Previous.TryGetValue(pair.Value, out previous)
                    || previous.Left != left
                    || previous.Top != top
                    || previous.Right != right
                    || previous.Bottom != bottom)
                {
                    pair.Value.SetNeighbors(left, top, right, bottom);
                    state.Previous[pair.Value] = new Neighbours
                    {
                        Left = left,
                        Top = top,
                        Right = right,
                        Bottom = bottom
                    };
                }
            }

            state.Removed.Clear();
            foreach (var pair in state.Previous)
                if (pair.Key == null || !ContainsTerrain(state.Map, pair.Key))
                    state.Removed.Add(pair.Key);
            for (var index = 0; index < state.Removed.Count; index++)
                state.Previous.Remove(state.Removed[index]);

            return false;
        }

        private static bool IsOmitted(TerrainNeighbours owner, Terrain terrain)
        {
            return owner.terrainsToOmit != null && owner.terrainsToOmit.Contains(terrain);
        }

        private static bool ContainsTerrain(Dictionary<Vector2Int, Terrain> map, Terrain terrain)
        {
            foreach (var pair in map)
                if (pair.Value == terrain)
                    return true;
            return false;
        }
    }
}
