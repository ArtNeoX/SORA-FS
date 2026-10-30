// Emissive display surface. Writes straight into HDRP's pre-exposed colour buffer, so 1.0 is
// display white whatever the scene exposure (a sunlit cockpit does not black the screen out),
// and values above 1 feed the camera's bloom.
Shader "CirrusFS/GlassDisplayScreen"
{
    Properties
    {
        _MainTex ("Display", 2D) = "black" {}
        _Brightness ("Brightness", Float) = 1
        _OffColor ("Unlit glass colour", Color) = (0.004, 0.005, 0.006, 1)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }

        Pass
        {
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Cull Back ZWrite On ZTest LEqual

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _Brightness;
            float4 _OffColor;
            float4 _TOD_Exposure;   // time-of-day pre-exposure (x); 0 when the system is absent

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                // Brightness is in scene light units; the camera target is pre-exposed. Like real
                // avionics with a photocell, the display dims as the eye adapts to the dark: it never
                // shows brighter on screen than it does in daylight.
                float exposure = min(_TOD_Exposure.x > 0.0 ? _TOD_Exposure.x : 1.0, 1.0);
                return float4((tex2D(_MainTex, i.uv).rgb * _Brightness + _OffColor.rgb) * exposure, 1);
            }
            ENDCG
        }
    }
}
