using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Landfall.Network;
using UnityEngine;

namespace TabgInstaller.AdminRadar.Client
{
    [BepInPlugin("tabginstaller.adminradar.client", "Dummy Debug Radar Client", "1.6.0")]
    public class AdminRadarClientPlugin : BaseUnityPlugin
    {
        internal const byte RadarEventCode = 241;
        private const uint RadarPayloadMagic = 0x52445241; // "ARDR", little-endian
        private const byte RadarPayloadVersion = 1;
        private const byte PlayerSectionType = 1;
        private const byte BotDebugSectionType = 2;
        private const int MaxSerializedStringBytes = 512;
        private const int MaxLegacyStringBytes = 4096;

        internal static AdminRadarClientPlugin Instance;
        internal static BepInEx.Logging.ManualLogSource Log;

        private static readonly Dictionary<byte, RadarPlayer> Players = new Dictionary<byte, RadarPlayer>();
        private static readonly string[] LocalPlayerIndexMembers =
        {
            "PlayerIndex",
            "NetworkIndex",
            "m_playerIndex",
            "playerIndex",
            "m_networkIndex",
            "networkIndex"
        };

        private static Type _localPlayerType;
        private static MemberInfo _localPlayerIndexMember;
        private static bool _localPlayerIndexResolved;
        private static MethodInfo _respawnPlayerMethod;
        private static bool _respawnPlayerMethodResolved;
        private static int _serverPayloadCount;
        private static int _serverPlayerCount;
        private static int _dummySnapLogCount;
        private static int _dummyRespawnLogCount;

        private ConfigEntry<KeyCode> _toggleKey;
        private ConfigEntry<bool> _visible;
        private ConfigEntry<float> _radarRange;
        private ConfigEntry<int> _radarSize;
        private ConfigEntry<bool> _showNames;
        private ConfigEntry<bool> _showWorldMarkers;
        private ConfigEntry<bool> _showOnlyDummies;
        private ConfigEntry<float> _markerMaxDistance;
        private ConfigEntry<bool> _syncDummyClientBodies;
        private ConfigEntry<bool> _respawnDummyClientBodies;
        private ConfigEntry<bool> _markDummiesDroppedBeforeAllDrop;
        private ConfigEntry<bool> _allowLocalClientPlayerFallback;

        private Harmony _harmony;
        private GUIStyle _labelStyle;
        private GUIStyle _smallStyle;
        private GUIStyle _markerStyle;
        private Texture2D _bgTexture;
        private Texture2D _selfTexture;
        private Texture2D _playerTexture;
        private Texture2D _dummyTexture;
        private Texture2D _goalTexture;
        private Texture2D _lootTexture;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            _toggleKey = Config.Bind("Radar", "ToggleKey", KeyCode.F6, "Key to show/hide the server-authorized radar.");
            _visible = Config.Bind("Radar", "Visible", true, "Show radar overlay.");
            _radarRange = Config.Bind("Radar", "RangeMeters", 350f, "World range covered by the radar.");
            _radarSize = Config.Bind("Radar", "SizePixels", 220, "Radar size in pixels.");
            _showNames = Config.Bind("Radar", "ShowNames", true, "Show player names next to radar markers.");
            _showWorldMarkers = Config.Bind("Dummy Highlighter", "ShowWorldMarkers", true, "Show screen-space labels over dummy players.");
            _showOnlyDummies = Config.Bind("Dummy Highlighter", "OnlyDummies", true, "Only draw world markers for AIPlayer dummy names.");
            _markerMaxDistance = Config.Bind("Dummy Highlighter", "MaxDistanceMeters", 2500f, "Maximum distance for dummy world markers.");
            _syncDummyClientBodies = Config.Bind("Dummy Debug", "SyncClientDummyBodies", false, "Compatibility/debug only: move local dummy bodies after large server radar jumps.");
            _respawnDummyClientBodies = Config.Bind("Dummy Debug", "RespawnClientDummyBodies", false, "Compatibility/debug only: rebuild missing local dummy bodies after large server radar jumps.");
            _markDummiesDroppedBeforeAllDrop = Config.Bind("Dummy Debug", "MarkDummiesDroppedBeforeAllDrop", false, "Compatibility/debug only: mutate dummy client drop state before vanilla AllDrop handling.");
            _allowLocalClientPlayerFallback = Config.Bind("Dummy Debug", "AllowLocalClientPlayerFallback", false, "Compatibility/debug only: add locally known players that the server did not broadcast. This can expose real player positions.");

            RegisterSettings();

            _harmony = new Harmony("tabginstaller.adminradar.client");
            AdminRadarNetworkPatch.Apply(_harmony);

            Logger.LogInfo("[AdminRadar.Client] Loaded. Waiting for server radar packets.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        private TABGPlayerClient _lastLocal;
        private float _nextRefresh;
        private void Update()
        {
            var local=PhotonServerHandler.instance?.LocalPlayer;
            if(local!=_lastLocal){Players.Clear();_serverPayloadCount=_serverPlayerCount=0;_lastLocal=local;}
            if (Input.GetKeyDown(_toggleKey.Value))
                _visible.Value = !_visible.Value;

            if(Time.unscaledTime>=_nextRefresh){_nextRefresh=Time.unscaledTime+.1f;RefreshClientPlayers();RemoveExpiredPlayers();}
        }

        private void OnGUI()
        {
            if (!_visible.Value) return;

            InitGui();
            DrawStatusLine();
            if (Players.Count == 0) return;
            if (_showWorldMarkers.Value)
                DrawWorldMarkers();
            DrawRadar();
        }

        internal static void HandleRadarPayload(byte[] data)
        {
            if (data == null || data.Length == 0) return;

            string error;
            List<ParsedPlayerEntry> playerEntries;
            List<ParsedDebugEntry> debugEntries;
            if (!TryParseRadarPayload(data, out playerEntries, out debugEntries, out error))
            {
                Log?.LogWarning("[AdminRadar.Client] Ignoring malformed radar payload: " + error);
                return;
            }

            for (int i = 0; i < playerEntries.Count; i++)
            {
                ParsedPlayerEntry entry = playerEntries[i];
                ApplyPlayerPosition(entry.Index, entry.Name, entry.Position, entry.Alive);
                var colored=Players[entry.Index];colored.Team=entry.Team;Players[entry.Index]=colored;
            }

            for (int i = 0; i < debugEntries.Count; i++)
            {
                ParsedDebugEntry entry = debugEntries[i];
                ApplyBotDebug(entry.Index, entry.State, entry.TargetName, entry.WeaponName, entry.HasLineOfSight, entry.IsFiring, entry.HasMoveGoal, entry.MoveGoal, entry.HasLootGoal, entry.LootGoal, entry.LootName);
            }

            _serverPayloadCount++;
            _serverPlayerCount = Players.Count;
            if (_serverPayloadCount == 1 || _serverPayloadCount % 20 == 0)
                Log?.LogInfo($"[AdminRadar.Client] Received radar payload #{_serverPayloadCount} with {playerEntries.Count} player(s).");
        }

        private static bool TryParseRadarPayload(byte[] data, out List<ParsedPlayerEntry> playerEntries, out List<ParsedDebugEntry> debugEntries, out string error)
        {
            if (IsVersionedPayload(data))
                return TryParseVersionedPayload(data, out playerEntries, out debugEntries, out error);

            return TryParseLegacyPayload(data, out playerEntries, out debugEntries, out error);
        }

        private static bool IsVersionedPayload(byte[] data)
        {
            return data.Length >= 4 &&
                data[0] == (byte)'A' &&
                data[1] == (byte)'R' &&
                data[2] == (byte)'D' &&
                data[3] == (byte)'R';
        }

        private static bool TryParseVersionedPayload(byte[] data, out List<ParsedPlayerEntry> playerEntries, out List<ParsedDebugEntry> debugEntries, out string error)
        {
            playerEntries = new List<ParsedPlayerEntry>();
            debugEntries = new List<ParsedDebugEntry>();
            var reader = new BoundedReader(data);

            uint magic;
            byte version;
            byte sectionCount;
            if (!reader.TryReadUInt32(out magic) || magic != RadarPayloadMagic)
            {
                error = "invalid radar payload magic";
                return false;
            }

            if (!reader.TryReadByte(out version) || version != RadarPayloadVersion)
            {
                error = "unsupported radar payload version";
                return false;
            }

            if (!reader.TryReadByte(out sectionCount))
            {
                error = "missing radar section count";
                return false;
            }

            for (int i = 0; i < sectionCount; i++)
            {
                byte sectionType;
                ushort sectionLength;
                byte[] sectionBytes;
                if (!reader.TryReadByte(out sectionType) ||
                    !reader.TryReadUInt16(out sectionLength) ||
                    !reader.TryReadBytes(sectionLength, out sectionBytes))
                {
                    error = "truncated radar section header or body";
                    return false;
                }

                var sectionReader = new BoundedReader(sectionBytes);
                switch (sectionType)
                {
                    case PlayerSectionType:
                        if (!TryReadPlayerSection(sectionReader, true, playerEntries, out error))
                            return false;
                        break;
                    case BotDebugSectionType:
                        if (!TryReadBotDebugSection(sectionReader, true, debugEntries, out error))
                            return false;
                        break;
                    case 3:
                        if(!sectionReader.TryReadByte(out byte count) || sectionReader.Remaining!=count*2){error="invalid team section";return false;}
                        for(int j=0;j<count;j++)
                        {
                            sectionReader.TryReadByte(out byte member);sectionReader.TryReadByte(out byte team);
                            int found=playerEntries.FindIndex(p=>p.Index==member);
                            if(found<0){error="team without player entry";return false;}
                            var entry=playerEntries[found];entry.Team=team;playerEntries[found]=entry;
                        }
                        break;
                    default:
                        break;
                }
            }

            if (reader.Remaining != 0)
            {
                error = "unexpected trailing radar payload bytes";
                return false;
            }

            error = null;
            return true;
        }

        private static bool TryParseLegacyPayload(byte[] data, out List<ParsedPlayerEntry> playerEntries, out List<ParsedDebugEntry> debugEntries, out string error)
        {
            playerEntries = new List<ParsedPlayerEntry>();
            debugEntries = new List<ParsedDebugEntry>();
            var reader = new BoundedReader(data);
            if (!TryReadPlayerSection(reader, false, playerEntries, out error))
                return false;

            if (reader.Remaining == 0)
                return true;

            byte marker;
            if (!reader.TryReadByte(out marker))
            {
                error = "truncated legacy debug marker";
                return false;
            }

            if (marker != 219)
            {
                error = "unknown legacy radar extension marker";
                return false;
            }

            return TryReadBotDebugSection(reader, false, debugEntries, out error);
        }

        private static bool TryReadPlayerSection(BoundedReader reader, bool versionedStrings, List<ParsedPlayerEntry> entries, out string error)
        {
            byte count;
            if (!reader.TryReadByte(out count))
            {
                error = "missing radar player count";
                return false;
            }

            for (int i = 0; i < count; i++)
            {
                byte index;
                string name;
                float x;
                float y;
                float z;
                bool alive;

                if (!reader.TryReadByte(out index) ||
                    !TryReadString(reader, versionedStrings, out name) ||
                    !reader.TryReadSingle(out x) ||
                    !reader.TryReadSingle(out y) ||
                    !reader.TryReadSingle(out z) ||
                    !reader.TryReadBool(out alive))
                {
                    error = "truncated radar player entry";
                    return false;
                }

                entries.Add(new ParsedPlayerEntry
                {
                    Index = index,
                    Name = name,
                    Position = new Vector3(x, y, z),
                    Alive = alive
                });
            }

            if (versionedStrings && reader.Remaining != 0)
            {
                error = "unexpected trailing radar player bytes";
                return false;
            }

            error = null;
            return true;
        }

        private static bool TryReadBotDebugSection(BoundedReader reader, bool versionedStrings, List<ParsedDebugEntry> entries, out string error)
        {
            byte debugCount;
            if (!reader.TryReadByte(out debugCount))
            {
                error = "missing radar debug count";
                return false;
            }

            for (int i = 0; i < debugCount; i++)
            {
                byte index;
                string state;
                string targetName;
                string weaponName;
                bool hasLineOfSight;
                bool isFiring;
                bool hasMoveGoal;
                float moveX;
                float moveY;
                float moveZ;
                bool hasLootGoal;
                float lootX;
                float lootY;
                float lootZ;
                string lootName;

                if (!reader.TryReadByte(out index) ||
                    !TryReadString(reader, versionedStrings, out state) ||
                    !TryReadString(reader, versionedStrings, out targetName) ||
                    !TryReadString(reader, versionedStrings, out weaponName) ||
                    !reader.TryReadBool(out hasLineOfSight) ||
                    !reader.TryReadBool(out isFiring) ||
                    !reader.TryReadBool(out hasMoveGoal) ||
                    !reader.TryReadSingle(out moveX) ||
                    !reader.TryReadSingle(out moveY) ||
                    !reader.TryReadSingle(out moveZ) ||
                    !reader.TryReadBool(out hasLootGoal) ||
                    !reader.TryReadSingle(out lootX) ||
                    !reader.TryReadSingle(out lootY) ||
                    !reader.TryReadSingle(out lootZ) ||
                    !TryReadString(reader, versionedStrings, out lootName))
                {
                    error = "truncated radar debug entry";
                    return false;
                }

                entries.Add(new ParsedDebugEntry
                {
                    Index = index,
                    State = state,
                    TargetName = targetName,
                    WeaponName = weaponName,
                    HasLineOfSight = hasLineOfSight,
                    IsFiring = isFiring,
                    HasMoveGoal = hasMoveGoal,
                    MoveGoal = new Vector3(moveX, moveY, moveZ),
                    HasLootGoal = hasLootGoal,
                    LootGoal = new Vector3(lootX, lootY, lootZ),
                    LootName = lootName
                });
            }

            if (versionedStrings && reader.Remaining != 0)
            {
                error = "unexpected trailing radar debug bytes";
                return false;
            }

            error = null;
            return true;
        }

        private static bool TryReadString(BoundedReader reader, bool versioned, out string value)
        {
            return versioned
                ? reader.TryReadString16(MaxSerializedStringBytes, out value)
                : reader.TryReadLegacyString(MaxLegacyStringBytes, out value);
        }

        private static void ApplyPlayerPosition(byte index, string name, Vector3 serverPosition, bool alive)
        {
            RadarPlayer previous;
            bool hadPrevious = Players.TryGetValue(index, out previous);
            bool largeServerJump = hadPrevious &&
                previous.LastServerSeen > 0f &&
                Vector3.Distance(previous.Position, serverPosition) > 45f;

            Players[index] = new RadarPlayer
            {
                Index = index,
                Name = string.IsNullOrWhiteSpace(name) ? "Player " + index : name,
                Position = serverPosition,
                Alive = alive,
                LastSeen = Time.unscaledTime,
                LastServerSeen = Time.unscaledTime,
                PreviousPosition = hadPrevious?VisualPosition(previous):serverPosition,
                BotState = hadPrevious ? previous.BotState : null,
                TargetName = hadPrevious ? previous.TargetName : null,
                WeaponName = hadPrevious ? previous.WeaponName : null,
                HasLineOfSight = hadPrevious && previous.HasLineOfSight,
                IsFiring = hadPrevious && previous.IsFiring,
                HasMoveGoal = hadPrevious && previous.HasMoveGoal,
                MoveGoal = hadPrevious ? previous.MoveGoal : Vector3.zero,
                HasLootGoal = hadPrevious && previous.HasLootGoal,
                LootGoal = hadPrevious ? previous.LootGoal : Vector3.zero,
                LootName = hadPrevious ? previous.LootName : null,
                LastDebugSeen = hadPrevious ? previous.LastDebugSeen : 0f
            };

            if (IsDummyName(name) && largeServerJump)
                ForceClientDummyPosition(index, name, serverPosition, true);
        }

        private static void ApplyBotDebug(
            byte index,
            string state,
            string targetName,
            string weaponName,
            bool hasLineOfSight,
            bool isFiring,
            bool hasMoveGoal,
            Vector3 moveGoal,
            bool hasLootGoal,
            Vector3 lootGoal,
            string lootName)
        {
            RadarPlayer player;
            if (!Players.TryGetValue(index, out player))
                return;

            player.BotState = state;
            player.TargetName = targetName;
            player.WeaponName = weaponName;
            player.HasLineOfSight = hasLineOfSight;
            player.IsFiring = isFiring;
            player.HasMoveGoal = hasMoveGoal;
            player.MoveGoal = moveGoal;
            player.HasLootGoal = hasLootGoal;
            player.LootGoal = lootGoal;
            player.LootName = lootName;
            player.LastDebugSeen = Time.unscaledTime;
            Players[index] = player;
        }

        internal static void MarkDummyPlayersDroppedBeforeAllDrop()
        {
            try
            {
                if (Instance == null || !Instance._markDummiesDroppedBeforeAllDrop.Value)
                    return;

                PhotonServerHandler handler = PhotonServerHandler.instance;
                if (handler == null || handler.Players == null)
                    return;

                int marked = 0;
                foreach (TABGPlayerClient player in handler.Players)
                {
                    if (player == null || !IsDummyName(player.PlayerName) || player.HasDropped)
                        continue;

                    player.Dropped();
                    if (player.PlayerObject != null)
                    {
                        Skydiving skydiving = player.PlayerObject.GetComponent<Skydiving>();
                        if (skydiving != null)
                            skydiving.enabled = false;
                    }

                    marked++;
                }

                if (marked > 0)
                    Log?.LogInfo($"[AdminRadar.Client] Marked {marked} dummy player(s) as dropped before vanilla AllDrop.");
            }
            catch (Exception ex)
            {
                Log?.LogDebug("[AdminRadar.Client] Could not pre-mark dummy AllDrop state: " + ex.Message);
            }
        }

        private static void RefreshClientPlayers()
        {
            try
            {
                PhotonServerHandler handler = PhotonServerHandler.instance;
                if (handler == null || handler.Players == null)
                    return;

                foreach (TABGPlayerClient player in handler.Players)
                {
                    if (player == null)
                        continue;

                    RadarPlayer existing;
                    bool hasExisting = Players.TryGetValue(player.PlayerIndex, out existing);
                    if (!hasExisting && (Instance == null || !Instance._allowLocalClientPlayerFallback.Value))
                        continue;

                    string playerName = string.IsNullOrWhiteSpace(player.PlayerName) ? "Player " + player.PlayerIndex : player.PlayerName;
                    if (hasExisting && existing.LastServerSeen>0)
                    {
                        // Keep server bot identity and authoritative positions.

                        // Alive and position both come from the authoritative server snapshot.
                        // Do not refresh server TTL from stale client objects.
                        Players[player.PlayerIndex] = existing;
                        continue;
                    }

                    Vector3 position = player.PlayerPosition;
                    if (player.PlayerHip != null)
                        position = player.PlayerHip.position;
                    else if (player.PlayerObject != null)
                        position = player.PlayerObject.transform.position;

                    Players[player.PlayerIndex] = new RadarPlayer
                    {
                        Index = player.PlayerIndex,
                        Name = playerName,
                        Position = position,
                        Alive = !player.IsDead,
                        LastSeen = Time.unscaledTime,
                        LastServerSeen = existing.LastServerSeen,
                        Team = existing.Team,
                        BotState = existing.BotState,
                        TargetName = existing.TargetName,
                        WeaponName = existing.WeaponName,
                        HasLineOfSight = existing.HasLineOfSight,
                        IsFiring = existing.IsFiring,
                        HasMoveGoal = existing.HasMoveGoal,
                        MoveGoal = existing.MoveGoal,
                        HasLootGoal = existing.HasLootGoal,
                        LootGoal = existing.LootGoal,
                        LootName = existing.LootName,
                        LastDebugSeen = existing.LastDebugSeen
                    };
                }
            }
            catch (Exception ex)
            {
                Log?.LogDebug("[AdminRadar.Client] Client player scan failed: " + ex.Message);
            }
        }

        private static void ForceClientDummyPosition(byte index, string name, Vector3 position, bool allowRespawn)
        {
            try
            {
                if (Instance == null || !Instance._syncDummyClientBodies.Value)
                    return;

                PhotonServerHandler handler = PhotonServerHandler.instance;
                if (handler == null || handler.Players == null)
                    return;

                foreach (TABGPlayerClient player in handler.Players)
                {
                    if (player == null || player.PlayerIndex != index)
                        continue;

                    if (!IsDummyName(player.PlayerName))
                    {
                        Log?.LogDebug($"[AdminRadar.Client] Ignored radar dummy sync for non-dummy client player {player.PlayerName} ({index}).");
                        return;
                    }

                    if (allowRespawn && Instance._respawnDummyClientBodies.Value)
                        TryRespawnClientDummy(handler, player, position);

                    ForceClientDummyPosition(player, position);
                    if (_dummySnapLogCount < 12)
                    {
                        _dummySnapLogCount++;
                        Log?.LogInfo($"[AdminRadar.Client] Snapped dummy {name} ({index}) to server position {position}.");
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                Log?.LogDebug("[AdminRadar.Client] Dummy client lookup failed: " + ex.Message);
            }
        }

        private static void ForceClientDummyPosition(TABGPlayerClient player, Vector3 position)
        {
            try
            {
                player.UpdatePosition(position);

                if (player.PlayerObject != null)
                    player.PlayerObject.transform.position = position;

                if (player.PlayerHip != null)
                    player.PlayerHip.position = position;
            }
            catch (Exception ex)
            {
                Log?.LogDebug("[AdminRadar.Client] Dummy client snap failed: " + ex.Message);
            }
        }

        private static void TryRespawnClientDummy(PhotonServerHandler handler, TABGPlayerClient player, Vector3 position)
        {
            try
            {
                if (handler == null || player == null)
                    return;

                bool needsRespawn = player.PlayerObject == null;
                if (!needsRespawn)
                    needsRespawn = Vector3.Distance(player.PlayerObject.transform.position, position) > 20f;
                if (!needsRespawn)
                    return;

                MethodInfo method = GetRespawnPlayerMethod(handler.GetType());
                if (method == null)
                    return;

                player.Respawn();
                float health = Mathf.Max(1f, player.Health);
                method.Invoke(handler, new object[] { player, health, position, player.PlayerIndex, 0f });

                if (_dummyRespawnLogCount < 16)
                {
                    _dummyRespawnLogCount++;
                    Log?.LogInfo($"[AdminRadar.Client] Rebuilt dummy {player.PlayerName} ({player.PlayerIndex}) body at server position {position}.");
                }
            }
            catch (Exception ex)
            {
                Log?.LogDebug("[AdminRadar.Client] Dummy client respawn failed: " + ex.Message);
            }
        }

        private static MethodInfo GetRespawnPlayerMethod(Type handlerType)
        {
            if (_respawnPlayerMethodResolved)
                return _respawnPlayerMethod;

            _respawnPlayerMethodResolved = true;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            _respawnPlayerMethod = handlerType.GetMethod(
                "RespawnPlayer",
                flags,
                null,
                new[] { typeof(TABGPlayerClient), typeof(float), typeof(Vector3), typeof(byte), typeof(float) },
                null);

            return _respawnPlayerMethod;
        }

        private void DrawRadar()
        {
            int size = Mathf.Clamp(_radarSize.Value, 120, 420);
            float x = Screen.width - size - 18f;
            float y = 58f;
            float center = size * 0.5f;
            float range = Mathf.Max(50f, _radarRange.Value);

            var oldColor = GUI.color;
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(x, y, size, size), _bgTexture);
            GUI.color = new Color(1f, 1f, 1f, 0.35f);
            GUI.Box(new Rect(x, y, size, size), GUIContent.none);

            GUI.color = Color.white;
            GUI.Label(new Rect(x + 8f, y + 6f, size - 16f, 22f), "ADMIN RADAR", _labelStyle);

            var localPosition = GetLocalPosition();
            var localForward = GetLocalForward();
            float yaw = Mathf.Atan2(localForward.x, localForward.z);

            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(x + center - 3f, y + center - 3f, 6f, 6f), _selfTexture);

            foreach (var player in Players.Values)
            {
                if (!player.Alive) continue;
                if (IsLocalPlayer(player)) continue;

                Vector3 offset = VisualPosition(player) - localPosition;
                float cos = Mathf.Cos(-yaw);
                float sin = Mathf.Sin(-yaw);
                float rx = offset.x * cos - offset.z * sin;
                float rz = offset.x * sin + offset.z * cos;

                float px = Mathf.Clamp(rx / range, -1f, 1f) * center;
                float py = Mathf.Clamp(-rz / range, -1f, 1f) * center;

                float dotX = x + center + px;
                float dotY = y + center + py;

                GUI.color = TeamColor(player);
                GUI.DrawTexture(new Rect(dotX - 4f, dotY - 4f, 8f, 8f), _dummyTexture);

                if (_showNames.Value)
                {
                    GUI.color = Color.white;
                    GUI.Label(new Rect(dotX + 6f, dotY - 10f, 110f, 20f), player.Name+(player.Team>0?" [T"+player.Team+"]":""), _smallStyle);
                }
            }

            foreach (var player in Players.Values)
            {
                if (!player.Alive || !IsDummyName(player.Name) || Time.unscaledTime - player.LastDebugSeen > 2f)
                    continue;

                if (player.HasMoveGoal)
                    DrawRadarPoint(x, y, center, range, yaw, localPosition, player.MoveGoal, _goalTexture, new Color(0.3f, 0.9f, 1f, 0.9f), "path");
                if (player.HasLootGoal)
                    DrawRadarPoint(x, y, center, range, yaw, localPosition, player.LootGoal, _lootTexture, new Color(0.15f, 1f, 0.45f, 0.95f), string.IsNullOrWhiteSpace(player.LootName) ? "loot" : player.LootName);
            }

            GUI.color = oldColor;
        }

        private void DrawRadarPoint(float radarX, float radarY, float center, float range, float yaw, Vector3 localPosition, Vector3 worldPosition, Texture2D texture, Color color, string label)
        {
            Vector3 offset = worldPosition - localPosition;
            float cos = Mathf.Cos(-yaw);
            float sin = Mathf.Sin(-yaw);
            float rx = offset.x * cos - offset.z * sin;
            float rz = offset.x * sin + offset.z * cos;

            float px = Mathf.Clamp(rx / range, -1f, 1f) * center;
            float py = Mathf.Clamp(-rz / range, -1f, 1f) * center;
            float dotX = radarX + center + px;
            float dotY = radarY + center + py;

            GUI.color = color;
            GUI.DrawTexture(new Rect(dotX - 3f, dotY - 3f, 6f, 6f), texture);
            if (_showNames.Value)
                GUI.Label(new Rect(dotX + 5f, dotY - 9f, 120f, 18f), label, _smallStyle);
        }

        private void DrawStatusLine()
        {
            int dummyCount = 0;
            foreach (var player in Players.Values)
            {
                if (player.Alive && IsDummyName(player.Name))
                    dummyCount++;
            }

            var oldColor = GUI.color;
            GUI.color = dummyCount > 0 ? new Color(1f, 0.9f, 0.05f, 0.95f) : new Color(1f, 1f, 1f, 0.55f);
            GUI.Label(new Rect(18f, 58f, 520f, 24f), $"DUMMY HIGHLIGHTER: {dummyCount} dummy marker(s)  server:{_serverPayloadCount}/{_serverPlayerCount}", _smallStyle);
            GUI.color = oldColor;
        }

        private void DrawWorldMarkers()
        {
            Camera camera = Camera.main;
            if (camera == null) return;

            Vector3 localPosition = GetLocalPosition();
            float maxDistance = Mathf.Max(50f, _markerMaxDistance.Value);
            var oldColor = GUI.color;

            foreach (var player in Players.Values)
            {
                if (!player.Alive) continue;
                if (IsLocalPlayer(player)) continue;
                if (_showOnlyDummies.Value && !IsDummyName(player.Name)) continue;

                Vector3 world = VisualPosition(player) + Vector3.up * 2.2f;
                float distance = Vector3.Distance(localPosition, player.Position);
                if (distance > maxDistance) continue;

                Vector3 screen = camera.WorldToScreenPoint(world);
                bool behind = screen.z < 0f;
                Vector2 point;
                if (behind)
                {
                    point = new Vector2(Screen.width - screen.x, screen.y);
                }
                else
                {
                    point = new Vector2(screen.x, Screen.height - screen.y);
                }

                point.x = Mathf.Clamp(point.x, 24f, Screen.width - 24f);
                point.y = Mathf.Clamp(point.y, 24f, Screen.height - 24f);

                Color markerColor = TeamColor(player);

                GUI.color = markerColor;
                GUI.DrawTexture(new Rect(point.x - 7f, point.y - 7f, 14f, 14f), _dummyTexture);
                string debugLine = string.Empty;
                if (Time.unscaledTime - player.LastDebugSeen <= 2f && !string.IsNullOrWhiteSpace(player.BotState))
                {
                    string los = player.HasLineOfSight ? "LOS" : "NO LOS";
                    string firing = player.IsFiring ? " firing" : string.Empty;
                    debugLine = $"\n{player.BotState}  {(player.Team>0?"Team "+player.Team:"Solo")}  {player.TargetName}  {player.WeaponName}  {los}{firing}";
                }

                GUI.Label(
                    new Rect(point.x + 10f, point.y - 24f, 480f, 58f),
                    $"{player.Name}  {distance:0}m  ({player.Position.x:0},{player.Position.y:0},{player.Position.z:0}){debugLine}",
                    _markerStyle);
            }

            GUI.color = oldColor;
        }

        private static void RemoveExpiredPlayers()
        {
            var stale = new List<byte>();
            foreach (var kvp in Players)
            {
                if (Time.unscaledTime - kvp.Value.LastSeen > 5f)
                    stale.Add(kvp.Key);
            }

            foreach (byte key in stale)
                Players.Remove(key);
        }

        private static Vector3 GetLocalPosition()
        {
            if (Player.localPlayer == null) return Vector3.zero;
            return Player.localPlayer.m_hip != null
                ? Player.localPlayer.m_hip.transform.position
                : Player.localPlayer.transform.position;
        }

        private static Vector3 GetLocalForward()
        {
            if (Player.localPlayer == null) return Vector3.forward;
            return Player.localPlayer.transform.forward;
        }

        private static bool IsLocalPlayer(RadarPlayer player)
        {
            if (Player.localPlayer == null) return false;

            byte localIndex;
            if (TryGetLocalPlayerIndex(out localIndex))
                return localIndex == player.Index;

            try
            {
                return !IsDummyName(player.Name) &&
                    Vector3.Distance(player.Position, GetLocalPosition()) < 2.5f;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetLocalPlayerIndex(out byte index)
        {
            index = byte.MaxValue;
            object localPlayer = Player.localPlayer;
            if (localPlayer == null)
                return false;

            ResolveLocalPlayerIndexMember(localPlayer.GetType());
            if (_localPlayerIndexMember == null)
                return false;

            try
            {
                object value = null;
                var property = _localPlayerIndexMember as PropertyInfo;
                if (property != null)
                    value = property.GetValue(localPlayer, null);

                var field = _localPlayerIndexMember as FieldInfo;
                if (field != null)
                    value = field.GetValue(localPlayer);

                if (value is byte b)
                {
                    index = b;
                    return true;
                }

                if (value is int i && i >= 0 && i <= 255)
                {
                    index = (byte)i;
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static void ResolveLocalPlayerIndexMember(Type playerType)
        {
            if (_localPlayerIndexResolved && _localPlayerType == playerType)
                return;

            _localPlayerType = playerType;
            _localPlayerIndexResolved = true;
            _localPlayerIndexMember = null;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (int i = 0; i < LocalPlayerIndexMembers.Length; i++)
            {
                string name = LocalPlayerIndexMembers[i];
                PropertyInfo property = playerType.GetProperty(name, flags);
                if (property != null)
                {
                    _localPlayerIndexMember = property;
                    return;
                }

                FieldInfo field = playerType.GetField(name, flags);
                if (field != null)
                {
                    _localPlayerIndexMember = field;
                    return;
                }
            }
        }

        private void InitGui()
        {
            if (_labelStyle != null) return;

            _bgTexture = MakeTex(new Color(0f, 0f, 0f, 0.62f));
            _selfTexture = MakeTex(new Color(0.2f, 0.85f, 1f, 1f));
            _playerTexture = MakeTex(new Color(1f, 0.2f, 0.15f, 1f));
            _dummyTexture = MakeTex(Color.white);
            _goalTexture = MakeTex(new Color(0.3f, 0.9f, 1f, 1f));
            _lootTexture = MakeTex(new Color(0.15f, 1f, 0.45f, 1f));

            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft
            };
            _labelStyle.normal.textColor = Color.white;

            _smallStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft
            };
            _smallStyle.normal.textColor = Color.white;

            _markerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft
            };
            _markerStyle.normal.textColor = new Color(1f, 0.92f, 0.08f, 1f);
        }

        private static Texture2D MakeTex(Color color)
        {
            var texture = new Texture2D(1, 1);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }

        private void RegisterSettings()
        {
            try
            {
                TabgInstaller.ModSettings.ModSettingsUI.Register("Dummy Debug Radar", "Toggle Key", "Key to show/hide radar", _toggleKey);
                TabgInstaller.ModSettings.ModSettingsUI.Register("Dummy Debug Radar", "Visible", "Show radar overlay", _visible);
                TabgInstaller.ModSettings.ModSettingsUI.Register("Dummy Debug Radar", "Range", "Radar range in meters", _radarRange);
                TabgInstaller.ModSettings.ModSettingsUI.Register("Dummy Debug Radar", "Size", "Radar size in pixels", _radarSize);
                TabgInstaller.ModSettings.ModSettingsUI.Register("Dummy Debug Radar", "Show Names", "Show player names", _showNames);
                TabgInstaller.ModSettings.ModSettingsUI.Register("Dummy Highlighter", "World Markers", "Show labels over dummy players", _showWorldMarkers);
                TabgInstaller.ModSettings.ModSettingsUI.Register("Dummy Highlighter", "Only Dummies", "Only label AI dummy players", _showOnlyDummies);
                TabgInstaller.ModSettings.ModSettingsUI.Register("Dummy Highlighter", "Max Distance", "Maximum label distance", _markerMaxDistance);
                TabgInstaller.ModSettings.ModSettingsUI.Register("Dummy Debug", "Sync Bodies", "Move local dummy bodies after large server radar jumps", _syncDummyClientBodies);
                TabgInstaller.ModSettings.ModSettingsUI.Register("Dummy Debug", "Respawn Bodies", "Rebuild missing local dummy bodies after large server radar jumps", _respawnDummyClientBodies);
                TabgInstaller.ModSettings.ModSettingsUI.Register("Dummy Debug", "Drop Compatibility", "Mutate dummy client drop state before vanilla AllDrop", _markDummiesDroppedBeforeAllDrop);
                TabgInstaller.ModSettings.ModSettingsUI.Register("Dummy Debug", "Local Player Fallback", "Add locally known players that server did not broadcast", _allowLocalClientPlayerFallback);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[AdminRadar.Client] ModSettings registration failed: " + ex.Message);
            }
        }

        private static bool IsDummyName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (name.StartsWith("[BOT] ", StringComparison.Ordinal)) return true;
            if (name.StartsWith("AIPlayer", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!name.StartsWith("Player", StringComparison.OrdinalIgnoreCase))
                return false;

            if (name.Length <= "Player".Length)
                return false;

            for (int i = "Player".Length; i < name.Length; i++)
            {
                if (!char.IsDigit(name[i]))
                    return false;
            }

            return true;
        }

        private static Color TeamColor(RadarPlayer player)
        {
            if(!IsDummyName(player.Name))return new Color(1,.25f,.2f);
            if(player.Team==0)return new Color(.85f,.85f,.85f);
            return Color.HSVToRGB((player.Team*.61803399f)%1,.7f,1);
        }
        private static Vector3 VisualPosition(RadarPlayer player)
        {
            if(player.LastServerSeen<=0 || (player.PreviousPosition-player.Position).sqrMagnitude>100*100)return player.Position;
            return Vector3.Lerp(player.PreviousPosition,player.Position,Mathf.Clamp01((Time.unscaledTime-player.LastServerSeen)/.5f));
        }

        private static bool HasFreshServerPosition(RadarPlayer player)
        {
            return player.LastServerSeen > 0f && Time.unscaledTime - player.LastServerSeen <= 1.5f;
        }

        private struct ParsedPlayerEntry
        {
            public byte Index;
            public string Name;
            public Vector3 Position;
            public bool Alive;
            public byte Team;
        }

        private struct ParsedDebugEntry
        {
            public byte Index;
            public string State;
            public string TargetName;
            public string WeaponName;
            public bool HasLineOfSight;
            public bool IsFiring;
            public bool HasMoveGoal;
            public Vector3 MoveGoal;
            public bool HasLootGoal;
            public Vector3 LootGoal;
            public string LootName;
        }

        private sealed class BoundedReader
        {
            private readonly byte[] _data;
            private int _position;

            public BoundedReader(byte[] data)
            {
                _data = data ?? new byte[0];
            }

            public int Remaining => _data.Length - _position;

            public bool TryReadByte(out byte value)
            {
                value = 0;
                if (Remaining < 1) return false;
                value = _data[_position++];
                return true;
            }

            public bool TryReadBool(out bool value)
            {
                byte raw;
                if (!TryReadByte(out raw))
                {
                    value = false;
                    return false;
                }

                value = raw != 0;
                return true;
            }

            public bool TryReadUInt16(out ushort value)
            {
                value = 0;
                if (Remaining < 2) return false;
                value = (ushort)(_data[_position] | (_data[_position + 1] << 8));
                _position += 2;
                return true;
            }

            public bool TryReadUInt32(out uint value)
            {
                value = 0;
                if (Remaining < 4) return false;
                value = (uint)(_data[_position] |
                    (_data[_position + 1] << 8) |
                    (_data[_position + 2] << 16) |
                    (_data[_position + 3] << 24));
                _position += 4;
                return true;
            }

            public bool TryReadSingle(out float value)
            {
                value = 0f;
                if (Remaining < 4) return false;
                value = BitConverter.ToSingle(_data, _position);
                _position += 4;
                return true;
            }

            public bool TryReadBytes(int count, out byte[] bytes)
            {
                bytes = null;
                if (count < 0 || Remaining < count) return false;
                bytes = new byte[count];
                Buffer.BlockCopy(_data, _position, bytes, 0, count);
                _position += count;
                return true;
            }

            public bool TryReadString16(int maxBytes, out string value)
            {
                value = string.Empty;
                ushort length;
                if (!TryReadUInt16(out length) || length > maxBytes || Remaining < length)
                    return false;

                if (length == 0)
                    return true;

                value = Encoding.UTF8.GetString(_data, _position, length);
                _position += length;
                return true;
            }

            public bool TryReadLegacyString(int maxBytes, out string value)
            {
                value = string.Empty;
                int length = 0;
                int shift = 0;

                for (int i = 0; i < 5; i++)
                {
                    byte b;
                    if (!TryReadByte(out b))
                        return false;

                    length |= (b & 0x7F) << shift;
                    if ((b & 0x80) == 0)
                    {
                        if (length < 0 || length > maxBytes || Remaining < length)
                            return false;

                        if (length == 0)
                            return true;

                        value = Encoding.UTF8.GetString(_data, _position, length);
                        _position += length;
                        return true;
                    }

                    shift += 7;
                }

                return false;
            }
        }

        private struct RadarPlayer
        {
            public byte Index;
            public string Name;
            public Vector3 Position;
            public bool Alive;
            public byte Team;
            public float LastSeen;
            public float LastServerSeen;
            public Vector3 PreviousPosition;
            public string BotState;
            public string TargetName;
            public string WeaponName;
            public bool HasLineOfSight;
            public bool IsFiring;
            public bool HasMoveGoal;
            public Vector3 MoveGoal;
            public bool HasLootGoal;
            public Vector3 LootGoal;
            public string LootName;
            public float LastDebugSeen;
        }
    }
}
