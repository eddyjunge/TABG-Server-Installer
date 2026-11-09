// TABG Visuals: snow flakes, rain streaks and blowing dust. Plain alpha-blended particles tinted by the
// current light, so the mod doesn't depend on which of Unity's particle shaders the game happens to ship.
Shader "Hidden/TabgVisuals/WeatherParticle"
{
    Properties
    {
        _MainTex ("Particle", 2D) = "white" {}
        _Tint ("Tint", Color) = (1, 1, 1, 1)
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _Tint;

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                float fade : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.uv = v.uv;
                // Nothing big and sharp right in front of the lens.
                o.fade = saturate((length(UnityObjectToViewPos(v.vertex)) - 0.6) / 2.0);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv) * i.color * _Tint;
                c.a *= i.fade;
                return c;
            }
            ENDCG
        }
    }
    Fallback Off
}
