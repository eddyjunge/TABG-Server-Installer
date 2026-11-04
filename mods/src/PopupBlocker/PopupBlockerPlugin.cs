using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Landfall;
using TabgInstaller.ModSettings;
using UnityEngine;

namespace TabgInstaller.PopupBlocker
{
    [BepInPlugin("tabginstaller.popupblocker", "TABG Popup Blocker", "1.0.0")]
    [BepInDependency("tabginstaller.modsettings", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class PopupBlockerPlugin : BaseUnityPlugin
    {
        internal static ConfigEntry<bool> BlockAntiCheatPopups;
        internal static ConfigEntry<bool> SkipAntiCheatSessionWhenUnavailable;
        internal static ConfigEntry<bool> LogBlockedMessages;

        private static Harmony _harmony;

        private void Awake()
        {
            BlockAntiCheatPopups = Config.Bind("Popups", "BlockAntiCheatPopups", true, "Suppress anti-cheat boot/fail message boxes in the modded client.");
            SkipAntiCheatSessionWhenUnavailable = Config.Bind("AntiCheat", "SkipSessionWhenUnavailable", true, "Avoid null anti-cheat session calls when the modded client starts without EAC.");
            LogBlockedMessages = Config.Bind("Diagnostics", "LogBlockedMessages", false, "Write blocked popup messages to the BepInEx log.");

            RegisterSettings();

            _harmony = new Harmony("tabginstaller.popupblocker");
            _harmony.PatchAll(typeof(PopupBlockerPlugin).Assembly);

            Logger.LogInfo("[PopupBlocker] Loaded.");
        }

        internal static bool ShouldBlockMessage(string message)
        {
            if (!BlockAntiCheatPopups.Value || string.IsNullOrWhiteSpace(message))
                return false;

            return message.IndexOf("Anti Cheat", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("anti-cheat", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("Easy Anti-Cheat", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("EAC", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void RegisterSettings()
        {
            try
            {
                ModSettingsUI.Register("Popup Blocker", "Block Anti-Cheat Popups", "Hide anti-cheat message boxes in the modded client", BlockAntiCheatPopups);
                ModSettingsUI.Register("Popup Blocker", "Skip Missing EAC Session", "Avoid anti-cheat calls when EAC is unavailable", SkipAntiCheatSessionWhenUnavailable);
                ModSettingsUI.Register("Popup Blocker", "Log Blocked Messages", "Write blocked popup text to the BepInEx log", LogBlockedMessages);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[PopupBlocker] ModSettings registration failed: " + ex.Message);
            }
        }
    }

    [HarmonyPatch(typeof(UIMessageBox), "QueueMessage", new[] { typeof(string), typeof(Action) })]
    internal static class UiMessageBoxPatch
    {
        private static bool Prefix(string message)
        {
            if (!PopupBlockerPlugin.ShouldBlockMessage(message))
                return true;

            if (PopupBlockerPlugin.LogBlockedMessages.Value)
                Debug.Log("[PopupBlocker] Blocked popup: " + message);

            return false;
        }
    }

    [HarmonyPatch]
    internal static class EasyAcSetInterfacePatch
    {
        private static readonly FieldInfo InterfaceField = AccessTools.Field(typeof(Easy_AC_Client), "m_Interface");

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Easy_AC_Client), "SetACInterface");
        }

        private static bool Prefix(object ACInterface)
        {
            if (ACInterface != null || !PopupBlockerPlugin.BlockAntiCheatPopups.Value)
                return true;

            InterfaceField?.SetValue(null, null);

            if (PopupBlockerPlugin.LogBlockedMessages.Value)
                Debug.Log("[PopupBlocker] Suppressed anti-cheat boot popup because EAC interface is unavailable.");

            return false;
        }
    }

    [HarmonyPatch(typeof(Easy_AC_Client), "BeginSession")]
    internal static class EasyAcBeginSessionPatch
    {
        private static readonly FieldInfo InterfaceField = AccessTools.Field(typeof(Easy_AC_Client), "m_Interface");

        private static bool Prefix()
        {
            if (!PopupBlockerPlugin.SkipAntiCheatSessionWhenUnavailable.Value)
                return true;

            if (InterfaceField?.GetValue(null) != null)
                return true;

            if (PopupBlockerPlugin.LogBlockedMessages.Value)
                Debug.Log("[PopupBlocker] Skipped anti-cheat BeginSession because EAC interface is unavailable.");

            return false;
        }
    }

    [HarmonyPatch(typeof(Easy_AC_Client), "HandleACMessage")]
    internal static class EasyAcHandleMessagePatch
    {
        private static readonly FieldInfo InterfaceField = AccessTools.Field(typeof(Easy_AC_Client), "m_Interface");

        private static bool Prefix()
        {
            return InterfaceField?.GetValue(null) != null;
        }
    }
}
