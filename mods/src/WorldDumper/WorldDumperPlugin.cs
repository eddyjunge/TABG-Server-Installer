using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
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
    /// </summary>
    [BepInPlugin("tabg.worlddumper", "tabg world dumper", "0.1.0")]
    public sealed class WorldDumperPlugin : BaseUnityPlugin
    {
        private const float GiveUpAfterSeconds = 300f;
        private float _elapsed;
        private bool _done;

        private void Update()
        {
            if (_done)
            {
                return;
            }

            float before = _elapsed;
            _elapsed += Time.unscaledDeltaTime;
            SpawnPointTAG[] spawns = FindObjectsOfType<SpawnPointTAG>();
            if (spawns.Length == 0 && _elapsed < GiveUpAfterSeconds)
            {
                if ((int)(before / 10f) != (int)(_elapsed / 10f))
                {
                    Logger.LogInfo($"Waiting for the map to load ({(int)_elapsed}s)...");
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
            sb.Append("}\n");
            return sb.ToString();
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
                            return "[" + p.m_itemIndex.ToString(CultureInfo.InvariantCulture) + ", " + l.quanitity.ToString(CultureInfo.InvariantCulture) + "]";
                        });
                    return "{\"spawn_rate\": " + F(d.spawnRate) + ", \"items\": [" + string.Join(", ", items) + "]}";
                })));
                sb.Append("]}");
            }

            sb.Append("\n  ],\n  \"items\": {");
            sb.Append(string.Join(", ", names.Select(kv => "\"" + kv.Key.ToString(CultureInfo.InvariantCulture) + "\": " + Quote(kv.Value))));
            sb.Append("}\n");
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
