// Bright additive glow for shots (ShotHighlight): bullets, their tracers, bullet holes and the sparks they throw.
// Hidden behind walls (ZTest LessEqual). Its shape is the alpha of _MainTex (a bullet hole's decal texture, a particle's
// texture; white = the whole mesh), times the vertex colour's alpha for particles / lines (_UseVertexAlpha).
Shader "FPSChess/ShotGlow"
{
    Properties
    {
        _Color ("Color", Color) = (1, 0.9, 0.5, 1)
        _Intensity ("Intensity", Range(0, 8)) = 2
        _MainTex ("Shape (alpha)", 2D) = "white" {}
        [Toggle] _UseVertexAlpha ("Use Vertex Alpha (particles, lines)", Float) = 0
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("ZTest", Float) = 4
        _OffsetFactor ("Depth Offset Factor", Float) = 0
        _OffsetUnits ("Depth Offset Units", Float) = 0
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Transparent" "Queue" = "Transparent+100" "IgnoreProjector" = "True" }

        Pass
        {
            Name "ShotGlow"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            ZTest [_ZTest]
            Offset [_OffsetFactor], [_OffsetUnits]
            ZWrite Off
            Cull Off
            Blend SrcAlpha One

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half _Intensity;
                float4 _MainTex_ST;
                half _UseVertexAlpha;
                float _ZTest;
                float _OffsetFactor;
                float _OffsetUnits;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionHCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half shape = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv).a;
                half vertexAlpha = lerp(1.0h, i.color.a, _UseVertexAlpha); // a mesh may have no vertex colours
                return half4(_Color.rgb * _Intensity, saturate(shape * vertexAlpha * _Color.a));
            }
            ENDHLSL
        }
    }
}
