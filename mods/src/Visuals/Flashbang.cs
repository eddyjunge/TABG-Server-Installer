using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace TabgInstaller.Visuals
{
    /// <summary>
    /// Replaces the flat white image of the game's stun effect (flashbangs, MGL flashes, blinding light)
    /// with eyes that have just been flashed: an instant glare, the frame you saw burned in and fading
    /// inside the whiteout, an unfocused and over-exposed recovery, and a faint negative after-image.
    /// How long and how strongly you are blinded stays exactly the game's: it is read from the alpha
    /// the game gives its own overlay, and the white veil is a UI image right where that overlay was, so
    /// it covers the HUD the same way. Falls back to the overlay if the image effect isn't running.
    /// </summary>
    internal static class Flashbang
    {
        private const float TailSeconds = 2.5f;

        private static int _blindFrame = -1;

        internal static float Blind;
        internal static float Tail;
        internal static float Burn;
        internal static float Age;
        internal static bool CapturePending;
        private static float _flashTime = float.NegativeInfinity;

        /// <summary>The veil to show (rendered by StylizeEffect); null shows plain white.</summary>
        internal static Texture VeilTexture;
        private static RawImage _veil;
        private static StunEffect _veilOwner;

        internal static bool Visible => Blind > 0f || Tail > 0f;

        private static bool ReplacesOverlay
        {
            get
            {
                var p = VisualsPlugin.Instance;
                return p != null && p.Enabled.Value && p.RealisticFlashbang.Value && p.Material != null
                    && Time.frameCount - StylizeEffect.LastRenderFrame <= 2;
            }
        }

        internal static void OnStun(StunEffect stun, float strength)
        {
            // Looking away from a grenade gives a negative strength, which the game ignores.
            if (strength <= 0.01f || !ReplacesOverlay) return;
            _flashTime = Time.unscaledTime;
            Burn = Mathf.Clamp01(strength / 6f);
            Tail = 1f;
            CapturePending = true;
        }

        internal static void AfterGameUpdate(StunEffect stun)
        {
            if (Time.frameCount != _blindFrame)
            {
                _blindFrame = Time.frameCount;
                Blind = 0f;
            }
            float blind = 0f;
            if (stun.stunImage != null && stun.stunImage.enabled && ReplacesOverlay)
            {
                blind = stun.stunImage.color.a;
                stun.stunImage.enabled = false;
                Blind = Mathf.Max(Blind, blind);
            }
            ShowVeil(stun, blind);
        }

        private static void ShowVeil(StunEffect stun, float blind)
        {
            if (blind <= 0f)
            {
                if (stun == _veilOwner && _veil != null) _veil.enabled = false;
                return;
            }
            if (_veil == null || stun != _veilOwner) _veil = CreateVeil(stun);
            float glare = Mathf.Exp(-(Time.unscaledTime - _flashTime) / 0.09f) * Burn;
            _veil.texture = VeilTexture;
            _veil.color = new Color(1f, 1f, 1f, Mathf.Max(blind, glare));
            _veil.enabled = true;
        }

        private static RawImage CreateVeil(StunEffect stun)
        {
            if (_veil != null) Object.Destroy(_veil.gameObject);
            _veilOwner = stun;
            // A child of the game's overlay: same canvas and draw order, above the HUD.
            var go = new GameObject("TABG Visuals flash veil", typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(stun.stunImage.transform, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            var image = go.AddComponent<RawImage>();
            image.raycastTarget = false;
            return image;
        }

        /// <summary>Once per rendered frame, when every Update has run.</summary>
        internal static void Tick()
        {
            Age = Time.unscaledTime - _flashTime;
            if (Time.frameCount != _blindFrame) Blind = 0f;
            if (Blind <= 0f)
                Tail = Mathf.Max(0f, Tail - Time.unscaledDeltaTime / TailSeconds);
        }
    }

    [HarmonyPatch(typeof(StunEffect), nameof(StunEffect.Stun))]
    internal static class StunPatch
    {
        private static void Postfix(StunEffect __instance, float strength) => Flashbang.OnStun(__instance, strength);
    }

    [HarmonyPatch(typeof(StunEffect), "Update")]
    internal static class StunUpdatePatch
    {
        private static void Postfix(StunEffect __instance) => Flashbang.AfterGameUpdate(__instance);
    }
}
