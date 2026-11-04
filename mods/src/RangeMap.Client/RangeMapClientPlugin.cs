using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Landfall.Network;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TabgInstaller.RangeMap.Client
{
    [BepInPlugin("tabginstaller.rangemap.client", "Range Map Client", "1.1.1")]
    public sealed class RangeMapClientPlugin : BaseUnityPlugin
    {
        internal static RangeMapClientPlugin Instance;
        internal static bool RangeServerAccepted;
        internal static bool RangeSceneActive;

        private ConfigEntry<KeyCode> _itemMenuKey;
        private Harmony _harmony;
        private bool _showItems;
        private bool _savedUsingInterface;
        private bool _hasSavedUsingInterface;
        private string _search = string.Empty;
        private string _lastSearch = string.Empty;
        private string _requestStatus = "Range client v1.1.1 PAGED is active.";
        private int _page;
        private Rect _window = new Rect(30f, 60f, 530f, 650f);
        private List<ItemChoice> _items;
        private const int ItemsPerPage = 16;

        internal void LogRangeInfo(string message) => Logger.LogInfo(message);
        internal void LogRangeWarning(string message) => Logger.LogWarning(message);

        private void Awake()
        {
            Instance = this;
            _itemMenuKey = Config.Bind("RangeMap", "ItemMenuKey", KeyCode.F6, "Open the server-authoritative all-items menu.");
            _harmony = new Harmony("tabginstaller.rangemap.client");
            _harmony.PatchAll();
            SceneManager.sceneLoaded += OnSceneLoaded;
            Logger.LogInfo("[RangeMap] Client ready. The item menu is F6 while connected to a Range server.");
        }

        private void Update()
        {
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
            _window = GUI.Window(24201, _window, DrawItemWindow, "Range items v1.1.1 PAGED - server-authoritative");
        }

        private void DrawItemWindow(int id)
        {
            var oldColor = GUI.color;
            GUI.color = new Color(0.07f, 0.08f, 0.10f, 1f);
            GUI.DrawTexture(new Rect(1f, 20f, _window.width - 2f, _window.height - 21f),
                Texture2D.whiteTexture, ScaleMode.StretchToFill, false);
            GUI.color = oldColor;

            GUILayout.Space(18f);
            GUILayout.Label("Search by item name, type, or numeric ID. Ammo is granted as a full stack.");
            GUI.SetNextControlName("RangeItemSearch");
            _search = GUILayout.TextField(_search ?? string.Empty);
            var needle = (_search ?? string.Empty).Trim();
            if (!string.Equals(needle, _lastSearch, StringComparison.Ordinal))
            {
                _lastSearch = needle;
                _page = 0;
            }

            var visible = (_items ?? new List<ItemChoice>())
                .Where(item => needle.Length == 0 || item.SearchText.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            var pageCount = Math.Max(1, (visible.Count + ItemsPerPage - 1) / ItemsPerPage);
            _page = Math.Max(0, Math.Min(_page, pageCount - 1));
            GUILayout.Label(visible.Count + " items - page " + (_page + 1) + "/" + pageCount);

            foreach (var item in visible.Skip(_page * ItemsPerPage).Take(ItemsPerPage))
            {
                var quantity = item.Type == Pickup.WeaponType.Ammo ? (byte)255 : (byte)1;
                if (GUILayout.Button(item.Name + "  [" + item.Id + "]  " + item.Type))
                    RequestItem(item.Id, quantity);
            }

            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            GUI.enabled = _page > 0;
            if (GUILayout.Button("Previous page"))
                _page--;
            GUI.enabled = _page + 1 < pageCount;
            if (GUILayout.Button("Next page"))
                _page++;
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Label(_requestStatus ?? string.Empty);

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
                Logger.LogError("[RangeMap] Could not build item catalog: " + ex);
                _items = new List<ItemChoice>();
            }
        }

        private void RequestItem(int itemId, byte quantity)
        {
            try
            {
                var connector = ServerConnector.Instance;
                if (connector == null)
                {
                    _requestStatus = "Not connected to a Range server.";
                    return;
                }

                // Refresh compatibility immediately before the request so a
                // reconnect or reassigned player index cannot silently drop it.
                connector.SendMessageToServer((EventCode)RangeProtocol.EventCode,
                    RangeProtocol.CreateHello(), true);
                connector.SendMessageToServer((EventCode)RangeProtocol.EventCode,
                    RangeProtocol.CreateGiveItem(itemId, quantity), true);
                _requestStatus = "Requesting item " + itemId + "...";
            }
            catch (Exception ex)
            {
                _requestStatus = "Request failed: " + ex.Message;
                Logger.LogWarning("[RangeMap] Item request failed: " + ex.Message);
            }
        }

        internal void HandleItemResult(byte operation, int itemId, byte quantity)
        {
            var item = (_items ?? new List<ItemChoice>()).FirstOrDefault(choice => choice.Id == itemId);
            var name = item == null ? "item " + itemId : item.Name;
            if (operation == RangeProtocol.ItemGranted)
                _requestStatus = "Added " + name + " x" + quantity + ".";
            else
                _requestStatus = "Server denied " + name + "; wait a moment and try again.";
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!RangeServerAccepted || !string.Equals(scene.name, "WilhelmTest", StringComparison.Ordinal))
                return;

            RangeSceneActive = true;
            if (_showItems)
                CloseItemMenu();
            _items = null;

            try
            {
                var spawnAction = UnityEngine.Object.FindObjectOfType<GM_SpawnPlayerAction>();
                if (spawnAction != null)
                    UnityEngine.Object.DestroyImmediate(spawnAction.gameObject);

                // WilhelmTest ships with its own offline local player. Remove it before the
                // network prefab creates the authoritative local and remote player objects.
                foreach (var offlinePlayer in UnityEngine.Object.FindObjectsOfType<Player>())
                    UnityEngine.Object.DestroyImmediate(offlinePlayer.gameObject);

                if (PhotonServerHandler.instance == null)
                {
                    var networkPrefab = TABGGameModeObjectDataBase.Instance.GetNetworkGameObject();
                    if (networkPrefab == null)
                        throw new InvalidOperationException("TABG network client prefab is missing.");
                    UnityEngine.Object.Instantiate(networkPrefab);
                }

                Logger.LogInfo("[RangeMap] Loaded WilhelmTest and started the multiplayer network handler. Press F6 for all items.");
            }
            catch (Exception ex)
            {
                Logger.LogError("[RangeMap] Range network bootstrap failed: " + ex);
            }
        }

        private void OnDestroy()
        {
            if (_showItems)
                CloseItemMenu();
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _harmony?.UnpatchSelf();
            RangeServerAccepted = false;
            RangeSceneActive = false;
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

    [HarmonyPatch(typeof(ServerConnector), "SendInitRequest")]
    internal static class RangeHelloPatch
    {
        private static void Prefix(ServerConnector __instance)
        {
            RangeMapClientPlugin.RangeServerAccepted = false;
            RangeMapClientPlugin.RangeSceneActive = false;
            __instance.SendMessageToServer((EventCode)RangeProtocol.EventCode, RangeProtocol.CreateHello(), true);
        }
    }

    [HarmonyPatch(typeof(ServerConnector), "OnEvent")]
    internal static class RangeAcceptedPatch
    {
        private static bool Prefix(ClientPackage clientPackage)
        {
            if (clientPackage.Code == EventCode.Login && RangeMapClientPlugin.RangeSceneActive)
            {
                var handler = PhotonServerHandler.instance;
                if (handler == null)
                {
                    RangeMapClientPlugin.Instance?.LogRangeWarning(
                        "[RangeMap] Login packet arrived before the active network handler was ready; falling back to vanilla routing.");
                    return true;
                }

                try
                {
                    handler.HandlePlayerJoin(clientPackage);
                    RangeMapClientPlugin.Instance?.LogRangeInfo(
                        "[RangeMap] Routed Login world state to the active Range network handler.");
                    return false;
                }
                catch (Exception ex)
                {
                    RangeMapClientPlugin.Instance?.LogRangeWarning(
                        "[RangeMap] Direct Login routing failed; falling back to vanilla routing: " + ex.Message);
                    return true;
                }
            }

            if ((byte)clientPackage.Code != RangeProtocol.EventCode)
                return true;

            byte operation;
            int itemId;
            byte quantity;
            if (RangeProtocol.TryRead(clientPackage.Buffer, out operation, out itemId, out quantity) && operation == RangeProtocol.Accepted)
            {
                RangeMapClientPlugin.RangeServerAccepted = true;
                RangeMapClientPlugin.Instance?.LogRangeInfo("[RangeMap] Server handshake accepted.");
            }
            else if (operation == RangeProtocol.ItemGranted || operation == RangeProtocol.ItemDenied)
            {
                RangeMapClientPlugin.Instance?.HandleItemResult(operation, itemId, quantity);
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(ServerConnector), "LoadGameScene")]
    internal static class RangeSceneRedirectPatch
    {
        private static bool Prefix(GameMode gameMode)
        {
            if (gameMode != GameMode.Test || !RangeMapClientPlugin.RangeServerAccepted)
                return true;
            RangeMapClientPlugin.RangeSceneActive = true;
            SceneManager.LoadScene("WilhelmTest");
            return false;
        }
    }
}
