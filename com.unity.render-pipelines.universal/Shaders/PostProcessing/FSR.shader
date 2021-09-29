Shader "Hidden/Universal Render Pipeline/FSR"
{
    HLSLINCLUDE
        #pragma exclude_renderers gles
        #pragma multi_compile_local_fragment _ _FXAA
        #pragma multi_compile_local_fragment _ _USE_16BIT

        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Filtering.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/Shaders/PostProcessing/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Debug/DebuggingFullscreen.hlsl"

        // Setup pre-portability-header defines (sets up GLSL/HLSL path, packed math support, etc)
        #define A_GPU 1
        #define A_HLSL 1

        #if _USE_16BIT
            #define A_HALF
        #endif

        #include "Packages/com.unity.render-pipelines.core/Runtime/PostProcessing/Shaders/ffx/ffx_a.hlsl"

        #if _USE_16BIT
            #define FSR_EASU_H 1
            #define FSR_RCAS_H 1
        #else
            #define FSR_EASU_F 1
            #define FSR_RCAS_F 1
        #endif

        #include "Packages/com.unity.render-pipelines.core/Runtime/PostProcessing/Shaders/ffx/ffx_fsr1.hlsl"

        TEXTURE2D_X(_SourceTex);

        float4 _FsrConstants0;
        float4 _FsrConstants1;
        float4 _FsrConstants2;
        float4 _FsrConstants3;
        float4 _SourceSize;

        #define FSR_CONSTANTS_0 asuint(_FsrConstants0)
        #define FSR_CONSTANTS_1 asuint(_FsrConstants1)
        #define FSR_CONSTANTS_2 asuint(_FsrConstants2)
        #define FSR_CONSTANTS_3 asuint(_FsrConstants3)

        // EASU glue functions
        #if _USE_16BIT
        AH4 FsrEasuRH(AF2 p)
        #else
        AF4 FsrEasuRF(AF2 p)
        #endif
        {
            return GATHER_RED_TEXTURE2D_X(_SourceTex, sampler_LinearClamp, p);
        }

        #if _USE_16BIT
        AH4 FsrEasuGH(AF2 p)
        #else
        AF4 FsrEasuGF(AF2 p)
        #endif
        {
            return GATHER_GREEN_TEXTURE2D_X(_SourceTex, sampler_LinearClamp, p);
        }

        #if _USE_16BIT
        AH4 FsrEasuBH(AF2 p)
        #else
        AF4 FsrEasuBF(AF2 p)
        #endif
        {
            return GATHER_BLUE_TEXTURE2D_X(_SourceTex, sampler_LinearClamp, p);
        }

        // RCAS glue functions
        #if _USE_16BIT
        AH4 FsrRcasLoadH(ASW2 p)
        #else
        AF4 FsrRcasLoadF(ASU2 p)
        #endif
        {
            return _SourceTex[p];
        }

        #if _USE_16BIT
        void FsrRcasInputH(inout AH1 r, inout AH1 g, inout AH1 b)
        #else
        void FsrRcasInputF(inout AF1 r, inout AF1 g, inout AF1 b)
        #endif
        {
            // No conversion to linear necessary since it's already performed during EASU output
        }

        half4 FragSetup(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            float2 uv = UnityStereoTransformScreenSpaceTex(input.uv);
            float2 positionNDC = uv;
            int2   positionSS = uv * _SourceSize.xy;

            half3 color = SAMPLE_TEXTURE2D_X(_SourceTex, sampler_LinearClamp, uv).xyz;

            #if _FXAA
            {
                color = ApplyFXAA(color, positionNDC, positionSS, _SourceSize, TEXTURE2D_ARGS(_SourceTex, sampler_LinearClamp));
            }
            #endif

            // EASU expects the input image to be in gamma 2.0 color space
            color = LinearToGamma20(color);

            half4 finalColor = half4(color, 1);

            #if defined(DEBUG_DISPLAY)
            half4 debugColor = 0;

            if(CanDebugOverrideOutputColor(finalColor, uv, debugColor))
            {
                return debugColor;
            }
            #endif

            return finalColor;
        }

        half4 FragEASU(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            float2 uv = UnityStereoTransformScreenSpaceTex(input.uv);
            uint2 integerUv = uv * _ScreenParams.xy;

            // Note: The input data for EASU should always be in gamma2.0 color space from the previous pass

            #if _USE_16BIT
            AH3 color;
            FsrEasuH(
            #else
            AF3 color;
            FsrEasuF(
            #endif
                color, integerUv, FSR_CONSTANTS_0, FSR_CONSTANTS_1, FSR_CONSTANTS_2, FSR_CONSTANTS_3
            );

            // Convert back to linear color space before this data is sent into RCAS
            color = Gamma20ToLinear(color);

            return half4(color, 1.0);
        }

        half4 FragRCAS(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            float2 uv = UnityStereoTransformScreenSpaceTex(input.uv);
            uint2 integerUv = uv * _ScreenParams.xy;

            #if _USE_16BIT
            AH3 color;
            FsrRcasH(
            #else
            AF3 color;
            FsrRcasF(
            #endif
                color.r, color.g, color.b, integerUv, FSR_CONSTANTS_0
            );

            return half4(color, 1.0);
        }

    ENDHLSL

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline"}
        LOD 100
        ZTest Always ZWrite Off Cull Off

        Pass
        {
            Name "Setup"

            HLSLPROGRAM
                #pragma vertex FullscreenVert
                #pragma fragment FragSetup
            ENDHLSL
        }

        Pass
        {
            Name "EASU"

            HLSLPROGRAM
                #pragma vertex FullscreenVert
                #pragma fragment FragEASU
                #pragma target 4.5
            ENDHLSL
        }

        Pass
        {
            Name "RCAS"

            HLSLPROGRAM
                #pragma vertex FullscreenVert
                #pragma fragment FragRCAS
            ENDHLSL
        }
    }
}
