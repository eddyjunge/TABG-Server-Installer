// TABG Visuals: final image effect for the main camera, run after the game's own post-processing
// (colour grading, bloom, AO, SMAA). Two parts, both kept subtle to stay in TABG's cartoon style:
//
//  1. Ink: soft outlines on silhouettes and creases, found from the depth buffer alone (no extra
//     scene pass). Lines darken the colour underneath instead of drawing black, so they keep the
//     palette. They thin out and fade with distance and never touch the sky.
//  2. Sharpen: contrast-adaptive sharpening after AMD FidelityFX CAS (MIT), to win back the detail
//     that SMAA/TAA soften. Flat areas and existing hard edges get little, soft detail gets most.
//
// Edges: for any plane, 1/depth is an affine function of screen position, so its second
// derivative is 0 on flat surfaces (even seen at a grazing angle) and only spikes where surfaces
// meet or one object passes in front of another. Measured relative to the centre depth, so the
// same threshold works near and far.
Shader "Hidden/TabgVisuals/Stylize"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

            // x: ink strength 0..1, y: line width in pixels at 1080p, z: fade start (m), w: fade end (m)
            float4 _Ink;
            // x: darken factor of lines (0 = black, 1 = invisible), y: edge sensitivity,
            // z: relative depth jump that marks the background side of a silhouette, w: unused
            float4 _InkShape;
            // x: sharpen 0..1, y: vibrance -1..1, z: debug view (0 off, 1 ink mask, 2 depth, 3 passthrough), w: unused
            float4 _Tone;

            float EyeDepth(float2 uv)
            {
                return LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv));
            }

            float InkMask(float2 uv)
            {
                float rawC = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
                #if defined(UNITY_REVERSED_Z)
                    if (rawC <= 1e-5) return 0;            // sky / far plane
                #else
                    if (rawC >= 1 - 1e-5) return 0;
                #endif
                float dC = LinearEyeDepth(rawC);

                // Lines keep a constant on-screen width across resolutions.
                float2 px = _MainTex_TexelSize.xy * max(1.0, _Ink.y * _ScreenParams.y / 1080.0);
                float dL = EyeDepth(uv - float2(px.x, 0));
                float dR = EyeDepth(uv + float2(px.x, 0));
                float dD = EyeDepth(uv - float2(0, px.y));
                float dU = EyeDepth(uv + float2(0, px.y));

                // Second derivative of 1/depth: 0 on any plane (even at grazing angles), large at
                // creases and silhouettes. Relative to the centre, so it works at every distance.
                float iC = 1.0 / dC;
                float lapX = abs(1.0 / dL + 1.0 / dR - 2.0 * iC);
                float lapY = abs(1.0 / dD + 1.0 / dU - 2.0 * iC);
                // Soft threshold: gentle creases (terrain triangles, depth-buffer noise) stay
                // clean, real silhouettes and hard corners get a full line.
                float edge = smoothstep(0.5, 1.5, (max(lapX, lapY) / iC) * _InkShape.y);

                // A silhouette lights up on both sides; keep only the near object's side so lines
                // hug the object instead of haloing the background behind it.
                float nearest = min(min(dL, dR), min(dD, dU));
                edge *= 1.0 - step(_InkShape.z, (dC - nearest) / dC);

                // Silhouettes (something clearly farther right next to us) keep their line out to
                // the fade distance; creases within one surface only close up, where they add
                // detail. Far creases are just terrain triangles and ocean waves.
                float farthest = max(max(dL, dR), max(dD, dU));
                float silhouette = smoothstep(0.02, 0.06, (farthest - dC) / dC);
                float creaseFade = 1.0 - saturate((dC - 0.4 * _Ink.z) / max(1.0, 0.6 * _Ink.z));
                edge *= max(silhouette, creaseFade);

                // Only ink where the eye can see an edge: haze and fog wash out contrast with
                // distance, so lines fade into the fog like the objects do, and seams between
                // two identical-looking surfaces (ocean tiles) stay invisible.
                float lC = Luminance(tex2D(_MainTex, uv).rgb);
                float contrast = max(
                    max(abs(Luminance(tex2D(_MainTex, uv - float2(px.x, 0)).rgb) - lC),
                        abs(Luminance(tex2D(_MainTex, uv + float2(px.x, 0)).rgb) - lC)),
                    max(abs(Luminance(tex2D(_MainTex, uv - float2(0, px.y)).rgb) - lC),
                        abs(Luminance(tex2D(_MainTex, uv + float2(0, px.y)).rgb) - lC)));
                edge *= smoothstep(0.01, 0.05, contrast);
                float fade = 1.0 - saturate((dC - _Ink.z) / max(1.0, _Ink.w - _Ink.z));
                return edge * fade * _Ink.x;
            }

            float3 Sharpen(float2 uv, float3 e)
            {
                float2 t = _MainTex_TexelSize.xy;
                float3 b = tex2D(_MainTex, uv + float2(0, t.y)).rgb;
                float3 d = tex2D(_MainTex, uv - float2(t.x, 0)).rgb;
                float3 f = tex2D(_MainTex, uv + float2(t.x, 0)).rgb;
                float3 h = tex2D(_MainTex, uv - float2(0, t.y)).rgb;
                float3 mn = min(min(min(b, d), min(f, h)), e);
                float3 mx = max(max(max(b, d), max(f, h)), e);
                float3 amp = sqrt(saturate(min(mn, 2.0 - mx) / max(mx, 1e-4)));
                float3 w = amp * (-1.0 / lerp(8.0, 5.0, _Tone.x));
                return saturate((b * w + d * w + f * w + h * w + e) / (1.0 + 4.0 * w));
            }

            float3 Vibrance(float3 c, float v)
            {
                float luma = dot(c, float3(0.2126, 0.7152, 0.0722));
                float sat = max(c.r, max(c.g, c.b)) - min(c.r, min(c.g, c.b));
                // Boost dull colours more than already-saturated ones.
                return lerp(luma.xxx, c, 1.0 + v * (1.0 - sat));
            }

            fixed4 frag(v2f_img i) : SV_Target
            {
                if (_Tone.z > 2.5) return tex2D(_MainTex, i.uv);   // passthrough
                if (_Tone.z > 1.5)
                {
                    float d = EyeDepth(i.uv);
                    return float4(frac(d / 10.0).xxx * 0.8 + 0.2 * saturate(d / 300.0), 1);
                }
                if (_Tone.z > 0.5) return float4((1.0 - InkMask(i.uv)).xxx, 1);
                float4 src = tex2D(_MainTex, i.uv);
                float3 c = src.rgb;
                if (_Tone.x > 0) c = Sharpen(i.uv, c);
                if (_Tone.y != 0) c = Vibrance(c, _Tone.y);
                if (_Ink.x > 0)
                {
                    float ink = InkMask(i.uv);
                    c = lerp(c, c * _InkShape.x, ink);
                }
                return float4(c, src.a);
            }
            ENDCG
        }
    }
    Fallback Off
}
