using System.Collections.Generic;
using UnityEngine;

namespace TabgInstaller.Visuals
{
    /// <summary>
    /// Better sea by tuning TABG's own Crest ocean material (the tiles use shader "Crest/Ocean"),
    /// so Crest's real waves, LODs and caustics stay. TABG ships it with foam switched off even
    /// though the foam simulation runs; this turns foam on and gives the water richer colour and
    /// more sun glowing through the waves. Original values come back when switched off.
    /// </summary>
    internal static class Ocean
    {
        private static Material _material;
        private static readonly Dictionary<string, float> _floats = new Dictionary<string, float>();
        private static readonly Dictionary<string, Color> _colors = new Dictionary<string, Color>();
        private static bool _foamWasOn;
        private static bool _applied;
        private static float _nextSearch;

        private static readonly (string, float)[] Floats =
        {
            ("_Foam", 1f),
            ("_WaveFoamCoverage", 0.55f),
            ("_SubSurfaceSun", 3.5f),
            ("_Roughness", 0.06f),
        };

        private static readonly (string, Color)[] Colors =
        {
            ("_Diffuse", new Color(0.05f, 0.22f, 0.36f)),
            ("_DiffuseGrazing", new Color(0.36f, 0.56f, 0.70f)),
            ("_SubSurfaceColour", new Color(0.0f, 0.42f, 0.42f)),
            ("_SubSurfaceShallowColour", new Color(0.35f, 0.80f, 0.72f)),
        };

        internal static void Apply(bool better)
        {
            if (_material == null)
            {
                _applied = false;
                if (Time.unscaledTime < _nextSearch) return;
                _nextSearch = Time.unscaledTime + 3f;
                foreach (var r in Object.FindObjectsOfType<MeshRenderer>())
                {
                    var m = r.sharedMaterial;
                    if (m != null && m.shader != null && m.shader.name == "Crest/Ocean")
                    {
                        _material = m;
                        break;
                    }
                }
                if (_material == null) return;
                _floats.Clear();
                _colors.Clear();
                foreach (var (name, _) in Floats)
                    if (_material.HasProperty(name)) _floats[name] = _material.GetFloat(name);
                foreach (var (name, _) in Colors)
                    if (_material.HasProperty(name)) _colors[name] = _material.GetColor(name);
                _foamWasOn = _material.IsKeywordEnabled("_FOAM_ON");
                Debug.Log($"[Visuals] Crest ocean material '{_material.name}' (foam was {(_foamWasOn ? "on" : "off")})");
            }

            if (better == _applied) return;
            _applied = better;
            if (better)
            {
                _material.EnableKeyword("_FOAM_ON");
                foreach (var (name, value) in Floats)
                    if (_floats.ContainsKey(name)) _material.SetFloat(name, value);
                foreach (var (name, value) in Colors)
                    if (_colors.ContainsKey(name)) _material.SetColor(name, value);
            }
            else
            {
                if (!_foamWasOn) _material.DisableKeyword("_FOAM_ON");
                foreach (var kv in _floats) _material.SetFloat(kv.Key, kv.Value);
                foreach (var kv in _colors) _material.SetColor(kv.Key, kv.Value);
            }
        }
    }
}
