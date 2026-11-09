using System.Collections.Generic;
using HarmonyLib;
using Landfall.Network;
using UnityEngine;

namespace TabgInstaller.Visuals
{
    /// <summary>
    /// Light and glow for explosions. Every Explosion.Explode with real damage (grenades, missiles,
    /// air strikes, cluster/bounce bombs, fireballs, car crashes, orbital strikes...) gets
    ///  - a point light that flares and fades, lighting the ground, walls and players around it;
    ///  - in the image effect, a bloom of the bright parts around it and a soft coloured halo
    ///    (only if the camera can see the blast).
    /// Spells, melee and auras that also use the Explosion component are skipped by name.
    /// Purely cosmetic and local.
    /// </summary>
    internal static class ExplosionGlow
    {
        internal struct Flash
        {
            public Vector3 Position;
            public float Radius;
            public float Start;
            public float Duration;
            public float Power;
            public Color Color;
            public Light Light;
        }

        internal static readonly List<Flash> Active = new List<Flash>();

        private static readonly Color Fire = new Color(1f, 0.62f, 0.28f);
        private static readonly Color Energy = new Color(0.55f, 0.75f, 1f);
        private static readonly Color Magnesium = new Color(0.96f, 0.97f, 1f);

        // Explosion-component users that are not fiery blasts.
        private static readonly string[] Skip =
        {
            "katana", "sword", "summon", "torrent", "promise", "jarl", "viking", "blessing", "spell",
            "heal", "freez", "ice", "time", "gravity", "reveal", "implosion", "blackhole", "stun",
            "knockback", "splinter", "spotted", "drop", "shockwave",
        };

        private static readonly string[] EnergyNames = { "gauss", "smite", "orbital", "taser", "lightning" };

        internal static void OnExplode(Explosion e)
        {
            var p = VisualsPlugin.Instance;
            if (p == null || !p.Enabled.Value || p.ExplosionGlowStrength.Value <= 0f || e == null) return;
            string name = e.transform.root.name.ToLowerInvariant() + " " + e.name.ToLowerInvariant();
            if (p.DebugView.Value != 0)
                Debug.Log($"[Visuals] explosion '{name}' damage {e.damage} radius {e.radius} at {e.transform.position}");
            // A flashbang does no damage but is the brightest thing in the game, for a moment.
            if (e.transform.root.name.StartsWith("Ex_Stun", System.StringComparison.Ordinal))
            {
                Spawn(e.transform.position, Mathf.Clamp(e.radius, 6f, 25f), Magnesium, 0.3f, 2.5f);
                return;
            }
            if (GrenadeFx.IsField(e.transform.root.name)) return;    // fields tick; they have their own look
            if (e.damage < 10f || e.radius < 1.5f) return;
            foreach (var s in Skip)
                if (name.Contains(s)) return;
            bool energy = false;
            foreach (var s in EnergyNames)
                if (name.Contains(s)) { energy = true; break; }

            float radius = e.radius * (e.scale ? e.transform.localScale.x : 1f);
            radius = Mathf.Clamp(radius, 2f, 40f);
            if (!energy) GrenadeFx.OnBlast(e, radius);
            Spawn(e.transform.position, radius, energy ? Energy : Fire);
        }

        /// <summary>Also used by tests: a flash at a point.</summary>
        internal static void Spawn(Vector3 position, float radius, Color color, float duration = -1f, float boost = 1f)
        {
            var p = VisualsPlugin.Instance;
            float power = (p != null ? p.ExplosionGlowStrength.Value : 1f) * boost;
            if (duration <= 0f) duration = Mathf.Lerp(0.6f, 1.4f, Mathf.InverseLerp(2f, 20f, radius));

            var go = new GameObject("TABG Visuals explosion light");
            go.transform.position = position + Vector3.up * Mathf.Min(1.5f, radius * 0.2f);
            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.range = radius * 4f;
            light.intensity = 0f;
            light.shadows = LightShadows.None;
            light.renderMode = LightRenderMode.ForcePixel;
            Object.Destroy(go, duration + 0.1f);

            if (Active.Count >= 8) Active.RemoveAt(0);
            Active.Add(new Flash
            {
                Position = position, Radius = radius, Start = Time.time, Duration = duration,
                Power = power, Color = color, Light = light,
            });
        }

        /// <summary>0..1..0 envelope: a fast flare, then a longer fade.</summary>
        internal static float Envelope(in Flash f)
        {
            float t = (Time.time - f.Start) / f.Duration;
            if (t < 0f || t > 1f) return 0f;
            return t < 0.08f ? t / 0.08f : Mathf.Pow(1f - (t - 0.08f) / 0.92f, 2f);
        }

        internal static void Update()
        {
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                var f = Active[i];
                float k = Envelope(f);
                if (Time.time - f.Start > f.Duration)
                {
                    Active.RemoveAt(i);
                    continue;
                }
                if (f.Light != null)
                    f.Light.intensity = k * 6f * f.Power * Mathf.Lerp(0.7f, 1.3f, Mathf.InverseLerp(2f, 20f, f.Radius));
            }
        }
    }

    [HarmonyPatch(typeof(Explosion), nameof(Explosion.Explode), typeof(NetworkOwner))]
    internal static class ExplodePatch
    {
        private static void Postfix(Explosion __instance) => ExplosionGlow.OnExplode(__instance);
    }
}
