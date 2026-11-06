// TABG Visuals: final image effect for the main camera, run after the game's own post-processing
// (colour grading, bloom, AO, SMAA). Aims for a warm, cinematic storybook look while keeping
// TABG's shapes and palette:
//
//  - Grade: warm highlights / cool shadows (split toning), vibrance + saturation, soft S-curve.
//  - Aerial haze: distant geometry fades into a cool lavender haze that turns warm toward the sun.
//  - Sun rays: bright sky around the sun, blurred radially from the sun's screen position
//    (passes 1 and 2, quarter resolution), added back in the sun's colour; plus a soft sun glow.
//  - Ink (optional, subtle by default): outlines from the second derivative of 1/depth, which is
//    0 on any plane, so only silhouettes and creases get lines. Near side only, creases only up
//    close, and only where there is visible contrast (so lines fade into fog like objects do).
//  - Sharpen: contrast-adaptive sharpening after AMD FidelityFX CAS (MIT).
//  - Volumetric sunlight from Volumetric.shader (shadowed light shafts), added in the sun colour.
//  - Procedural cumulus clouds from Clouds.shader over the sky.
//  - Vignette.
Shader "Hidden/TabgVisuals/Stylize"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
        _RaysTex ("Rays", 2D) = "black" {}
        _VolTex ("Volumetric", 2D) = "black" {}
        _CloudTex ("Clouds", 2D) = "black" {}
    }

    CGINCLUDE
    #include "UnityCG.cginc"

    sampler2D _MainTex;
    float4 _MainTex_TexelSize;
    sampler2D _RaysTex;
    sampler2D _VolTex;      // volumetric sunlight (Volumetric.shader), R = scattered light
    sampler2D _CloudTex;    // procedural clouds (Clouds.shader): rgb premultiplied, a coverage
    float4 _Clouds;         // x: 1 = clouds on
    float4 _Vol;            // x: strength (0 = off)
    UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

    // x: ink strength 0..1, y: line width (px at 1080p), z: fade start (m), w: fade end (m)
    float4 _Ink;
    // x: line darkness (0 black .. 1 invisible), y: edge sensitivity, z: background-side jump
    float4 _InkShape;
    // x: sharpen 0..1, y: vibrance, z: debug view (0 off, 1 ink, 2 depth, 3 passthrough, 4 rays),
    // w: saturation
    float4 _Tone;
    // x: warmth (split toning), y: contrast, z: vignette, w: haze strength
    float4 _Grade;
    // xy: sun position in viewport uv, z: sun visibility 0..1 (in front, near the screen), w: rays
    float4 _Sun;
    float4 _SunColor;       // rgb: sun light colour (already scaled)
    float4 _HazeColor;      // rgb: far haze colour, a: haze start (m)
    float4 _RayParams;      // x: blur step, y: sky threshold, z: haze density (1/m)
    float4 _Atmos;          // x: sky deepening (deeper blue toward the top of the screen)

    bool IsSky(float raw)
    {
        #if defined(UNITY_REVERSED_Z)
            return raw <= 1e-5;
        #else
            return raw >= 1 - 1e-5;
        #endif
    }

    float EyeDepth(float2 uv)
    {
        return LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv));
    }

    float2 SunDelta(float2 uv)
    {
        // Aspect-corrected offset from the sun, so its glow is round.
        float2 d = uv - _Sun.xy;
        d.x *= _MainTex_TexelSize.w / _MainTex_TexelSize.z;
        return d;
    }
    ENDCG

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        // 0: composite
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0

            float InkMask(float2 uv)
            {
                float rawC = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
                if (IsSky(rawC)) return 0;
                float dC = LinearEyeDepth(rawC);

                float2 px = _MainTex_TexelSize.xy * max(1.0, _Ink.y * _ScreenParams.y / 1080.0);
                float dL = EyeDepth(uv - float2(px.x, 0));
                float dR = EyeDepth(uv + float2(px.x, 0));
                float dD = EyeDepth(uv - float2(0, px.y));
                float dU = EyeDepth(uv + float2(0, px.y));

                float iC = 1.0 / dC;
                float lapX = abs(1.0 / dL + 1.0 / dR - 2.0 * iC);
                float lapY = abs(1.0 / dD + 1.0 / dU - 2.0 * iC);
                float edge = smoothstep(0.5, 1.5, (max(lapX, lapY) / iC) * _InkShape.y);

                float nearest = min(min(dL, dR), min(dD, dU));
                edge *= 1.0 - step(_InkShape.z, (dC - nearest) / dC);

                float farthest = max(max(dL, dR), max(dD, dU));
                float silhouette = smoothstep(0.02, 0.06, (farthest - dC) / dC);
                float creaseFade = 1.0 - saturate((dC - 0.4 * _Ink.z) / max(1.0, 0.6 * _Ink.z));
                edge *= max(silhouette, creaseFade);

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

            float3 Grade(float3 c, float warmth)
            {
                float luma = Luminance(c);
                // Vibrance lifts dull colours more than saturated ones; saturation lifts all.
                float sat = max(c.r, max(c.g, c.b)) - min(c.r, min(c.g, c.b));
                c = lerp(luma.xxx, c, 1.0 + _Tone.y * (1.0 - sat) + _Tone.w);
                c = max(c, 0);
                // Split toning: cool shadows, warm highlights.
                float hi = smoothstep(0.35, 1.0, luma);
                float lo = 1.0 - smoothstep(0.0, 0.55, luma);
                c *= lerp(1.0, float3(1.07, 1.0, 0.86), hi * warmth);
                c *= lerp(1.0, float3(0.90, 0.97, 1.10), lo * warmth);
                // Soft S-curve.
                float3 s = c * c * (3.0 - 2.0 * saturate(c));
                c = lerp(c, s, _Grade.y);
                return c;
            }

            fixed4 frag(v2f_img i) : SV_Target
            {
                if (_Tone.z > 6.5) { float4 cl = tex2D(_CloudTex, i.uv); return float4(cl.rgb + (1 - cl.a) * float3(0.2, 0.3, 0.5), 1); }
                if (_Tone.z > 5.5) return float4(tex2D(_VolTex, i.uv).rrr, 1);
                if (_Tone.z > 4.5)
                {
                    float rawD = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
                    if (IsSky(rawD)) return float4(1, 0, 0, 1);    // true sky: red
                    return float4((LinearEyeDepth(rawD) / 5000.0).xxx, 1);
                }
                if (_Tone.z > 3.5) return float4(tex2D(_RaysTex, i.uv).rgb, 1);
                if (_Tone.z > 2.5) return tex2D(_MainTex, i.uv);
                if (_Tone.z > 1.5)
                {
                    float d = EyeDepth(i.uv);
                    return float4(frac(d / 10.0).xxx * 0.8 + 0.2 * saturate(d / 300.0), 1);
                }
                if (_Tone.z > 0.5) return float4((1.0 - InkMask(i.uv)).xxx, 1);

                float4 src = tex2D(_MainTex, i.uv);
                float3 c = src.rgb;
                if (_Tone.x > 0) c = Sharpen(i.uv, c);

                float raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
                bool sky = IsSky(raw);
                float sunNear = exp(-length(SunDelta(i.uv)) * 3.0) * _Sun.z;

                // Aerial perspective on geometry only (the sky already is "haze").
                if (!sky && _Grade.w > 0)
                {
                    float d = LinearEyeDepth(raw);
                    float f = (1.0 - exp(-max(0.0, d - _HazeColor.a) * _RayParams.z)) * _Grade.w;
                    float3 haze = lerp(_HazeColor.rgb, _SunColor.rgb, saturate(sunNear));
                    c = lerp(c, haze, f);
                }

                // Volumetric sunlight: shafts through the air, shadowed by the world.
                // Over the sky (already bright) only a little, so shafts stay in front of the world.
                if (_Vol.x > 0) c += tex2D(_VolTex, i.uv).r * _Vol.x * (sky ? 0.25 : 1.0) * _SunColor.rgb;

                // Sky is not sunlit surface: no warm highlights there (they would cancel the blue).
                c = Grade(c, sky ? 0.0 : _Grade.x);

                // Sky gradient: warm cream at the horizon, deep blue overhead. Clouds (the
                // brightest, whitest sky) and the glow around the sun keep their colour.
                if (sky && _Atmos.x > 0)
                {
                    float t = smoothstep(0.3, 1.0, i.uv.y);
                    float3 tint = lerp(float3(1.02, 0.99, 0.95), float3(0.62, 0.76, 1.06), t);
                    float sunCore = exp(-length(SunDelta(i.uv)) * 10.0) * _Sun.z;
                    float w = (1.0 - smoothstep(0.93, 0.99, Luminance(c))) * (1.0 - saturate(sunCore * 2.0));
                    c = lerp(c, c * tint, w * _Atmos.x);
                }

                if (_Ink.x > 0) c = lerp(c, c * _InkShape.x, InkMask(i.uv));

                // Procedural clouds, only where the sky shows (terrain always stays in front).
                if (sky && _Clouds.x > 0)
                {
                    float4 cl = tex2D(_CloudTex, i.uv);
                    c = c * (1.0 - cl.a) + cl.rgb;
                }

                // Sun rays and a soft glow around the sun, screen-blended so nothing clips hard.
                float3 light = tex2D(_RaysTex, i.uv).rgb * _Sun.w * 0.6 * _SunColor.rgb;
                light += _SunColor.rgb * pow(sunNear, 3.0) * 0.25 * _Sun.w;
                c = 1.0 - (1.0 - saturate(c)) * (1.0 - saturate(light));

                // Vignette.
                float v = length((i.uv - 0.5) * float2(1.0, 0.8)) * 1.414;
                c *= 1.0 - _Grade.z * smoothstep(0.45, 1.1, v);

                return float4(saturate(c), src.a);
            }
            ENDCG
        }

        // 1: rays prefilter: bright sky near the sun, at quarter resolution
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0

            fixed4 frag(v2f_img i) : SV_Target
            {
                float raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
                if (!IsSky(raw)) return 0;
                float3 c = tex2D(_MainTex, i.uv).rgb;
                float bright = saturate((Luminance(c) - _RayParams.y) / max(1e-3, 1.0 - _RayParams.y));
                // TABG's sky is bright everywhere; only the sky right around the sun feeds rays.
                float nearSun = exp(-length(SunDelta(i.uv)) * 6.0);
                return float4((bright * nearSun).xxx, 1);
            }
            ENDCG
        }

        // 2: radial blur toward the sun (run several times with growing steps)
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0

            fixed4 frag(v2f_img i) : SV_Target
            {
                const int N = 12;
                float2 step = (_Sun.xy - i.uv) * _RayParams.x / N;
                float2 uv = i.uv;
                float3 sum = 0;
                float w = 1.0, wsum = 0;
                for (int k = 0; k < N; k++)
                {
                    sum += tex2D(_MainTex, uv).rgb * w;
                    wsum += w;
                    w *= 0.92;
                    uv += step;
                }
                return float4(sum / wsum, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
