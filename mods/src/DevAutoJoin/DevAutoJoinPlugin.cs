using System;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Landfall.Network;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Tabg.DevAutoJoin
{
    /// <summary>
    /// Development harness for testing tabg's server with the real client, without clicking menus.
    /// Does nothing unless TABG_AUTOJOIN is set.
    ///
    ///   TABG_AUTOJOIN        "ip:port" to join once the main menu is up, or "code:JOINCODE" to join
    ///                        through Unity Relay like the join-code box does
    ///   TABG_AUTOJOIN_QUIT   seconds after joining to quit the game (default: never)
    ///   TABG_AUTOJOIN_SHOTS  comma-separated seconds after joining to take screenshots
    ///   TABG_AUTOJOIN_JUMP   jump out of the plane at this fraction of its 45 s flight (e.g. 0.5)
    ///   TABG_AUTOJOIN_SEAT   seconds after jumping to get into the nearest car's driver seat
    ///   TABG_AUTOJOIN_CHAT   "text@seconds" (after joining) to send a chat line, e.g. "/give 131 5@20"
    ///   TABG_AUTOJOIN_HEAL   item id to heal with (once), as soon as the local player is hurt, e.g. 131
    ///   TABG_AUTOJOIN_DROP   "item:count@seconds" (after joining) to drop from the inventory, e.g. "132:1@75"
    ///   TABG_AUTOJOIN_BENCH  "plugin.guid/Section/Key=v1,v2@start,seconds,rounds": from `start` seconds after
    ///                        joining, alternate the setting through the values `rounds` times, measuring
    ///                        frame times for `seconds` each (uncapped: vsync off, no frame limit); logs
    ///                        "[bench]" lines with average FPS, frame time and 1 % lows per value
    ///   TABG_AUTOJOIN_EXPLODE "Prefab@seconds,distance": spawn one of the game's loaded explosion prefabs
    ///                        (e.g. Ex_MissileExpl, Ex_Stun) or a grenade's smoke ("Smoke Grenade") that far in
    ///                        front of the camera; with TABG_AUTOJOIN_COMPARE, once per value, captured at
    ///                        the flash (boom-*.png)
    ///   TABG_AUTOJOIN_BURST  comma-separated seconds after the explosion to capture (default 0.12)
    ///   TABG_AUTOJOIN_SEQUENCE "A,B,C@seconds,distance,shot1,shot2": spawn these prefabs one after another in front of
    ///                        the camera, each captured at the two delays (seq-NAME-1.png, seq-NAME-2.png)
    ///   TABG_AUTOJOIN_GOTO   "Layer@seconds": teleport to the terrain most painted with a layer whose name holds
    ///                        Layer (Deser, Snow, Grass), to test biome-dependent things
    ///   TABG_AUTOJOIN_SETTLE seconds to let a TABG_AUTOJOIN_COMPARE value take effect before its screenshot
    ///   TABG_AUTOJOIN_TARGET "Name@shots": stand in front of the first building whose name starts with Name, face it,
    ///                        and put each TABG_AUTOJOIN_EXPLODE blast on its wall (to test destruction)
    ///   TABG_AUTOJOIN_FPS    frame cap, and keeps running when unfocused (for a hidden test window)
    ///   TABG_AUTOJOIN_COMPARE "plugin.guid/Section/Key": every screenshot is taken once per value, a few
    ///                        frames apart: off/on for a bool, or "...Key=v1,v2" (files -off/-on or -v1/-v2)
    ///
    /// Logs scene changes, every event the client receives, and the local player's position, and
    /// saves screenshots to BepInEx/autojoin-*.png.
    /// </summary>
    [BepInPlugin("tabg.dev.autojoin", "tabg dev auto-join", "0.1.0")]
    public sealed class DevAutoJoinPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        private string _target;
        private float _quitAfter = -1f;
        private float _sinceMenu = -1f;
        private float _sinceJoin = -1f;
        private float _nextStatus;
        private int _shot;
        private float _jumpAt = -1f;
        private float _seatAfter = -1f;
        private float _sinceJump = -1f;
        private bool _seated;
        private string _chatText;
        private float _chatAt = -1f;
        internal static float FlyingSince = -1f;
        private int _healItem = -1;
        private int _dropItem = -1;
        private int _dropCount;
        private float _dropAt = -1f;
        private int _fpsCap = -1;
        private bool _benching;
        private float[] _shotTimes = { 10f, 30f, 50f, 60f, 70f, 85f };

        private void Awake()
        {
            Log = Logger;
            _target = Environment.GetEnvironmentVariable("TABG_AUTOJOIN");
            if (string.IsNullOrEmpty(_target))
            {
                Logger.LogInfo("TABG_AUTOJOIN not set; doing nothing.");
                enabled = false;
                return;
            }

            int.TryParse(Environment.GetEnvironmentVariable("TABG_AUTOJOIN_FPS"), out _fpsCap);
            if (float.TryParse(Environment.GetEnvironmentVariable("TABG_AUTOJOIN_QUIT"), out float quit))
            {
                _quitAfter = quit;
            }

            string shots = Environment.GetEnvironmentVariable("TABG_AUTOJOIN_SHOTS");
            if (!string.IsNullOrEmpty(shots))
            {
                _shotTimes = shots.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            }

            float.TryParse(Environment.GetEnvironmentVariable("TABG_AUTOJOIN_JUMP"), NumberStyles.Float, CultureInfo.InvariantCulture, out _jumpAt);
            if (!float.TryParse(Environment.GetEnvironmentVariable("TABG_AUTOJOIN_SEAT"), NumberStyles.Float, CultureInfo.InvariantCulture, out _seatAfter))
            {
                _seatAfter = -1f;
            }

            string chat = Environment.GetEnvironmentVariable("TABG_AUTOJOIN_CHAT");
            int at = chat?.LastIndexOf('@') ?? -1;
            if (at > 0 && float.TryParse(chat.Substring(at + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float chatAt))
            {
                _chatText = chat.Substring(0, at);
                _chatAt = chatAt;
            }

            if (!int.TryParse(Environment.GetEnvironmentVariable("TABG_AUTOJOIN_HEAL"), out _healItem))
            {
                _healItem = -1;
            }

            string drop = Environment.GetEnvironmentVariable("TABG_AUTOJOIN_DROP");
            int dropAt = drop?.LastIndexOf('@') ?? -1;
            int colon = drop?.IndexOf(':') ?? -1;
            if (dropAt > 0 && colon > 0 && colon < dropAt
                && int.TryParse(drop.Substring(0, colon), out _dropItem)
                && int.TryParse(drop.Substring(colon + 1, dropAt - colon - 1), out _dropCount)
                && float.TryParse(drop.Substring(dropAt + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out _dropAt))
            {
                Logger.LogInfo($"will drop {_dropCount} x {_dropItem} at {_dropAt} s");
            }
            else
            {
                _dropAt = -1f;
            }

            if (_jumpAt <= 0f)
            {
                _jumpAt = -1f;
            }

            SceneManager.sceneLoaded += (scene, mode) => Logger.LogInfo($"scene loaded: {scene.name} ({mode})");
            new Harmony("tabg.dev.autojoin").PatchAll(typeof(DevAutoJoinPlugin).Assembly);
            Logger.LogInfo($"Will join {_target} once the menu is up.");
        }

        private void TickJumpAndSeat(float dt)
        {
            if (_jumpAt > 0f && FlyingSince >= 0f && _sinceJump < 0f && Time.unscaledTime - FlyingSince >= _jumpAt * 45f)
            {
                PhotonServerHandler handler = FindObjectOfType<PhotonServerHandler>();
                if (handler != null)
                {
                    Logger.LogInfo("Jumping out of the plane");
                    handler.ClientRequestPlaneDrop(Vector3.down);
                }

                _sinceJump = 0f;
                return;
            }

            if (_sinceJump < 0f)
            {
                return;
            }

            float before = _sinceJump;
            _sinceJump += dt;
            if ((int)(before / 5f) != (int)(_sinceJump / 5f))
            {
                Logger.LogInfo($"cars spawned in the scene: {FindObjectsOfType<Car>().Length}");
            }

            if (_seated || _seatAfter < 0f || _sinceJump < _seatAfter || Camera.main == null)
            {
                return;
            }

            _seated = true;
            Vector3 me = Camera.main.transform.position;
            Car nearest = FindObjectsOfType<Car>().OrderBy(c => Vector3.Distance(c.transform.position, me)).FirstOrDefault();
            Seat seat = nearest != null ? nearest.GetComponentsInChildren<Seat>().OrderBy(st => st.NetworkIndex).FirstOrDefault() : null;
            if (seat == null)
            {
                Logger.LogInfo("No car to sit in");
                return;
            }

            Logger.LogInfo($"Getting into {nearest.name} {Vector3.Distance(nearest.transform.position, me):F0} m away (seat {seat.NetworkIndex})");
            FindObjectOfType<PhotonServerHandler>()?.ClientRequestSeat(seat, SeatAction.GetIn);
        }

        /// Heal and drop through the client's own inventory code, like a player would.
        private bool _benchStarted;
        private bool _explodeStarted;

        private System.Collections.IEnumerator Explosions(string prefabName, float distance)
        {
            GameObject prefab = null;
            ParticleSystem smoke = null;
            var names = new System.Collections.Generic.SortedSet<string>();
            foreach (var e in Resources.FindObjectsOfTypeAll<Explosion>())
            {
                var root = e.transform.root.gameObject;
                if (root.scene.IsValid()) continue;     // only prefab assets
                names.Add(root.name);
                if (root.name == prefabName) prefab = root;
            }
            foreach (var g in Resources.FindObjectsOfTypeAll<Grenade>())
            {
                var root = g.transform.root.gameObject;
                if (root.scene.IsValid()) continue;
                names.Add(root.name);
                if (root.name != prefabName) continue;
                // Only the smoke itself: the whole item needs a thrower and a network owner.
                smoke = root.GetComponentsInChildren<ParticleSystem>(true).FirstOrDefault(ps => !ps.main.playOnAwake);
            }
            if (prefab == null && smoke == null)
            {
                Logger.LogWarning($"explode: no loaded prefab {prefabName}; loaded: {string.Join(", ", names)}");
                yield break;
            }
            string compare = Environment.GetEnvironmentVariable("TABG_AUTOJOIN_COMPARE");
            string[] kv = string.IsNullOrEmpty(compare) ? null : compare.Split(new[] { '=' }, 2);
            var entry = kv != null ? FindSetting(kv[0]) : null;
            string[] values = entry == null ? new[] { "" } : kv.Length > 1 ? kv[1].Split(',') : new[] { "false", "true" };
            // Far-away map chunks are inactive; go there first so the target's bounds are real.
            yield return ApproachTarget();
            bool targeted = AimAtTarget(out Vector3 wall, out Vector3 along, out int shots);
            if (targeted)
                values = Enumerable.Range(0, shots).Select(i => i.ToString()).ToArray();
            string burst = Environment.GetEnvironmentVariable("TABG_AUTOJOIN_BURST");
            float[] delays = string.IsNullOrEmpty(burst)
                ? new[] { 0.12f }
                : burst.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            object original = entry?.BoxedValue;
            int shot = 0;
            foreach (string v in values)
            {
                if (entry != null) entry.SetSerializedValue(v);
                for (int f = 0; f < 5; f++) yield return null;
                var cam = Camera.main;
                if (cam == null) yield break;
                Vector3 fwd = cam.transform.forward;
                fwd.y = 0f;
                Vector3 pos = cam.transform.position + fwd.normalized * distance;
                if (Physics.Raycast(pos + Vector3.up * 50f, Vector3.down, out RaycastHit hit, 200f)) pos = hit.point + Vector3.up * 0.3f;
                if (targeted)
                    pos = wall + along * (shot - (shots - 1) * 0.5f) * 3.2f + Vector3.up * Mathf.Min(shot, 2);
                ParticleSystem spawned = null;
                if (smoke != null)
                {
                    spawned = Instantiate(smoke, pos, smoke.transform.rotation);
                    spawned.gameObject.SetActive(true);
                    spawned.Play(true);
                }
                else
                {
                    Instantiate(prefab, pos, Quaternion.identity);
                }
                Logger.LogInfo($"explode: {prefabName} at {pos} ({(entry == null ? "" : kv[0] + "=" + v)})");
                if (targeted) LogWallProbe("before", pos);
                string label = v == "false" ? "off" : v == "true" ? "on" : v;
                float start = Time.realtimeSinceStartup;
                for (int d = 0; d < delays.Length; d++)
                {
                    while (Time.realtimeSinceStartup - start < delays[d]) yield return null;
                    yield return new WaitForEndOfFrame();
                    var tex = ScreenCapture.CaptureScreenshotAsTexture();
                    string file = delays.Length == 1 ? $"boom-{shot}-{label}.png" : $"boom-{shot}-{label}-{delays[d].ToString("0.00", CultureInfo.InvariantCulture)}s.png";
                    File.WriteAllBytes(Path.Combine(Paths.BepInExRootPath, file), ImageConversion.EncodeToPNG(tex));
                    Destroy(tex);
                    if (spawned != null)
                    {
                        var r = spawned.GetComponent<ParticleSystemRenderer>();
                        Logger.LogInfo($"smoke t={delays[d]:0.00}: particles={spawned.particleCount} rendererEnabled={r.enabled} center={r.bounds.center} size={r.bounds.size}");
                    }
                }
                float t = Time.realtimeSinceStartup + 3f;
                while (Time.realtimeSinceStartup < t) yield return null;
                if (targeted) LogWallProbe("after", pos);
                shot++;
            }
            if (entry != null) entry.BoxedValue = original;
        }

        private static BepInEx.Configuration.ConfigEntryBase FindSetting(string path)
        {
            string[] parts = path.Split('/');
            if (parts.Length == 3 && BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(parts[0], out var info) && info.Instance != null)
            {
                var def = new BepInEx.Configuration.ConfigDefinition(parts[1], parts[2]);
                if (info.Instance.Config.ContainsKey(def)) return info.Instance.Config[def];
            }
            return null;
        }

        private System.Collections.IEnumerator Bench(string setting, float seconds, int rounds)
        {
            string[] kv = setting.Split(new[] { '=' }, 2);
            var entry = FindSetting(kv[0]);
            if (entry == null)
            {
                Logger.LogWarning($"bench: no setting {kv[0]}");
                yield break;
            }
            string[] values = kv.Length > 1 ? kv[1].Split(',') : new[] { "false", "true" };
            object original = entry.BoxedValue;
            _benching = true;
            int vsync = QualitySettings.vSyncCount, target = Application.targetFrameRate;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            Logger.LogInfo($"[bench] {kv[0]} values {string.Join(",", values)}, {rounds} rounds x {seconds}s, " +
                $"{Screen.width}x{Screen.height}, {SystemInfo.graphicsDeviceName}, vsync was {vsync}, target was {target}");
            var times = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<float>>();
            foreach (string v in values) times[v] = new System.Collections.Generic.List<float>();
            for (int r = 0; r < rounds; r++)
            {
                foreach (string v in values)
                {
                    entry.SetSerializedValue(v);
                    float settle = Time.realtimeSinceStartup + 1f;
                    while (Time.realtimeSinceStartup < settle) yield return null;
                    long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    Logger.LogInfo($"[bench] phase {v} {ms} {ms + (long)(seconds * 1000)}");
                    float end = Time.realtimeSinceStartup + seconds;
                    float last = Time.realtimeSinceStartup;
                    while (Time.realtimeSinceStartup < end)
                    {
                        yield return null;
                        float now = Time.realtimeSinceStartup;
                        times[v].Add(now - last);
                        last = now;
                    }
                }
            }
            entry.BoxedValue = original;
            QualitySettings.vSyncCount = vsync;
            Application.targetFrameRate = target;
            _benching = false;
            foreach (string v in values)
            {
                var t = times[v];
                t.Sort();
                float sum = 0f;
                foreach (float x in t) sum += x;
                float avg = sum / Math.Max(1, t.Count);
                // 1 % low: average of the slowest 1 % of frames.
                int n = Math.Max(1, t.Count / 100);
                float slow = 0f;
                for (int i = t.Count - n; i < t.Count; i++) slow += t[i];
                slow /= n;
                Logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "[bench] {0}={1}: {2:0.0} fps avg ({3:0.00} ms), 1% low {4:0.0} fps ({5:0.00} ms), {6} frames",
                    kv[0], v, 1f / avg, avg * 1000f, 1f / slow, slow * 1000f, t.Count));
            }
        }

        private System.Collections.IEnumerator CompareShots(int index, string setting)
        {
            // "guid/Section/Key" (bool: off then on) or "guid/Section/Key=v1,v2,..." (any type).
            string[] kv = setting.Split(new[] { '=' }, 2);
            string[] parts = kv[0].Split('/');
            BepInEx.Configuration.ConfigEntryBase entry = null;
            if (parts.Length == 3 && BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(parts[0], out var info) && info.Instance != null)
            {
                var def = new BepInEx.Configuration.ConfigDefinition(parts[1], parts[2]);
                if (info.Instance.Config.ContainsKey(def)) entry = info.Instance.Config[def];
            }
            if (entry == null)
            {
                Logger.LogWarning($"compare: no setting {kv[0]}");
                yield break;
            }
            string[] values = kv.Length > 1 ? kv[1].Split(',') : new[] { "false", "true" };
            object original = entry.BoxedValue;
            foreach (string v in values)
            {
                entry.SetSerializedValue(v);
                for (int f = 0; f < 5; f++) yield return null;
                float.TryParse(Environment.GetEnvironmentVariable("TABG_AUTOJOIN_SETTLE"), NumberStyles.Float, CultureInfo.InvariantCulture, out float settle);
                float settled = Time.realtimeSinceStartup + settle;
                while (Time.realtimeSinceStartup < settled) yield return null;
                // CaptureScreenshot(path) lands an unpredictable number of frames later under
                // Proton, so grab this very frame synchronously instead.
                yield return new WaitForEndOfFrame();
                var tex = ScreenCapture.CaptureScreenshotAsTexture();
                string label = v == "false" ? "off" : v == "true" ? "on" : v;
                string path = Path.Combine(Paths.BepInExRootPath, $"autojoin-{index}-{label}.png");
                File.WriteAllBytes(path, ImageConversion.EncodeToPNG(tex));
                Destroy(tex);
                Logger.LogInfo($"screenshot {path}");
            }
            entry.BoxedValue = original;
        }

        /// <summary>Is the wall at the blast still solid for collisions and bullets?</summary>
        private void LogWallProbe(string when, Vector3 blast)
        {
            Vector3 toCentre = _lookAt - blast;
            toCentre.y = 0f;
            toCentre.Normalize();
            Vector3 from = blast - toCentre * 2f;
            bool hit = Physics.Raycast(from, toCentre, out RaycastHit h, 60f, ~0, QueryTriggerInteraction.Ignore);
            Logger.LogInfo($"wall probe {when}: {(hit ? $"hit {h.collider.name} at {h.distance:0.0} m" : "nothing within 60 m")}");
        }

        private bool _lookingAt;
        private Vector3 _lookAt;

        private System.Collections.IEnumerator ApproachTarget()
        {
            string target = Environment.GetEnvironmentVariable("TABG_AUTOJOIN_TARGET");
            if (string.IsNullOrEmpty(target) || Player.localPlayer == null)
                yield break;
            string name = target.Split('@')[0];
            var far = Resources.FindObjectsOfTypeAll<Transform>().FirstOrDefault(t => t.gameObject.scene.IsValid()
                && t.name.StartsWith(name, StringComparison.Ordinal) && !IsUnder(t, "BossFights"));
            if (far == null)
                yield break;
            Player.localPlayer.MovePlayer(far.position + Vector3.up * 40f);
            float until = Time.realtimeSinceStartup + 2.5f;
            while (Time.realtimeSinceStartup < until) yield return null;
        }

        private static bool IsUnder(Transform t, string parent)
        {
            for (; t != null; t = t.parent)
                if (t.name.StartsWith(parent, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>Teleports next to the TABG_AUTOJOIN_TARGET building, facing its wall.</summary>
        private bool AimAtTarget(out Vector3 wall, out Vector3 along, out int shots)
        {
            wall = along = Vector3.zero;
            shots = 1;
            string target = Environment.GetEnvironmentVariable("TABG_AUTOJOIN_TARGET");
            if (string.IsNullOrEmpty(target) || Player.localPlayer == null)
                return false;
            string[] at = target.Split('@');
            if (at.Length > 1) shots = int.Parse(at[1], CultureInfo.InvariantCulture);
            var found = FindObjectsOfType<Transform>().FirstOrDefault(t => t.name.StartsWith(at[0], StringComparison.Ordinal)
                && !IsUnder(t, "BossFights") && t.GetComponentsInChildren<MeshRenderer>(true).Length > 0);
            if (found == null)
            {
                Logger.LogWarning($"target: nothing named {at[0]}");
                return false;
            }
            var renderers = found.GetComponentsInChildren<MeshRenderer>(true);
            var bounds = renderers[0].bounds;
            foreach (var r in renderers) bounds.Encapsulate(r.bounds);
            // Approach from the side that is open.
            Vector3 centre = bounds.center;
            Vector3 best = Vector3.forward;
            float bestRoom = -1f;
            foreach (Vector3 d in new[] { Vector3.forward, Vector3.back, Vector3.right, Vector3.left })
            {
                Vector3 outside = centre + d * (Mathf.Abs(Vector3.Dot(bounds.extents, d)) + 3f);
                float room = Physics.Raycast(outside, d, out RaycastHit hit, 60f) ? hit.distance : 60f;
                if (room > bestRoom)
                {
                    bestRoom = room;
                    best = d;
                }
            }
            float reach = Mathf.Abs(Vector3.Dot(bounds.extents, best));
            Vector3 stand = centre + best * (reach + Mathf.Min(bestRoom * 0.7f, 26f));
            stand.y = bounds.min.y;
            if (Physics.Raycast(new Vector3(stand.x, bounds.max.y + 8f, stand.z), Vector3.down, out RaycastHit ground, 200f)) stand.y = ground.point.y;
            Player.localPlayer.MovePlayer(stand + Vector3.up * 2f);
            wall = centre + best * reach;
            wall.y = bounds.min.y + 2f;
            along = Vector3.Cross(best, Vector3.up);
            _lookAt = centre;
            _lookingAt = true;
            Logger.LogInfo($"target: {found.name} at {centre} size {bounds.size}, standing at {stand}");
            return true;
        }

        private void LateUpdate()
        {
            if (!_lookingAt || Player.localPlayer == null || Player.localPlayer.m_playerCamera == null)
                return;
            Transform cam = Player.localPlayer.m_playerCamera.transform;
            cam.rotation = Quaternion.LookRotation(_lookAt - cam.position);
        }

        private bool _gotoDone;

        private bool _sequenceStarted;

        private System.Collections.IEnumerator Sequence(string[] names, float distance, float shot1, float shot2)
        {
            var prefabs = new System.Collections.Generic.Dictionary<string, GameObject>();
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                if (!go.scene.IsValid() && go.transform.parent == null) prefabs[go.name] = go;
            foreach (string name in names)
            {
                if (!prefabs.TryGetValue(name, out GameObject prefab))
                {
                    Logger.LogWarning($"sequence: no loaded prefab {name}");
                    continue;
                }
                var cam = Camera.main;
                if (cam == null) yield break;
                Vector3 fwd = cam.transform.forward;
                fwd.y = 0f;
                Vector3 pos = cam.transform.position + fwd.normalized * distance;
                if (Physics.Raycast(pos + Vector3.up * 50f, Vector3.down, out RaycastHit hit, 200f)) pos = hit.point + Vector3.up * 0.3f;
                var spawned = Instantiate(prefab, pos, Quaternion.identity);
                Logger.LogInfo($"sequence: {name} at {pos}");
                float start = Time.realtimeSinceStartup;
                for (int i = 1; i <= 2; i++)
                {
                    float at = i == 1 ? shot1 : shot2;
                    while (Time.realtimeSinceStartup - start < at) yield return null;
                    yield return new WaitForEndOfFrame();
                    var tex = ScreenCapture.CaptureScreenshotAsTexture();
                    File.WriteAllBytes(Path.Combine(Paths.BepInExRootPath, $"seq-{name.Replace(' ', '_')}-{i}.png"), ImageConversion.EncodeToPNG(tex));
                    Destroy(tex);
                }
                if (spawned != null) Destroy(spawned);
                float next = Time.realtimeSinceStartup + 2.5f;
                while (Time.realtimeSinceStartup < next) yield return null;
            }
        }

        private void TickSequence()
        {
            if (_sequenceStarted) return;
            string text = Environment.GetEnvironmentVariable("TABG_AUTOJOIN_SEQUENCE");
            if (string.IsNullOrEmpty(text)) { _sequenceStarted = true; return; }
            string[] at = text.Split('@');
            string[] nums = at[1].Split(',');
            if (_sinceJoin < float.Parse(nums[0], CultureInfo.InvariantCulture)) return;
            _sequenceStarted = true;
            StartCoroutine(Sequence(at[0].Split(','), float.Parse(nums[1], CultureInfo.InvariantCulture),
                float.Parse(nums[2], CultureInfo.InvariantCulture), float.Parse(nums[3], CultureInfo.InvariantCulture)));
        }

        private void TickGoto()
        {
            if (_gotoDone) return;
            string target = Environment.GetEnvironmentVariable("TABG_AUTOJOIN_GOTO");
            if (string.IsNullOrEmpty(target)) { _gotoDone = true; return; }
            string[] at = target.Split('@');
            float when = at.Length > 1 ? float.Parse(at[1], CultureInfo.InvariantCulture) : 5f;
            if (_sinceJoin < when || Player.localPlayer == null) return;
            _gotoDone = true;

            float bestScore = -1f;
            Vector3 best = Vector3.zero;
            int terrains = 0;
            foreach (var t in Terrain.activeTerrains)
            {
                if (t == null || t.terrainData == null) continue;
                terrains++;
                var data = t.terrainData;
                var layers = data.terrainLayers;
                for (int x = 1; x < 8; x++)
                for (int z = 1; z < 8; z++)
                {
                    float score = 0f;
                    for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        int ax = Mathf.Clamp(data.alphamapWidth * (x * 8 + dx * 2) / 64, 0, data.alphamapWidth - 1);
                        int az = Mathf.Clamp(data.alphamapHeight * (z * 8 + dz * 2) / 64, 0, data.alphamapHeight - 1);
                        var map = data.GetAlphamaps(ax, az, 1, 1);
                        for (int l = 0; l < layers.Length; l++)
                            if (layers[l] != null && layers[l].name.Contains(at[0])) score += map[0, 0, l];
                    }
                    if (score <= bestScore) continue;
                    Vector3 spot = t.GetPosition() + new Vector3(data.size.x * x / 8f, 0f, data.size.z * z / 8f);
                    spot.y = t.GetPosition().y + t.SampleHeight(spot);
                    if (spot.y < 170f) continue;    // the sea bed is painted too
                    bestScore = score;
                    best = spot;
                }
            }
            Logger.LogInfo($"goto {at[0]}: best {best} score {bestScore / 9f:0.00} of {terrains} terrains");
            if (bestScore > 0f) Player.localPlayer.MovePlayer(best + Vector3.up * 2f);
        }

        private void TickInventory()
        {
            Player me = Player.localPlayer;
            if (me == null)
            {
                return;
            }

            if (_healItem >= 0 && me.m_playerDeath.health < 100f && !me.m_playerDeath.isDown && !me.m_playerDeath.dead)
            {
                int have = me.m_inventory.GetPickupTypeCountInInventory(_healItem);
                Logger.LogInfo($"Hurt ({me.m_playerDeath.health:F0} HP): healing with item {_healItem}, carrying {have}");
                InventoryUI.RequestHealingUsingItemType(_healItem);
                _healItem = -1;
            }

            if (_dropAt >= 0f && _sinceJoin >= _dropAt)
            {
                _dropAt = -1f;
                int have = me.m_inventory.GetPickupTypeCountInInventory(_dropItem);
                Logger.LogInfo($"Dropping {_dropCount} x item {_dropItem}, carrying {have}");
                me.m_interactionHandler.Drop(_dropItem, _dropCount);
            }
        }

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            // The game resets these when its settings load, so keep them applied (except while benching).
            if (_fpsCap > 0 && !_benching)
            {
                Application.runInBackground = true;
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = _fpsCap;
            }

            if (_sinceJoin < 0f)
            {
                // Joining before the Epic login finishes fails with "Failed generating token".
                if (ServerConnector.Instance == null || !SteamManager.Initialized || EOSSDKComponent.LocalUser == null || !EOSSDKComponent.LocalUser.IsValid())
                {
                    return;
                }

                _sinceMenu = _sinceMenu < 0f ? 0f : _sinceMenu + dt;
                if (_sinceMenu < 8f)
                {
                    return;
                }

                _sinceJoin = 0f;
                if (_target.StartsWith("code:", StringComparison.Ordinal))
                {
                    string code = _target.Substring("code:".Length);
                    Logger.LogInfo($"Joining relay code {code} (scene {SceneManager.GetActiveScene().name})");
                    ServerConnector.Instance.ConnectToRelayServer(code);
                    return;
                }
                string[] parts = _target.Split(':');
                ushort port = parts.Length > 1 ? ushort.Parse(parts[1]) : (ushort)7777;
                Logger.LogInfo($"Joining {parts[0]}:{port} (scene {SceneManager.GetActiveScene().name})");
                ServerConnector.Instance.ConnectToServerIP(parts[0], port);
                return;
            }

            _sinceJoin += dt;
            TickJumpAndSeat(dt);
            TickGoto();
            TickSequence();
            if (!_explodeStarted)
            {
                string ex = Environment.GetEnvironmentVariable("TABG_AUTOJOIN_EXPLODE");
                if (string.IsNullOrEmpty(ex)) _explodeStarted = true;
                else
                {
                    string[] at = ex.Split('@');
                    string[] nums = at.Length > 1 ? at[1].Split(',') : new string[0];
                    float when = nums.Length > 0 ? float.Parse(nums[0], CultureInfo.InvariantCulture) : 20f;
                    if (_sinceJoin >= when)
                    {
                        _explodeStarted = true;
                        float dist = nums.Length > 1 ? float.Parse(nums[1], CultureInfo.InvariantCulture) : 18f;
                        StartCoroutine(Explosions(at[0], dist));
                    }
                }
            }
            if (!_benchStarted)
            {
                string bench = Environment.GetEnvironmentVariable("TABG_AUTOJOIN_BENCH");
                if (!string.IsNullOrEmpty(bench))
                {
                    string[] at = bench.Split('@');
                    string[] nums = at.Length > 1 ? at[1].Split(',') : new string[0];
                    float start = nums.Length > 0 ? float.Parse(nums[0], CultureInfo.InvariantCulture) : 15f;
                    if (_sinceJoin >= start)
                    {
                        _benchStarted = true;
                        float secs = nums.Length > 1 ? float.Parse(nums[1], CultureInfo.InvariantCulture) : 4f;
                        int rounds = nums.Length > 2 ? int.Parse(nums[2]) : 3;
                        StartCoroutine(Bench(at[0], secs, rounds));
                    }
                }
                else _benchStarted = true;
            }
            if (_chatAt >= 0f && _sinceJoin >= _chatAt)
            {
                _chatAt = -1f;
                Logger.LogInfo($"Saying: {_chatText}");
                FindObjectOfType<PhotonServerHandler>()?.SendChatMessage(_chatText);
            }
            TickInventory();
            // -batchmode -nographics (the test script's default) has nothing to capture.
            if (!Application.isBatchMode && _shot < _shotTimes.Length && _sinceJoin >= _shotTimes[_shot])
            {
                string compare = Environment.GetEnvironmentVariable("TABG_AUTOJOIN_COMPARE");
                if (!string.IsNullOrEmpty(compare))
                {
                    StartCoroutine(CompareShots(_shot, compare));
                }
                else
                {
                    string path = Path.Combine(Paths.BepInExRootPath, $"autojoin-{_shot}.png");
                    ScreenCapture.CaptureScreenshot(path);
                    Logger.LogInfo($"screenshot {path}");
                }
                _shot++;
            }

            if (_sinceJoin >= _nextStatus)
            {
                _nextStatus = _sinceJoin + 5f;
                Player p = Player.localPlayer;
                string where = p != null ? p.transform.position.ToString() : "no local player";
                var cars = HarmonyLib.AccessTools.Field(typeof(PhotonServerHandler), "m_Cars")?.GetValue(PhotonServerHandler.instance) as System.Collections.ICollection;
                var visual = FindObjectsOfType<Car>();
                string nearest = "";
                if (p != null && visual.Length > 0)
                {
                    Car best = null;
                    foreach (Car c in visual)
                        if (best == null || (c.transform.position - p.transform.position).sqrMagnitude < (best.transform.position - p.transform.position).sqrMagnitude)
                            best = c;
                    nearest = $" nearest={best.name}@{Vector3.Distance(best.transform.position, p.transform.position):F0}m";
                }
                Logger.LogInfo($"t={_sinceJoin:F0}s scene={SceneManager.GetActiveScene().name} player={where} index={ServerConnector.Instance?.GetPlayerIndex()} cars={cars?.Count ?? -1} visual={visual.Length}{nearest}");
            }

            if (_quitAfter >= 0f && _sinceJoin >= _quitAfter)
            {
                Logger.LogInfo("Quitting.");
                Application.Quit();
                enabled = false;
            }
        }
    }

    [HarmonyPatch(typeof(ServerConnector), nameof(ServerConnector.OnEvent))]
    internal static class LogEvents
    {
        private static void Prefix(ClientPackage clientPackage)
        {
            DevAutoJoinPlugin.Log?.LogInfo($"recv {(byte)clientPackage.Code} {clientPackage.Code} len={clientPackage.Buffer?.Length ?? -1}");
            if (clientPackage.Code == EventCode.ChatMessage && clientPackage.Buffer != null && clientPackage.Buffer.Length > 2)
                DevAutoJoinPlugin.Log?.LogInfo("chat: " + System.Text.Encoding.Unicode.GetString(clientPackage.Buffer, 2, Math.Min(clientPackage.Buffer[1], clientPackage.Buffer.Length - 2)));
            if (clientPackage.Code == EventCode.GameStateChanged && clientPackage.Buffer != null && clientPackage.Buffer.Length > 0 && clientPackage.Buffer[0] == (byte)GameState.Flying)
            {
                DevAutoJoinPlugin.FlyingSince = Time.unscaledTime;
            }
        }
    }
}
