using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Landfall.Network;
using UnityEngine;

namespace TabgInstaller.CustomGameSkins.Client
{
    [BepInPlugin("tabginstaller.customgameskins.client", "Custom Game All Skins Client", "1.0.0")]
    public sealed class CustomGameSkinsClientPlugin : BaseUnityPlugin
    {
        internal static CustomGameSkinsClientPlugin Instance;
        internal static bool ServerAuthorized;

        private const int SkinsPerPage = 13;
        private static readonly Gear.GearType[] Slots =
        {
            Gear.GearType.HEAD,
            Gear.GearType.TORSO,
            Gear.GearType.LEGS,
            Gear.GearType.FEET,
            Gear.GearType.ARMOR,
            Gear.GearType.HELMET,
        };

        private ConfigEntry<KeyCode> _menuKey;
        private Harmony _harmony;
        private bool _showMenu;
        private bool _savedUsingInterface;
        private bool _hasSavedInputState;
        private bool _savedCursorVisible;
        private CursorLockMode _savedCursorLock;
        private Rect _window = new Rect(35f, 45f, 590f, 700f);
        private List<SkinChoice> _skins;
        private int[] _selection = CreateEmptyOutfit();
        private Gear.GearType _selectedSlot = Gear.GearType.HEAD;
        private string _search = string.Empty;
        private string _lastSearch = string.Empty;
        private int _page;
        private string _status = "Waiting for a compatible custom server.";
        private string _notice;
        private float _noticeUntil;

        private void Awake()
        {
            Instance = this;
            _menuKey = Config.Bind("CustomGameSkins", "MenuKey", KeyCode.F7,
                "Open the all-skins wardrobe after the custom server authorizes it.");
            _harmony = new Harmony("tabginstaller.customgameskins.client");
            _harmony.PatchAll();
            Logger.LogInfo("[CustomGameSkins] Client loaded. F7 unlocks only after a compatible custom server authorizes the session.");
        }

        private void Update()
        {
            if (_showMenu && (!ServerAuthorized || !HasLocalPlayer()))
                CloseMenu();

            if (!Input.GetKeyDown(_menuKey.Value))
                return;

            if (_showMenu)
            {
                CloseMenu();
                return;
            }

            if (!ServerAuthorized)
            {
                ShowNotice("All Skins is locked: this server did not authorize the custom-game plugin.");
                return;
            }

            if (!HasLocalPlayer())
            {
                ShowNotice("All Skins will be available after your player has spawned.");
                return;
            }

            OpenMenu();
        }

        private void LateUpdate()
        {
            if (!_showMenu)
                return;

            Player.usingInterface = true;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }

        private void OnGUI()
        {
            if (!string.IsNullOrEmpty(_notice) && Time.unscaledTime < _noticeUntil)
                GUI.Box(new Rect(20f, 20f, 570f, 42f), _notice);

            if (_showMenu)
                _window = GUI.Window(24401, _window, DrawWindow, "Custom Games: All Skins - server authorized");
        }

        private void DrawWindow(int id)
        {
            var oldColor = GUI.color;
            GUI.color = new Color(0.07f, 0.08f, 0.10f, 1f);
            GUI.DrawTexture(new Rect(1f, 20f, _window.width - 2f, _window.height - 21f),
                Texture2D.whiteTexture, ScaleMode.StretchToFill, false);
            GUI.color = oldColor;

            GUILayout.Space(16f);
            GUILayout.Label("Every built-in clothing prefab is available here for this custom-server session only.");
            GUILayout.Label("Nothing is purchased, uploaded to PlayFab, or saved as your public outfit.");
            GUILayout.Space(4f);

            GUILayout.BeginHorizontal();
            foreach (var slot in Slots)
            {
                var previous = GUI.backgroundColor;
                if (slot == _selectedSlot)
                    GUI.backgroundColor = new Color(0.3f, 0.75f, 1f, 1f);
                if (GUILayout.Button(SlotLabel(slot)))
                {
                    _selectedSlot = slot;
                    _page = 0;
                }
                GUI.backgroundColor = previous;
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Selected: " + SelectedSkinName(), GUILayout.Width(330f));
            if (GUILayout.Button("Remove from slot"))
            {
                _selection[SlotOffset(_selectedSlot)] = -1;
                _selection[SlotOffset(_selectedSlot) + 1] = -1;
            }
            GUILayout.EndHorizontal();

            DrawColorControls();

            GUI.SetNextControlName("CustomGameSkinSearch");
            _search = GUILayout.TextField(_search ?? string.Empty);
            var needle = (_search ?? string.Empty).Trim();
            if (!string.Equals(needle, _lastSearch, StringComparison.Ordinal))
            {
                _lastSearch = needle;
                _page = 0;
            }

            EnsureCatalog();
            var sourceType = ItemTypeForSlot(_selectedSlot);
            var visible = (_skins ?? new List<SkinChoice>())
                .Where(item => item.Type == sourceType &&
                    (needle.Length == 0 || item.SearchText.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0))
                .ToList();
            var pageCount = Math.Max(1, (visible.Count + SkinsPerPage - 1) / SkinsPerPage);
            _page = Math.Max(0, Math.Min(_page, pageCount - 1));
            GUILayout.Label(visible.Count + " skins for " + SlotLabel(_selectedSlot) + " - page " + (_page + 1) + "/" + pageCount);

            foreach (var skin in visible.Skip(_page * SkinsPerPage).Take(SkinsPerPage))
            {
                if (GUILayout.Button(skin.Name + "  [" + skin.ItemId + " / " + skin.Index + "]"))
                {
                    _selection[SlotOffset(_selectedSlot)] = skin.Index;
                    _selection[SlotOffset(_selectedSlot) + 1] = -1;
                }
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

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Load current outfit"))
                LoadCurrentOutfit();
            if (GUILayout.Button("Randomize all 6 slots"))
                RandomizeOutfit();
            GUILayout.EndHorizontal();

            var previousBackground = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.25f, 0.85f, 0.45f, 1f);
            if (GUILayout.Button("Apply on this custom server"))
                RequestOutfit();
            GUI.backgroundColor = previousBackground;
            GUILayout.Label(_status ?? string.Empty);
            if (GUILayout.Button("Close (" + _menuKey.Value + ")"))
                CloseMenu();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 24f));
        }

        private void DrawColorControls()
        {
            var offset = SlotOffset(_selectedSlot) + 1;
            var selectedColor = _selection[offset];
            var colors = GearDatabase.Instance.Colors;
            var colorCount = colors == null ? 0 : colors.Length;

            GUILayout.BeginHorizontal();
            GUILayout.Label("Color: " + (selectedColor < 0 ? "Default" : (selectedColor + 1) + "/" + colorCount), GUILayout.Width(180f));
            if (GUILayout.Button("Default", GUILayout.Width(90f)))
                _selection[offset] = -1;
            GUI.enabled = colorCount > 0;
            if (GUILayout.Button("Previous color"))
                _selection[offset] = selectedColor <= 0 ? colorCount - 1 : selectedColor - 1;
            if (GUILayout.Button("Next color"))
                _selection[offset] = selectedColor < 0 || selectedColor + 1 >= colorCount ? 0 : selectedColor + 1;
            GUI.enabled = true;
            if (_selection[offset] >= 0 && _selection[offset] < colorCount)
            {
                var previous = GUI.color;
                GUI.color = colors[_selection[offset]];
                GUILayout.Box(Texture2D.whiteTexture, GUILayout.Width(42f), GUILayout.Height(20f));
                GUI.color = previous;
            }
            GUILayout.EndHorizontal();
        }

        private void OpenMenu()
        {
            EnsureCatalog();
            LoadCurrentOutfit();
            _showMenu = true;
            _savedUsingInterface = Player.usingInterface;
            _savedCursorVisible = Cursor.visible;
            _savedCursorLock = Cursor.lockState;
            _hasSavedInputState = true;
            Player.usingInterface = true;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }

        private void CloseMenu()
        {
            _showMenu = false;
            if (_hasSavedInputState)
            {
                Player.usingInterface = _savedUsingInterface;
                Cursor.visible = _savedCursorVisible;
                Cursor.lockState = _savedCursorLock;
            }
            _hasSavedInputState = false;
        }

        private void EnsureCatalog()
        {
            if (_skins != null)
                return;

            try
            {
                _skins = GearDatabase.Instance.GetAllGearItems()
                    .Where(entry => entry.m_gear != null)
                    .Select(entry => new SkinChoice(entry))
                    .OrderBy(entry => entry.Type)
                    .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(entry => entry.Index)
                    .ToList();
                _status = "Loaded " + _skins.Count + " built-in skins. Choose an outfit and apply it.";
            }
            catch (Exception ex)
            {
                _skins = new List<SkinChoice>();
                _status = "Could not load GearDatabase: " + ex.Message;
                Logger.LogError("[CustomGameSkins] Could not build skin catalog: " + ex);
            }
        }

        private void LoadCurrentOutfit()
        {
            var player = Player.localPlayer;
            var gearHandler = player == null ? null : player.m_characterGearHandler;
            if (gearHandler == null)
            {
                _status = "Your local player is not ready yet.";
                return;
            }

            var current = gearHandler.GetAllGearIndices();
            _selection = current != null && current.Length == CustomGameSkinsProtocol.GearValueCount
                ? (int[])current.Clone()
                : CreateEmptyOutfit();
            _status = "Loaded the outfit currently visible in this session.";
        }

        private void RandomizeOutfit()
        {
            EnsureCatalog();
            foreach (var slot in Slots)
            {
                var choices = _skins.Where(skin => skin.Type == ItemTypeForSlot(slot)).ToList();
                var offset = SlotOffset(slot);
                if (choices.Count == 0)
                {
                    _selection[offset] = -1;
                    _selection[offset + 1] = -1;
                    continue;
                }

                var selected = choices[UnityEngine.Random.Range(0, choices.Count)];
                _selection[offset] = selected.Index;
                _selection[offset + 1] = -1;
            }
            _status = "Random outfit ready. Press Apply to ask the custom server to use it.";
        }

        private void RequestOutfit()
        {
            if (!ServerAuthorized || !HasLocalPlayer())
            {
                _status = "The custom server is not ready to accept an outfit.";
                return;
            }

            try
            {
                ServerConnector.Instance.SendMessageToServer((EventCode)CustomGameSkinsProtocol.EventCode,
                    CustomGameSkinsProtocol.CreateApplyOutfit(_selection), true);
                _status = "Waiting for server validation...";
            }
            catch (Exception ex)
            {
                _status = "Could not send outfit: " + ex.Message;
                Logger.LogWarning("[CustomGameSkins] Outfit request failed: " + ex.Message);
            }
        }

        internal void HandleAccepted()
        {
            ServerAuthorized = true;
            _status = "Custom server authorized every built-in skin. Press " + _menuKey.Value + ".";
            ShowNotice("Custom-game All Skins authorized - press " + _menuKey.Value + " in game.");
            Logger.LogInfo("[CustomGameSkins] Compatible custom server authorized the wardrobe.");
        }

        internal void HandleApplied(int[] gear)
        {
            if (gear == null || gear.Length != CustomGameSkinsProtocol.GearValueCount)
                return;

            try
            {
                var player = Player.localPlayer;
                var gearHandler = player == null ? null : player.m_characterGearHandler;
                var networkHandler = PhotonServerHandler.instance;
                if (gearHandler == null || networkHandler == null || networkHandler.LocalPlayer == null)
                {
                    _status = "Server accepted the outfit, but your local player was not ready.";
                    return;
                }

                _selection = (int[])gear.Clone();
                networkHandler.LocalPlayer.AssignGearData((int[])gear.Clone());
                gearHandler.RemoveAllGear();
                gearHandler.AttachGear(gear);
                _status = "Outfit applied for this custom-server session.";
                ShowNotice("Custom-game outfit applied.");
            }
            catch (Exception ex)
            {
                _status = "Server accepted the outfit, but local rendering failed: " + ex.Message;
                Logger.LogError("[CustomGameSkins] Local outfit apply failed: " + ex);
            }
        }

        internal void HandleDenied(byte reason)
        {
            _status = "Server denied the outfit: " + DeniedReason(reason) + ".";
            ShowNotice(_status);
        }

        internal void ResetAuthorization()
        {
            ServerAuthorized = false;
            _status = "Waiting for a compatible custom server.";
            _skins = null;
            if (_showMenu)
                CloseMenu();
        }

        private void ShowNotice(string message)
        {
            _notice = message;
            _noticeUntil = Time.unscaledTime + 5f;
        }

        private static bool HasLocalPlayer()
        {
            return Player.localPlayer != null &&
                Player.localPlayer.m_characterGearHandler != null &&
                PhotonServerHandler.instance != null &&
                PhotonServerHandler.instance.LocalPlayer != null;
        }

        private string SelectedSkinName()
        {
            var itemIndex = _selection[SlotOffset(_selectedSlot)];
            if (itemIndex < 0)
                return "None";
            var skin = (_skins ?? new List<SkinChoice>()).FirstOrDefault(entry => entry.Index == itemIndex);
            return skin == null ? "Skin " + itemIndex : skin.Name;
        }

        private static int SlotOffset(Gear.GearType slot) => ((int)slot) * 2;

        private static Gear.GearType ItemTypeForSlot(Gear.GearType slot)
        {
            if (slot == Gear.GearType.ARMOR)
                return Gear.GearType.TORSO;
            if (slot == Gear.GearType.HELMET)
                return Gear.GearType.HEAD;
            return slot;
        }

        private static string SlotLabel(Gear.GearType slot)
        {
            switch (slot)
            {
                case Gear.GearType.HEAD:
                    return "Head 1";
                case Gear.GearType.TORSO:
                    return "Torso 1";
                case Gear.GearType.LEGS:
                    return "Legs";
                case Gear.GearType.FEET:
                    return "Feet";
                case Gear.GearType.ARMOR:
                    return "Torso 2";
                case Gear.GearType.HELMET:
                    return "Head 2";
                default:
                    return slot.ToString();
            }
        }

        private static string DeniedReason(byte reason)
        {
            switch (reason)
            {
                case CustomGameSkinsProtocol.DeniedDisabled:
                    return "the server owner disabled All Skins";
                case CustomGameSkinsProtocol.DeniedNotAuthorized:
                    return "the compatibility handshake was not accepted";
                case CustomGameSkinsProtocol.DeniedPlayerNotReady:
                    return "your server player is not ready";
                case CustomGameSkinsProtocol.DeniedInvalidOutfit:
                    return "one or more skin or color IDs are invalid";
                case CustomGameSkinsProtocol.DeniedRateLimited:
                    return "changes are being sent too quickly";
                default:
                    return "unknown reason " + reason;
            }
        }

        private static int[] CreateEmptyOutfit()
        {
            var result = new int[CustomGameSkinsProtocol.GearValueCount];
            for (var i = 0; i < result.Length; i++)
                result[i] = -1;
            return result;
        }

        private void OnDestroy()
        {
            if (_showMenu)
                CloseMenu();
            _harmony?.UnpatchSelf();
            ResetAuthorization();
            Instance = null;
        }

        private sealed class SkinChoice
        {
            internal readonly int Index;
            internal readonly string ItemId;
            internal readonly string Name;
            internal readonly Gear.GearType Type;
            internal readonly string SearchText;

            internal SkinChoice(GearDataEntry entry)
            {
                Index = entry.m_gear.Index;
                ItemId = entry.itemID;
                Name = entry.m_gear.DisplayName;
                Type = entry.m_gear.GearT;
                SearchText = Name + " " + ItemId + " " + Index;
            }
        }
    }

    [HarmonyPatch(typeof(ServerConnector), "SendInitRequest")]
    internal static class CustomGameSkinsHelloPatch
    {
        private static void Prefix(ServerConnector __instance)
        {
            CustomGameSkinsClientPlugin.Instance?.ResetAuthorization();
            __instance.SendMessageToServer((EventCode)CustomGameSkinsProtocol.EventCode,
                CustomGameSkinsProtocol.CreateHello(), true);
        }
    }

    [HarmonyPatch(typeof(ServerConnector), "OnEvent")]
    internal static class CustomGameSkinsReceivePatch
    {
        private static bool Prefix(ClientPackage clientPackage)
        {
            if ((byte)clientPackage.Code != CustomGameSkinsProtocol.EventCode)
                return true;

            byte operation;
            int[] gear;
            byte reason;
            if (!CustomGameSkinsProtocol.TryRead(clientPackage.Buffer, out operation, out gear, out reason))
                return false;

            var plugin = CustomGameSkinsClientPlugin.Instance;
            if (operation == CustomGameSkinsProtocol.Accepted)
                plugin?.HandleAccepted();
            else if (operation == CustomGameSkinsProtocol.OutfitApplied)
                plugin?.HandleApplied(gear);
            else if (operation == CustomGameSkinsProtocol.Denied)
                plugin?.HandleDenied(reason);
            return false;
        }
    }

    [HarmonyPatch(typeof(ServerConnector), "OnMainMenu")]
    internal static class CustomGameSkinsDisconnectPatch
    {
        private static void Prefix()
        {
            CustomGameSkinsClientPlugin.Instance?.ResetAuthorization();
        }
    }
}
