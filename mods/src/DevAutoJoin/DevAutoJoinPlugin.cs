using System;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Landfall.Network;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Tabg.DevAutoJoin
{
    /// <summary>
    /// Development harness for testing tabg's server with the real client, without clicking menus.
    /// Does nothing unless TABG_AUTOJOIN is set.
    ///
    ///   TABG_AUTOJOIN        "ip:port" to join once the main menu is up
    ///   TABG_AUTOJOIN_QUIT   seconds after joining to quit the game (default: never)
    ///   TABG_AUTOJOIN_SHOTS  comma-separated seconds after joining to take screenshots
    ///
    /// Logs scene changes, every event the client receives, and the local player's position, and
    /// saves screenshots to BepInEx/autojoin-*.png.
    /// </summary>
    [BepInPlugin("tabg.dev.autojoin", "tabg dev auto-join", "0.1.0")]
    public sealed class DevAutoJoinPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        private string _target;
        private float _quitAfter = -1f;
        private float _sinceMenu = -1f;
        private float _sinceJoin = -1f;
        private float _nextStatus;
        private int _shot;
        private float[] _shotTimes = { 10f, 30f, 50f, 60f, 70f, 85f };

        private void Awake()
        {
            Log = Logger;
            _target = Environment.GetEnvironmentVariable("TABG_AUTOJOIN");
            if (string.IsNullOrEmpty(_target))
            {
                Logger.LogInfo("TABG_AUTOJOIN not set; doing nothing.");
                enabled = false;
                return;
            }

            if (float.TryParse(Environment.GetEnvironmentVariable("TABG_AUTOJOIN_QUIT"), out float quit))
            {
                _quitAfter = quit;
            }

            string shots = Environment.GetEnvironmentVariable("TABG_AUTOJOIN_SHOTS");
            if (!string.IsNullOrEmpty(shots))
            {
                _shotTimes = shots.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            }

            SceneManager.sceneLoaded += (scene, mode) => Logger.LogInfo($"scene loaded: {scene.name} ({mode})");
            new Harmony("tabg.dev.autojoin").PatchAll(typeof(DevAutoJoinPlugin).Assembly);
            Logger.LogInfo($"Will join {_target} once the menu is up.");
        }

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            if (_sinceJoin < 0f)
            {
                // Joining before the Epic login finishes fails with "Failed generating token".
                if (ServerConnector.Instance == null || !SteamManager.Initialized || EOSSDKComponent.LocalUser == null || !EOSSDKComponent.LocalUser.IsValid())
                {
                    return;
                }

                _sinceMenu = _sinceMenu < 0f ? 0f : _sinceMenu + dt;
                if (_sinceMenu < 8f)
                {
                    return;
                }

                string[] parts = _target.Split(':');
                ushort port = parts.Length > 1 ? ushort.Parse(parts[1]) : (ushort)7777;
                Logger.LogInfo($"Joining {parts[0]}:{port} (scene {SceneManager.GetActiveScene().name})");
                ServerConnector.Instance.ConnectToServerIP(parts[0], port);
                _sinceJoin = 0f;
                return;
            }

            _sinceJoin += dt;
            if (_shot < _shotTimes.Length && _sinceJoin >= _shotTimes[_shot])
            {
                string path = Path.Combine(Paths.BepInExRootPath, $"autojoin-{_shot}.png");
                ScreenCapture.CaptureScreenshot(path);
                Logger.LogInfo($"screenshot {path}");
                _shot++;
            }

            if (_sinceJoin >= _nextStatus)
            {
                _nextStatus = _sinceJoin + 5f;
                Player p = Player.localPlayer;
                string where = p != null ? p.transform.position.ToString() : "no local player";
                Logger.LogInfo($"t={_sinceJoin:F0}s scene={SceneManager.GetActiveScene().name} player={where} index={ServerConnector.Instance?.GetPlayerIndex()}");
            }

            if (_quitAfter >= 0f && _sinceJoin >= _quitAfter)
            {
                Logger.LogInfo("Quitting.");
                Application.Quit();
                enabled = false;
            }
        }
    }

    [HarmonyPatch(typeof(ServerConnector), nameof(ServerConnector.OnEvent))]
    internal static class LogEvents
    {
        private static void Prefix(ClientPackage clientPackage)
        {
            DevAutoJoinPlugin.Log?.LogInfo($"recv {(byte)clientPackage.Code} {clientPackage.Code} len={clientPackage.Buffer?.Length ?? -1}");
        }
    }
}
