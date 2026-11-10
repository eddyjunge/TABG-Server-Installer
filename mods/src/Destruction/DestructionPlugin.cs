using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Landfall.Network;
using UnityEngine;
using UnityEngine.SceneManagement;

[assembly: InternalsVisibleTo("ClientMods.Tests")]

namespace TabgInstaller.Destruction
{
    /// <summary>
    /// Buildings break where they are hit: walls open up, and what loses its support comes down.
    /// Clients report explosions (event 246); the server keeps one ordered list and everybody
    /// applies it, so holes are identical for all players with this mod, also for late joiners.
    /// Needs a host with <c>game.destruction</c> on; on any other server the mod stays inactive.
    /// </summary>
    [BepInPlugin("tabginstaller.destruction", "TABG Destruction", "1.1.0")]
    [BepInDependency("tabginstaller.modsettings", BepInDependency.DependencyFlags.SoftDependency)]
    public class DestructionPlugin : BaseUnityPlugin
    {
        internal static DestructionPlugin Instance;

        internal ConfigEntry<float> DebrisAmount;
        internal ConfigEntry<bool> Dust;
        internal ConfigEntry<bool> LogExplosions;

        /// <summary>Per-blast statistics: on request, and always in the real-client tests.</summary>
        internal bool Verbose => LogExplosions.Value || Environment.GetEnvironmentVariable("TABG_AUTOJOIN") != null;

        /// <summary>Melee hits, heals and effects that only look like explosions.</summary>
        private static readonly string[] NotBlasts =
        {
            "katana", "sword", "blessing", "heal", "freez", "ice", "time", "gravity", "reveal", "stun",
            "spotted", "shockwave", "taser", "spelldrop",
        };

        private readonly List<Blast> _pending = new List<Blast>();
        /// <summary>Blasts already applied, to redo if more of the map loads afterwards.</summary>
        private readonly List<Blast> _history = new List<Blast>();
        private Harmony _harmony;
        private ServerConnector _connector;
        private WorldIndex _index;
        private DebrisSpawner _debris;
        private Transform _root;
        private bool _accepted;
        private int _framesSinceSceneChange;

        private void Awake()
        {
            Instance = this;
            DebrisAmount = Config.Bind("Effects", "Debris", 1f, new ConfigDescription(
                "How many falling pieces (0 = none; holes and collapses still happen)", new AcceptableValueRange<float>(0f, 1f)));
            Dust = Config.Bind("Effects", "Dust", true, "Dust clouds where buildings break");
            LogExplosions = Config.Bind("Debug", "LogExplosions", false, "Write every explosion and what it counted as to the log");
            try { RegisterSettings(); } catch (Exception) { /* ModSettings isn't installed */ }
            SceneManager.sceneLoaded += OnSceneLoaded;
            _harmony = new Harmony("tabginstaller.destruction");
            _harmony.PatchAll(typeof(DestructionPlugin).Assembly);
            Logger.LogInfo("[Destruction] Loaded; waiting for a server that allows it.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void RegisterSettings()
        {
            ModSettings.ModSettingsUI.Register("Destruction", "Debris", "How many falling pieces", DebrisAmount);
            ModSettings.ModSettingsUI.Register("Destruction", "Dust", "Dust clouds where buildings break", Dust);
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _harmony?.UnpatchSelf();
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            _framesSinceSceneChange = 0;
            if (mode == LoadSceneMode.Single)
            {
                ForgetMap();
                return;
            }
            // The world arrives as additive scenes, after the player can already exist.
            if (_index != null)
            {
                RestoreBuildings();
                _index = null;
                _pending.InsertRange(0, _history);
                _history.Clear();
            }
        }

        private void ForgetMap()
        {
            _index = null;
            _pending.Clear();
            _history.Clear();
            _debris = null;
            if (_root != null)
                Destroy(_root.gameObject);
            _root = null;
            MeshData.ClearCache();
        }

        internal void Joining(ServerConnector connector)
        {
            _connector = connector;
            _accepted = false;
            ForgetMap();
            connector.SendMessageToServer((EventCode)DestructionProtocol.EventCode, DestructionProtocol.Hello(), true);
        }

        internal void Handle(byte[] data)
        {
            if (!DestructionProtocol.TryRead(data, out DestructionOp op, out Blast blast))
                return;
            switch (op)
            {
                case DestructionOp.Accepted:
                    _accepted = true;
                    Logger.LogInfo("[Destruction] The server allows destruction.");
                    break;
                case DestructionOp.Denied:
                    _accepted = false;
                    Logger.LogInfo("[Destruction] Off on this server.");
                    break;
                case DestructionOp.Blast:
                    _pending.Add(blast);
                    break;
                case DestructionOp.Reset:
                    RestoreMap();
                    break;
            }
        }

        private void RestoreMap()
        {
            _pending.Clear();
            _history.Clear();
            RestoreBuildings();
        }

        private void RestoreBuildings()
        {
            if (_index == null)
                return;
            foreach (Structure s in _index.Active)
                s.Restore();
            _index.Active.Clear();
            _index.Merged.Restore();
            _debris?.Clear();
        }

        internal void OnExplode(Explosion e)
        {
            if (!_accepted || e == null || _connector == null)
                return;
            string name = (e.transform.root.name + " " + e.name).ToLowerInvariant();
            float radius = e.radius * (e.scale ? e.transform.localScale.x : 1f);
            // Effect pools set themselves up at the world origin. Knockback, implosion and black holes
            // do little damage but push hard: they count by their force.
            float force = Mathf.Abs(e.force);
            bool blast = (e.damage >= 20f || force >= 100f) && radius >= 1.5f && e.transform.position != Vector3.zero;
            foreach (string s in NotBlasts)
                blast &= !name.Contains(s);
            if (Verbose)
                Logger.LogInfo($"[Destruction] explosion '{name}' damage {e.damage} radius {radius} blast={blast}");
            if (!blast)
                return;

            float hole = Mathf.Clamp(1.4f + 0.35f * radius, 2.2f, 9f);
            float power = Mathf.Clamp(Mathf.Max(e.damage * 2f, force * 0.3f), 40f, 400f);
            if (name.Contains("missile") || name.Contains("rocket"))
            {
                hole = Mathf.Max(hole, 3.6f);
                power = Mathf.Max(power, 200f);
            }
            byte[] report = DestructionProtocol.Report(e.transform.position, hole, power);
            _connector.SendMessageToServer((EventCode)DestructionProtocol.EventCode, report, true);
        }

        private void Update()
        {
            _framesSinceSceneChange++;
            if (!_accepted)
                return;
            if (_index == null)
            {
                if (Player.localPlayer == null || _framesSinceSceneChange < 30)
                    return;
                BuildIndex();
            }
            if (_pending.Count > 0)
                FlushBlasts();
            for (int i = 0; i < _index.Active.Count; i++)
                _index.Active[i].Pump(_root, _debris);
            _debris.Tick();
        }

        private void BuildIndex()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            _index = WorldIndex.Build();
            if (_root == null)
                _root = new GameObject("TABG Destruction").transform;
            if (_debris == null)
                _debris = new DebrisSpawner(_root);
            Logger.LogInfo($"[Destruction] {_index.Structures.Count} buildings found in {watch.ElapsedMilliseconds} ms; {_index.Merged.Statistics()}");
        }

        private void FlushBlasts()
        {
            Vector3 viewer = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            foreach (Blast blast in _pending)
            {
                int hit = 0;
                foreach (Structure s in _index.Near(blast.Position, blast.Radius))
                {
                    s.Enqueue(blast, viewer, DebrisAmount.Value);
                    hit++;
                }
                _history.Add(blast);
                if (Verbose)
                    Logger.LogInfo($"[Destruction] blast {blast.Seq} at {blast.Position} radius {blast.Radius} power {blast.Power}: {hit} buildings");
            }
            _pending.Clear();
        }
    }

    [HarmonyPatch(typeof(Explosion), nameof(Explosion.Explode), typeof(NetworkOwner))]
    internal static class ExplodePatch
    {
        private static void Postfix(Explosion __instance) => DestructionPlugin.Instance?.OnExplode(__instance);
    }

    [HarmonyPatch(typeof(ServerConnector), "SendInitRequest")]
    internal static class HelloPatch
    {
        private static void Prefix(ServerConnector __instance) => DestructionPlugin.Instance?.Joining(__instance);
    }

    [HarmonyPatch(typeof(ServerConnector), "OnEvent")]
    internal static class ReceivePatch
    {
        private static bool Prefix(ClientPackage clientPackage)
        {
            if ((byte)clientPackage.Code != DestructionProtocol.EventCode)
                return true;
            DestructionPlugin.Instance?.Handle(clientPackage.Buffer);
            return false;
        }
    }
}
