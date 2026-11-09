using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace TabgInstaller.Visuals
{
    /// <summary>
    /// Swaps the game's smoke-grenade particles for a raymarched volume. The particle system keeps
    /// simulating, only its renderer is off; every few frames an ellipsoid is fitted to the live
    /// particles, so the volume has the size, position, colour and lifetime of the original smoke
    /// (including anything CustomGrenades did to it) and hides exactly what the particles would.
    /// </summary>
    internal static class VolumetricSmoke
    {
        internal const int MaxDrawn = 8;
        private const string SmokeMaterial = "m_Smoke";
        private const float ScanInterval = 0.3f;
        private const float FitInterval = 0.08f;

        private sealed class Source
        {
            public ParticleSystem Ps;
            public ParticleSystemRenderer Renderer;
            public Vector3 Center;
            public float RadiusH, RadiusV;
            public float Density;
            public Color Albedo = new Color(0.8f, 0.8f, 0.8f);
            public int PeakCount;
            public bool Fitted;
            public float Distance;
        }

        private static readonly List<Source> Sources = new List<Source>();
        private static readonly Dictionary<int, bool> IsSmokeMaterial = new Dictionary<int, bool>();
        private static ParticleSystem.Particle[] _particles = new ParticleSystem.Particle[4096];
        private static float _nextScan, _nextFit;
        private static bool _wasOn;

        internal static void Update()
        {
            var p = VisualsPlugin.Instance;
            bool on = p != null && p.Enabled.Value && p.VolumetricSmoke.Value && p.SmokeMaterial != null;
            if (!on)
            {
                if (_wasOn) Restore();
                _wasOn = false;
                return;
            }
            _wasOn = true;

            float now = Time.unscaledTime;
            if (now >= _nextScan)
            {
                _nextScan = now + ScanInterval;
                Scan();
            }
            if (Sources.Count == 0 || now < _nextFit) return;
            _nextFit = now + FitInterval;
            for (int i = Sources.Count - 1; i >= 0; i--)
                if (!Fit(Sources[i])) Sources.RemoveAt(i);
        }

        private static void Scan()
        {
            foreach (var r in Object.FindObjectsOfType<ParticleSystemRenderer>())
            {
                var mat = r.sharedMaterial;
                if (mat == null || !IsSmoke(mat)) continue;
                var ps = r.GetComponent<ParticleSystem>();
                if (ps == null || ps.particleCount == 0 || Tracked(ps)) continue;
                Sources.Add(new Source { Ps = ps, Renderer = r });
            }
        }

        private static bool IsSmoke(Material mat)
        {
            int id = mat.GetInstanceID();
            if (!IsSmokeMaterial.TryGetValue(id, out bool smoke))
                IsSmokeMaterial[id] = smoke = mat.name.StartsWith(SmokeMaterial, System.StringComparison.Ordinal);
            return smoke;
        }

        private static bool Tracked(ParticleSystem ps)
        {
            foreach (var s in Sources)
                if (s.Ps == ps) return true;
            return false;
        }

        private static void Restore()
        {
            foreach (var s in Sources)
                if (s.Renderer != null) s.Renderer.enabled = true;
            Sources.Clear();
        }

        /// <summary>Refits one smoke to its particles; false once it is gone.</summary>
        private static bool Fit(Source s)
        {
            if (s.Ps == null || s.Renderer == null) return false;
            s.Renderer.enabled = false;
            int n = s.Ps.GetParticles(_particles);
            if (n == 0) return s.Ps.IsAlive(true) && !s.Fitted;

            Vector3 c = Vector3.zero;
            Vector4 color = Vector4.zero;
            for (int i = 0; i < n; i++)
            {
                c += _particles[i].position;
                color += (Vector4)(Color)_particles[i].GetCurrentColor(s.Ps);
            }
            c /= n;
            color /= n;

            // Mean plus spread of each particle's outer edge: robust to stragglers.
            float toMetres = HalfSizeInMetres(s);
            float sumH = 0f, sumH2 = 0f, sumV = 0f, sumV2 = 0f;
            for (int i = 0; i < n; i++)
            {
                float half = _particles[i].GetCurrentSize(s.Ps) * toMetres;
                Vector3 d = _particles[i].position - c;
                float h = Mathf.Sqrt(d.x * d.x + d.z * d.z) + half;
                float v = Mathf.Abs(d.y) + half;
                sumH += h; sumH2 += h * h;
                sumV += v; sumV2 += v * v;
            }
            float meanH = sumH / n, meanV = sumV / n;
            float radiusH = Mathf.Max(0.5f, meanH + 1.3f * Mathf.Sqrt(Mathf.Max(0f, sumH2 / n - meanH * meanH)));
            float radiusV = Mathf.Max(0.5f, meanV + 1.3f * Mathf.Sqrt(Mathf.Max(0f, sumV2 / n - meanV * meanV)));

            s.PeakCount = Mathf.Max(s.PeakCount, n);
            float amount = Mathf.Clamp01((float)n / s.PeakCount * 1.6f);
            Color albedo = Albedo(color);

            float blend = s.Fitted ? 0.25f : 1f;
            s.Center = Vector3.Lerp(s.Center, c, blend);
            s.RadiusH = Mathf.Lerp(s.RadiusH, radiusH, blend);
            s.RadiusV = Mathf.Lerp(s.RadiusV, radiusV, blend);
            s.Density = Mathf.Lerp(s.Density, amount, blend);
            s.Albedo = Color.Lerp(s.Albedo, albedo, blend);
            s.Fitted = true;

            var p = VisualsPlugin.Instance;
            if (p != null && p.DebugView.Value != 0 && Time.frameCount % 30 == 0)
                Debug.Log($"[Visuals] smoke '{s.Ps.name}' n={n} centre={s.Center} radiusH={s.RadiusH:0.0} radiusV={s.RadiusV:0.0} density={s.Density:0.00} albedo={s.Albedo}");
            return true;
        }

        /// <summary>
        /// Half a particle's width in metres per unit of its size: a size is a billboard's width, but
        /// only a scale factor on a mesh particle's model.
        /// </summary>
        private static float HalfSizeInMetres(Source s)
        {
            float half = 0.5f;
            if (s.Renderer.renderMode == ParticleSystemRenderMode.Mesh && s.Renderer.mesh != null)
            {
                Vector3 e = s.Renderer.mesh.bounds.extents;
                half = Mathf.Max(e.x, Mathf.Max(e.y, e.z));
            }
            var t = s.Ps.transform;
            switch (s.Ps.main.scalingMode)
            {
                case ParticleSystemScalingMode.Local: return half * t.localScale.x;
                case ParticleSystemScalingMode.Hierarchy: return half * t.lossyScale.x;
                default: return half;
            }
        }

        /// <summary>Grey particles become neutral white-grey smoke; coloured ones keep their hue.</summary>
        private static Color Albedo(Vector4 c)
        {
            float max = Mathf.Max(c.x, Mathf.Max(c.y, c.z));
            if (max <= 1e-3f) return new Color(0.74f, 0.74f, 0.74f);
            float min = Mathf.Min(c.x, Mathf.Min(c.y, c.z));
            float saturation = (max - min) / max;
            Color hue = new Color(c.x / max, c.y / max, c.z / max);
            return Color.Lerp(new Color(0.74f, 0.74f, 0.76f), hue * 0.7f, Mathf.SmoothStep(0.15f, 0.5f, saturation));
        }

        private struct Candidate
        {
            public Vector3 Center;
            public float RadiusH, RadiusV, Density, Shell, Distance;
            public Color Albedo, Emit;
        }

        private static readonly List<Candidate> Candidates = new List<Candidate>();

        /// <summary>Fills the shader arrays with the nearest smokes and grenade effects; returns how many.</summary>
        internal static int Fill(Vector3 cam, bool smoke, Vector4[] pos, Vector4[] info, Vector4[] color, Vector4[] emit, out float largest)
        {
            largest = 0f;
            Candidates.Clear();
            if (smoke)
                foreach (var s in Sources)
                {
                    if (!s.Fitted) continue;
                    Candidates.Add(new Candidate
                    {
                        Center = s.Center, RadiusH = s.RadiusH, RadiusV = s.RadiusV, Density = s.Density,
                        Albedo = s.Albedo, Distance = (s.Center - cam).sqrMagnitude,
                    });
                }
            foreach (var b in GrenadeFx.Active)
            {
                if (b.Amount <= 0.001f) continue;
                Candidates.Add(new Candidate
                {
                    Center = b.Center, RadiusH = b.RadiusH, RadiusV = b.RadiusV, Density = b.Amount,
                    Albedo = b.Albedo, Distance = (b.Center - cam).sqrMagnitude,
                });
            }
            if (Candidates.Count == 0) return 0;
            Candidates.Sort((x, y) => x.Distance.CompareTo(y.Distance));
            int n = Mathf.Min(Candidates.Count, MaxDrawn);
            for (int i = 0; i < n; i++)
            {
                var c = Candidates[i];
                pos[i] = new Vector4(c.Center.x, c.Center.y, c.Center.z, c.RadiusH);
                info[i] = new Vector4(c.RadiusV, c.Density, 0f, 0f);
                color[i] = c.Albedo;
                emit[i] = new Vector4(c.Emit.r, c.Emit.g, c.Emit.b, c.Shell);
                largest = Mathf.Max(largest, Mathf.Max(c.RadiusH, c.RadiusV));
            }
            return n;
        }
    }

    /// <summary>
    /// A tileable 3D noise for the smoke: R = billowy inverted-Worley fractal (the puffs), GBA = soft
    /// value noise for warping. Built on a worker thread so loading the mod doesn't hitch.
    /// </summary>
    internal static class SmokeNoise
    {
        private const int Size = 64;
        private static volatile Color32[] _data;
        private static Texture3D _texture;

        internal static void Build() => ThreadPool.QueueUserWorkItem(_ => _data = Generate());

        /// <summary>The texture, or null if it is not ready yet.</summary>
        internal static Texture3D Texture
        {
            get
            {
                if (_texture == null && _data != null)
                {
                    _texture = new Texture3D(Size, Size, Size, TextureFormat.RGBA32, false)
                    {
                        name = "TABG Visuals smoke noise",
                        wrapMode = TextureWrapMode.Repeat,
                        filterMode = FilterMode.Bilinear,
                        hideFlags = HideFlags.HideAndDontSave,
                    };
                    _texture.SetPixels32(_data);
                    _texture.Apply(false, true);
                    _data = null;
                }
                return _texture;
            }
        }

        private static Color32[] Generate()
        {
            var random = new System.Random(7031);
            var worley = new[] { Points(random, 4), Points(random, 8), Points(random, 16) };
            var soft = new[] { Lattice(random, 4), Lattice(random, 4), Lattice(random, 4) };
            var result = new Color32[Size * Size * Size];
            for (int z = 0; z < Size; z++)
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float u = (float)x / Size, v = (float)y / Size, w = (float)z / Size;
                float billow = 0.625f * Worley(worley[0], 4, u, v, w)
                             + 0.25f * Worley(worley[1], 8, u, v, w)
                             + 0.125f * Worley(worley[2], 16, u, v, w);
                result[x + y * Size + z * Size * Size] = new Color32(
                    Bytes(billow),
                    Bytes(Value(soft[0], 4, u, v, w)),
                    Bytes(Value(soft[1], 4, u, v, w)),
                    Bytes(Value(soft[2], 4, u, v, w)));
            }
            return result;
        }

        private static byte Bytes(float v) => (byte)Mathf.Clamp(v * 255f, 0f, 255f);

        private static Vector3[] Points(System.Random random, int cells)
        {
            var points = new Vector3[cells * cells * cells];
            for (int i = 0; i < points.Length; i++)
                points[i] = new Vector3((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble());
            return points;
        }

        private static float[] Lattice(System.Random random, int cells)
        {
            var values = new float[cells * cells * cells];
            for (int i = 0; i < values.Length; i++) values[i] = (float)random.NextDouble();
            return values;
        }

        private static int Wrap(int i, int n) => ((i % n) + n) % n;

        /// <summary>Inverted distance to the nearest cell point: 1 in a puff's centre, 0 in the creases.</summary>
        private static float Worley(Vector3[] points, int cells, float u, float v, float w)
        {
            float fx = u * cells, fy = v * cells, fz = w * cells;
            int cx = (int)fx, cy = (int)fy, cz = (int)fz;
            float best = float.MaxValue;
            for (int k = -1; k <= 1; k++)
            for (int j = -1; j <= 1; j++)
            for (int i = -1; i <= 1; i++)
            {
                Vector3 p = points[Wrap(cx + i, cells) + Wrap(cy + j, cells) * cells + Wrap(cz + k, cells) * cells * cells];
                float dx = cx + i + p.x - fx, dy = cy + j + p.y - fy, dz = cz + k + p.z - fz;
                best = Mathf.Min(best, dx * dx + dy * dy + dz * dz);
            }
            return 1f - Mathf.Clamp01(Mathf.Sqrt(best));
        }

        private static float Value(float[] lattice, int cells, float u, float v, float w)
        {
            float fx = u * cells, fy = v * cells, fz = w * cells;
            int x = (int)fx, y = (int)fy, z = (int)fz;
            float tx = Smooth(fx - x), ty = Smooth(fy - y), tz = Smooth(fz - z);
            float At(int i, int j, int k) => lattice[Wrap(x + i, cells) + Wrap(y + j, cells) * cells + Wrap(z + k, cells) * cells * cells];
            float a = Mathf.Lerp(Mathf.Lerp(At(0, 0, 0), At(1, 0, 0), tx), Mathf.Lerp(At(0, 1, 0), At(1, 1, 0), tx), ty);
            float b = Mathf.Lerp(Mathf.Lerp(At(0, 0, 1), At(1, 0, 1), tx), Mathf.Lerp(At(0, 1, 1), At(1, 1, 1), tx), ty);
            return Mathf.Lerp(a, b, tz);
        }

        private static float Smooth(float t) => t * t * (3f - 2f * t);
    }
}
