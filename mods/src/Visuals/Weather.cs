using System.Collections.Generic;
using UnityEngine;

namespace TabgInstaller.Visuals
{
    /// <summary>
    /// Weather that follows the biome under the player. The biome is read from the terrain's painted
    /// layers (L_Snow, L_Sand_Deser, L_Grass); each biome has its own list of events, and every few minutes one
    /// is picked from the game's synced time of day, so all players with this mod get the same weather
    /// without any network traffic. Near biome borders the events blend. Purely cosmetic and local.
    /// </summary>
    internal static class Weather
    {
        private sealed class Event
        {
            public string Name;
            public float Weight, Snow, Rain, Dust, FogRange, Overcast, Wind;
            public bool Lightning;
            public Color Fog;
        }

        private static readonly Color SnowFog = new Color(0.8f, 0.84f, 0.92f);
        private static readonly Color RainFog = new Color(0.55f, 0.6f, 0.66f);
        private static readonly Color StormFog = new Color(0.4f, 0.44f, 0.52f);
        private static readonly Color MistFog = new Color(0.78f, 0.81f, 0.84f);
        private static readonly Color SandFog = new Color(0.78f, 0.6f, 0.37f);

        private static readonly Event[] SnowEvents =
        {
            new Event { Name = "Flurries", Weight = 45f, Snow = 0.25f, FogRange = 260f, Overcast = 0.2f, Wind = 1.5f, Fog = SnowFog },
            new Event { Name = "Snowfall", Weight = 35f, Snow = 0.6f, FogRange = 170f, Overcast = 0.45f, Wind = 2.5f, Fog = SnowFog },
            new Event { Name = "Blizzard", Weight = 20f, Snow = 1f, FogRange = 65f, Overcast = 0.8f, Wind = 11f, Fog = SnowFog },
        };

        private static readonly Event[] GrassEvents =
        {
            new Event { Name = "Clear", Weight = 45f, Fog = MistFog },
            new Event { Name = "Overcast", Weight = 15f, Overcast = 0.55f, FogRange = 700f, Fog = RainFog },
            new Event { Name = "Mist", Weight = 15f, Overcast = 0.3f, FogRange = 180f, Fog = MistFog },
            new Event { Name = "Rain", Weight = 15f, Rain = 0.7f, Overcast = 0.7f, FogRange = 260f, Wind = 2f, Fog = RainFog },
            new Event { Name = "Storm", Weight = 10f, Rain = 1f, Overcast = 0.95f, FogRange = 170f, Wind = 7f, Lightning = true, Fog = StormFog },
        };

        private static readonly Event[] SandEvents =
        {
            new Event { Name = "Clear", Weight = 55f, Fog = SandFog },
            new Event { Name = "Haze", Weight = 25f, Dust = 0.3f, Overcast = 0.1f, FogRange = 320f, Wind = 5f, Fog = SandFog },
            new Event { Name = "Sandstorm", Weight = 20f, Dust = 1f, Overcast = 0.55f, FogRange = 70f, Wind = 16f, Fog = SandFog },
        };

        private const float SlotSeconds = 180f;
        private const float SampleInterval = 1f;
        private const float BiomeRadius = 40f;
        private const float FadeSeconds = 12f;
        private const float DustLife = 5f;
        private const float DustCount = 500f;

        // What the image effect reads.
        internal static float Fog, Overcast, Flash;
        internal static Color FogColor = SnowFog;
        internal static float Snow, Rain, Wind;
        internal static float LensRain, LensSnow, LensSand, Wet;
        internal static float Sand, Gust;
        internal static Vector2 WindOffset;
        internal static Color PrecipColor = Color.white;

        private static float _snow, _rain, _dust, _wind, _fogDensity, _overcast, _lightning;
        private static Color _fogColor = SnowFog;
        private static float _targetSnow, _targetRain, _targetDust, _targetWind, _targetFog, _targetOvercast, _targetLightning;
        private static Color _targetColor = SnowFog;

        private static float _nextSample, _nextRoofCheck, _roof, _roofTarget, _nextFlash;
        private static float _flashStart = float.NegativeInfinity;
        private static string _force = "", _logged = "";
        private static readonly Dictionary<int, int[]> LayerKinds = new Dictionary<int, int[]>();
        private static readonly RaycastHit[] RoofHits = new RaycastHit[8];

        private static GameObject _root;
        private static ParticleSystem _dustFx;
        private static Material _dustMat;

        private static IEnumerable<Event> AllEvents()
        {
            foreach (var e in SnowEvents) yield return e;
            foreach (var e in GrassEvents) yield return e;
            foreach (var e in SandEvents) yield return e;
        }

        internal static void LateUpdate()
        {
            var p = VisualsPlugin.Instance;
            var cam = Camera.main;
            bool on = p != null && p.Enabled.Value && p.WeatherEnabled.Value && p.WeatherMaterial != null && cam != null;
            if (!on)
            {
                ClearTargets();
                Snap();
                Publish(p, 1f);
                ApplyParticles(null, 1f);
                Wet = 0f;
                ApplyShadows();
                return;
            }

            float now = Time.unscaledTime;
            string force = p.WeatherForce.Value;
            bool forced = force != _force;
            if (forced) _force = force;
            if (now >= _nextSample || forced)
            {
                _nextSample = now + SampleInterval;
                Sample(p, cam.transform.position);
            }
            if (forced)
            {
                Snap();
                LensRain = LensSnow = LensSand = 0f;
                Wet = _rain;
            }

            float k = 1f - Mathf.Exp(-Time.unscaledDeltaTime / FadeSeconds);
            _snow = Mathf.Lerp(_snow, _targetSnow, k);
            _rain = Mathf.Lerp(_rain, _targetRain, k);
            _dust = Mathf.Lerp(_dust, _targetDust, k);
            _wind = Mathf.Lerp(_wind, _targetWind, k);
            _fogDensity = Mathf.Lerp(_fogDensity, _targetFog, k);
            _overcast = Mathf.Lerp(_overcast, _targetOvercast, k);
            _lightning = Mathf.Lerp(_lightning, _targetLightning, k);
            _fogColor = Color.Lerp(_fogColor, _targetColor, k);

            float altitude = AltitudeFade(cam.transform.position);
            Publish(p, altitude);
            DimTheSun();
            TickLightning(now);
            CheckRoof(now, cam.transform.position);
            EnsureParticles(p);
            ApplyParticles(cam, altitude);
        }

        private static void ClearTargets()
        {
            _targetSnow = _targetRain = _targetDust = _targetWind = _targetFog = _targetOvercast = _targetLightning = 0f;
            _targetColor = Color.black;
        }

        private static void Snap()
        {
            _snow = _targetSnow; _rain = _targetRain; _dust = _targetDust; _wind = _targetWind;
            _fogDensity = _targetFog; _overcast = _targetOvercast; _lightning = _targetLightning; _fogColor = _targetColor;
        }

        private static void Publish(VisualsPlugin p, float altitude)
        {
            // Gusts: the visibility surges and drops as the wind comes and goes, most of all in sand.
            float t = Time.unscaledTime;
            Gust = Mathf.Clamp01(0.5f + 0.5f * Mathf.Sin(t * 0.37f + Mathf.Sin(t * 0.13f) * 2f) * Mathf.Sin(t * 0.23f + 1.3f) + 0.25f * Mathf.Sin(t * 1.1f));
            Fog = _fogDensity * (p != null ? p.WeatherFog.Value : 1f) * altitude * (1f + 0.6f * (Gust - 0.5f) * Mathf.Clamp01(_dust * 2f));
            Overcast = _overcast;
            Flash = Mathf.Max(0f, 1f - (Time.unscaledTime - _flashStart) / 0.28f);
            if (Time.unscaledTime - _flashStart > 0.12f && Time.unscaledTime - _flashStart < 0.2f) Flash *= 1.6f;
            // At night fog is dark too.
            var cycle = DayAndNightCycle.instance;
            float light = cycle != null && cycle.light != null ? Mathf.Clamp01(cycle.light.intensity) : 1f;
            FogColor = _fogColor * Mathf.Lerp(0.2f, 1f, light);
        }

        // ------------------------------------------------------------ choosing the weather

        private static void Sample(VisualsPlugin p, Vector3 at)
        {
            if (p.WeatherForce.Value != "Auto")
            {
                foreach (var e in AllEvents())
                    if (e.Name == p.WeatherForce.Value)
                    {
                        ClearTargets();
                        Add(e, 1f);
                        Log(p, "forced " + e.Name);
                        return;
                    }
            }

            ReadBiome(at, out float snow, out float sand, out float grass);
            float total = snow + sand + grass;
            ClearTargets();
            if (total <= 0.001f)
            {
                grass = total = 1f;
            }
            int slot = TimeSlot();
            Event snowEvent = Pick(SnowEvents, 1, slot), sandEvent = Pick(SandEvents, 2, slot), grassEvent = Pick(GrassEvents, 3, slot);
            Add(snowEvent, snow / total);
            Add(sandEvent, sand / total);
            Add(grassEvent, grass / total);
            Log(p, $"biome snow {snow / total:0.00} sand {sand / total:0.00} grass {grass / total:0.00}: {(snow >= sand && snow >= grass ? snowEvent.Name : sand >= grass ? sandEvent.Name : grassEvent.Name)}");
        }

        private static void Add(Event e, float weight)
        {
            _targetSnow += e.Snow * weight;
            _targetRain += e.Rain * weight;
            _targetDust += e.Dust * weight;
            _targetWind += e.Wind * weight;
            _targetFog += (e.FogRange > 0f ? 1f / e.FogRange : 0f) * weight;
            _targetOvercast += e.Overcast * weight;
            if (e.Lightning) _targetLightning += weight;
            _targetColor += e.Fog * weight;
        }

        private static void Log(VisualsPlugin p, string state)
        {
            if (state == _logged) return;
            _logged = state;
            p.Log($"[Visuals] weather: {state}");
        }

        private static int TimeSlot()
        {
            var cycle = DayAndNightCycle.instance;
            return cycle != null ? (int)(cycle.timeOfDay * cycle.dayLenght / SlotSeconds) : 0;
        }

        private static Event Pick(Event[] events, int biome, int slot)
        {
            unchecked
            {
                uint h = (uint)slot * 2654435761u ^ (uint)biome * 40503u;
                h ^= h >> 13; h *= 3266489917u; h ^= h >> 16;
                float total = 0f;
                foreach (var e in events) total += e.Weight;
                float roll = h % 10000u / 10000f * total;
                foreach (var e in events)
                {
                    roll -= e.Weight;
                    if (roll <= 0f) return e;
                }
                return events[events.Length - 1];
            }
        }

        private static void ReadBiome(Vector3 at, out float snow, out float sand, out float grass)
        {
            snow = sand = grass = 0f;
            for (int x = -1; x <= 1; x++)
            for (int z = -1; z <= 1; z++)
            {
                Vector3 point = at + new Vector3(x * BiomeRadius, 0f, z * BiomeRadius);
                var terrain = TerrainAt(point);
                if (terrain == null) continue;
                var data = terrain.terrainData;
                int[] kinds = Kinds(data);
                Vector3 local = point - terrain.GetPosition();
                int ax = Mathf.Clamp((int)(local.x / data.size.x * data.alphamapWidth), 0, data.alphamapWidth - 1);
                int az = Mathf.Clamp((int)(local.z / data.size.z * data.alphamapHeight), 0, data.alphamapHeight - 1);
                var map = data.GetAlphamaps(ax, az, 1, 1);
                for (int layer = 0; layer < kinds.Length && layer < map.GetLength(2); layer++)
                {
                    if (kinds[layer] == 0) snow += map[0, 0, layer];
                    else if (kinds[layer] == 1) sand += map[0, 0, layer];
                    else if (kinds[layer] == 2) grass += map[0, 0, layer];
                }
            }
        }

        private static Terrain TerrainAt(Vector3 point)
        {
            foreach (var t in Terrain.activeTerrains)
            {
                if (t == null || t.terrainData == null) continue;
                Vector3 local = point - t.GetPosition();
                Vector3 size = t.terrainData.size;
                if (local.x >= 0f && local.z >= 0f && local.x <= size.x && local.z <= size.z) return t;
            }
            return null;
        }

        private static int[] Kinds(TerrainData data)
        {
            int id = data.GetInstanceID();
            if (LayerKinds.TryGetValue(id, out int[] kinds)) return kinds;
            var layers = data.terrainLayers;
            kinds = new int[layers.Length];
            for (int i = 0; i < layers.Length; i++)
            {
                string name = layers[i] != null ? layers[i].name : "";
                // L_Sand_Deser* is the desert; L_Sand_Light is beaches and the sea bed, which get grassland weather.
                kinds[i] = name.Contains("Snow") ? 0 : name.Contains("Deser") ? 1 : name.Contains("Grass") || name.Contains("Sand") ? 2 : -1;
            }
            return LayerKinds[id] = kinds;
        }

        // ------------------------------------------------------------ the world's response

        /// <summary>1 near the ground, 0 from the plane: no blanket of fog when you look down from up there.</summary>
        private static float AltitudeFade(Vector3 at)
        {
            var terrain = TerrainAt(at);
            if (terrain == null) return 1f;
            return Mathf.InverseLerp(260f, 70f, at.y - terrain.SampleHeight(at) - terrain.GetPosition().y);
        }

        private static void DimTheSun()
        {
            ApplyShadows();
            // The day cycle sets these every Update, so scaling them once per LateUpdate never compounds.
            // Under cloud the sun all but goes, but the sky still lights everything: flat, shadowless light.
            var cycle = DayAndNightCycle.instance;
            if (cycle == null || !cycle.isActiveAndEnabled || cycle.light == null || _overcast <= 0.001f) return;
            cycle.light.intensity *= 1f - 0.8f * _overcast;
            float ambient = 1f - 0.12f * _overcast;
            RenderSettings.ambientSkyColor *= ambient;
            RenderSettings.ambientEquatorColor *= ambient;
            RenderSettings.ambientGroundColor *= ambient;
        }

        private static Light _shadowLight;
        private static float _shadowBase;

        private static void ApplyShadows()
        {
            var cycle = DayAndNightCycle.instance;
            if (cycle == null || cycle.light == null) return;
            if (cycle.light != _shadowLight)
            {
                _shadowLight = cycle.light;
                _shadowBase = cycle.light.shadowStrength;
            }
            cycle.light.shadowStrength = _shadowBase * (1f - 0.85f * _overcast);
        }

        private static void TickLightning(float now)
        {
            if (_lightning < 0.5f) return;
            if (now < _nextFlash) return;
            _nextFlash = now + UnityEngine.Random.Range(6f, 18f);
            _flashStart = now;
        }

        private static void CheckRoof(float now, Vector3 at)
        {
            if (now >= _nextRoofCheck)
            {
                _nextRoofCheck = now + 0.25f;
                _roofTarget = 0f;
                int n = Physics.RaycastNonAlloc(at + Vector3.up * 0.3f, Vector3.up, RoofHits, 30f, ~0, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n; i++)
                    if (RoofHits[i].collider.GetComponentInParent<Player>() == null)
                    {
                        _roofTarget = 1f;
                        break;
                    }
            }
            _roof = Mathf.MoveTowards(_roof, _roofTarget, Time.unscaledDeltaTime / 0.35f);
        }

        // ------------------------------------------------------------ particles

        private static void EnsureParticles(VisualsPlugin p)
        {
            if (_root != null) return;
            _root = new GameObject("TABG Visuals weather");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _dustFx = Make("dust", p, Dot(), new Vector3(90f, 14f, 90f), 750, DustLife, 6f, 14f, 0.3f, out _dustMat);
            var dustMain = _dustFx.main;
            dustMain.startColor = new Color(1f, 1f, 1f, 0.16f);
        }

        private static ParticleSystem Make(string name, VisualsPlugin p, Texture2D dot, Vector3 box, int max, float life, float minSize, float maxSize, float speed, out Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_root.transform, false);
            go.SetActive(false);
            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.loop = true;
            main.playOnAwake = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = max;
            main.startLifetime = life;
            main.startSize = new ParticleSystem.MinMaxCurve(minSize, maxSize);
            main.startSpeed = 0f;
            main.useUnscaledTime = true;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = box;

            var emission = ps.emission;
            emission.rateOverTime = 0f;

            var velocity = ps.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.World;
            // Lighter flakes and finer drops fall slower; all three axes must use the same curve mode.
            velocity.x = new ParticleSystem.MinMaxCurve(0f, 0f);
            velocity.y = new ParticleSystem.MinMaxCurve(-speed * 1.3f, -speed * 0.7f);
            velocity.z = new ParticleSystem.MinMaxCurve(0f, 0f);

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.sortMode = ParticleSystemSortMode.None;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            material = new Material(p.WeatherMaterial) { hideFlags = HideFlags.HideAndDontSave, mainTexture = dot };
            renderer.sharedMaterial = material;
            go.SetActive(true);
            return ps;
        }

        private static Texture2D Dot()
        {
            const int Size = 32;
            var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
            {
                name = "TABG Visuals weather dot", hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp,
            };
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float d = new Vector2((x + 0.5f) / Size - 0.5f, (y + 0.5f) / Size - 0.5f).magnitude * 2f;
                float a = Mathf.Clamp01((1f - d) * 1.6f);
                texture.SetPixel(x, y, new Color(1f, 1f, 1f, a * a * (3f - 2f * a)));
            }
            texture.Apply();
            return texture;
        }

        private static void ApplyParticles(Camera cam, float altitude)
        {
            var p = VisualsPlugin.Instance;
            float amount = (p != null ? p.WeatherParticles.Value : 1f) * altitude;
            float visible = 1f - _roof;

            // Snow and rain are drawn by the image effect, which reads these.
            Snow = Mathf.Clamp01(_snow * amount) * visible;
            Rain = Mathf.Clamp01(_rain * amount) * visible;
            Sand = Mathf.Clamp01(_dust * amount) * visible;
            Wind = _wind;
            // The lens gets wet in a few seconds and dries much more slowly.
            LensRain = Approach(LensRain, Rain);
            LensSnow = Approach(LensSnow, Snow);
            LensSand = Approach(LensSand, Sand);
            // The ground soaks up over half a minute and dries over a couple of minutes.
            float wetTarget = Mathf.Clamp01(_rain * 1.3f) * altitude;
            Wet = Mathf.MoveTowards(Wet, wetTarget, (wetTarget > Wet ? 1f / 30f : 1f / 120f) * Time.unscaledDeltaTime);
            WindOffset += new Vector2(0.8f, 0.6f) * _wind * Time.unscaledDeltaTime;

            var cycle = DayAndNightCycle.instance;
            Color sun = cycle != null && cycle.light != null ? cycle.light.color * cycle.light.intensity : Color.white;
            Color lit = RenderSettings.ambientSkyColor * 0.9f + sun * 0.75f;
            Color clamped = new Color(Mathf.Min(1f, lit.r), Mathf.Min(1f, lit.g), Mathf.Min(1f, lit.b), 1f);
            PrecipColor = new Color(0.94f, 0.96f, 1f) * clamped;

            if (_root == null) return;
            if (cam != null)
            {
                Vector3 drift = new Vector3(0.8f, 0f, 0.6f) * _wind;
                _dustFx.transform.position = cam.transform.position + Flat(cam.transform.forward) * 14f
                    + Vector3.up * 5f - drift * DustLife * 0.5f;
            }
            Rate(_dustFx, _dust * DustCount * amount / DustLife, _wind);
            Tint(_dustMat, new Color(clamped.r * 0.86f, clamped.g * 0.7f, clamped.b * 0.48f, visible));
        }

        private static float Approach(float value, float target)
        {
            float rate = target > value ? 1f / 6f : 1f / 14f;
            return Mathf.MoveTowards(value, target, rate * Time.unscaledDeltaTime);
        }

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-4f ? v.normalized : Vector3.zero;
        }

        private static void Rate(ParticleSystem ps, float rate, float wind)
        {
            var emission = ps.emission;
            emission.rateOverTime = rate;
            var velocity = ps.velocityOverLifetime;
            velocity.x = new ParticleSystem.MinMaxCurve(wind * 0.68f, wind * 0.92f);
            velocity.z = new ParticleSystem.MinMaxCurve(wind * 0.51f, wind * 0.69f);
        }

        private static void Tint(Material m, Color c)
        {
            if (m != null) m.SetColor("_Tint", c);
        }
    }
}
