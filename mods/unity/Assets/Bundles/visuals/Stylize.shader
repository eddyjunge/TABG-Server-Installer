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
//  - Explosion glow: bloom of bright parts around explosions plus a soft halo (passes 3, 4).
//  - Weather fog: distance fog on the world and a fade toward the fog colour in the sky (Weather.cs).
//  - Snow and rain: flakes and streaks on world-anchored planes along the view ray, stopped by scene depth.
//  - Lens (pass 6): rain beads and running drops that refract the finished frame, flakes that land and melt.
//  - Volumetric smoke from Smoke.shader, edge-aware upsampled over the frame.
//  - Flashbang: the game's blindness, drawn as flashed eyes (burned-in frame, blurred recovery).
//  - Vignette.
Shader "Hidden/TabgVisuals/Stylize"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
        _RaysTex ("Rays", 2D) = "black" {}
        _VolTex ("Volumetric", 2D) = "black" {}
        _CloudTex ("Clouds", 2D) = "black" {}
        _GlowTex ("Glow", 2D) = "black" {}
        _SmokeTex ("Smoke", 2D) = "black" {}
        _StunFrozen ("Stun frozen frame", 2D) = "white" {}
        _StunBlur ("Stun blurred view", 2D) = "black" {}
    }

    CGINCLUDE
    #include "UnityCG.cginc"

    sampler2D _MainTex;
    float4 _MainTex_TexelSize;
    sampler2D _RaysTex;
    sampler2D _VolTex;      // volumetric sunlight (Volumetric.shader), R = scattered light
    sampler2D _CloudTex;    // procedural clouds (Clouds.shader): rgb premultiplied, a coverage
    float4 _Clouds;         // x: 1 = clouds on
    // Explosions (up to 4): xy viewport uv, z halo radius (uv, aspect-corrected), w intensity.
    float4 _Flash[4];
    float4 _FlashColor[4];
    float4 _FlashInfo;      // x: count, y: halo strength, z: bloom strength
    sampler2D _GlowTex;     // blurred bright parts around explosions (passes 3 and 4)
    float4 _BlurDir;

    // Sum of the explosions' soft halos at this pixel (rgb), weighted by colour.
    float3 FlashHalo(float2 uv)
    {
        float3 sum = 0;
        float aspect = _MainTex_TexelSize.w / _MainTex_TexelSize.z;
        for (int k = 0; k < 4; k++)
        {
            if (k >= (int)_FlashInfo.x) break;
            float2 d = uv - _Flash[k].xy;
            d.x /= aspect;
            float r = max(1e-3, _Flash[k].z);
            float q = dot(d, d) / (r * r);
            // Warm-white core, saturated outer glow.
            float3 col = _FlashColor[k].rgb;
            float3 core = lerp(col, float3(1, 1, 1), 0.35);
            sum += _Flash[k].w * (core * exp(-q * 3.0) * 0.6 + col * col * exp(-q * 0.5) * 0.4);
        }
        return sum;
    }
    float4 _Vol;            // x: strength (0 = off)
    // x: fog density (1/m), y: overcast 0..1, z: fog start (m), w: lightning flash
    float4 _Weather;
    float4 _WeatherColor;
    sampler3D _SmokeNoise;  // the billow noise of the smoke, also used for clouds of dust
    float4 _Precip;         // x: snow 0..1, y: rain 0..1, z: how wet the ground is 0..1, w: blowing sand 0..1
    float4 _PrecipWind;     // xy: accumulated wind drift (m, world x and z), z: wind speed (m/s)
    float4 _PrecipColor;
    float4 _Lens;           // x: rain wetness of the lens 0..1, y: snow on it, z: sand on it, w: wind gust 0..1
    float4 _CamPos;
    float4x4 _FrustumRays;  // rows BL, BR, TL, TR (world, view depth 1)
    sampler2D _SmokeTex;    // volumetric smoke (Smoke.shader): rgb premultiplied, a coverage
    float4 _SmokeTex_TexelSize;
    float4 _SmokeOn;        // x: 1 = there is smoke this frame
    // x: blindness 0..1 (the game's own overlay alpha), y: seconds since the flash,
    // z: burn strength 0..1, w: after-image tail 1..0 once the game's blindness is over
    float4 _Stun;
    sampler2D _StunFrozen;  // the frame at the moment of the flash, softened
    sampler2D _StunBlur;    // the current view, blurred
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

    // Flashed eyes: almost pure white, with the moment of the flash faintly burned in for a second or two.
    float3 FlashVeil(float2 uv)
    {
        float age = _Stun.y, burn = _Stun.z;
        float2 sway = float2(sin(age * 0.7), cos(age * 0.53)) * 0.006 * saturate(age);
        float3 ghost = 1.0 - exp(-tex2D(_StunFrozen, uv + sway).rgb * 2.6);
        float burned = exp(-age / 1.6) * burn;

        float settle = saturate(age / 4.0);
        float2 q = uv * float2(2.3, 1.6) + float2(age * 0.05, -age * 0.035);
        float blotch = sin(q.x * 3.1 + sin(q.y * 2.7 + age * 0.21)) * sin(q.y * 3.7 + sin(q.x * 2.1 - age * 0.17));
        float3 veil = lerp(float3(1.0, 1.0, 1.0), float3(0.985, 0.975, 0.95), settle) * (1.0 - 0.025 * settle * (blotch * 0.5 + 0.5));
        veil *= lerp(1.0, lerp(0.6, 1.0, ghost), burned);

        // The detonation itself: nothing but white for a tenth of a second.
        return lerp(veil, 1.0, exp(-age / 0.09) * burn);
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

            // Half-res smoke, taken from the neighbour with the closest depth so it doesn't bleed
            // over the edges of nearer objects.
            float4 SmokeAt(float2 uv)
            {
                float2 t = _SmokeTex_TexelSize.xy * 0.5;
                float depth = EyeDepth(uv);
                float2 best = uv;
                float bestDiff = 1e9;
                for (int k = 0; k < 4; k++)
                {
                    float2 o = uv + float2(k < 2 ? -t.x : t.x, (k & 1) == 0 ? -t.y : t.y);
                    float diff = abs(EyeDepth(o) - depth);
                    if (diff < bestDiff) { bestDiff = diff; best = o; }
                }
                return tex2D(_SmokeTex, best);
            }

            // The world under the flash veil (FlashVeil, drawn above the HUD with the game's blindness as
            // alpha, so no more of the world shows than through the game's own white overlay).
            float3 Flashed(float2 uv, float3 c)
            {
                float blind = _Stun.x, burn = _Stun.z, tail = _Stun.w;

                // Recovery: out of focus and over-exposed, the bright parts of the world come back last.
                float3 r = lerp(c, tex2D(_StunBlur, uv).rgb, saturate(blind * 1.6 + tail * 0.35));
                r = 1.0 - exp(-r * (1.0 + blind * blind * 9.0));
                r = lerp(r, Luminance(r).xxx, blind * 0.75);

                // Once vision is back, the burned-in bright areas linger as a faint inverted patch.
                float negative = smoothstep(0.45, 0.95, Luminance(tex2D(_StunFrozen, uv).rgb)) * tail * (1.0 - blind) * burn * 0.12;
                return lerp(r, r * float3(0.62, 0.55, 0.92) + float3(0.0, 0.05, 0.1), negative);
            }

            float PrecipHash(float2 p, float seed)
            {
                p = frac((p + seed) * float2(0.1031, 0.1030));
                p += dot(p, p.yx + 33.33);
                return frac((p.x + p.y) * p.x);
            }

            // Snow and rain as tiny marks on vertical planes fixed in the world, crossed one by one along
            // the view ray: real parallax when you move, and stopped by walls through the scene depth. Where
            // a mark is smaller than a pixel it widens and dims by the same amount, so far away it melts
            // into a soft sheet rather than shimmering.
            void Precipitation(float3 ray, float rayLen, float sceneT, out float snow, out float rain, out float grains)
            {
                snow = 0;
                rain = 0;
                grains = 0;
                const float Spacing = 2.5;
                const float SnowCell = 0.55;
                const float2 RainCell = float2(0.4, 2.6);
                float3 ro = _CamPos.xyz;
                bool useX = abs(ray.x) > abs(ray.z);
                float a = useX ? ray.x : ray.z;
                if (abs(a) < 1e-4) return;
                float oa = useX ? ro.x : ro.z;
                float dir = a > 0 ? 1.0 : -1.0;
                float first = floor(oa / Spacing) + (dir > 0 ? 1.0 : 0.0);
                float pixel = length(_FrustumRays[2].xyz - _FrustumRays[0].xyz) / _ScreenParams.y;
                float drift = useX ? _PrecipWind.y : _PrecipWind.x;
                float windRatio = _PrecipWind.z * (useX ? 0.6 : 0.8);
                float time = _Time.y;

                for (int i = 0; i < 14; i++)
                {
                    float k = first + dir * i;
                    float t = (k * Spacing - oa) / a;
                    if (t > sceneT) break;
                    float3 p = ro + ray * t;
                    float2 q = float2(useX ? p.z : p.x, p.y);
                    float fade = smoothstep(0.4, 2.0, t) * (1.0 - smoothstep(22.0, 34.0, t));
                    float seed = k * 7.13 + (useX ? 3.7 : 0.0);
                    float px = t * pixel;

                    if (_Precip.x > 0.0)
                    {
                        float2 g = (q + float2(-drift, time * 2.4)) / SnowCell;
                        float2 id = floor(g);
                        float h = PrecipHash(id, seed);
                        if (h < _Precip.x * 0.7)
                        {
                            float h2 = PrecipHash(id, seed + 11.0), h3 = PrecipHash(id, seed + 23.0), h4 = PrecipHash(id, seed + 37.0);
                            float2 pos = float2(0.5 + (h2 - 0.5) * 0.5 + sin(time * (0.7 + h3) + h4 * 6.28) * 0.1,
                                                0.5 + (h3 - 0.5) * 0.5 + cos(time * (0.5 + h2) + h4 * 6.28) * 0.06);
                            float2 m = (frac(g) - pos) * SnowCell;
                            // Motion blur along the flake's path: round when it drifts, a streak in a gale.
                            // A gale also drives flakes down faster, so they never fly quite flat.
                            m.x -= m.y * windRatio / (2.4 + _PrecipWind.z * 0.5);
                            m.y /= 1.0 + _PrecipWind.z * 0.1;
                            float radius = lerp(0.016, 0.04, h4);
                            // Right in front of the eye a flake is out of focus: larger and fainter.
                            float bokeh = radius * 3.5 * (1.0 - smoothstep(0.5, 5.0, t));
                            float soft = max(radius + bokeh, px * 0.75);
                            float shape = saturate(1.0 - length(m) / soft);
                            shape = shape * shape * (3.0 - 2.0 * shape) * (radius / soft) * (radius / soft);
                            snow = 1.0 - (1.0 - snow) * (1.0 - shape * fade * (0.7 + 0.3 * h2));
                        }
                    }

                    if (_Precip.y > 0.0)
                    {
                        float2 g = (q + float2(-drift, time * 23.0)) / RainCell;
                        float2 id = floor(g);
                        float h = PrecipHash(id, seed + 51.0);
                        if (h < _Precip.y * 0.8)
                        {
                            float h2 = PrecipHash(id, seed + 61.0), h3 = PrecipHash(id, seed + 71.0);
                            float2 m = (frac(g) - float2(0.25 + h2 * 0.5, 0.5)) * RainCell;
                            m.x -= m.y * windRatio / 23.0;
                            float width = lerp(0.006, 0.012, h3);
                            float soft = max(width + 0.02 * (1.0 - smoothstep(0.5, 5.0, t)), px * 0.75);
                            float along = saturate(1.0 - abs(m.y) / lerp(0.5, 1.05, h2));
                            float shape = saturate(1.0 - abs(m.x) / soft) * (width / soft) * along * along;
                            rain = 1.0 - (1.0 - rain) * (1.0 - shape * fade);
                        }
                    }

                    // Sand: a fine, dense spray of tiny grains blown along the wind.
                    if (_Precip.w > 0.0)
                    {
                        float2 g = (q + float2(-drift, sin(time * 0.6 + q.x * 0.3) * 0.15)) / 0.2;
                        float2 id = floor(g);
                        float h = PrecipHash(id, seed + 83.0);
                        if (h < _Precip.w * 0.9)
                        {
                            float h2 = PrecipHash(id, seed + 91.0), h3 = PrecipHash(id, seed + 97.0);
                            float2 m = (frac(g) - 0.5 - float2(h2 - 0.5, h3 - 0.5) * 0.6) * 0.2;
                            float radius = lerp(0.007, 0.015, h3);
                            float soft = max(radius, px * 0.75);
                            float shape = saturate(1.0 - length(m) / soft) * (radius / soft) * (radius / soft);
                            grains = 1.0 - (1.0 - grains) * (1.0 - shape * fade);
                        }
                    }
                }
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
                if (_Tone.z > 8.5) { float4 sm = tex2D(_SmokeTex, i.uv); return float4(sm.rgb + (1 - sm.a) * float3(0.1, 0.1, 0.2), 1); }
                if (_Tone.z > 7.5) return float4(tex2D(_GlowTex, i.uv).rgb + FlashHalo(i.uv), 1);
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

                // Wet ground: darker where it has soaked in, and flat surfaces mirror the sky at a low angle,
                // most of all where the ground dips into puddles.
                if (_Precip.z > 0 && !sky)
                {
                    float3 wetRay = lerp(lerp(_FrustumRays[0].xyz, _FrustumRays[1].xyz, i.uv.x),
                                         lerp(_FrustumRays[2].xyz, _FrustumRays[3].xyz, i.uv.x), i.uv.y);
                    float3 pos = wetRay * LinearEyeDepth(raw);
                    float3 n = normalize(cross(ddy(pos), ddx(pos)));
                    float flat = smoothstep(0.6, 0.95, abs(n.y));
                    float3 world = pos + _CamPos.xyz;
                    float dips = sin(world.x * 0.33 + sin(world.z * 0.21)) * sin(world.z * 0.29 + sin(world.x * 0.17));
                    float puddle = smoothstep(0.15, 0.6, dips) * flat;
                    float wet = _Precip.z * flat;
                    c *= lerp(1.0, 0.68, wet);
                    float grazing = pow(1.0 - saturate(abs(normalize(wetRay).y)), 3.0);
                    c = lerp(c, _WeatherColor.rgb, _Precip.z * saturate(wet * grazing * 0.45 + puddle * lerp(0.25, 0.75, grazing)));
                }

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

                // Weather fog: by distance on the world; in the sky, thickest at the horizon.
                if (_Weather.x > 0 || _Weather.y > 0 || _Weather.w > 0)
                {
                    float3 ray = normalize(lerp(lerp(_FrustumRays[0].xyz, _FrustumRays[1].xyz, i.uv.x),
                                                lerp(_FrustumRays[2].xyz, _FrustumRays[3].xyz, i.uv.x), i.uv.y));
                    // Rolling clouds of dust, carried along by the wind.
                    float billow = 0.5;
                    if (_Precip.w > 0)
                    {
                        float3 cloud = _CamPos.xyz + ray * 60.0;
                        cloud.xz -= _PrecipWind.xy;
                        billow = tex3Dlod(_SmokeNoise, float4(cloud * 0.012, 0)).r * 0.6 + tex3Dlod(_SmokeNoise, float4(cloud * 0.035, 0)).r * 0.4;
                    }
                    float fog;
                    float low = 0.0;
                    if (sky)
                    {
                        low = 1.0 - saturate(ray.y / 0.55);
                        fog = saturate((1.0 - exp(-_Weather.x * 1800.0)) * low * low + _Weather.y * 0.85);
                        fog = saturate(fog + (billow - 0.5) * low * 0.7 * _Precip.w);
                    }
                    else
                    {
                        // Gusts: the fog thickens and thins in slow drifting banks.
                        float3 bank = _CamPos.xyz + ray * 45.0;
                        float gust = sin(bank.x * 0.05 + _Time.y * 0.35 + sin(bank.z * 0.07 - _Time.y * 0.2))
                                   * sin(bank.z * 0.06 + _Time.y * 0.25 + sin(bank.x * 0.04));
                        fog = 1.0 - exp(-max(0.0, LinearEyeDepth(raw) - _Weather.z) * _Weather.x * (1.0 + 0.55 * gust) * lerp(1.0, lerp(0.5, 1.6, billow), _Precip.w));
                    }
                    // Blowing sand filters the light: warmer and dimmer, with the sun a pale disc in the murk.
                    c *= lerp(1.0, float3(0.84, 0.77, 0.68), _Precip.w);
                    // Far away the dust is a heavy, dark wall rather than a pale veil.
                    c = lerp(c, Luminance(c).xxx, _Precip.w * 0.3);
                    c = lerp(c, c * c * (3.0 - 2.0 * saturate(c)), _Precip.w * 0.35);
                    float wall = _Precip.w * (sky ? low * low : smoothstep(0.4, 0.95, fog)) * lerp(0.6, 1.3, billow);
                    c = lerp(c, lerp(_WeatherColor.rgb, _WeatherColor.rgb * float3(0.62, 0.52, 0.42), wall * 0.8), fog);
                    if (sky && _Precip.w > 0 && _Sun.z > 0)
                    {
                        float l = length(SunDelta(i.uv));
                        c = lerp(c, float3(1.0, 0.93, 0.75), _Precip.w * exp(-l * l * 900.0) * 0.8 * saturate(_Sun.z * 4.0));
                    }
                    c += _Weather.w * (sky ? float3(0.8, 0.85, 1.0) : float3(0.3, 0.33, 0.42));
                }

                // Snow and rain, in front of the fogged world and behind any nearer geometry.
                if (_Precip.x > 0 || _Precip.y > 0 || _Precip.w > 0)
                {
                    float3 pray = lerp(lerp(_FrustumRays[0].xyz, _FrustumRays[1].xyz, i.uv.x),
                                       lerp(_FrustumRays[2].xyz, _FrustumRays[3].xyz, i.uv.x), i.uv.y);
                    float prayLen = length(pray);
                    float flakes, drops, grains;
                    Precipitation(pray / prayLen, prayLen, sky ? 1e6 : LinearEyeDepth(raw) * prayLen, flakes, drops, grains);

                    // Sand drifting low over the ground: broad soft banks, moving with the wind.
                    if (_Precip.w > 0 && !sky)
                    {
                        float3 w = pray * LinearEyeDepth(raw) + _CamPos.xyz;
                        float2 a = float2(w.x * 0.8 + w.z * 0.6, -w.x * 0.6 + w.z * 0.8);
                        a.x -= (_PrecipWind.x * 0.8 + _PrecipWind.y * 0.6) * 1.5;
                        float drift = smoothstep(-0.2, 0.8, sin(a.x * 0.1 - _Time.y * 0.9 + sin(a.y * 0.12) * 2.0) * sin(a.y * 0.09 + sin(a.x * 0.05)));
                        c = lerp(c, _WeatherColor.rgb, drift * _Precip.w * 0.18 * (1.0 - smoothstep(25.0, 70.0, LinearEyeDepth(raw))));
                    }
                    c = lerp(c, _WeatherColor.rgb * float3(1.1, 1.02, 0.9), saturate(grains) * 0.6);
                    c = lerp(c, _PrecipColor.rgb, saturate(flakes) * 0.95);
                    c = lerp(c, _PrecipColor.rgb * float3(0.82, 0.87, 0.98), saturate(drops) * 0.28);
                }

                // Smoke: in front of the scene, depth-tested when it was marched. Graded look, not hazed.
                if (_SmokeOn.x > 0)
                {
                    float4 smoke = SmokeAt(i.uv);
                    c = c * (1.0 - smoke.a) + smoke.rgb;
                }

                // Explosions: bloom of their bright parts plus a soft coloured halo.
                if (_FlashInfo.x > 0)
                {
                    float3 glow = tex2D(_GlowTex, i.uv).rgb * _FlashInfo.z + FlashHalo(i.uv) * _FlashInfo.y;
                    c = 1.0 - (1.0 - saturate(c)) * (1.0 - saturate(glow));
                }

                // Sun rays and a soft glow around the sun, screen-blended so nothing clips hard.
                float3 light = tex2D(_RaysTex, i.uv).rgb * _Sun.w * 0.6 * _SunColor.rgb;
                light += _SunColor.rgb * pow(sunNear, 3.0) * 0.25 * _Sun.w;
                c = 1.0 - (1.0 - saturate(c)) * (1.0 - saturate(light));

                // Vignette.
                float v = length((i.uv - 0.5) * float2(1.0, 0.8)) * 1.414;
                c *= 1.0 - _Grade.z * smoothstep(0.45, 1.1, v);

                // Last, over everything: being blinded isn't vignetted.
                if (_Stun.x > 0 || _Stun.w > 0) c = Flashed(i.uv, saturate(c));

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

        // 3: bright pass around explosions (quarter resolution): what is bright near a flash glows
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0

            fixed4 frag(v2f_img i) : SV_Target
            {
                float3 c = tex2D(_MainTex, i.uv).rgb;
                float3 halo = FlashHalo(i.uv);
                float near = saturate(max(halo.r, max(halo.g, halo.b)) * 2.0);
                float lum = Luminance(c);
                float3 bright = c * smoothstep(0.45, 0.9, lum);
                return float4(bright * near, 1);
            }
            ENDCG
        }

        // 4: 9-tap gaussian blur along _BlurDir (RGB)
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0

            fixed4 frag(v2f_img i) : SV_Target
            {
                float2 d = _BlurDir.xy;
                float3 s = tex2D(_MainTex, i.uv).rgb * 0.2270270270;
                s += (tex2D(_MainTex, i.uv + d * 1.3846153846).rgb + tex2D(_MainTex, i.uv - d * 1.3846153846).rgb) * 0.3162162162;
                s += (tex2D(_MainTex, i.uv + d * 3.2307692308).rgb + tex2D(_MainTex, i.uv - d * 3.2307692308).rgb) * 0.0702702703;
                return float4(s, 1);
            }
            ENDCG
        }

        // 5: the flash veil, shown by a UI image above the HUD
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0

            fixed4 frag(v2f_img i) : SV_Target
            {
                return float4(FlashVeil(i.uv), 1);
            }
            ENDCG
        }

        // 6: what sticks to the lens, over the finished frame
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0

            float Hash(float2 p, float seed)
            {
                p = frac((p + seed) * float2(0.1031, 0.1030));
                p += dot(p, p.yx + 33.33);
                return frac((p.x + p.y) * p.x);
            }

            float Aspect() { return _MainTex_TexelSize.z / _MainTex_TexelSize.w; }

            // A bead of water that forms, sits and evaporates. Refracts like a tiny upside-down lens.
            void Bead(float2 uv, float cells, float seed, float amount, inout float2 off, inout float mask)
            {
                float aspect = Aspect();
                float2 p = float2(uv.x * aspect, uv.y) * cells;
                float2 id = floor(p);
                float h = Hash(id, seed);
                if (h >= amount * 0.3) return;
                float h2 = Hash(id, seed + 7.0), h3 = Hash(id, seed + 13.0);
                float phase = frac(_Time.y * (0.06 + 0.08 * h2) + h3);
                float life = smoothstep(0.0, 0.04, phase) * smoothstep(1.0, 0.55, phase);
                float2 d = frac(p) - 0.5 - float2(h2 - 0.5, h3 - 0.5) * 0.5;
                float r = lerp(0.08, 0.2, h3) * life;
                float l = length(d) / max(r, 1e-3);
                if (l >= 1.0) return;
                float2 refract = -d / cells * 2.2 * (1.0 - l * l);
                off += float2(refract.x / aspect, refract.y);
                mask = max(mask, smoothstep(1.0, 0.5, l) * life);
            }

            // A drop that runs down the glass in fits and starts and leaves a thin wet trail.
            void Runner(float2 uv, float columns, float seed, float amount, inout float2 off, inout float mask)
            {
                float aspect = Aspect();
                float fx = uv.x * aspect * columns;
                float column = floor(fx);
                float h = Hash(float2(column, 0.0), seed);
                if (h >= amount * 0.4) return;
                float h2 = Hash(float2(column, 1.0), seed), h3 = Hash(float2(column, 2.0), seed);
                float y = 1.0 - frac(_Time.y * (0.12 + 0.12 * h2) + h3);
                y += sin(y * 40.0 + h * 6.28) * 0.008;
                float x = sin(y * 22.0 + h2 * 6.28) * 0.12;
                float dx = (frac(fx) - 0.5 - x) / columns;
                float dy = uv.y - y;
                float r = 0.007 + 0.006 * h3;
                float l = length(float2(dx, dy * 0.65)) / r;
                if (l < 1.0)
                {
                    off -= float2(dx / aspect, dy) * 1.6 * (1.0 - l * l);
                    mask = max(mask, smoothstep(1.0, 0.75, l));
                }
                const float Trail = 0.22;
                if (dy > 0.0 && dy < Trail)
                {
                    float width = r * 0.35 * (1.0 - dy / Trail);
                    float t = abs(dx) / max(width, 1e-4);
                    if (t < 1.0) off.x -= (dx / aspect) * 2.0 * (1.0 - t);
                }
            }

            // A flake that lands on the lens, then melts into a bead.
            void Flake(float2 uv, float cells, float seed, float amount, inout float2 off, inout float snow)
            {
                float aspect = Aspect();
                float2 p = float2(uv.x * aspect, uv.y) * cells;
                float2 id = floor(p);
                float h = Hash(id, seed);
                if (h >= amount * 0.14) return;
                float h2 = Hash(id, seed + 7.0), h3 = Hash(id, seed + 13.0);
                float phase = frac(_Time.y * (0.1 + 0.12 * h2) + h3);
                float appear = smoothstep(0.0, 0.03, phase);
                float melt = smoothstep(0.55, 0.2, phase);
                float2 d = frac(p) - 0.5 - float2(h2 - 0.5, h3 - 0.5) * 0.5;
                float r = lerp(0.05, 0.12, h3) * appear;
                // Slightly uneven, and out of focus: the lens is far too close for crisp crystals.
                float l = length(d) * (1.0 + 0.08 * cos(3.0 * atan2(d.y, d.x) + h * 6.28)) / max(r, 1e-3);
                if (l >= 1.0) return;
                snow = max(snow, smoothstep(1.0, 0.15, l) * melt * 0.5);
                float2 refract = -d / cells * 1.8 * (1.0 - melt) * (1.0 - l * l);
                off += float2(refract.x / aspect, refract.y);
            }

            // Grains of sand stuck to the glass: tiny, dark and many.
            void Grit(float2 uv, float cells, float seed, float amount, inout float grit)
            {
                float2 p = float2(uv.x * Aspect(), uv.y) * cells;
                float2 id = floor(p);
                float h = Hash(id, seed);
                // Rare in the middle of the view, gathering toward the edges of the glass.
                float edge = smoothstep(0.15, 0.7, length((uv - 0.5) * float2(Aspect(), 1.0)));
                if (h >= amount * (0.015 + 0.1 * edge)) return;
                float h2 = Hash(id, seed + 7.0), h3 = Hash(id, seed + 13.0);
                float2 d = frac(p) - 0.5 - float2(h2 - 0.5, h3 - 0.5) * 0.5;
                grit = max(grit, smoothstep(lerp(0.12, 0.25, h3), 0.0, length(d)) * (0.25 + 0.25 * h2));
            }

            fixed4 frag(v2f_img i) : SV_Target
            {
                float2 off = 0;
                float mask = 0, snow = 0, grit = 0;
                if (_Lens.x > 0)
                {
                    Bead(i.uv, 11.0, 1.0, _Lens.x, off, mask);
                    Bead(i.uv, 19.0, 2.0, _Lens.x, off, mask);
                    Bead(i.uv, 31.0, 3.0, _Lens.x, off, mask);
                    Runner(i.uv, 5.0, 4.0, _Lens.x, off, mask);
                    Runner(i.uv + float2(0.37, 0.0), 9.0, 5.0, _Lens.x, off, mask);
                }
                if (_Lens.y > 0)
                {
                    Flake(i.uv, 13.0, 6.0, _Lens.y, off, snow);
                    Flake(i.uv, 23.0, 7.0, _Lens.y, off, snow);
                }
                if (_Lens.z > 0)
                {
                    Grit(i.uv, 70.0, 8.0, _Lens.z, grit);
                    Grit(i.uv, 130.0, 9.0, _Lens.z, grit);
                }

                // Water on a lens is out of focus: soften what is seen through it, and catch a little light.
                float2 uv = i.uv + off;
                // The wind buffets the view in gusts. Only the picture moves: the aim stays where it is.
                float shake = _Lens.z * _Lens.w * _Lens.w;
                uv += float2(sin(_Time.y * 37.0) * 0.0011 + sin(_Time.y * 91.0) * 0.0006, cos(_Time.y * 43.0) * 0.0009) * shake;
                float3 c = tex2D(_MainTex, uv).rgb;
                if (mask > 0.0)
                {
                    float2 t = _MainTex_TexelSize.xy * 2.5;
                    float3 soft = (tex2D(_MainTex, uv + t).rgb + tex2D(_MainTex, uv - t).rgb
                                 + tex2D(_MainTex, uv + float2(t.x, -t.y)).rgb + tex2D(_MainTex, uv + float2(-t.x, t.y)).rgb) * 0.25;
                    c = lerp(c, soft, mask) + mask * 0.03;
                }
                if (_Lens.z > 0)
                {
                    // Dust settles on the glass, thickest toward the edges.
                    float edge = smoothstep(0.3, 0.85, length((i.uv - 0.5) * float2(Aspect(), 1.0)));
                    c = lerp(c, _WeatherColor.rgb, edge * _Lens.z * (0.12 + 0.15 * _Lens.w));
                    c = lerp(c, c * float3(0.62, 0.52, 0.4), grit);
                }
                return float4(lerp(c, _PrecipColor.rgb, saturate(snow)), 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
