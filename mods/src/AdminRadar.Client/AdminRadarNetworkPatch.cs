using System;
using HarmonyLib;
using Landfall.Network;

namespace TabgInstaller.AdminRadar.Client
{
    internal static class AdminRadarNetworkPatch
    {
        private static bool _patched;

        public static void Apply(Harmony harmony)
        {
            if (_patched) return;

            bool patchedAny = false;
            if (TryPatchNetwork(harmony, "ServerConnector", "OnEvent")) patchedAny = true;
            else if (TryPatchNetwork(harmony, "NetworkPlayer", "OnEvent")) patchedAny = true;
            else if (TryPatchNetwork(harmony, "PhotonEventHandler", "OnEvent")) patchedAny = true;
            else if (TryPatchNetwork(harmony, "GameNetworkManager", "HandleEvent")) patchedAny = true;
            else if (TryPatchNetwork(harmony, "ServerClient", "HandleEvent")) patchedAny = true;

            if (TryPatchAllDropHandler(harmony, "Landfall.Network.PhotonServerHandler")) patchedAny = true;
            else if (TryPatchAllDropHandler(harmony, "PhotonServerHandler")) patchedAny = true;

            _patched = patchedAny;
            if (!patchedAny)
                AdminRadarClientPlugin.Log?.LogWarning("[AdminRadar.Client] Could not find a network event handler to patch.");
        }

        private static bool TryPatchNetwork(Harmony harmony, string typeName, string methodName)
        {
            try
            {
                var method = AccessTools.Method(typeName + ":" + methodName);
                if (method == null) return false;

                var prefix = typeof(AdminRadarNetworkPatch).GetMethod(
                    nameof(NetworkEventPrefix),
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

                harmony.Patch(method, prefix: new HarmonyMethod(prefix));
                AdminRadarClientPlugin.Log?.LogInfo("[AdminRadar.Client] Patched network handler: " + typeName + "." + methodName);
                return true;
            }
            catch (Exception ex)
            {
                AdminRadarClientPlugin.Log?.LogDebug("[AdminRadar.Client] Patch failed for " + typeName + "." + methodName + ": " + ex.Message);
                return false;
            }
        }

        private static bool TryPatchAllDropHandler(Harmony harmony, string handlerTypeName)
        {
            bool patchedAny = false;
            try
            {
                var allDrop = AccessTools.Method(handlerTypeName + ":HandleAllPlayersAirplaneDrop");
                var playerDrop = AccessTools.Method(handlerTypeName + ":HandlePlayerAirplaneDrop");
                var prefix = typeof(AdminRadarNetworkPatch).GetMethod(
                    nameof(AirplaneDropHandlerPrefix),
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

                if (allDrop != null)
                {
                    harmony.Patch(allDrop, prefix: new HarmonyMethod(prefix));
                    AdminRadarClientPlugin.Log?.LogInfo("[AdminRadar.Client] Patched PhotonServerHandler.HandleAllPlayersAirplaneDrop.");
                    patchedAny = true;
                }

                if (playerDrop != null)
                {
                    harmony.Patch(playerDrop, prefix: new HarmonyMethod(prefix));
                    AdminRadarClientPlugin.Log?.LogInfo("[AdminRadar.Client] Patched PhotonServerHandler.HandlePlayerAirplaneDrop.");
                    patchedAny = true;
                }
            }
            catch (Exception ex)
            {
                AdminRadarClientPlugin.Log?.LogDebug("[AdminRadar.Client] Direct airplane-drop patch failed for " + handlerTypeName + ": " + ex.Message);
            }

            return patchedAny;
        }

        private static void AirplaneDropHandlerPrefix()
        {
            AdminRadarClientPlugin.MarkDummyPlayersDroppedBeforeAllDrop();
        }

        private static bool NetworkEventPrefix(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length == 0) return true;

                byte eventCode;
                if (!TryGetEventCode(__args, out eventCode))
                    return true;

                if (eventCode == (byte)EventCode.AllDrop)
                {
                    AdminRadarClientPlugin.MarkDummyPlayersDroppedBeforeAllDrop();
                    return true;
                }

                if (eventCode != AdminRadarClientPlugin.RadarEventCode)
                    return true;

                var data = ExtractData(__args);
                if (data != null)
                    AdminRadarClientPlugin.HandleRadarPayload(data);
            }
            catch (Exception ex)
            {
                AdminRadarClientPlugin.Log?.LogDebug("[AdminRadar.Client] Network event parse failed: " + ex.Message);
            }

            return true;
        }

        private static bool TryGetEventCode(object[] args, out byte eventCode)
        {
            eventCode = 0;
            if (args == null || args.Length == 0) return false;

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg == null) continue;

                if (TryConvertCode(arg, out eventCode))
                    return true;

                try
                {
                    var codeField = arg.GetType().GetField("Code");
                    if (codeField != null && TryConvertCode(codeField.GetValue(arg), out eventCode))
                        return true;

                    var codeProp = arg.GetType().GetProperty("Code");
                    if (codeProp != null && TryConvertCode(codeProp.GetValue(arg, null), out eventCode))
                        return true;
                }
                catch { }
            }

            return false;
        }

        private static bool TryConvertCode(object value, out byte eventCode)
        {
            eventCode = 0;
            if (value == null) return false;

            if (value is byte b)
            {
                eventCode = b;
                return true;
            }

            if (value is int i && i >= 0 && i <= 255)
            {
                eventCode = (byte)i;
                return true;
            }

            if (value is short s && s >= 0 && s <= 255)
            {
                eventCode = (byte)s;
                return true;
            }

            var type = value.GetType();
            if (type.IsEnum)
            {
                try
                {
                    int converted = Convert.ToInt32(value);
                    if (converted >= 0 && converted <= 255)
                    {
                        eventCode = (byte)converted;
                        return true;
                    }
                }
                catch { }
            }

            return false;
        }

        private static byte[] ExtractData(object[] args)
        {
            if (args.Length > 1 && args[1] is byte[] bytes)
                return bytes;

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg == null) continue;

                if (arg is byte[] direct)
                    return direct;

                try
                {
                    var bufferField = arg.GetType().GetField("Buffer");
                    if (bufferField?.GetValue(arg) is byte[] fieldBuffer)
                        return fieldBuffer;

                    var bufferProp = arg.GetType().GetProperty("Buffer");
                    if (bufferProp?.GetValue(arg, null) is byte[] buffer)
                        return buffer;
                }
                catch { }
            }

            return null;
        }
    }
}
