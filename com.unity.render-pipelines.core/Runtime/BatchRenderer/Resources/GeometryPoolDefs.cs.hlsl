//
// This file was automatically generated. Please don't edit by hand. Execute Editor command [ Edit > Rendering > Generate Shader Includes ] instead
//

#ifndef GEOMETRYPOOLDEFS_CS_HLSL
#define GEOMETRYPOOLDEFS_CS_HLSL
//
// UnityEngine.Rendering.GeometryPoolConstants:  static fields
//
#define GEO_POOL_POS_BYTE_SIZE (12)
#define GEO_POOL_UV0BYTE_SIZE (8)
#define GEO_POOL_UV1BYTE_SIZE (8)
#define GEO_POOL_NORMAL_BYTE_SIZE (12)
#define GEO_POOL_TANGENT_BYTE_SIZE (12)
#define GEO_POOL_INDEX_BYTE_SIZE (4)
#define GEO_POOL_VERTEX_BYTE_SIZE (64)

//
// UnityEngine.Rendering.GeoPoolInputFlags:  static fields
//
#define GEOPOOLINPUTFLAGS_HAS_UV1 (1)
#define GEOPOOLINPUTFLAGS_HAS_TANGENT (2)

// Generated from UnityEngine.Rendering.GeometryPoolVertex
// PackingRules = Exact
struct GeometryPoolVertex
{
    float3 pos;
    float2 uv;
    float2 uv1;
    float3 N;
    float3 T;
};


#endif
