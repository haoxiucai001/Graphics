Shader "Hidden/Universal Render Pipeline/FSR"
{
    HLSLINCLUDE
        #pragma exclude_renderers gles

        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Filtering.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/Shaders/PostProcessing/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Debug/DebuggingFullscreen.hlsl"

        // Setup pre-portability-header defines (sets up GLSL/HLSL path, packed math support, etc)
        #define A_GPU 1
        #define A_HLSL 1

        #include "Packages/com.unity.render-pipelines.core/Runtime/PostProcessing/Shaders/ffx/ffx_a.hlsl"

        #define FSR_EASU_F 1
        #define FSR_RCAS_F 1

        #include "Packages/com.unity.render-pipelines.core/Runtime/PostProcessing/Shaders/ffx/ffx_fsr1.hlsl"

        #define COMPARE_ENABLED true
        #define COMPARE_XPOS 0.49

        #define FXAA_SPAN_MAX           (8.0)
        #define FXAA_REDUCE_MUL         (1.0 / 8.0)
        #define FXAA_REDUCE_MIN         (1.0 / 128.0)

        TEXTURE2D_X(_SourceTex);

        float4 _SourceSize;

        half3 Fetch(float2 coords, float2 offset)
        {
            float2 uv = coords + offset;
            return SAMPLE_TEXTURE2D_X(_SourceTex, sampler_LinearClamp, uv).xyz;
        }

        half3 Load(int2 icoords, int idx, int idy)
        {
            #if SHADER_API_GLES
            float2 uv = (icoords + int2(idx, idy)) * _SourceSize.zw;
            return SAMPLE_TEXTURE2D_X(_SourceTex, sampler_LinearClamp, uv).xyz;
            #else
            return LOAD_TEXTURE2D_X(_SourceTex, clamp(icoords + int2(idx, idy), 0, _SourceSize.xy - 1.0)).xyz;
            #endif
        }

        // EASU glue functions
        AF4 FsrEasuRF(AF2 p) { return GATHER_RED_TEXTURE2D_X(_SourceTex, sampler_LinearClamp, p); }
        AF4 FsrEasuGF(AF2 p) { return GATHER_GREEN_TEXTURE2D_X(_SourceTex, sampler_LinearClamp, p); }
        AF4 FsrEasuBF(AF2 p) { return GATHER_BLUE_TEXTURE2D_X(_SourceTex, sampler_LinearClamp, p); }

        // RCAS glue functions
        AF4 FsrRcasLoadF(ASU2 p) { return _SourceTex[p]; }
        void FsrRcasInputF(inout AF1 r, inout AF1 g, inout AF1 b)
        {
            // No conversion to linear necessary since it's already performed during EASU output
        }

        half4 FragEASU(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            float2 uv = UnityStereoTransformScreenSpaceTex(input.uv);
            uint2 integerUv = floor(uv * _ScreenParams.xy);

            float3 color = SAMPLE_TEXTURE2D_X(_SourceTex, sampler_PointClamp, uv).xyz;

            AU4 con0 = (AU4)0;
            AU4 con1 = (AU4)0;
            AU4 con2 = (AU4)0;
            AU4 con3 = (AU4)0;

            FsrEasuCon(con0, con1, con2, con3,
                floor(_ScaledScreenParams.x), floor(_ScaledScreenParams.y), // Input viewport size
                floor(_ScaledScreenParams.x), floor(_ScaledScreenParams.y), // Size of input image (This may be larger than the input viewport in some cases)
                _ScreenParams.x, _ScreenParams.y);            // Size of output image

            // Note: The input data for EASU should always be in gamma2.0 color space from the previous pass

            AF3 c;
            FsrEasuF(c, integerUv, con0, con1, con2, con3);

            float3 finalColor = float3(0.0, 0.0, 0.0);

//#if COMPARE_ENABLED
//            const float pixelSize = (_ScreenParams.z - 1.0);
//            const float pixelBarDist = (pixelSize * 2.0) + (pixelSize * 0.5);
//            const float xPos = COMPARE_XPOS;//fmod(_Time.x, 1.0);
//            if (abs(xPos - uv.x) > pixelBarDist)
//            {
//                finalColor = uv.x < xPos ? c : color;
//            }
//#else
            finalColor = c;
            //finalColor = color;
//#endif

            // Convert back to linear color space before this data is sent into RCAS
            finalColor = Gamma20ToLinear(finalColor);

            return half4(finalColor, 1.0);
        }

        half4 FragRCAS(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            float2 uv = UnityStereoTransformScreenSpaceTex(input.uv);
            uint2 integerUv = floor(uv * _ScreenParams.xy);

            float3 color = SAMPLE_TEXTURE2D_X(_SourceTex, sampler_PointClamp, uv).xyz;

            AU4 con = (AU4)0;
            FsrRcasCon(con, 0.2);

            AF3 c;
            FsrRcasF(c.r, c.g, c.b, integerUv, con);

            float3 finalColor = float3(0.0, 0.0, 0.0);

//#if COMPARE_ENABLED
//            const float pixelSize = (_ScreenParams.z - 1.0);
//            const float pixelBarDist = (pixelSize * 2.0) + (pixelSize * 0.5);
//            const float xPos = COMPARE_XPOS;//fmod(_Time.x, 1.0);
//            if (abs(xPos - uv.x) > pixelBarDist)
//            {
//                finalColor = uv.x < xPos ? c : color;
//            }
//#else
            finalColor = c;
            //finalColor = color;
//#endif

            return half4(finalColor, 1.0);
        }

        half4 FragFXAA(Varyings input) : SV_Target
        {
            float2 uv = UnityStereoTransformScreenSpaceTex(input.uv);
            float2 positionNDC = uv;
            int2   positionSS  = uv * _SourceSize.xy;

            half3 color = Load(positionSS, 0, 0).xyz;

            // Edge detection
            half3 rgbNW = Load(positionSS, -1, -1);
            half3 rgbNE = Load(positionSS,  1, -1);
            half3 rgbSW = Load(positionSS, -1,  1);
            half3 rgbSE = Load(positionSS,  1,  1);

            rgbNW = saturate(rgbNW);
            rgbNE = saturate(rgbNE);
            rgbSW = saturate(rgbSW);
            rgbSE = saturate(rgbSE);
            color = saturate(color);

            half lumaNW = Luminance(rgbNW);
            half lumaNE = Luminance(rgbNE);
            half lumaSW = Luminance(rgbSW);
            half lumaSE = Luminance(rgbSE);
            half lumaM = Luminance(color);

            float2 dir;
            dir.x = -((lumaNW + lumaNE) - (lumaSW + lumaSE));
            dir.y = ((lumaNW + lumaSW) - (lumaNE + lumaSE));

            half lumaSum = lumaNW + lumaNE + lumaSW + lumaSE;
            float dirReduce = max(lumaSum * (0.25 * FXAA_REDUCE_MUL), FXAA_REDUCE_MIN);
            float rcpDirMin = rcp(min(abs(dir.x), abs(dir.y)) + dirReduce);

            dir = min((FXAA_SPAN_MAX).xx, max((-FXAA_SPAN_MAX).xx, dir * rcpDirMin)) * _SourceSize.zw;

            // Blur
            half3 rgb03 = Fetch(positionNDC, dir * (0.0 / 3.0 - 0.5));
            half3 rgb13 = Fetch(positionNDC, dir * (1.0 / 3.0 - 0.5));
            half3 rgb23 = Fetch(positionNDC, dir * (2.0 / 3.0 - 0.5));
            half3 rgb33 = Fetch(positionNDC, dir * (3.0 / 3.0 - 0.5));

            rgb03 = saturate(rgb03);
            rgb13 = saturate(rgb13);
            rgb23 = saturate(rgb23);
            rgb33 = saturate(rgb33);

            half3 rgbA = 0.5 * (rgb13 + rgb23);
            half3 rgbB = rgbA * 0.5 + 0.25 * (rgb03 + rgb33);

            half lumaB = Luminance(rgbB);

            half lumaMin = Min3(lumaM, lumaNW, Min3(lumaNE, lumaSW, lumaSE));
            half lumaMax = Max3(lumaM, lumaNW, Max3(lumaNE, lumaSW, lumaSE));

            color = ((lumaB < lumaMin) || (lumaB > lumaMax)) ? rgbA : rgbB;

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

        Pass
        {
            Name "FXAA"

            HLSLPROGRAM
                #pragma vertex FullscreenVert
                #pragma fragment FragFXAA
            ENDHLSL
        }
    }
}
