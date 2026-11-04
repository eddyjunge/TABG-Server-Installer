using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TabgInstaller.PerformanceClient
{
    internal sealed class CentralPhysicsCullingManager : MonoBehaviour
    {
        private sealed class PhysicsEntry
        {
            internal PhysicCullingSystem System;
            internal Rigidbody Body;
            internal Vector3 Velocity;
            internal Vector3 AngularVelocity;
            internal bool IsFar;
        }

        private sealed class TerrainEntry
        {
            internal TerrainCullingSystem System;
            internal Terrain Terrain;
            internal Vector3 Offset;
            internal float Radius;
            internal bool IsFar;
        }

        internal static CentralPhysicsCullingManager Instance { get; private set; }

        private readonly List<PhysicsEntry> _physics = new List<PhysicsEntry>(128);
        private readonly List<TerrainEntry> _terrains = new List<TerrainEntry>(32);
        private CullingGroup _physicsGroup;
        private CullingGroup _terrainGroup;
        private BoundingSphere[] _physicsSpheres = new BoundingSphere[0];
        private BoundingSphere[] _terrainSpheres = new BoundingSphere[0];
        private Camera _camera;
        private float _nextSphereUpdate;
        private float _nextCameraCheck;

        private void Awake()
        {
            Instance = this;
        }

        internal void Register(PhysicCullingSystem system)
        {
            if (system == null)
                return;
            for (var index = 0; index < _physics.Count; index++)
                if (_physics[index].System == system)
                    return;

            var body = system.GetComponent<Rigidbody>();
            if (body == null)
                return;
            _physics.Add(new PhysicsEntry { System = system, Body = body });
            RebuildPhysicsGroup();
            ApplyPhysicsDistance(_physics.Count - 1);
        }

        internal void Unregister(PhysicCullingSystem system)
        {
            for (var index = _physics.Count - 1; index >= 0; index--)
            {
                if (_physics[index].System != system)
                    continue;
                RestorePhysics(_physics[index]);
                _physics.RemoveAt(index);
                RebuildPhysicsGroup();
                return;
            }
        }

        internal void Register(TerrainCullingSystem system)
        {
            if (system == null)
                return;
            for (var index = 0; index < _terrains.Count; index++)
                if (_terrains[index].System == system)
                    return;

            var terrain = system.GetComponent<Terrain>();
            if (terrain == null || terrain.terrainData == null)
                return;
            var size = terrain.terrainData.size;
            var radius = Mathf.Max(size.x, size.z) * 0.75f;
            _terrains.Add(new TerrainEntry
            {
                System = system,
                Terrain = terrain,
                Offset = new Vector3(size.x * 0.5f, size.y * 0.25f, size.z * 0.5f),
                Radius = radius
            });
            RebuildTerrainGroup();
            ApplyTerrainDistance(_terrains.Count - 1);
        }

        internal void Unregister(TerrainCullingSystem system)
        {
            for (var index = _terrains.Count - 1; index >= 0; index--)
            {
                if (_terrains[index].System != system)
                    continue;
                RestoreTerrain(_terrains[index]);
                _terrains.RemoveAt(index);
                RebuildTerrainGroup();
                return;
            }
        }

        private void Update()
        {
            if (!HotPathEnabled.Value)
                return;

            if (Time.unscaledTime >= _nextCameraCheck)
            {
                _nextCameraCheck = Time.unscaledTime + 0.5f;
                var current = Camera.main;
                if (current != _camera)
                {
                    _camera = current;
                    ConfigureCamera(_physicsGroup);
                    ConfigureCamera(_terrainGroup);
                }
            }

            if (Time.unscaledTime < _nextSphereUpdate)
                return;
            _nextSphereUpdate = Time.unscaledTime + 0.1f;

            for (var index = _physics.Count - 1; index >= 0; index--)
            {
                var entry = _physics[index];
                if (entry.System == null || entry.Body == null)
                {
                    _physics.RemoveAt(index);
                    RebuildPhysicsGroup();
                    continue;
                }
                _physicsSpheres[index].position = entry.System.transform.position;
            }

            for (var index = _terrains.Count - 1; index >= 0; index--)
            {
                var entry = _terrains[index];
                if (entry.System == null || entry.Terrain == null)
                {
                    _terrains.RemoveAt(index);
                    RebuildTerrainGroup();
                    continue;
                }
                _terrainSpheres[index].position = entry.System.transform.position + entry.Offset;
            }

            // CullingGroup normally notices sphere-array mutations. The manual
            // distance pass also handles headless/temporarily camera-less frames
            // and makes transitions deterministic after world-origin shifts.
            for (var index = 0; index < _physics.Count; index++)
                ApplyPhysicsDistance(index);
            for (var index = 0; index < _terrains.Count; index++)
                ApplyTerrainDistance(index);
        }

        private void RebuildPhysicsGroup()
        {
            if (_physicsGroup != null)
            {
                _physicsGroup.Dispose();
                _physicsGroup = null;
            }
            _physicsSpheres = new BoundingSphere[_physics.Count];
            if (_physics.Count == 0)
                return;

            for (var index = 0; index < _physics.Count; index++)
                _physicsSpheres[index] = new BoundingSphere(_physics[index].System.transform.position, 0.5f);
            _physicsGroup = new CullingGroup();
            _physicsGroup.SetBoundingSpheres(_physicsSpheres);
            _physicsGroup.SetBoundingSphereCount(_physicsSpheres.Length);
            _physicsGroup.SetBoundingDistances(new[] { PhysicsDistance });
            _physicsGroup.onStateChanged = OnPhysicsStateChanged;
            ConfigureCamera(_physicsGroup);
        }

        private void RebuildTerrainGroup()
        {
            if (_terrainGroup != null)
            {
                _terrainGroup.Dispose();
                _terrainGroup = null;
            }
            _terrainSpheres = new BoundingSphere[_terrains.Count];
            if (_terrains.Count == 0)
                return;

            for (var index = 0; index < _terrains.Count; index++)
            {
                var entry = _terrains[index];
                _terrainSpheres[index] = new BoundingSphere(entry.System.transform.position + entry.Offset, entry.Radius);
            }
            _terrainGroup = new CullingGroup();
            _terrainGroup.SetBoundingSpheres(_terrainSpheres);
            _terrainGroup.SetBoundingSphereCount(_terrainSpheres.Length);
            _terrainGroup.SetBoundingDistances(new[] { TerrainDistance });
            _terrainGroup.onStateChanged = OnTerrainStateChanged;
            ConfigureCamera(_terrainGroup);
        }

        private void ConfigureCamera(CullingGroup group)
        {
            if (group == null)
                return;
            group.targetCamera = _camera;
            if (_camera != null)
                group.SetDistanceReferencePoint(_camera.transform);
        }

        private void OnPhysicsStateChanged(CullingGroupEvent change)
        {
            if (change.index >= 0 && change.index < _physics.Count)
                SetPhysicsFar(_physics[change.index], change.currentDistance > 0);
        }

        private void OnTerrainStateChanged(CullingGroupEvent change)
        {
            if (change.index >= 0 && change.index < _terrains.Count)
                SetTerrainFar(_terrains[change.index], change.currentDistance > 0);
        }

        private void ApplyPhysicsDistance(int index)
        {
            if (_camera == null || index < 0 || index >= _physics.Count)
                return;
            var delta = _physics[index].System.transform.position - _camera.transform.position;
            SetPhysicsFar(_physics[index], delta.sqrMagnitude > PhysicsDistance * PhysicsDistance);
        }

        private void ApplyTerrainDistance(int index)
        {
            if (_camera == null || index < 0 || index >= _terrains.Count)
                return;
            var entry = _terrains[index];
            var delta = entry.System.transform.position + entry.Offset - _camera.transform.position;
            var distance = TerrainDistance + entry.Radius;
            SetTerrainFar(entry, delta.sqrMagnitude > distance * distance);
        }

        private static void SetPhysicsFar(PhysicsEntry entry, bool far)
        {
            if (entry == null || entry.Body == null || entry.IsFar == far)
                return;
            entry.IsFar = far;
            if (far)
            {
                entry.Velocity = entry.Body.velocity;
                entry.AngularVelocity = entry.Body.angularVelocity;
                entry.Body.isKinematic = true;
            }
            else
            {
                entry.Body.isKinematic = false;
                entry.Body.velocity = entry.Velocity;
                entry.Body.angularVelocity = entry.AngularVelocity;
            }
        }

        private static void SetTerrainFar(TerrainEntry entry, bool far)
        {
            if (entry == null || entry.Terrain == null || entry.IsFar == far)
                return;
            entry.IsFar = far;
            entry.Terrain.drawHeightmap = !far;
            if (entry.System != null && entry.System.disableTrees)
                entry.Terrain.drawTreesAndFoliage = !far;
        }

        private static void RestorePhysics(PhysicsEntry entry)
        {
            if (entry != null && entry.IsFar)
                SetPhysicsFar(entry, false);
        }

        private static void RestoreTerrain(TerrainEntry entry)
        {
            if (entry != null && entry.IsFar)
                SetTerrainFar(entry, false);
        }

        private float PhysicsDistance => PerformanceClientPlugin.Instance != null
            ? PerformanceClientPlugin.Instance.PhysicsObjectSimulationDistance
            : 300f;

        private float TerrainDistance => PerformanceClientPlugin.Instance != null
            ? PerformanceClientPlugin.Instance.TerrainCullingDistance
            : 1200f;

        private void OnDestroy()
        {
            for (var index = 0; index < _physics.Count; index++)
                RestorePhysics(_physics[index]);
            for (var index = 0; index < _terrains.Count; index++)
                RestoreTerrain(_terrains[index]);
            _physicsGroup?.Dispose();
            _terrainGroup?.Dispose();
            if (Instance == this)
                Instance = null;
        }
    }

    [HarmonyPatch(typeof(PhysicCullingSystem), "Start")]
    internal static class PhysicsCullingRegisterPatch
    {
        private static bool Prefix(PhysicCullingSystem __instance)
        {
            if (!HotPathEnabled.Value || CentralPhysicsCullingManager.Instance == null)
                return true;
            CentralPhysicsCullingManager.Instance.Register(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(PhysicCullingSystem), "OnDisable")]
    internal static class PhysicsCullingUnregisterPatch
    {
        private static void Prefix(PhysicCullingSystem __instance)
        {
            if (HotPathEnabled.Value)
                CentralPhysicsCullingManager.Instance?.Unregister(__instance);
        }
    }

    [HarmonyPatch(typeof(TerrainCullingSystem), "Start")]
    internal static class TerrainCullingRegisterPatch
    {
        private static bool Prefix(TerrainCullingSystem __instance)
        {
            if (!HotPathEnabled.Value || CentralPhysicsCullingManager.Instance == null)
                return true;
            CentralPhysicsCullingManager.Instance.Register(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(TerrainCullingSystem), "OnDisable")]
    internal static class TerrainCullingUnregisterPatch
    {
        private static void Prefix(TerrainCullingSystem __instance)
        {
            if (HotPathEnabled.Value)
                CentralPhysicsCullingManager.Instance?.Unregister(__instance);
        }
    }
}
