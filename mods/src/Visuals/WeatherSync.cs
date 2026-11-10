using System.Text;
using HarmonyLib;
using Landfall.Network;

namespace TabgInstaller.Visuals
{
    /// <summary>
    /// The server's weather (our event 247, "WTHR" v1). On joining the mod says hello; a tabg server
    /// answers with the weather an admin picked with /weather, and sends it again whenever it changes.
    /// Servers that don't know the event ignore the hello, and the weather keeps following the biome.
    /// </summary>
    internal static class WeatherSync
    {
        internal const byte EventCode = 247;
        private static readonly byte[] Magic = { (byte)'W', (byte)'T', (byte)'H', (byte)'R' };
        private const byte Version = 1;
        private const byte Hello = 1;
        private const byte Set = 2;

        internal static void Joining(ServerConnector connector)
        {
            // A new server: its weather, not the last one's.
            Weather.ServerForce = null;
            var hello = new byte[6];
            Magic.CopyTo(hello, 0);
            hello[4] = Version;
            hello[5] = Hello;
            connector.SendMessageToServer((global::EventCode)EventCode, hello, true);
        }

        internal static void Handle(byte[] data)
        {
            if (data == null || data.Length < 7 || data[4] != Version || data[5] != Set) return;
            for (int i = 0; i < 4; i++)
                if (data[i] != Magic[i]) return;
            int length = data[6];
            if (data.Length < 7 + length) return;
            string name = Encoding.ASCII.GetString(data, 7, length);
            Weather.ServerForce = name == "Auto" ? null : name;
            UnityEngine.Debug.Log($"[Visuals] server weather: {name}");
        }
    }

    [HarmonyPatch(typeof(ServerConnector), "SendInitRequest")]
    internal static class WeatherHelloPatch
    {
        private static void Prefix(ServerConnector __instance) => WeatherSync.Joining(__instance);
    }

    [HarmonyPatch(typeof(ServerConnector), "OnEvent")]
    internal static class WeatherReceivePatch
    {
        private static bool Prefix(ClientPackage clientPackage)
        {
            if ((byte)clientPackage.Code != WeatherSync.EventCode) return true;
            WeatherSync.Handle(clientPackage.Buffer);
            return false;
        }
    }
}
