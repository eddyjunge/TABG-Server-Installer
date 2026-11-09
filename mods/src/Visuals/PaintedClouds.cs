using System.Collections.Generic;
using UnityEngine;

namespace TabgInstaller.Visuals
{
    /// <summary>
    /// Removes the clouds TABG paints, leaving only the volumetric ones: the cloud masks in the skybox
    /// material (swapped for black, which leaves the sky gradient) and the "Clouds" group of mesh clouds
    /// in the world. Everything comes back when the volumetric clouds are switched off.
    /// </summary>
    internal static class PaintedClouds
    {
        private static readonly string[] Faces = { "_FrontTex", "_BackTex", "_LeftTex", "_RightTex", "_UpTex", "_DownTex" };
        private const string CloudMesh = "SM_Generic_Cloud";

        private static Material _sky;
        private static readonly Texture[] Painted = new Texture[Faces.Length];
        private static Texture2D _black;
        private static readonly List<Renderer> Meshes = new List<Renderer>();

        internal static void Apply(bool hide)
        {
            ApplySky(hide);
            ApplyMeshes(hide);
        }

        private static void ApplySky(bool hide)
        {
            var sky = RenderSettings.skybox;
            bool cloudSky = sky != null && sky.HasProperty("_CloudIntensity") && sky.HasProperty(Faces[0]);
            if (hide && cloudSky && sky != _sky)
            {
                Restore();
                _sky = sky;
                for (int i = 0; i < Faces.Length; i++) Painted[i] = sky.GetTexture(Faces[i]);
                if (_black == null)
                {
                    // Opaque black: the skybox HDR decode takes a log of the alpha.
                    _black = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                    _black.SetPixel(0, 0, new Color(0f, 0f, 0f, 1f));
                    _black.Apply();
                }
                for (int i = 0; i < Faces.Length; i++) sky.SetTexture(Faces[i], _black);
            }
            else if (!hide)
            {
                Restore();
            }
        }

        private static void Restore()
        {
            if (_sky == null) return;
            for (int i = 0; i < Faces.Length; i++) _sky.SetTexture(Faces[i], Painted[i]);
            _sky = null;
        }

        private static void ApplyMeshes(bool hide)
        {
            Meshes.RemoveAll(r => r == null);
            if (hide && Meshes.Count == 0)
            {
                var group = GameObject.Find("Clouds");
                if (group != null)
                    foreach (var filter in group.GetComponentsInChildren<MeshFilter>(true))
                        if (filter.sharedMesh != null && filter.sharedMesh.name.StartsWith(CloudMesh, System.StringComparison.Ordinal))
                        {
                            var r = filter.GetComponent<Renderer>();
                            if (r != null) Meshes.Add(r);
                        }
            }
            foreach (var r in Meshes) r.enabled = !hide;
        }
    }
}
