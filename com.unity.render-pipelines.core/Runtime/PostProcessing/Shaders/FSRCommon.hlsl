// TODO: Move all of this to a common FSR hlsl file
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

// EASU glue functions
#if _USE_16BIT
AH4 FsrEasuRH(AF2 p)
{
    return (AH4)GATHER_RED_TEXTURE2D_X(FSR_INPUT_TEXTURE, FSR_INPUT_SAMPLER, p);
}
#else
AF4 FsrEasuRF(AF2 p)
{
    return GATHER_RED_TEXTURE2D_X(FSR_INPUT_TEXTURE, FSR_INPUT_SAMPLER, p);
}
#endif

#if _USE_16BIT
AH4 FsrEasuGH(AF2 p)
{
    return (AH4)GATHER_GREEN_TEXTURE2D_X(FSR_INPUT_TEXTURE, FSR_INPUT_SAMPLER, p);
}
#else
AF4 FsrEasuGF(AF2 p)
{
    return GATHER_GREEN_TEXTURE2D_X(FSR_INPUT_TEXTURE, FSR_INPUT_SAMPLER, p);
}
#endif

#if _USE_16BIT
AH4 FsrEasuBH(AF2 p)
{
    return (AH4)GATHER_BLUE_TEXTURE2D_X(FSR_INPUT_TEXTURE, FSR_INPUT_SAMPLER, p);
}
#else
AF4 FsrEasuBF(AF2 p)
{
    return GATHER_BLUE_TEXTURE2D_X(FSR_INPUT_TEXTURE, FSR_INPUT_SAMPLER, p);
}
#endif

// RCAS glue functions
#if _USE_16BIT
AH4 FsrRcasLoadH(ASW2 p)
{
    return (AH4)FSR_INPUT_TEXTURE[p];
}
#else
AF4 FsrRcasLoadF(ASU2 p)
{
    return FSR_INPUT_TEXTURE[p];
}
#endif

#if _USE_16BIT
void FsrRcasInputH(inout AH1 r, inout AH1 g, inout AH1 b)
{
    // No conversion to linear necessary since it's already performed during EASU output
}
#else
void FsrRcasInputF(inout AF1 r, inout AF1 g, inout AF1 b)
{
    // No conversion to linear necessary since it's already performed during EASU output
}
#endif

half3 ApplyEASU(uint2 positionSS)
{
    // Note: The input data for EASU should always be in gamma2.0 color space from the previous pass

    #if _USE_16BIT
    AH3 color;
    FsrEasuH(
    #else
    AF3 color;
    FsrEasuF(
    #endif
        color, positionSS, FSR_CONSTANTS_0, FSR_CONSTANTS_1, FSR_CONSTANTS_2, FSR_CONSTANTS_3
    );

    // Convert back to linear color space before this data is sent into RCAS
    color *= color;

    return color;
}

half3 ApplyRCAS(uint2 positionSS)
{
    #if _USE_16BIT
    AH3 color;
    FsrRcasH(
    #else
    AF3 color;
    FsrRcasF(
    #endif
        color.r, color.g, color.b, positionSS, FSR_CONSTANTS_0
    );

    return color;
}
