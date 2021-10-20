using System;

namespace UnityEngine.Rendering
{

    [GenerateHLSL]
    public static class GeometryPoolConstants
    {
        public static int GeoPoolPosByteSize = 3 * 4;
        public static int GeoPoolUV0ByteSize = 2 * 4;
        public static int GeoPoolUV1ByteSize = 2 * 4;
        public static int GeoPoolNormalByteSize = 3 * 4;
        public static int GeoPoolTangentByteSize = 3 * 4 ;

        public static int GeoPoolIndexByteSize = 4;
        public static int GeoPoolVertexByteSize =
            GeoPoolPosByteSize + GeoPoolUV0ByteSize + GeoPoolUV1ByteSize + GeoPoolNormalByteSize + GeoPoolTangentByteSize + GeoPoolNormalByteSize;
    }

    [GenerateHLSL(needAccessors = false)]
    internal struct GeometryPoolVertex
    {
        public Vector3 pos;
        public Vector2 uv;
        public Vector2 uv1;
        public Vector3 N;
        public Vector3 T;
    }

    [Flags]
    [GenerateHLSL]
    internal enum GeoPoolInputFlags
    {
        None = 0,
        HasUV1 = 1 << 0,
        HasTangent = 1 << 1
    }

}
