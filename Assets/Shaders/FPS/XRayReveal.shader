// Silhouette drawn THROUGH walls (ZTest Always) for revealed enemies (RevealSystem).
// Added as an extra material on the target's renderers while the reveal lasts.
Shader "FPSChess/XRayReveal"
{
    Properties
    {
        _Color ("Color", Color) = (1, 0.25, 0.15, 0.55)
        _RimPower ("Rim Power", Range(0.5, 8)) = 2.5
        _RimBoost ("Rim Boost", Range(0, 4)) = 1.5
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Transparent" "Queue" = "Overlay" }

        Pass
        {
            Name "XRay"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            ZTest Always
            ZWrite Off
            Cull Back
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half _RimPower;
                half _RimBoost;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 viewDirWS : TEXCOORD1;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                float3 positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionHCS = TransformWorldToHClip(positionWS);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.viewDirWS = GetWorldSpaceViewDir(positionWS);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half ndv = saturate(dot(normalize(i.normalWS), normalize(i.viewDirWS)));
                half rim = pow(1.0h - ndv, _RimPower) * _RimBoost;
                half4 c = _Color;
                c.rgb *= 1.0h + rim;
                c.a = saturate(c.a + rim * 0.5h);
                return c;
            }
            ENDHLSL
        }
    }
}
