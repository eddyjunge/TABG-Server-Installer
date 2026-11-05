using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Landfall.Network;
using UnityEngine;

[assembly: InternalsVisibleTo("ClientMods.Tests")]

namespace TabgInstaller.AdminRadar.Client
{
    /// <summary>
    /// The bot radar: shows the server's bots (never real players) and what they are doing, when
    /// the host turned <c>game.bot_radar</c> on. Event 241, <c>ARDR</c> v2: the client says hello
    /// when it joins, the server answers accepted/denied and sends snapshots four times a second.
    /// </summary>
    [BepInPlugin("tabginstaller.adminradar.client", "Bot Radar", "2.0.0")]
    [BepInDependency("tabginstaller.modsettings", BepInDependency.DependencyFlags.SoftDependency)]
    public class AdminRadarClientPlugin : BaseUnityPlugin
    {
        internal static AdminRadarClientPlugin Instance;

        private readonly Dictionary<byte, Bot> _bots = new Dictionary<byte, Bot>();
        private string _status = "waiting for the server";
        private float _lastSnapshot = -100f;

        private ConfigEntry<KeyCode> _toggleKey;
        private ConfigEntry<bool> _visible;
        private ConfigEntry<float> _range;
        private ConfigEntry<int> _size;
        private ConfigEntry<bool> _showNames;
        private ConfigEntry<bool> _worldLabels;
        private ConfigEntry<float> _labelDistance;

        private Harmony _harmony;
        private Texture2D _white;
        private GUIStyle _title;
        private GUIStyle _small;
        private GUIStyle _label;

        private void Awake()
        {
            Instance = this;
            _toggleKey = Config.Bind("Radar", "ToggleKey", KeyCode.F4, "Show or hide the bot radar.");
            _visible = Config.Bind("Radar", "Visible", true, "Show the bot radar.");
            _range = Config.Bind("Radar", "RangeMeters", 350f, "World range the radar covers.");
            _size = Config.Bind("Radar", "SizePixels", 220, "Radar size in pixels.");
            _showNames = Config.Bind("Radar", "ShowNames", true, "Bot names next to the radar dots.");
            _worldLabels = Config.Bind("Labels", "Show", true, "Labels over bots in the world (state, health, gun).");
            _labelDistance = Config.Bind("Labels", "MaxDistanceMeters", 400f, "Only label bots this close.");
            try { RegisterSettings(); } catch (Exception) { /* ModSettings isn't installed */ }

            _harmony = new Harmony("tabginstaller.adminradar.client");
            _harmony.PatchAll(typeof(AdminRadarClientPlugin).Assembly);
            Logger.LogInfo("[BotRadar] Loaded; the server sends bots if the host turned the radar on.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void RegisterSettings()
        {
            ModSettings.ModSettingsUI.Register("Bot Radar", "Toggle Key", "Show or hide the radar", _toggleKey);
            ModSettings.ModSettingsUI.Register("Bot Radar", "Visible", "Show the radar", _visible);
            ModSettings.ModSettingsUI.Register("Bot Radar", "Range", "Range in metres", _range);
            ModSettings.ModSettingsUI.Register("Bot Radar", "Size", "Size in pixels", _size);
            ModSettings.ModSettingsUI.Register("Bot Radar", "Show Names", "Names next to dots", _showNames);
            ModSettings.ModSettingsUI.Register("Bot Radar", "World Labels", "Labels over bots", _worldLabels);
            ModSettings.ModSettingsUI.Register("Bot Radar", "Label Distance", "Label bots this close", _labelDistance);
        }

        internal void Reset()
        {
            _bots.Clear();
            _status = "waiting for the server";
            _lastSnapshot = -100f;
        }

        internal void Handle(byte[] data)
        {
            RadarMessage message;
            if (!RadarProtocol.TryRead(data, out message))
                return;
            switch (message.Op)
            {
                case RadarProtocol.Accepted:
                    _status = "on";
                    break;
                case RadarProtocol.Denied:
                    _status = message.Reason == RadarProtocol.DeniedNotAdmin ? "admins only" : "off on this server";
                    break;
                case RadarProtocol.Snapshot:
                    _status = "on";
                    _lastSnapshot = Time.unscaledTime;
                    _bots.Clear();
                    foreach (Bot bot in message.Bots)
                        _bots[bot.Index] = bot;
                    break;
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(_toggleKey.Value))
                _visible.Value = !_visible.Value;
            // The server stopped sending (radar turned off, match over): forget stale dots.
            if (_bots.Count > 0 && Time.unscaledTime - _lastSnapshot > 3f)
                _bots.Clear();
        }

        private void OnGUI()
        {
            if (!_visible.Value || Player.localPlayer == null || _status == "waiting for the server")
                return;
            InitGui();
            if (_status != "on")
            {
                GUI.Label(new Rect(Screen.width - 260f, 110f, 250f, 20f), "Bot radar: " + _status, _small);
                return;
            }
            if (_worldLabels.Value)
                DrawLabels();
            DrawRadar();
        }

        private static Vector3 LocalPosition()
        {
            var p = Player.localPlayer;
            return p.m_hip != null ? p.m_hip.transform.position : p.transform.position;
        }

        private static Color StateColor(Bot bot)
        {
            if (!bot.Alive) return new Color(0.5f, 0.5f, 0.5f, 0.8f);
            switch (bot.State)
            {
                case BotState.Fighting: return new Color(1f, 0.25f, 0.2f);
                case BotState.Plane:
                case BotState.Falling: return new Color(0.4f, 0.8f, 1f);
                case BotState.Swimming: return new Color(0.3f, 0.5f, 1f);
                default: return new Color(1f, 0.85f, 0.1f);
            }
        }

        private void DrawRadar()
        {
            float size = Mathf.Clamp(_size.Value, 120, 420);
            // Below the game's alive count and ring progress.
            float x = Screen.width - size - 18f, y = 110f, half = size * 0.5f;
            float range = Mathf.Max(50f, _range.Value);
            Vector3 me = LocalPosition();
            Vector3 forward = Player.localPlayer.transform.forward;
            float yaw = Mathf.Atan2(forward.x, forward.z);
            float cos = Mathf.Cos(-yaw), sin = Mathf.Sin(-yaw);

            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(x, y, size, size), _white);
            GUI.color = Color.white;
            GUI.Label(new Rect(x + 8f, y + 4f, size - 16f, 20f), "BOT RADAR  " + CountAlive() + " alive", _title);
            GUI.color = new Color(0.2f, 0.85f, 1f);
            GUI.DrawTexture(new Rect(x + half - 3f, y + half - 3f, 6f, 6f), _white);

            foreach (Bot bot in _bots.Values)
            {
                if (!bot.Alive) continue;
                Vector2 dot = ToRadar(bot.Position - me, cos, sin, range, half);
                if (bot.HasGoal)
                {
                    Vector2 goal = ToRadar(bot.Goal - me, cos, sin, range, half);
                    GUI.color = new Color(0.3f, 0.9f, 1f, 0.6f);
                    GUI.DrawTexture(new Rect(x + goal.x - 2f, y + goal.y - 2f, 4f, 4f), _white);
                }
                GUI.color = StateColor(bot);
                GUI.DrawTexture(new Rect(x + dot.x - 4f, y + dot.y - 4f, 8f, 8f), _white);
                if (_showNames.Value)
                {
                    GUI.color = Color.white;
                    GUI.Label(new Rect(x + dot.x + 6f, y + dot.y - 10f, 120f, 20f), bot.Name, _small);
                }
            }
            GUI.color = old;
        }

        private static Vector2 ToRadar(Vector3 offset, float cos, float sin, float range, float half)
        {
            float rx = offset.x * cos - offset.z * sin;
            float rz = offset.x * sin + offset.z * cos;
            return new Vector2(half + Mathf.Clamp(rx / range, -1f, 1f) * half, half + Mathf.Clamp(-rz / range, -1f, 1f) * half);
        }

        private int CountAlive()
        {
            int n = 0;
            foreach (Bot bot in _bots.Values)
                if (bot.Alive) n++;
            return n;
        }

        private void DrawLabels()
        {
            Camera camera = Camera.main;
            if (camera == null) return;
            Vector3 me = LocalPosition();
            Color old = GUI.color;
            foreach (Bot bot in _bots.Values)
            {
                if (!bot.Alive) continue;
                float distance = Vector3.Distance(me, bot.Position);
                if (distance > _labelDistance.Value) continue;
                Vector3 screen = camera.WorldToScreenPoint(bot.Position + Vector3.up * 2.2f);
                if (screen.z < 0f) continue;
                var at = new Vector2(screen.x, Screen.height - screen.y);
                string target = bot.Target == 255 ? "" : (_bots.ContainsKey(bot.Target) ? " → " + _bots[bot.Target].Name : " → player " + bot.Target);
                GUI.color = StateColor(bot);
                GUI.Label(new Rect(at.x - 120f, at.y - 36f, 240f, 40f),
                    bot.Name + "  " + distance.ToString("0") + " m\n" + bot.State + "  " + bot.Health.ToString("0") + " HP" + target,
                    _label);
            }
            GUI.color = old;
        }

        private void InitGui()
        {
            if (_white != null) return;
            _white = new Texture2D(1, 1);
            _white.SetPixel(0, 0, Color.white);
            _white.Apply();
            _title = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold };
            _small = new GUIStyle(GUI.skin.label) { fontSize = 11, fontStyle = FontStyle.Bold };
            _label = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.LowerCenter };
        }
    }

    internal enum BotState : byte
    {
        Plane = 0,
        Falling = 1,
        Walking = 2,
        Fighting = 3,
        Swimming = 4,
    }

    internal struct Bot
    {
        public byte Index;
        public byte Group;
        public BotState State;
        public bool Alive;
        public bool HasGoal;
        public float Health;
        public Vector3 Position;
        public Vector3 Goal;
        public byte Target;
        public int Gun;
        public string Name;
    }

    internal struct RadarMessage
    {
        public byte Op;
        public byte Reason;
        public List<Bot> Bots;
    }

    internal static class RadarProtocol
    {
        public const byte EventCode = 241;
        public const byte Version = 2;
        public const byte Hello = 1;
        public const byte Accepted = 2;
        public const byte Snapshot = 3;
        public const byte Denied = 4;
        public const byte DeniedNotAdmin = 2;

        public static byte[] CreateHello()
        {
            return new byte[] { (byte)'A', (byte)'R', (byte)'D', (byte)'R', Version, Hello };
        }

        public static bool TryRead(byte[] data, out RadarMessage message)
        {
            message = new RadarMessage { Bots = new List<Bot>() };
            if (data == null || data.Length < 6 || data[0] != 'A' || data[1] != 'R' || data[2] != 'D' || data[3] != 'R' || data[4] != Version)
                return false;
            message.Op = data[5];
            int at = 6;
            try
            {
                if (message.Op == Denied)
                {
                    message.Reason = data[at];
                    return true;
                }
                if (message.Op != Snapshot)
                    return message.Op == Accepted;
                int count = data[at++];
                for (int i = 0; i < count; i++)
                {
                    var bot = new Bot();
                    bot.Index = data[at++];
                    bot.Group = data[at++];
                    bot.State = (BotState)data[at++];
                    byte flags = data[at++];
                    bot.Alive = (flags & 1) != 0;
                    bot.HasGoal = (flags & 2) != 0;
                    bot.Health = BitConverter.ToSingle(data, at); at += 4;
                    bot.Position = ReadVector(data, ref at);
                    bot.Goal = ReadVector(data, ref at);
                    bot.Target = data[at++];
                    bot.Gun = BitConverter.ToInt32(data, at); at += 4;
                    int length = BitConverter.ToUInt16(data, at); at += 2;
                    if (length > 64 || at + length > data.Length)
                        return false;
                    bot.Name = Encoding.UTF8.GetString(data, at, length); at += length;
                    message.Bots.Add(bot);
                }
                return at == data.Length;
            }
            catch (ArgumentException) { return false; }
            catch (IndexOutOfRangeException) { return false; }
        }

        private static Vector3 ReadVector(byte[] data, ref int at)
        {
            var v = new Vector3(BitConverter.ToSingle(data, at), BitConverter.ToSingle(data, at + 4), BitConverter.ToSingle(data, at + 8));
            at += 12;
            return v;
        }
    }

    [HarmonyPatch(typeof(ServerConnector), "SendInitRequest")]
    internal static class RadarHelloPatch
    {
        private static void Prefix(ServerConnector __instance)
        {
            AdminRadarClientPlugin.Instance?.Reset();
            __instance.SendMessageToServer((EventCode)RadarProtocol.EventCode, RadarProtocol.CreateHello(), true);
        }
    }

    [HarmonyPatch(typeof(ServerConnector), "OnEvent")]
    internal static class RadarReceivePatch
    {
        private static bool Prefix(ClientPackage clientPackage)
        {
            if ((byte)clientPackage.Code != RadarProtocol.EventCode)
                return true;
            AdminRadarClientPlugin.Instance?.Handle(clientPackage.Buffer);
            return false;
        }
    }
}
