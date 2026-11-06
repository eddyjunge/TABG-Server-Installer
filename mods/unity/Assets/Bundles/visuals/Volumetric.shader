// TABG Visuals: volumetric sunlight. Runs inside the sun's command buffer at
// LightEvent.BeforeScreenspaceMask, where Unity's cascade globals (unity_WorldToShadow,
// _LightSplitsNear/Far) describe the shadow map that an AfterShadowMap command buffer bound as
// _TabgShadowMap. For every pixel it marches the view ray (half resolution, dithered), tests each
// sample against the cascaded shadow map, and collects in-scattered sunlight with a
// Henyey-Greenstein phase function, so light shafts are really shadowed by trees and buildings.
//
// Pass 0: raymarch -> R (scattered light), pass 1: separable blur (direction in _BlurDir).
Shader "Hidden/TabgVisuals/Volumetric"
{
    Properties
    {
        _MainTex ("Source", 2D) = "black" {}
    }

    CGINCLUDE
    #include "UnityCG.cginc"

    sampler2D _MainTex;
    float4 _MainTex_TexelSize;
    UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
    UNITY_DECLARE_SHADOWMAP(_TabgShadowMap);

    // World-space view rays through the four screen corners, scaled so their dot with the
    // camera forward is 1 (then ray * eyeDepth = world offset). Rows: BL, BR, TL, TR.
    float4x4 _FrustumRays;
    float4 _CamPos;         // xyz: camera position
    float4 _SunDir;         // xyz: direction toward the sun
    // x: density, y: max march distance (m), z: anisotropy g, w: step count
    float4 _VolParams;
    float4 _BlurDir;        // xy: texel step

    float3 RayAt(float2 uv)
    {
        float3 bottom = lerp(_FrustumRays[0].xyz, _FrustumRays[1].xyz, uv.x);
        float3 top = lerp(_FrustumRays[2].xyz, _FrustumRays[3].xyz, uv.x);
        return lerp(bottom, top, uv.y);
    }

    float ShadowAt(float3 p, float viewZ)
    {
        float4 zNear = float4(viewZ >= _LightSplitsNear);
        float4 zFar = float4(viewZ < _LightSplitsFar);
        float4 w = zNear * zFar;
        if (dot(w, 1) < 0.5) return 1.0;    // beyond the shadow distance: lit
        float4 c = mul(unity_WorldToShadow[0], float4(p, 1)) * w.x
                 + mul(unity_WorldToShadow[1], float4(p, 1)) * w.y
                 + mul(unity_WorldToShadow[2], float4(p, 1)) * w.z
                 + mul(unity_WorldToShadow[3], float4(p, 1)) * w.w;
        return UNITY_SAMPLE_SHADOW(_TabgShadowMap, c.xyz);
    }

    float Phase(float cosT, float g)
    {
        float g2 = g * g;
        return (1.0 - g2) / (4.0 * UNITY_PI * pow(max(1e-4, 1.0 + g2 - 2.0 * g * cosT), 1.5));
    }
    ENDCG

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

            fixed4 frag(v2f_img i) : SV_Target
            {
                float3 ray = RayAt(i.uv);
                float viewZEnd = min(LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv)), _VolParams.y);
                int steps = (int)_VolParams.w;
                float dz = viewZEnd / steps;
                // Interleaved gradient noise: a different start offset per pixel; the blur hides it.
                float2 px = i.uv * _ScreenParams.xy;
                float jitter = frac(52.9829189 * frac(dot(px, float2(0.06711056, 0.00583715))));
                float rayLen = length(ray);
                float sum = 0;
                [loop]
                for (int k = 0; k < 48; k++)
                {
                    if (k >= steps) break;
                    float z = (k + jitter) * dz;
                    float3 p = _CamPos.xyz + ray * z;
                    // Thin air near the ground, thinner higher up.
                    float h = exp(-max(0.0, p.y - _CamPos.y) * 0.01);
                    sum += ShadowAt(p, z) * h;
                }
                float3 dir = ray / rayLen;
                float light = sum * dz * rayLen * _VolParams.x * Phase(dot(dir, _SunDir.xyz), _VolParams.z);
                return float4(light, 0, 0, 1);
            }
            ENDCG
        }

        // 1: 9-tap gaussian blur along _BlurDir
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0

            fixed4 frag(v2f_img i) : SV_Target
            {
                float2 d = _BlurDir.xy;
                float s = tex2D(_MainTex, i.uv).r * 0.2270270270;
                s += (tex2D(_MainTex, i.uv + d * 1.3846153846).r + tex2D(_MainTex, i.uv - d * 1.3846153846).r) * 0.3162162162;
                s += (tex2D(_MainTex, i.uv + d * 3.2307692308).r + tex2D(_MainTex, i.uv - d * 3.2307692308).r) * 0.0702702703;
                return float4(s, 0, 0, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
