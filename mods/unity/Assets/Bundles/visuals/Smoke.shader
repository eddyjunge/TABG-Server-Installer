// TABG Visuals: volumetric smoke for smoke grenades, raymarched at half resolution.
// Output: rgb = in-scattered light (premultiplied), a = coverage. The Stylize composite lays it over
// the frame after an edge-aware upsample.
//
// Each smoke is an ellipsoid fitted to the game's own particles every few frames, so its position,
// growth, colour and lifetime stay exactly those of the original effect. Inside the ellipsoids a
// tileable billow noise (a 3D texture, SmokeNoise.cs) carves puffs out of the edge, drifting upward so
// the cloud churns. Each step does Beer-Lambert extinction and a few samples toward the sun for
// self-shadowing, with dual-lobe Henyey-Greenstein scattering and a sky ambient that is dimmer low down
// and deep inside. Rays stop at scene depth.
Shader "Hidden/TabgVisuals/Smoke"
{
    Properties
    {
        _MainTex ("Unused", 2D) = "black" {}
        _SmokeNoise ("Noise", 3D) = "white" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        // 0: raymarch
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            float4x4 _FrustumRays;      // rows BL, BR, TL, TR (world, view depth 1)
            float4 _CamPos;
            float4 _SunDir;             // xyz toward the sun
            float4 _SunColor;
            float4 _AmbientTop;
            float4 _AmbientBottom;
            float4 _SmokePos[8];        // xyz centre, w horizontal radius
            float4 _SmokeInfo[8];       // x vertical radius, y density 0..1
            float4 _SmokeColor[8];      // rgb albedo
            float4 _SmokeEmit[8];       // rgb glow, a: 0 = hot core, 1 = glowing rim
            // x: count, y: steps, z: size of one noise tile (m), w: extinction (1/m at full density)
            float4 _SmokeParams;
            sampler3D _SmokeNoise;
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

            float3 Drift()
            {
                return float3(_Time.y * 0.22, -_Time.y * 0.5, _Time.y * 0.15);
            }

            float NoiseAt(float3 p, float scale)
            {
                return tex3Dlod(_SmokeNoise, float4(p * scale, 0)).r;
            }

            static float3 g_Emission;   // set by the detailed density samples

            float Density(float3 p, bool detail, out float3 albedo)
            {
                albedo = 0;
                if (detail) g_Emission = 0;
                float feature = 1.0 / _SmokeParams.z;
                float3 drift = Drift();
                float3 warp = tex3Dlod(_SmokeNoise, float4((p + drift) * feature * 0.5, 0)).gba - 0.5;
                float3 wp = p + warp * _SmokeParams.z * 0.45;

                float sum = 0;
                float3 tint = 0;
                float3 glow = 0;
                float shell = 0;
                for (int k = 0; k < 8; k++)
                {
                    if (k >= (int)_SmokeParams.x) break;
                    float3 q = (wp - _SmokePos[k].xyz) / float3(_SmokePos[k].w, _SmokeInfo[k].x, _SmokePos[k].w);
                    float r2 = dot(q, q);
                    if (r2 >= 1.0) continue;
                    float f = 1.0 - sqrt(r2);
                    f = f * (2.0 - f) * _SmokeInfo[k].y;
                    sum += f;
                    tint += f * _SmokeColor[k].rgb;
                    glow += f * _SmokeEmit[k].rgb;
                    shell += f * _SmokeEmit[k].a;
                }
                if (sum <= 1e-3) return 0;
                albedo = tint / sum;
                float3 glowColor = glow / sum;
                float glowShell = shell / sum;
                sum = saturate(sum * 2.2);

                // Puffs: billow noise eats into the thin outer shell, the core stays solid.
                float billow = NoiseAt(p + drift, feature);
                float v = saturate((sum - (1.0 - billow) * 0.75) / 0.25);
                if (detail && v > 0.0 && v < 1.0)
                    v = saturate(v - (1.0 - NoiseAt(p + drift * 1.7, feature * 2.7)) * 0.4 * (1.0 - v));
                // Heat burns in the thick of the cloud and flickers with the billows; a rim glows at the edge.
                if (detail) g_Emission = glowColor * lerp(v * v * (0.4 + 1.6 * billow * billow), 4.0 * v * (1.0 - v), glowShell);
                return v;
            }

            float Phase(float cosT, float g)
            {
                float g2 = g * g;
                return (1.0 - g2) / (4.0 * UNITY_PI * pow(max(1e-4, 1.0 + g2 - 2.0 * g * cosT), 1.5));
            }

            float4 frag(v2f_img i) : SV_Target
            {
                float3 bottom = lerp(_FrustumRays[0].xyz, _FrustumRays[1].xyz, i.uv.x);
                float3 top = lerp(_FrustumRays[2].xyz, _FrustumRays[3].xyz, i.uv.x);
                float3 ray = lerp(bottom, top, i.uv.y);
                float rayLen = length(ray);
                float3 rd = ray / rayLen;
                float3 ro = _CamPos.xyz;

                // The part of the ray inside any of the ellipsoids.
                float t0 = 1e9, t1 = -1.0;
                for (int k = 0; k < 8; k++)
                {
                    if (k >= (int)_SmokeParams.x) break;
                    // Slightly fattened: the domain warp can push density past the plain ellipsoid.
                    float3 s = float3(_SmokePos[k].w, _SmokeInfo[k].x, _SmokePos[k].w) * 1.25;
                    float3 o = (ro - _SmokePos[k].xyz) / s;
                    float3 d = rd / s;
                    float a = dot(d, d), b = dot(o, d), c = dot(o, o) - 1.0;
                    float disc = b * b - a * c;
                    if (disc <= 0.0) continue;
                    disc = sqrt(disc);
                    float near = (-b - disc) / a, far = (-b + disc) / a;
                    if (far <= 0.0) continue;
                    t0 = min(t0, max(0.0, near));
                    t1 = max(t1, far);
                }
                if (t1 <= t0) return 0;

                float raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
                #if defined(UNITY_REVERSED_Z)
                    bool sky = raw <= 1e-5;
                #else
                    bool sky = raw >= 1.0 - 1e-5;
                #endif
                if (!sky) t1 = min(t1, LinearEyeDepth(raw) * rayLen);
                if (t1 <= t0) return 0;

                int steps = (int)_SmokeParams.y;
                float dt = (t1 - t0) / steps;
                float2 px = i.uv * _ScreenParams.xy;
                float jitter = frac(52.9829189 * frac(dot(px, float2(0.06711056, 0.00583715))));

                float cosT = dot(rd, _SunDir.xyz);
                float phase = (Phase(cosT, 0.55) * 0.7 + Phase(cosT, -0.25) * 0.3) * 4.0 * UNITY_PI;
                float light = _SmokeParams.z * 0.2;
                float sigma = _SmokeParams.w;

                float T = 1.0;
                float3 col = 0;
                [loop]
                for (int k = 0; k < 64; k++)
                {
                    if (k >= steps || T < 0.012) break;
                    float3 p = ro + rd * (t0 + (k + jitter) * dt);
                    float3 albedo;
                    float d = Density(p, true, albedo);
                    if (d <= 0.002) continue;
                    float3 emission = g_Emission;

                    // Light reaching this point, through the smoke toward the sun.
                    float3 unused;
                    float od = Density(p + _SunDir.xyz * light * 0.5, false, unused) * 0.5
                             + Density(p + _SunDir.xyz * light * 1.3, false, unused) * 0.8
                             + Density(p + _SunDir.xyz * light * 2.8, false, unused) * 1.5;
                    od *= light * sigma;
                    // Two exponents: the shallow one stands in for multiple scattering, so the shaded
                    // side stays milky instead of turning black.
                    float sunT = lerp(exp(-od), exp(-od * 0.22), 0.45);

                    // Ambient: the sky from above, weaker at the bottom and inside.
                    float up = 1.0 - 0.6 * Density(p + float3(0, light, 0), false, unused);
                    float3 ambient = lerp(_AmbientBottom.rgb, _AmbientTop.rgb, up);

                    float3 lit = albedo * (_SunColor.rgb * sunT * (0.5 + 0.6 * phase) + ambient) + emission;

                    float a = 1.0 - exp(-d * sigma * dt);
                    col += T * a * lit;
                    T *= 1.0 - a;
                }
                return float4(col, 1.0 - T);
            }
            ENDCG
        }

        // 1: small separable blur (removes the dither), premultiplied RGBA
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _BlurDir;

            float4 frag(v2f_img i) : SV_Target
            {
                float2 d = _BlurDir.xy;
                return tex2D(_MainTex, i.uv) * 0.5
                     + (tex2D(_MainTex, i.uv + d) + tex2D(_MainTex, i.uv - d)) * 0.2
                     + (tex2D(_MainTex, i.uv + d * 2.0) + tex2D(_MainTex, i.uv - d * 2.0)) * 0.05;
            }
            ENDCG
        }
    }
    Fallback Off
}
