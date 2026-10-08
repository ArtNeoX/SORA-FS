// Off-screen passes for GlassDisplay. Never put on a scene renderer: HDRP would draw every
// untagged pass of it. The screen itself uses CirrusFS/GlassDisplayScreen.
Shader "Hidden/CirrusFS/GlassDisplayCanvas"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _GlowTex ("Glow", 2D) = "black" {}
    }

    CGINCLUDE
    #include "UnityCG.cginc"

    sampler2D _MainTex;
    float4 _MainTex_TexelSize;
    sampler2D _GlowTex;
    float2 _BlurDir;
    float _GlowThreshold;
    float _GlowIntensity;

    struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
    struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };

    v2f vert(appdata v)
    {
        v2f o;
        o.pos = UnityObjectToClipPos(v.vertex);
        o.uv = v.uv;
        o.color = v.color;
        return o;
    }
    ENDCG

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        // 0: painter geometry. Vertex colour, alpha taken from the texture (white for solids,
        // the font atlas for glyphs).
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target
            {
                float4 c = i.color;
                c.a *= tex2D(_MainTex, i.uv).a;
                return c;
            }
            ENDCG
        }

        // 1: glow prefilter. Soft threshold, so only bright symbology halos.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target
            {
                float3 c = tex2D(_MainTex, i.uv).rgb;
                float peak = max(c.r, max(c.g, c.b));
                float k = saturate((peak - _GlowThreshold) / max(1.0 - _GlowThreshold, 1e-4));
                return float4(c * k, 1);
            }
            ENDCG
        }

        // 2: separable 9-tap gaussian along _BlurDir (in texels).
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target
            {
                float2 d = _BlurDir * _MainTex_TexelSize.xy;
                float3 c = tex2D(_MainTex, i.uv).rgb * 0.2270270;
                c += (tex2D(_MainTex, i.uv + d * 1).rgb + tex2D(_MainTex, i.uv - d * 1).rgb) * 0.1945946;
                c += (tex2D(_MainTex, i.uv + d * 2).rgb + tex2D(_MainTex, i.uv - d * 2).rgb) * 0.1216216;
                c += (tex2D(_MainTex, i.uv + d * 3).rgb + tex2D(_MainTex, i.uv - d * 3).rgb) * 0.0540541;
                c += (tex2D(_MainTex, i.uv + d * 4).rgb + tex2D(_MainTex, i.uv - d * 4).rgb) * 0.0162162;
                return float4(c, 1);
            }
            ENDCG
        }

        // 3: composite symbology + halo.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target
            {
                float3 c = tex2D(_MainTex, i.uv).rgb + tex2D(_GlowTex, i.uv).rgb * _GlowIntensity;
                return float4(c, 1);
            }
            ENDCG
        }
    }
}
