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

            sb.Append("  ]\n}\n");
            return sb.ToString();
        }

        private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

        private static string Quote(string s) => "\"" + (s ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
