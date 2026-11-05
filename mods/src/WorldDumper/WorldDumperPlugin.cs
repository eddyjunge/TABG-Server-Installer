using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Tabg.WorldDumper
{
    /// <summary>
    /// Exports the map data tabg's own server needs (v0: spawn points) from the game the host
    /// already owns. Nothing is redistributed: the JSON is written next to the install and read by
    /// tabg locally.
    ///
    /// Environment:
    ///   TABG_WORLD_OUT   output file (default: tabg-world.json next to the game executable)
    ///   TABG_WORLD_QUIT  "1" to quit the game after writing
    ///   TABG_NAV_OUT     where to write the bots' walkability grid (default: next to the JSON)
    ///
    /// The server this runs in never appears in the community server list: its heartbeat is
    /// switched off.
    /// </summary>
    [BepInPlugin("tabg.worlddumper", "tabg world dumper", "0.1.0")]
    public sealed class WorldDumperPlugin : BaseUnityPlugin
    {
        private const float GiveUpAfterSeconds = 300f;
        private float _elapsed;
        private bool _done;

        private void Awake()
        {
            var heartbeat = AccessTools.Method(typeof(Landfall.TABG.CommunityBackendAPI), "GameServerHeartbeat");
            if (heartbeat != null)
            {
                new Harmony("tabg.worlddumper").Patch(heartbeat, prefix: new HarmonyMethod(typeof(WorldDumperPlugin), nameof(SkipHeartbeat)));
            }
        }

        private static bool SkipHeartbeat() => false;

        private void Update()
        {
            if (_done)
            {
                return;
            }

            float before = _elapsed;
            _elapsed += Time.unscaledDeltaTime;
            SpawnPointTAG[] spawns = FindObjectsOfType<SpawnPointTAG>();
            // Cars only exist once the server's game room has started (dedicated server only).
            bool waitForCars = RoomCars() == null && _elapsed < CarsWaitSeconds;
            if ((spawns.Length == 0 || waitForCars) && _elapsed < GiveUpAfterSeconds)
            {
                if ((int)(before / 10f) != (int)(_elapsed / 10f))
                {
                    Logger.LogInfo($"Waiting for the map and game room ({(int)_elapsed}s)...");
                }

                return;
            }

            _done = true;
            string path = Environment.GetEnvironmentVariable("TABG_WORLD_OUT");
            if (string.IsNullOrEmpty(path))
            {
                path = Path.Combine(Paths.GameRootPath, "tabg-world.json");
            }

            try
            {
                File.WriteAllText(path, BuildJson(spawns), new UTF8Encoding(false));
                Logger.LogInfo($"Wrote {spawns.Length} spawn points to {path}");
                string nav = Environment.GetEnvironmentVariable("TABG_NAV_OUT");
                if (string.IsNullOrEmpty(nav))
                {
                    nav = Path.ChangeExtension(path, ".nav");
                }
                WriteNavGrid(nav);
            }
            catch (Exception e)
            {
                Logger.LogError($"Could not write {path}: {e}");
            }

            if (Environment.GetEnvironmentVariable("TABG_WORLD_QUIT") == "1")
            {
                Application.Quit();
            }
        }

        private static string BuildJson(SpawnPointTAG[] spawns)
        {
            var sb = new StringBuilder();
            sb.Append("{\n  \"format\": 1,\n");
            sb.Append("  \"unity_version\": ").Append(Quote(Application.unityVersion)).Append(",\n");
            // The game version the community server list filters on (changes with every update).
            sb.Append("  \"app_version\": ").Append(Quote(Landfall.Network.ServerConnector.APP_VERSION)).Append(",\n");
            sb.Append("  \"scenes\": [");
            sb.Append(string.Join(", ", Enumerable.Range(0, SceneManager.sceneCount).Select(i => Quote(SceneManager.GetSceneAt(i).name))));
            sb.Append("],\n  \"spawn_points\": [\n");
            var ordered = spawns
                .Select(s => new { s.ID, s.transform.position, Yaw = s.transform.rotation.eulerAngles.y })
                .OrderBy(s => s.ID).ThenBy(s => s.position.x).ThenBy(s => s.position.z)
                .ToArray();
            for (int i = 0; i < ordered.Length; i++)
            {
                var s = ordered[i];
                sb.Append("    {\"group\": ").Append(s.ID.ToString(CultureInfo.InvariantCulture))
                  .Append(", \"position\": [").Append(F(s.position.x)).Append(", ").Append(F(s.position.y)).Append(", ").Append(F(s.position.z))
                  .Append("], \"rotation\": ").Append(F(s.Yaw)).Append('}')
                  .Append(i + 1 < ordered.Length ? ",\n" : "\n");
            }

            sb.Append("  ],\n");
            AppendRing(sb);
            sb.Append(",\n");
            AppendLoot(sb);
            sb.Append(",\n");
            AppendCars(sb);
            AppendBrawl(sb);
            AppendBomb(sb);
            AppendSouls(sb);
            sb.Append("}\n");
            return sb.ToString();
        }

        private static string Drops(LootPreset preset)
        {
            var drops = preset != null && preset.loot != null ? preset.loot : new System.Collections.Generic.List<LootDropWrapper>();
            return "[" + string.Join(", ", drops.Select(d =>
            {
                var items = (d.m_loot ?? new Loot[0])
                    .Where(l => l.loot != null && l.loot.GetComponent<Pickup>() != null)
                    .Select(l => "[" + l.loot.GetComponent<Pickup>().m_itemIndex.ToString(CultureInfo.InvariantCulture) + ", " + l.quanitity.ToString(CultureInfo.InvariantCulture) + "]");
                return "{\"spawn_rate\": " + F(d.spawnRate) + ", \"items\": [" + string.Join(", ", items) + "]}";
            })) + "]";
        }

        private static string Houses(Transform layout)
        {
            if (layout == null || layout.parent == null)
                return "[]";
            var houses = new System.Collections.Generic.List<string>();
            for (int j = 0; j < layout.parent.childCount; j++)
            {
                Transform house = layout.parent.GetChild(j);
                var loot = house.GetComponentsInChildren<Landfall.Network.LootTAG>(true)
                    .Select(t => "{\"tier\": " + Quote(t.transform.parent.name) + ", \"position\": [" + F(t.transform.position.x) + ", " + F(t.transform.position.y) + ", " + F(t.transform.position.z) + "]}");
                var spawns = house.GetComponentsInChildren<SpawnPointTAG>(true)
                    .Select(t => "{\"group\": " + t.ID.ToString(CultureInfo.InvariantCulture) + ", \"position\": [" + F(t.transform.position.x) + ", " + F(t.transform.position.y) + ", " + F(t.transform.position.z) + "], \"rotation\": " + F(t.transform.rotation.eulerAngles.y) + "}");
                houses.Add("{\"loot\": [" + string.Join(", ", loot) + "], \"spawns\": [" + string.Join(", ", spawns) + "]}");
            }
            return "[" + string.Join(",\n      ", houses) + "]";
        }

        /// <summary>
        /// Brawl: every spawn house's loot spots (by tier) and the presets the server draws one of
        /// each round (official BrawlLootSelector). Read from the unspawned prefab, because the live
        /// copy consumes its loot spots when the round starts.
        /// </summary>
        private static void AppendBrawl(StringBuilder sb)
        {
            var selectors = Resources.FindObjectsOfTypeAll<BrawlLootSelector>();
            // The prefab is loaded in every mode; only a live copy in the scene means Brawl.
            if (!selectors.Any(x => !string.IsNullOrEmpty(x.gameObject.scene.name)))
                return;
            var sel = selectors.FirstOrDefault(x => string.IsNullOrEmpty(x.gameObject.scene.name)) ?? selectors.FirstOrDefault();
            if (sel == null)
                return;
            sb.Append(",\n  \"brawl\": {\n    \"presets\": [");
            sb.Append(string.Join(", ", (sel.lootPresets ?? new BrawlLootPreset[0]).Select(p =>
                "{\"high\": " + Drops(p.highLoots) + ", \"med\": " + Drops(p.medLoots) + ", \"low\": " + Drops(p.lowLoots) +
                ", \"attach\": " + Drops(p.attachLoot) + ", \"redo_chance\": " + F(p.redoChance) + "}")));
            sb.Append("],\n    \"team_houses\": ").Append(Houses(sel.teamLootParent));
            sb.Append(",\n    \"solo_houses\": ").Append(Houses(sel.soloLootParent));
            sb.Append("\n  }");
        }

        /// <summary>
        /// Loot spots in the map, and the match modifiers whose presets decide what spawns there
        /// (the official server draws every spot from the current modifier's preset).
        /// </summary>
        private static void AppendLoot(StringBuilder sb)
        {
            var spots = FindObjectsOfType<Landfall.Network.LootTAG>()
                .Select(t => new { t.transform.position, Force = t.lootPreset != null && t.lootPreset.m_forceSpawn, HasPreset = t.lootPreset != null })
                .OrderBy(t => t.position.x).ThenBy(t => t.position.z)
                .ToArray();
            sb.Append("  \"loot_spots\": [\n");
            for (int i = 0; i < spots.Length; i++)
            {
                var s = spots[i];
                sb.Append("    {\"position\": [").Append(F(s.position.x)).Append(", ").Append(F(s.position.y)).Append(", ").Append(F(s.position.z))
                  .Append("], \"force_spawn\": ").Append(s.Force ? "true" : "false")
                  .Append(", \"has_preset\": ").Append(s.HasPreset ? "true" : "false").Append('}')
                  .Append(i + 1 < spots.Length ? ",\n" : "\n");
            }

            sb.Append("  ],\n  \"match_modifiers\": [\n");
            TABGLootPresetDatabase db = TABGLootPresetDatabase.Instance;
            var names = new System.Collections.Generic.SortedDictionary<int, string>();
            var heals = new System.Collections.Generic.SortedDictionary<int, float>();
            var grenades = new System.Collections.Generic.SortedSet<int>();
            bool first = true;
            for (int i = 0; db != null && i < 256; i++)
            {
                MatchModifier m = db.GetMatchModifier(i);
                if (m == null)
                {
                    break;
                }

                sb.Append(first ? "" : ",\n").Append("    {\"index\": ").Append(m.Index.ToString(CultureInfo.InvariantCulture))
                  .Append(", \"title\": ").Append(Quote(m.ModifierTitle))
                  .Append(", \"rarity\": ").Append(((int)m.Rarity).ToString(CultureInfo.InvariantCulture))
                  .Append(", \"drops\": [");
                first = false;
                var drops = m.Preset != null && m.Preset.loot != null ? m.Preset.loot : new System.Collections.Generic.List<LootDropWrapper>();
                sb.Append(string.Join(", ", drops.Select(d =>
                {
                    var items = (d.m_loot ?? new Loot[0])
                        .Where(l => l.loot != null && l.loot.GetComponent<Pickup>() != null)
                        .Select(l =>
                        {
                            Pickup p = l.loot.GetComponent<Pickup>();
                            names[p.m_itemIndex] = string.IsNullOrEmpty(p.itemName) ? l.loot.name : p.itemName;
                            HealingItem heal = l.loot.GetComponent<HealingItem>();
                            if (heal != null)
                            {
                                heals[p.m_itemIndex] = heal.healingAmount;
                            }

                            if (p.weaponType == Pickup.WeaponType.Grenade)
                            {
                                grenades.Add(p.m_itemIndex);
                            }
                            return "[" + p.m_itemIndex.ToString(CultureInfo.InvariantCulture) + ", " + l.quanitity.ToString(CultureInfo.InvariantCulture) + "]";
                        });
                    return "{\"spawn_rate\": " + F(d.spawnRate) + ", \"items\": [" + string.Join(", ", items) + "]}";
                })));
                sb.Append("]}");
            }

            sb.Append("\n  ],\n  \"items\": {");
            sb.Append(string.Join(", ", names.Select(kv => "\"" + kv.Key.ToString(CultureInfo.InvariantCulture) + "\": " + Quote(kv.Value))));
            sb.Append("},\n  \"heal_amounts\": {");
            sb.Append(string.Join(", ", heals.Select(kv => "\"" + kv.Key.ToString(CultureInfo.InvariantCulture) + "\": " + F(kv.Value))));
            sb.Append("},\n  \"grenades\": [");
            sb.Append(string.Join(", ", grenades.Select(g => g.ToString(CultureInfo.InvariantCulture))));
            sb.Append("]\n");
        }

        private static object Field(object o, string name)
        {
            return o?.GetType().GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(o);
        }

        /// <summary>
        /// Bomb (TABG_CS): where the bomb spawns each round, the bomb's item id and the weapon
        /// everyone starts a round with. Server-only types, so by reflection.
        /// </summary>
        private static void AppendBomb(StringBuilder sb)
        {
            Type bombTag = HarmonyLib.AccessTools.TypeByName("DABOMBTAG");
            UnityEngine.Object tag = bombTag != null ? FindObjectOfType(bombTag) : null;
            if (tag == null)
                return;
            Vector3 spawn = ((Component)tag).transform.position;
            Type refsType = HarmonyLib.AccessTools.TypeByName("BombGameModeReferences");
            UnityEngine.Object refs = refsType != null ? FindObjectOfType(refsType) : null;
            var start = Field(refs, "m_StartWeapon") as Pickup;
            Type serverClient = HarmonyLib.AccessTools.TypeByName("Landfall.Network.ServerClient");
            UnityEngine.Object sc = serverClient != null ? FindObjectOfType(serverClient) : null;
            var bomb = Field(sc, "m_BombPreset") as Pickup;
            sb.Append(",\n  \"bomb\": {\"spawn\": [").Append(F(spawn.x)).Append(", ").Append(F(spawn.y)).Append(", ").Append(F(spawn.z))
              .Append("], \"bomb_item\": ").Append((bomb != null ? bomb.m_itemIndex : -1).ToString(CultureInfo.InvariantCulture))
              .Append(", \"start_weapon\": ").Append((start != null ? start.m_itemIndex : -1).ToString(CultureInfo.InvariantCulture))
              .Append("}");
        }

        /// <summary>
        /// Souls (dropped by the dead, spent to respawn a teammate or cleanse a curse): the soul's
        /// item id and the curse indices a respawned player can get.
        /// </summary>
        private static void AppendSouls(StringBuilder sb)
        {
            Type serverClient = HarmonyLib.AccessTools.TypeByName("Landfall.Network.ServerClient");
            UnityEngine.Object sc = serverClient != null ? FindObjectOfType(serverClient) : null;
            object soul = Prop(Prop(sc, "GameRoomReference"), "SoulIndex");
            if (!(soul is int soulItem))
                return;
            var curses = Field(CurseDatabase.Instance, "m_Curses") as System.Collections.IDictionary;
            var keys = curses == null ? new string[0] : curses.Keys.Cast<object>().Select(k => Convert.ToInt32(k).ToString(CultureInfo.InvariantCulture)).ToArray();
            // Blessing pickups by rarity (spell drops hand these out), from the blessing loot preset.
            var byRarity = new System.Collections.Generic.SortedDictionary<string, System.Collections.Generic.List<int>>();
            var preset = CurseDatabase.Instance != null ? CurseDatabase.Instance.lp_blessings : null;
            foreach (var drop in preset != null && preset.loot != null ? preset.loot : new System.Collections.Generic.List<LootDropWrapper>())
            {
                GameObject go = drop.m_loot != null && drop.m_loot.Length > 0 ? drop.m_loot[0].loot : null;
                Pickup pickup = go != null ? go.GetComponent<Pickup>() : null;
                Component bp = go != null ? go.GetComponent(HarmonyLib.AccessTools.TypeByName("BlessingPickup")) : null;
                object rarity = Prop(Field(bp, "currentBlessing"), "rarity") ?? Field(Field(bp, "currentBlessing"), "rarity");
                if (pickup == null || rarity == null)
                    continue;
                string r = rarity.ToString().ToLowerInvariant();
                if (!byRarity.ContainsKey(r))
                    byRarity[r] = new System.Collections.Generic.List<int>();
                byRarity[r].Add(pickup.m_itemIndex);
            }
            // Spell drop timing (Spelldrop_Server on the server's scene).
            Type dropType = HarmonyLib.AccessTools.TypeByName("Spelldrop_Server");
            UnityEngine.Object dropper = dropType != null ? FindObjectOfType(dropType) : null;
            object min = Field(dropper, "min"), max = Field(dropper, "max");
            sb.Append(",\n  \"souls\": {\"soul_item\": ").Append(soulItem.ToString(CultureInfo.InvariantCulture))
              .Append(", \"curses\": [").Append(string.Join(", ", keys)).Append("]")
              .Append(", \"blessings\": {").Append(string.Join(", ", byRarity.Select(kv => Quote(kv.Key) + ": [" + string.Join(", ", kv.Value) + "]"))).Append("}");
            if (min is float mn && max is float mx)
                sb.Append(", \"spell_drop_interval\": [").Append(F(mn)).Append(", ").Append(F(mx)).Append("]");
            sb.Append("}");
        }

        /// <summary>
        /// The bots' map: the top surface height on a 1 m grid over the land (one ray down per
        /// cell), as little-endian u16 of (height - 100) * 64, 0 for water. Gzip-compressed, with a
        /// header: "TBNG", u32 version 1, f32 min x, f32 min z, f32 cell size, u32 width, u32 depth.
        /// </summary>
        private void WriteNavGrid(string path)
        {
            const float Cell = 1f, WaterLine = 109.5f, Margin = 40f;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // A coarse pass finds where the land is.
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            for (float cx = -2500f; cx < 2500f; cx += 8f)
                for (float cz = -2500f; cz < 2500f; cz += 8f)
                    if (Physics.Raycast(new Vector3(cx, 2000f, cz), Vector3.down, out RaycastHit h, 4000f, ~0, QueryTriggerInteraction.Ignore) && h.point.y > WaterLine)
                    {
                        minX = Mathf.Min(minX, cx); maxX = Mathf.Max(maxX, cx);
                        minZ = Mathf.Min(minZ, cz); maxZ = Mathf.Max(maxZ, cz);
                    }
            if (minX > maxX)
            {
                Logger.LogWarning("No land found for the walkability grid");
                return;
            }
            float MinX = minX - Margin, MinZ = minZ - Margin;
            int Width = Mathf.CeilToInt((maxX - minX + 2 * Margin) / Cell);
            int Depth = Mathf.CeilToInt((maxZ - minZ + 2 * Margin) / Cell);
            using (var file = File.Create(path))
            using (var gz = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Optimal))
            using (var w = new BinaryWriter(gz))
            {
                w.Write(new[] { (byte)'T', (byte)'B', (byte)'N', (byte)'G' });
                w.Write(1u);
                w.Write(MinX);
                w.Write(MinZ);
                w.Write(Cell);
                w.Write((uint)Width);
                w.Write((uint)Depth);
                var row = new byte[Width * 2];
                for (int zi = 0; zi < Depth; zi++)
                {
                    float z = MinZ + (zi + 0.5f) * Cell;
                    for (int xi = 0; xi < Width; xi++)
                    {
                        float x = MinX + (xi + 0.5f) * Cell;
                        ushort v = 0;
                        if (Physics.Raycast(new Vector3(x, 2000f, z), Vector3.down, out RaycastHit hit, 4000f, ~0, QueryTriggerInteraction.Ignore)
                            && hit.point.y > WaterLine)
                        {
                            v = (ushort)Mathf.Clamp(Mathf.RoundToInt((hit.point.y - 100f) * 64f), 1, 65535);
                        }
                        row[xi * 2] = (byte)v;
                        row[xi * 2 + 1] = (byte)(v >> 8);
                    }
                    w.Write(row);
                }
            }
            Logger.LogInfo($"Wrote the {Width}x{Depth} walkability grid to {path} in {sw.ElapsedMilliseconds} ms");
        }

        private const float CarsWaitSeconds = 120f;

        private static object Prop(object o, string name)
        {
            return o?.GetType().GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(o, null);
        }

        /// <summary>
        /// The dedicated server's spawned cars (<c>ServerClient.GameRoomReference.Cars</c>), read by
        /// reflection because these server types don't exist in the client assembly we build against.
        /// </summary>
        private static System.Collections.IList RoomCars()
        {
            Type serverClient = HarmonyLib.AccessTools.TypeByName("Landfall.Network.ServerClient");
            UnityEngine.Object sc = serverClient != null ? FindObjectOfType(serverClient) : null;
            return Prop(Prop(sc, "GameRoomReference"), "Cars") as System.Collections.IList;
        }

        /// <summary>Every car as the server spawned it: type, index, pose, seats and damageable parts.</summary>
        private static void AppendCars(StringBuilder sb)
        {
            System.Collections.IList cars = RoomCars();
            sb.Append("  \"cars\": [");
            if (cars != null)
            {
                var entries = new System.Collections.Generic.List<string>();
                foreach (object car in cars)
                {
                    var pos = (Vector3)Prop(car, "CarPosition");
                    var rot = (Quaternion)Prop(car, "CarRotation");
                    int seatCount = (int)Prop(car, "NumberOfSeats");
                    MethodInfoCache getSeat = new MethodInfoCache(car, "GetSeat");
                    var seats = Enumerable.Range(0, seatCount).Select(i =>
                    {
                        object seat = getSeat.Invoke(i);
                        return "{\"network_index\": " + Prop(seat, "NetworkIndex") + ", \"driver\": " + (((bool?)Prop(seat, "DriverSeat") ?? false) ? "true" : "false") + "}";
                    });
                    var parts = ((System.Collections.IEnumerable)Prop(car, "DamagableParts") ?? new object[0]).Cast<object>().Select(p =>
                        "{\"index\": " + Prop(p, "Index") + ", \"health\": " + F((float)Prop(p, "Health")) + ", \"name\": " + Quote((string)Prop(p, "PartName")) + "}");
                    entries.Add("    {\"car_type\": " + Prop(car, "CarTypeIdentifier") + ", \"car_index\": " + Prop(car, "CarIndex")
                        + ", \"position\": [" + F(pos.x) + ", " + F(pos.y) + ", " + F(pos.z) + "]"
                        + ", \"rotation\": [" + F(rot.x) + ", " + F(rot.y) + ", " + F(rot.z) + ", " + F(rot.w) + "]"
                        + ", \"health\": " + F((float)Prop(car, "CarHealth"))
                        + ", \"seats\": [" + string.Join(", ", seats) + "], \"parts\": [" + string.Join(", ", parts) + "]}");
                }

                sb.Append("\n").Append(string.Join(",\n", entries)).Append("\n  ");
            }

            sb.Append("]\n");
        }

        private sealed class MethodInfoCache
        {
            private readonly object _target;
            private readonly System.Reflection.MethodInfo _method;

            public MethodInfoCache(object target, string name)
            {
                _target = target;
                _method = target.GetType().GetMethod(name, new[] { typeof(int) });
            }

            public object Invoke(int i) => _method?.Invoke(_target, new object[] { i });
        }

        /// <summary>The ring prefab's timing and the map's ring hot spots (null if no ring exists).</summary>
        private static void AppendRing(StringBuilder sb)
        {
            TheRing ring = TheRing.Instance != null ? TheRing.Instance : FindObjectOfType<TheRing>();
            if (ring == null)
            {
                sb.Append("  \"ring\": null\n");
                return;
            }

            sb.Append("  \"ring\": {\n");
            sb.Append("    \"ring_times\": [").Append(string.Join(", ", (ring.ringTimes ?? new float[0]).Select(F))).Append("],\n");
            sb.Append("    \"start_blue_size\": ").Append(F(ring.blue != null ? ring.blue.localScale.x : ring.currentBlueSize)).Append(",\n");
            sb.Append("    \"base_time_to_travel\": ").Append(F(ring.baseTimeToTravel)).Append(",\n");
            sb.Append("    \"hot_spots\": [");
            if (ring.ringHotSpots != null)
            {
                sb.Append(string.Join(", ", ring.ringHotSpots.Cast<Transform>().Select(t => "[" + F(t.position.x) + ", " + F(t.position.y) + ", " + F(t.position.z) + "]")));
            }

            sb.Append("]\n  }");
        }

        private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

        private static string Quote(string s) => "\"" + (s ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
