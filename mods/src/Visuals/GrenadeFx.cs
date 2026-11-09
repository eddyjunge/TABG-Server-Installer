using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TabgInstaller.Visuals
{
    /// <summary>
    /// A light layer on top of the game's own grenade effects, which are left exactly as they are: soft
    /// smoke and dust that linger after a blast, a faint tinted haze inside fields, dust where things land.
    /// Drawn with the volumetric smoke renderer. Purely cosmetic: damage, radius and timing are the game's.
    /// </summary>
    internal static class GrenadeFx
    {
        internal sealed class Burst
        {
            public Transform Owner;
            public Transform Scale;     // fields grow by animating their scale; the haze follows it
            public bool Field;
            public Vector3 Position;
            public float Start, Life;
            public float R0, R1, V0, V1, Rise;
            public float Density, FadeIn = 0.02f, FadeOut = 0.5f;
            public Color Albedo;
            public float EndedAt = -1f;

            public Vector3 Center;
            public float RadiusH, RadiusV, Amount;
        }

        private const float FieldEndSeconds = 1.2f;
        private const int MaxBursts = 48;

        internal static readonly List<Burst> Active = new List<Burst>();

        private static readonly Color Soot = new Color(0.3f, 0.29f, 0.28f);
        private static readonly Color Dust = new Color(0.62f, 0.56f, 0.47f);

        private static readonly string[] Utility = { "BirdCage", "LaunchPad", "WallBase", "E_EnergyShield", "VolleyGrenadeEffect" };

        private static bool On
        {
            get
            {
                var p = VisualsPlugin.Instance;
                return p != null && p.Enabled.Value && p.GrenadeEffects.Value && p.SmokeMaterial != null;
            }
        }

        internal static void Update()
        {
            if (!On)
            {
                Active.Clear();
                return;
            }

            float now = Time.time;
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                var b = Active[i];
                if (b.Field && b.EndedAt < 0f && b.Owner == null) b.EndedAt = now;
                if (b.Field ? (b.EndedAt >= 0f && now - b.EndedAt > FieldEndSeconds) : now - b.Start > b.Life)
                {
                    Active.RemoveAt(i);
                    continue;
                }
                Advance(b, now);
            }
        }

        private static void Advance(Burst b, float now)
        {
            float age = now - b.Start;
            float k = b.Field ? Mathf.Clamp01(age / 1.5f) : Mathf.Clamp01(age / b.Life);
            float grow = 1f - (1f - k) * (1f - k) * (1f - k);
            float scale = b.Scale != null ? b.Scale.localScale.x : 1f;
            b.RadiusH = Mathf.Max(0.05f, Mathf.Lerp(b.R0, b.R1, grow) * scale);
            b.RadiusV = Mathf.Max(0.05f, Mathf.Lerp(b.V0, b.V1, grow) * scale);
            if (b.Owner != null) b.Position = b.Owner.position;
            b.Center = b.Position + Vector3.up * (b.RadiusV * 0.6f + b.Rise * age);

            float env = Mathf.Clamp01(k / Mathf.Max(0.001f, b.FadeIn));
            if (b.Field)
            {
                if (b.EndedAt >= 0f) env *= Mathf.Clamp01(1f - (now - b.EndedAt) / FieldEndSeconds);
            }
            else if (k > b.FadeOut)
            {
                env *= 1f - (k - b.FadeOut) / (1f - b.FadeOut);
            }
            b.Amount = b.Density * env;
        }

        private static void Add(Burst b)
        {
            b.Start = Time.time;
            if (Active.Count >= MaxBursts) Active.RemoveAt(0);
            Advance(b, b.Start);
            Active.Add(b);
        }

        /// <summary>Fields tick their damage; they must not count as a new blast each time.</summary>
        internal static bool IsField(string root) =>
            root.StartsWith("E_", StringComparison.Ordinal) || root.StartsWith("OrbitalStrike", StringComparison.Ordinal);

        /// <summary>After any real blast: thin smoke that lingers and dust kicked up from the ground.</summary>
        internal static void OnBlast(Explosion e, float radius)
        {
            if (!On) return;
            Vector3 at = e.transform.position;
            Add(new Burst
            {
                Position = at, Life = 6f, R0 = radius * 0.3f, R1 = radius * 0.9f, V0 = radius * 0.3f, V1 = radius, Rise = 0.9f,
                Density = 0.3f, FadeIn = 0.08f, FadeOut = 0.3f, Albedo = Soot,
            });
            Add(new Burst
            {
                Position = at, Life = 2.8f, R0 = radius * 0.4f, R1 = radius * 1.3f, V0 = radius * 0.1f, V1 = radius * 0.24f,
                Density = 0.3f, FadeOut = 0.3f, Albedo = Dust,
            });
        }

        internal static void OnEffectStart(Explosion e)
        {
            if (!On || e == null) return;
            var root = e.transform.root.gameObject;
            if (root.GetComponent<GrenadeFxMark>() != null) return;
            string name = root.name.Replace("(Clone)", "").Trim();
            Color tint;
            if (name.StartsWith("E_FireField", StringComparison.Ordinal)) tint = new Color(0.4f, 0.32f, 0.27f);
            else if (name.StartsWith("E_HealingField", StringComparison.Ordinal)) tint = new Color(0.62f, 0.9f, 0.7f);
            else if (name.StartsWith("E_FreezingField", StringComparison.Ordinal)) tint = new Color(0.82f, 0.92f, 1f);
            else if (name.StartsWith("E_TimeField", StringComparison.Ordinal)) tint = new Color(0.58f, 0.54f, 0.88f);
            else return;

            root.AddComponent<GrenadeFxMark>();
            float radius = e.radius;
            Add(new Burst
            {
                Owner = root.transform, Scale = e.scale ? e.transform : null, Field = true, Position = e.transform.position,
                R0 = radius * 0.5f, R1 = radius * 0.95f, V0 = radius * 0.12f, V1 = radius * 0.3f,
                Density = 0.22f, FadeIn = 0.4f, Albedo = tint,
            });
        }

        internal static void OnSpawn(SpawnObject spawner)
        {
            if (!On || spawner == null || spawner.objectToSpawn == null) return;
            string name = spawner.objectToSpawn.name;
            foreach (string u in Utility)
            {
                if (!name.StartsWith(u, StringComparison.Ordinal)) continue;
                Vector3 at = spawner.spawnPosition != null ? spawner.spawnPosition.position : spawner.transform.position;
                Add(new Burst
                {
                    Position = at, Life = 1.2f, R0 = 0.4f, R1 = 2.6f, V0 = 0.2f, V1 = 0.9f,
                    Density = 0.3f, FadeOut = 0.3f, Albedo = Dust,
                });
                return;
            }
        }
    }

    public sealed class GrenadeFxMark : MonoBehaviour { }

    // Awake, not Start: some fields keep the component disabled until later.
    [HarmonyPatch(typeof(Explosion), "Awake")]
    internal static class ExplosionStartPatch
    {
        private static void Postfix(Explosion __instance) => GrenadeFx.OnEffectStart(__instance);
    }

    [HarmonyPatch(typeof(SpawnObject), nameof(SpawnObject.Spawn), new Type[0])]
    internal static class SpawnObjectPatch
    {
        private static void Postfix(SpawnObject __instance) => GrenadeFx.OnSpawn(__instance);
    }
}
