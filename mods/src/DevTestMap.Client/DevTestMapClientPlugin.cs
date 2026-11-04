using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Landfall.Network;
using Landfall.TABG.UI;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TabgInstaller.DevTestMap.Client
{
    [BepInPlugin("tabginstaller.devtestmap.client", "DevTest Map Client", "1.0.0")]
    public sealed class DevTestMapClientPlugin : BaseUnityPlugin
    {
        internal static DevTestMapClientPlugin Instance;
        internal static bool DevTestServerAccepted;
        internal static bool DevTestSceneActive;

        private ConfigEntry<KeyCode> _itemMenuKey;
        private Harmony _harmony;
        private bool _showItems;
        private bool _savedUsingInterface;
        private bool _hasSavedUsingInterface;
        private string _search = string.Empty;
        private Vector2 _scroll;
        private Rect _window = new Rect(30f, 60f, 530f, 650f);
        private List<ItemChoice> _items;
        private float _lastHelloAt = -10f;

        internal void LogDevTestInfo(string message) => Logger.LogInfo(message);
        internal void LogDevTestWarning(string message) => Logger.LogWarning(message);

        internal static void DisableLocalDebugLoadout(Player player)
        {
            if (player == null || player != Player.localPlayer)
                return;

            var debugPickup = player.m_interactionHandler?.m_DebugSpawnWeapon;
            var testBlessing = player.curseHandler?.testCurse;
            if (player.m_interactionHandler != null)
            {
                // InteractionHandler.Start is a coroutine: it waits one frame before
                // injecting this serialized pickup. Null it before that continuation.
                player.m_interactionHandler.m_DebugSpawnWeapon = null;
                player.m_interactionHandler.setWeapon = null;
                player.m_interactionHandler.setWeapon2 = null;
            }
            if (player.curseHandler != null)
                player.curseHandler.testCurse = null;

            var pickupName = debugPickup != null ? debugPickup.itemName + " [" + debugPickup.m_itemIndex + "]" : "none";
            var blessingName = testBlessing != null ? testBlessing.curseName : "none";
            Instance?.LogDevTestInfo("[DevTestMap] Disabled player-prefab debug loadout before startup: pickup=" +
                pickupName + ", blessing=" + blessingName + ".");
        }

        internal static void HideDevTestScoreHud(GameUIHandler handler)
        {
            if (handler == null || !DevTestSceneActive)
                return;

            HideGameObjectField(handler, "m_PlayersAliveObject");
            HideGameObjectField(handler, "m_RingProgressTextObject");
            Instance?.LogDevTestInfo("[DevTestMap] Hid placement/kills and ring-progress HUD elements.");
        }

        private static void HideGameObjectField(GameUIHandler handler, string fieldName)
        {
            var field = AccessTools.Field(typeof(GameUIHandler), fieldName);
            var gameObject = field?.GetValue(handler) as GameObject;
            gameObject?.SetActive(false);
        }

        private void Awake()
        {
            Instance = this;
            _itemMenuKey = Config.Bind("DevTestMap", "ItemMenuKey", KeyCode.F6, "Open the server-authoritative all-items menu.");
            _harmony = new Harmony("tabginstaller.devtestmap.client");
            _harmony.PatchAll();
            SceneManager.sceneLoaded += OnSceneLoaded;
            Logger.LogInfo("[DevTestMap] Client ready. The item menu is F6 while connected to a DevTest server.");
        }

        private void Update()
        {
            if (DevTestSceneActive && Time.unscaledTime - _lastHelloAt >= 2f)
                SendHello(ServerConnector.Instance);

            if (Input.GetKeyDown(_itemMenuKey.Value))
            {
                if (_showItems)
                    CloseItemMenu();
                else
                    OpenItemMenu();
            }
        }

        private void LateUpdate()
        {
            if (!_showItems)
                return;

            Player.usingInterface = true;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }

        private void OnGUI()
        {
            if (!_showItems)
                return;
            _window = GUI.Window(24301, _window, DrawItemWindow, "DevTest items - all server-authoritative");
        }

        private void DrawItemWindow(int id)
        {
            GUILayout.Label("Search by item name, type, or numeric ID. Ammo is granted as a full stack.");
            GUI.SetNextControlName("DevTestItemSearch");
            _search = GUILayout.TextField(_search ?? string.Empty);
            var needle = (_search ?? string.Empty).Trim();
            var visible = (_items ?? new List<ItemChoice>())
                .Where(item => needle.Length == 0 || item.SearchText.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            GUILayout.Label(visible.Count + " items - click one to add it to your inventory");

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(525f));
            foreach (var item in visible)
            {
                var quantity = item.Type == Pickup.WeaponType.Ammo ? (byte)255 : (byte)1;
                if (GUILayout.Button(item.Name + "  [" + item.Id + "]  " + item.Type))
                    RequestItem(item.Id, quantity);
            }
            GUILayout.EndScrollView();

            if (GUILayout.Button("Close (" + _itemMenuKey.Value + ")"))
                CloseItemMenu();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 24f));
        }

        private void OpenItemMenu()
        {
            _showItems = true;
            _savedUsingInterface = Player.usingInterface;
            _hasSavedUsingInterface = true;
            Player.usingInterface = true;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
            EnsureItems();
        }

        private void CloseItemMenu()
        {
            _showItems = false;
            if (_hasSavedUsingInterface)
                Player.usingInterface = _savedUsingInterface;
            _hasSavedUsingInterface = false;
            Cursor.visible = false;
            Cursor.lockState = CursorLockMode.Locked;
        }

        private void EnsureItems()
        {
            if (_items != null)
                return;
            try
            {
                _items = LootDatabase.Instance.GetPickups()
                    .Where(entry => entry.pickup != null)
                    .Select(entry => new ItemChoice(entry.pickup))
                    .GroupBy(item => item.Id)
                    .Select(group => group.First())
                    .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.Id)
                    .ToList();
            }
            catch (Exception ex)
            {
                Logger.LogError("[DevTestMap] Could not build item catalog: " + ex);
                _items = new List<ItemChoice>();
            }
        }

        private void RequestItem(int itemId, byte quantity)
        {
            try
            {
                var connector = ServerConnector.Instance;
                SendHello(connector);
                connector?.SendMessageToServer((EventCode)DevTestProtocol.EventCode,
                    DevTestProtocol.CreateGiveItem(itemId, quantity), true);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("[DevTestMap] Item request failed: " + ex.Message);
            }
        }

        internal void SendHello(ServerConnector connector)
        {
            if (connector == null)
                return;

            _lastHelloAt = Time.unscaledTime;
            connector.SendMessageToServer((EventCode)DevTestProtocol.EventCode,
                DevTestProtocol.CreateHello(), true);
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!DevTestServerAccepted || !string.Equals(scene.name, "DevTest", StringComparison.Ordinal))
                return;

            DevTestSceneActive = true;
            if (_showItems)
                CloseItemMenu();
            _items = null;

            try
            {
                var spawnAction = UnityEngine.Object.FindObjectOfType<GM_SpawnPlayerAction>();
                if (spawnAction != null)
                    UnityEngine.Object.DestroyImmediate(spawnAction.gameObject);

                // DevTest ships with an offline local player path. Remove it before the
                // network prefab creates the server-authoritative local and remote players.
                foreach (var offlinePlayer in UnityEngine.Object.FindObjectsOfType<Player>())
                    UnityEngine.Object.DestroyImmediate(offlinePlayer.gameObject);

                if (PhotonServerHandler.instance == null)
                {
                    var networkPrefab = TABGGameModeObjectDataBase.Instance.GetNetworkGameObject();
                    if (networkPrefab == null)
                        throw new InvalidOperationException("TABG network client prefab is missing.");
                    UnityEngine.Object.Instantiate(networkPrefab);
                }

                Logger.LogInfo("[DevTestMap] Loaded DevTest and started the multiplayer network handler. Press F6 for all items.");
            }
            catch (Exception ex)
            {
                Logger.LogError("[DevTestMap] DevTest network bootstrap failed: " + ex);
            }
        }

        private void OnDestroy()
        {
            if (_showItems)
                CloseItemMenu();
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _harmony?.UnpatchSelf();
            DevTestServerAccepted = false;
            DevTestSceneActive = false;
            Instance = null;
        }

        private sealed class ItemChoice
        {
            internal readonly int Id;
            internal readonly string Name;
            internal readonly Pickup.WeaponType Type;
            internal readonly string SearchText;

            internal ItemChoice(Pickup pickup)
            {
                Id = pickup.m_itemIndex;
                Name = string.IsNullOrWhiteSpace(pickup.itemName) ? pickup.gameObject.name : pickup.itemName;
                Type = pickup.weaponType;
                SearchText = Name + " " + Type + " " + Id;
            }
        }
    }

    [HarmonyPatch(typeof(GameUIHandler), "Start")]
    internal static class HideDevTestScoreHudPatch
    {
        private static void Postfix(GameUIHandler __instance)
        {
            DevTestMapClientPlugin.HideDevTestScoreHud(__instance);
        }
    }

    [HarmonyPatch(typeof(ServerConnector), "SendInitRequest")]
    internal static class DevTestHelloPatch
    {
        private static void Prefix(ServerConnector __instance)
        {
            DevTestMapClientPlugin.DevTestServerAccepted = false;
            DevTestMapClientPlugin.DevTestSceneActive = false;
            DevTestMapClientPlugin.Instance?.SendHello(__instance);
        }
    }

    [HarmonyPatch(typeof(ServerBrowserSelectedServerUI), "AttemptConnect")]
    internal static class DebounceCommunityServerConnectPatch
    {
        private const float RetryDelaySeconds = 5f;
        private static float _lastAttemptAt = -RetryDelaySeconds;

        private static bool Prefix()
        {
            var now = Time.unscaledTime;
            if (now - _lastAttemptAt >= RetryDelaySeconds)
            {
                _lastAttemptAt = now;
                return true;
            }

            DevTestMapClientPlugin.Instance?.LogDevTestInfo(
                "[DevTestMap] Ignored a duplicate community-server Connect click while the first request is pending.");
            return false;
        }
    }

    [HarmonyPatch(typeof(Player), "Start")]
    internal static class DisableDevTestPlayerDebugLoadoutPatch
    {
        private static void Prefix(Player __instance)
        {
            if (DevTestMapClientPlugin.DevTestSceneActive)
                DevTestMapClientPlugin.DisableLocalDebugLoadout(__instance);
        }
    }

    [HarmonyPatch(typeof(CurseHandler), "Start")]
    internal static class DisableDevTestSerializedBlessingPatch
    {
        private static void Prefix(CurseHandler __instance)
        {
            if (DevTestMapClientPlugin.DevTestSceneActive)
                __instance.testCurse = null;
        }
    }

    // DevTest contains offline-only scene helpers that inject a blessing and an
    // invalid ammo pickup whenever its local player appears. Multiplayer items
    // must come from the server-authoritative F6 request path instead.
    [HarmonyPatch(typeof(GM_PlayerSpawnWithItems), "GiveItems")]
    internal static class SuppressDevTestStarterItemsPatch
    {
        private static bool Prefix()
        {
            return !DevTestMapClientPlugin.DevTestSceneActive;
        }
    }

    [HarmonyPatch(typeof(GM_GiveAmmoForWeapon), nameof(GM_GiveAmmoForWeapon.Spawn))]
    internal static class SuppressDevTestOfflineAmmoPatch
    {
        private static bool Prefix()
        {
            return !DevTestMapClientPlugin.DevTestSceneActive;
        }
    }

    // DevTest has no SonigonSoundAreaManager. Remote fire still reaches the
    // normal PhotonServerHandler -> NetworkShoot -> Gun.Shoot path, but the
    // missing reflection-sound singleton throws before Gun.Shoot can finish
    // initializing its ProjectileSyncWatcher. Keep the normal muzzle sound and
    // projectile creation; only omit the unavailable ambient reflection layer.
    [HarmonyPatch(typeof(GunSFX), "PlayShootReflectionSound")]
    internal static class SuppressUnavailableDevTestGunReflectionPatch
    {
        private static bool _loggedMissingManager;

        private static bool Prefix()
        {
            if (!DevTestMapClientPlugin.DevTestSceneActive || Sonigon.SonigonSoundAreaManager.Instance != null)
                return true;

            if (!_loggedMissingManager)
            {
                _loggedMissingManager = true;
                DevTestMapClientPlugin.Instance?.LogDevTestInfo(
                    "[DevTestMap] Gun reflection ambience is unavailable in DevTest; continuing shots without it.");
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(ServerConnector), "OnEvent")]
    internal static class DevTestAcceptedPatch
    {
        private static bool Prefix(ClientPackage clientPackage)
        {
            if (clientPackage.Code == EventCode.Login && DevTestMapClientPlugin.DevTestSceneActive)
            {
                var handler = PhotonServerHandler.instance;
                if (handler == null)
                {
                    DevTestMapClientPlugin.Instance?.LogDevTestWarning(
                        "[DevTestMap] Login packet arrived before the active network handler was ready; falling back to vanilla routing.");
                    return true;
                }

                try
                {
                    handler.HandlePlayerJoin(clientPackage);
                    DevTestMapClientPlugin.Instance?.LogDevTestInfo(
                        "[DevTestMap] Routed Login world state to the active DevTest network handler.");
                    return false;
                }
                catch (Exception ex)
                {
                    DevTestMapClientPlugin.Instance?.LogDevTestWarning(
                        "[DevTestMap] Direct Login routing failed; falling back to vanilla routing: " + ex.Message);
                    return true;
                }
            }

            if ((byte)clientPackage.Code != DevTestProtocol.EventCode)
                return true;

            byte operation;
            int itemId;
            byte quantity;
            if (!DevTestProtocol.TryRead(clientPackage.Buffer, out operation, out itemId, out quantity))
                return false;

            if (operation == DevTestProtocol.Accepted)
            {
                DevTestMapClientPlugin.DevTestServerAccepted = true;
                DevTestMapClientPlugin.Instance?.LogDevTestInfo("[DevTestMap] Server handshake accepted.");
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(ServerConnector), "LoadGameScene")]
    internal static class DevTestScenePatch
    {
        private static bool Prefix(GameMode gameMode)
        {
            if (gameMode != GameMode.Test || !DevTestMapClientPlugin.DevTestServerAccepted)
                return true;

            DevTestMapClientPlugin.DevTestSceneActive = true;
            SceneManager.LoadScene("DevTest");
            return false;
        }
    }
}
