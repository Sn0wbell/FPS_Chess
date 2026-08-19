Shader "FPS/ZoneOverlay"
{
    Properties
    {
        _BaseMap ("Base Map", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1,1,1,1)

        _RimPower ("Rim Power", Range(0.5, 8)) = 3
        _RimIntensity ("Rim Intensity", Range(0,3)) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline"="UniversalRenderPipeline"
            "RenderType"="Opaque"
            "Queue"="Geometry"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            ZWrite On
            Blend One OneMinusSrcAlpha
            Cull Back

            HLSLPROGRAM

            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            #define MAX_CAGE 16

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 worldPos    : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float2 uv          : TEXCOORD2;
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            float4 _BaseColor;

            float _RimPower;
            float _RimIntensity;

            int _CageCount;
            float4 _CageData[MAX_CAGE];
            float4 _CageHeight[MAX_CAGE];
            float4 _CageColor[MAX_CAGE];
            float4 _CageType[MAX_CAGE];

            Varyings vert (Attributes v)
            {
                Varyings o;

                VertexPositionInputs pos = GetVertexPositionInputs(v.positionOS.xyz);
                VertexNormalInputs normal = GetVertexNormalInputs(v.normalOS);

                o.positionHCS = pos.positionCS;
                o.worldPos = pos.positionWS;
                o.normalWS = normal.normalWS;
                o.uv = v.uv;

                return o;
            }

            half4 frag (Varyings i) : SV_Target
            {
                float3 normal = normalize(i.normalWS);
                float3 viewDir = normalize(GetCameraPositionWS() - i.worldPos);

                float4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv);
                float3 baseCol = tex.rgb * _BaseColor.rgb;

                float bestRadius = 1e9;
                float3 zoneColor = baseCol;
                float bestWeight = 0;

                for (int c = 0; c < _CageCount; c++)
                {
                    float3 center = _CageData[c].xyz;
                    float radius  = _CageData[c].w;

                    float distXZ = distance(i.worldPos.xz, center.xz);
                    
                    float inside = step(distXZ, radius);

                    if (inside <= 0.0)
                        continue;

                    float4 zc = _CageColor[c];

                    if (radius < bestRadius)
                    {
                        bestRadius = radius;
                        zoneColor = zc.rgb;
                        bestWeight = zc.a;
                    }
                }

                // ===== LIGHTING =====
                Light mainLight = GetMainLight();
                float NdotL = saturate(dot(normal, mainLight.direction));
                float shadow = mainLight.shadowAttenuation;

                float3 diffuse = baseCol * mainLight.color * (NdotL * 1.2) * shadow;
                float3 ambient = SampleSH(normal) * baseCol * 1.1;

                float3 halfDir = normalize(mainLight.direction + viewDir);
                float spec = pow(saturate(dot(normal, halfDir)), 32.0);
                float3 specular = spec * mainLight.color * 0.4;

                float3 additional = 0;
                #ifdef _ADDITIONAL_LIGHTS
                uint count = GetAdditionalLightsCount();
                for (uint j = 0; j < count; j++)
                {
                    Light l = GetAdditionalLight(j, i.worldPos);
                    float ndl = saturate(dot(normal, l.direction));
                    additional += baseCol * l.color * ndl;
                }
                #endif

                float3 litColor =
                    diffuse +
                    ambient +
                    specular +
                    additional;

                // ===== RIM =====
                float rim = pow(1.0 - saturate(dot(viewDir, normal)), _RimPower);
                float3 rimLight = rim * _RimIntensity * litColor;

                float3 finalLit = litColor + rimLight;

                // ===== FINAL TINT =====
                float intensity = bestWeight;

                float3 finalColor = lerp(finalLit, zoneColor, intensity);

                return float4(saturate(finalColor), 1.0);
            }

            ENDHLSL
        }
    }
}