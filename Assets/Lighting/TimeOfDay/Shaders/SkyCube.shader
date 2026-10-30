// Renders one face of the sky cubemap that reflections and ambient light use: the sky and the
// ground seen through the atmosphere, without the sun and moon discs or the stars. Drawn by
// EnvironmentLighting, one full-screen triangle per face. Pre-exposed by _TOD_CubeFace.z, the
// exposure at the time of the render (Unity clamps reflection intensities, so the cube has to
// carry it); EnvironmentLighting divides it back out of the ambient.
Shader "Hidden/CirrusFS/TimeOfDay/SkyCube"
{
    SubShader
    {
        ZWrite Off ZTest Always Cull Off Blend Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "TimeOfDaySky.hlsl"

            float4 _TOD_CubeFace;   // x: face index (+X, -X, +Y, -Y, +Z, -Z), y: face size, z: pre-exposure

            float4 Vert(uint id : SV_VertexID) : SV_Position
            {
                float2 uv = float2((id << 1) & 2, id & 2);
                return float4(uv * 2.0 - 1.0, 0.0, 1.0);
            }

            // Cubemap face texel -> direction, D3D/Unity convention: u right, v down, rows in
            // memory order (SV_Position addresses them the same way on every API).
            float3 FaceDirection(int face, float2 st)
            {
                float u = st.x, v = st.y;
                float3 d = face == 0 ? float3(1, -v, -u)
                         : face == 1 ? float3(-1, -v, u)
                         : face == 2 ? float3(u, 1, v)
                         : face == 3 ? float3(u, -1, -v)
                         : face == 4 ? float3(u, -v, 1)
                         :             float3(-u, -v, -1);
                return normalize(d);
            }

            float4 Frag(float4 p : SV_Position) : SV_Target
            {
                float2 st = p.xy / _TOD_CubeFace.y * 2.0 - 1.0;
                float3 v = FaceDirection((int)_TOD_CubeFace.x, st);
                bool ground;
                float3 sky = TodSkyBackground(v, dot(v, _TOD_Up.xyz), ground);
                return float4(sky * _TOD_CubeFace.z, 1.0);
            }
            ENDHLSL
        }
    }
}
