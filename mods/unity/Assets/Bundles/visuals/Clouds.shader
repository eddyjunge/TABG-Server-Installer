// TABG Visuals: procedural cumulus clouds over TABG's painted skybox.
// Renders at half resolution into an RGBA target (rgb = cloud colour premultiplied by alpha,
// a = coverage); the Stylize composite lays it over sky pixels only.
//
// A slab of air between _CloudLayer.x and _CloudLayer.y (world height) holds the clouds:
// a large-scale 2D coverage noise decides where clouds are, a 3D detail noise erodes their edges,
// and a height profile gives flat bottoms and puffy tops. Each view ray is marched through the
// slab (dithered), with two samples toward the sun for self-shadowing, a "powder" term so edges
// glow and insides stay soft, and Henyey-Greenstein forward scattering for the silver lining.
Shader "Hidden/TabgVisuals/Clouds"
{
    Properties
    {
        _MainTex ("Unused", 2D) = "black" {}
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

            float4x4 _FrustumRays;      // rows BL, BR, TL, TR (world, view depth 1)
            float4 _CamPos;
            float4 _SunDir;             // xyz toward the sun
            float4 _SunColor;
            float4 _AmbientTop;         // sky colour lighting cloud tops
            float4 _AmbientBottom;      // cool shadow colour for cloud bottoms
            // x: base height, y: top height, z: coverage 0..1, w: density
            float4 _CloudLayer;
            // xy: wind offset (m), z: feature scale (m), w: step count
            float4 _CloudShape;

            float3 RayAt(float2 uv)
            {
                float3 bottom = lerp(_FrustumRays[0].xyz, _FrustumRays[1].xyz, uv.x);
                float3 top = lerp(_FrustumRays[2].xyz, _FrustumRays[3].xyz, uv.x);
                return normalize(lerp(bottom, top, uv.y));
            }

            float Hash(float3 p)
            {
                p = frac(p * 0.3183099 + 0.1);
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            float Noise(float3 x)
            {
                float3 i = floor(x);
                float3 f = frac(x);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(lerp(Hash(i + float3(0, 0, 0)), Hash(i + float3(1, 0, 0)), f.x),
                                 lerp(Hash(i + float3(0, 1, 0)), Hash(i + float3(1, 1, 0)), f.x), f.y),
                            lerp(lerp(Hash(i + float3(0, 0, 1)), Hash(i + float3(1, 0, 1)), f.x),
                                 lerp(Hash(i + float3(0, 1, 1)), Hash(i + float3(1, 1, 1)), f.x), f.y), f.z);
            }

            float Fbm(float3 p, int octaves)
            {
                float v = 0, a = 0.5;
                for (int k = 0; k < 4; k++)
                {
                    if (k >= octaves) break;
                    v += a * Noise(p);
                    p = p * 2.03 + float3(1.7, 9.2, 3.1);
                    a *= 0.5;
                }
                return v;
            }

            float Density(float3 p, bool detail)
            {
                float h = saturate((p.y - _CloudLayer.x) / (_CloudLayer.y - _CloudLayer.x));
                // Flat bottoms, rounded tops.
                float profile = smoothstep(0.0, 0.12, h) * smoothstep(1.0, 0.45, h);
                float3 q = float3(p.x + _CloudShape.x, p.y, p.z + _CloudShape.y) / _CloudShape.z;
                float coverage = Fbm(float3(q.x, 0.0, q.z) * 0.6, 3);
                // 3D billows give each cloud puffy, lumpy volume instead of a flat sheet.
                float billow = Fbm(q * 1.6, 3);
                float shape = (coverage - (1.0 - _CloudLayer.z)) / max(0.05, _CloudLayer.z);
                shape = saturate(shape + (billow - 0.5) * 0.9) * profile;
                if (shape <= 0.0) return 0.0;
                if (detail)
                    shape -= (Fbm(q * 5.0, 2) - 0.4) * 0.45 * (1.0 - shape);
                return saturate(shape * 2.6 - 0.3) * _CloudLayer.w;
            }

            float Phase(float cosT, float g)
            {
                float g2 = g * g;
                return (1.0 - g2) / (4.0 * UNITY_PI * pow(max(1e-4, 1.0 + g2 - 2.0 * g * cosT), 1.5));
            }

            fixed4 frag(v2f_img i) : SV_Target
            {
                float3 ro = _CamPos.xyz;
                float3 rd = RayAt(i.uv);
                if (abs(rd.y) < 1e-4) return 0;

                // Enter and leave the cloud slab.
                float tA = (_CloudLayer.x - ro.y) / rd.y;
                float tB = (_CloudLayer.y - ro.y) / rd.y;
                float t0 = max(0.0, min(tA, tB));
                float t1 = max(tA, tB);
                if (t1 <= 0.0) return 0;
                t1 = min(t1, t0 + 6000.0);           // don't march to the end of the world
                if (t0 > 30000.0) return 0;

                int steps = (int)_CloudShape.w;
                float dt = (t1 - t0) / steps;
                float2 px = i.uv * _ScreenParams.xy;
                float jitter = frac(52.9829189 * frac(dot(px, float2(0.06711056, 0.00583715))));

                float cosT = dot(rd, _SunDir.xyz);
                float phase = lerp(Phase(cosT, 0.6), Phase(cosT, -0.2), 0.3) * 4.0 * UNITY_PI;
                float lightStep = (_CloudLayer.y - _CloudLayer.x) * 0.18;

                float T = 1.0;
                float3 col = 0;
                [loop]
                for (int k = 0; k < 48; k++)
                {
                    if (k >= steps || T < 0.02) break;
                    float3 p = ro + rd * (t0 + (k + jitter) * dt);
                    float d = Density(p, true);
                    if (d <= 0.001) continue;

                    // Two samples toward the sun for self-shadowing.
                    float ld = Density(p + _SunDir.xyz * lightStep, false)
                             + Density(p + _SunDir.xyz * lightStep * 2.5, false);
                    float sunT = exp(-ld * lightStep * 0.02);
                    float powder = 1.0 - exp(-d * dt * 0.03);
                    float h = saturate((p.y - _CloudLayer.x) / (_CloudLayer.y - _CloudLayer.x));
                    // Ambient: dark, cool bottoms; tops catch more sky light.
                    float3 ambient = lerp(_AmbientBottom.rgb, _AmbientTop.rgb, h * h);
                    // Shadowed parts go cool lavender-grey (not a dim, muddy sun colour).
                    float sunAmt = sunT * lerp(0.55, 1.0, powder);
                    float3 lit = lerp(ambient, _SunColor.rgb * (0.8 + 0.45 * phase) + ambient * 0.3, sunAmt);

                    float a = 1.0 - exp(-d * dt * 0.008);
                    col += T * a * lit;
                    T *= 1.0 - a;
                }
                float alpha = 1.0 - T;
                // Fade toward the horizon, where the slab gets paper-thin and aliases.
                float horizon = smoothstep(0.0, 0.08, abs(rd.y)) * (1.0 - smoothstep(12000.0, 30000.0, t0));
                return float4(col * horizon, alpha * horizon);
            }
            ENDCG
        }
    }
    Fallback Off
}
