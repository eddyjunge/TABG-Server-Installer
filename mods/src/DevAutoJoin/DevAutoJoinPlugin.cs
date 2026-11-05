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
    ///   TABG_AUTOJOIN        "ip:port" to join once the main menu is up, or "code:JOINCODE" to join
    ///                        through Unity Relay like the join-code box does
    ///   TABG_AUTOJOIN_QUIT   seconds after joining to quit the game (default: never)
    ///   TABG_AUTOJOIN_SHOTS  comma-separated seconds after joining to take screenshots
    ///   TABG_AUTOJOIN_JUMP   jump out of the plane at this fraction of its 45 s flight (e.g. 0.5)
    ///   TABG_AUTOJOIN_SEAT   seconds after jumping to get into the nearest car's driver seat
    ///   TABG_AUTOJOIN_CHAT   "text@seconds" (after joining) to send a chat line, e.g. "/give 131 5@20"
    ///   TABG_AUTOJOIN_HEAL   item id to heal with (once), as soon as the local player is hurt, e.g. 131
    ///   TABG_AUTOJOIN_DROP   "item:count@seconds" (after joining) to drop from the inventory, e.g. "132:1@75"
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
        private float _jumpAt = -1f;
        private float _seatAfter = -1f;
        private float _sinceJump = -1f;
        private bool _seated;
        private string _chatText;
        private float _chatAt = -1f;
        internal static float FlyingSince = -1f;
        private int _healItem = -1;
        private int _dropItem = -1;
        private int _dropCount;
        private float _dropAt = -1f;
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

            float.TryParse(Environment.GetEnvironmentVariable("TABG_AUTOJOIN_JUMP"), NumberStyles.Float, CultureInfo.InvariantCulture, out _jumpAt);
            if (!float.TryParse(Environment.GetEnvironmentVariable("TABG_AUTOJOIN_SEAT"), NumberStyles.Float, CultureInfo.InvariantCulture, out _seatAfter))
            {
                _seatAfter = -1f;
            }

            string chat = Environment.GetEnvironmentVariable("TABG_AUTOJOIN_CHAT");
            int at = chat?.LastIndexOf('@') ?? -1;
            if (at > 0 && float.TryParse(chat.Substring(at + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float chatAt))
            {
                _chatText = chat.Substring(0, at);
                _chatAt = chatAt;
            }

            if (!int.TryParse(Environment.GetEnvironmentVariable("TABG_AUTOJOIN_HEAL"), out _healItem))
            {
                _healItem = -1;
            }

            string drop = Environment.GetEnvironmentVariable("TABG_AUTOJOIN_DROP");
            int dropAt = drop?.LastIndexOf('@') ?? -1;
            int colon = drop?.IndexOf(':') ?? -1;
            if (dropAt > 0 && colon > 0 && colon < dropAt
                && int.TryParse(drop.Substring(0, colon), out _dropItem)
                && int.TryParse(drop.Substring(colon + 1, dropAt - colon - 1), out _dropCount)
                && float.TryParse(drop.Substring(dropAt + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out _dropAt))
            {
                Logger.LogInfo($"will drop {_dropCount} x {_dropItem} at {_dropAt} s");
            }
            else
            {
                _dropAt = -1f;
            }

            if (_jumpAt <= 0f)
            {
                _jumpAt = -1f;
            }

            SceneManager.sceneLoaded += (scene, mode) => Logger.LogInfo($"scene loaded: {scene.name} ({mode})");
            new Harmony("tabg.dev.autojoin").PatchAll(typeof(DevAutoJoinPlugin).Assembly);
            Logger.LogInfo($"Will join {_target} once the menu is up.");
        }

        private void TickJumpAndSeat(float dt)
        {
            if (_jumpAt > 0f && FlyingSince >= 0f && _sinceJump < 0f && Time.unscaledTime - FlyingSince >= _jumpAt * 45f)
            {
                PhotonServerHandler handler = FindObjectOfType<PhotonServerHandler>();
                if (handler != null)
                {
                    Logger.LogInfo("Jumping out of the plane");
                    handler.ClientRequestPlaneDrop(Vector3.down);
                }

                _sinceJump = 0f;
                return;
            }

            if (_sinceJump < 0f)
            {
                return;
            }

            float before = _sinceJump;
            _sinceJump += dt;
            if ((int)(before / 5f) != (int)(_sinceJump / 5f))
            {
                Logger.LogInfo($"cars spawned in the scene: {FindObjectsOfType<Car>().Length}");
            }

            if (_seated || _seatAfter < 0f || _sinceJump < _seatAfter || Camera.main == null)
            {
                return;
            }

            _seated = true;
            Vector3 me = Camera.main.transform.position;
            Car nearest = FindObjectsOfType<Car>().OrderBy(c => Vector3.Distance(c.transform.position, me)).FirstOrDefault();
            Seat seat = nearest != null ? nearest.GetComponentsInChildren<Seat>().OrderBy(st => st.NetworkIndex).FirstOrDefault() : null;
            if (seat == null)
            {
                Logger.LogInfo("No car to sit in");
                return;
            }

            Logger.LogInfo($"Getting into {nearest.name} {Vector3.Distance(nearest.transform.position, me):F0} m away (seat {seat.NetworkIndex})");
            FindObjectOfType<PhotonServerHandler>()?.ClientRequestSeat(seat, SeatAction.GetIn);
        }

        /// Heal and drop through the client's own inventory code, like a player would.
        private void TickInventory()
        {
            Player me = Player.localPlayer;
            if (me == null)
            {
                return;
            }

            if (_healItem >= 0 && me.m_playerDeath.health < 100f && !me.m_playerDeath.isDown && !me.m_playerDeath.dead)
            {
                int have = me.m_inventory.GetPickupTypeCountInInventory(_healItem);
                Logger.LogInfo($"Hurt ({me.m_playerDeath.health:F0} HP): healing with item {_healItem}, carrying {have}");
                InventoryUI.RequestHealingUsingItemType(_healItem);
                _healItem = -1;
            }

            if (_dropAt >= 0f && _sinceJoin >= _dropAt)
            {
                _dropAt = -1f;
                int have = me.m_inventory.GetPickupTypeCountInInventory(_dropItem);
                Logger.LogInfo($"Dropping {_dropCount} x item {_dropItem}, carrying {have}");
                me.m_interactionHandler.Drop(_dropItem, _dropCount);
            }
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

                _sinceJoin = 0f;
                if (_target.StartsWith("code:", StringComparison.Ordinal))
                {
                    string code = _target.Substring("code:".Length);
                    Logger.LogInfo($"Joining relay code {code} (scene {SceneManager.GetActiveScene().name})");
                    ServerConnector.Instance.ConnectToRelayServer(code);
                    return;
                }
                string[] parts = _target.Split(':');
                ushort port = parts.Length > 1 ? ushort.Parse(parts[1]) : (ushort)7777;
                Logger.LogInfo($"Joining {parts[0]}:{port} (scene {SceneManager.GetActiveScene().name})");
                ServerConnector.Instance.ConnectToServerIP(parts[0], port);
                return;
            }

            _sinceJoin += dt;
            TickJumpAndSeat(dt);
            if (_chatAt >= 0f && _sinceJoin >= _chatAt)
            {
                _chatAt = -1f;
                Logger.LogInfo($"Saying: {_chatText}");
                FindObjectOfType<PhotonServerHandler>()?.SendChatMessage(_chatText);
            }
            TickInventory();
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
                var cars = HarmonyLib.AccessTools.Field(typeof(PhotonServerHandler), "m_Cars")?.GetValue(PhotonServerHandler.instance) as System.Collections.ICollection;
                var visual = FindObjectsOfType<Car>();
                string nearest = "";
                if (p != null && visual.Length > 0)
                {
                    Car best = null;
                    foreach (Car c in visual)
                        if (best == null || (c.transform.position - p.transform.position).sqrMagnitude < (best.transform.position - p.transform.position).sqrMagnitude)
                            best = c;
                    nearest = $" nearest={best.name}@{Vector3.Distance(best.transform.position, p.transform.position):F0}m";
                }
                Logger.LogInfo($"t={_sinceJoin:F0}s scene={SceneManager.GetActiveScene().name} player={where} index={ServerConnector.Instance?.GetPlayerIndex()} cars={cars?.Count ?? -1} visual={visual.Length}{nearest}");
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
            if (clientPackage.Code == EventCode.ChatMessage && clientPackage.Buffer != null && clientPackage.Buffer.Length > 2)
                DevAutoJoinPlugin.Log?.LogInfo("chat: " + System.Text.Encoding.Unicode.GetString(clientPackage.Buffer, 2, Math.Min(clientPackage.Buffer[1], clientPackage.Buffer.Length - 2)));
            if (clientPackage.Code == EventCode.GameStateChanged && clientPackage.Buffer != null && clientPackage.Buffer.Length > 0 && clientPackage.Buffer[0] == (byte)GameState.Flying)
            {
                DevAutoJoinPlugin.FlyingSince = Time.unscaledTime;
            }
        }
    }
}
