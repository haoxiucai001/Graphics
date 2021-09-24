using Unity.Collections;

namespace UnityEngine.Rendering
{
    /// <summary>
    /// FSR Utility class.
    /// </summary>
    public static class FSRUtils
    {
        /// <summary>
        /// Calculates the constant values required by the FSR EASU shader and returns them in an array
        /// </summary>
        /// <param name="inputViewportSizeInPixels">This the rendered image resolution being upscaled</param>
        /// <param name="inputImageSizeInPixels">This is the resolution of the resource containing the input image (useful for dynamic resolution)</param>
        /// <param name="outputImageSizeInPixels">This is the display resolution which the input image gets upscaled to</param>
        public static NativeArray<uint> CalculateFsrEasuConstants(Vector2 inputViewportSizeInPixels, Vector2 inputImageSizeInPixels, Vector2 outputImageSizeInPixels)
        {
            NativeArray<uint> constantsArray = new NativeArray<uint>(16, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            NativeSlice<float> constants = new NativeSlice<uint>(constantsArray).SliceConvert<float>();

            // Output integer position to a pixel position in viewport.
            constants[0] = (inputViewportSizeInPixels.x / outputImageSizeInPixels.x);
            constants[1] = (inputViewportSizeInPixels.y / outputImageSizeInPixels.y);
            constants[2] = (0.5f * inputViewportSizeInPixels.x / outputImageSizeInPixels.x - 0.5f);
            constants[3] = (0.5f * inputViewportSizeInPixels.y / outputImageSizeInPixels.y - 0.5f);

            // Viewport pixel position to normalized image space.
            // This is used to get upper-left of 'F' tap.
            constants[4] = (1.0f / inputImageSizeInPixels.x);
            constants[5] = (1.0f / inputImageSizeInPixels.y);

            // Centers of gather4, first offset from upper-left of 'F'.
            //      +---+---+
            //      |   |   |
            //      +--(0)--+
            //      | b | c |
            //  +---F---+---+---+
            //  | e | f | g | h |
            //  +--(1)--+--(2)--+
            //  | i | j | k | l |
            //  +---+---+---+---+
            //      | n | o |
            //      +--(3)--+
            //      |   |   |
            //      +---+---+
            constants[6] = (1.0f / inputImageSizeInPixels.x);
            constants[7] = (-1.0f / inputImageSizeInPixels.y);

            // These are from (0) instead of 'F'.
            constants[8]  = (-1.0f / inputImageSizeInPixels.x);
            constants[9]  = (2.0f / inputImageSizeInPixels.y);
            constants[10] = (1.0f / inputImageSizeInPixels.x);
            constants[11] = (2.0f / inputImageSizeInPixels.y);
            constants[12] = (0.0f / inputImageSizeInPixels.x);
            constants[13] = (4.0f / inputImageSizeInPixels.y);

            // We just write 0.0f to initialize the memory to zero here since float zero and uint zero share the same bit pattern
            constants[14] = 0.0f;
            constants[15] = 0.0f;

            return constantsArray;
        }


        /// <summary>
        /// Calculates the constant values required by the FSR RCAS shader and returns them in an array
        /// </summary>
        /// <param name="sharpness">The scale is {0.0 := maximum, to N>0, where N is the number of stops(halving) of the reduction of sharpness</param>
        public static NativeArray<uint> CalculateFsrRcasConstants(float sharpness)
        {
            NativeArray<uint> constantsArray = new NativeArray<uint>(4, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            NativeSlice<uint> constants = new NativeSlice<uint>(constantsArray);

            // Transform from stops to linear value.
            sharpness = Mathf.Pow(2.0f, -sharpness);

            constantsArray.ReinterpretStore<float>(0, sharpness);
            constants[1] = ((uint)Mathf.FloatToHalf(sharpness)) | ((uint)(Mathf.FloatToHalf(sharpness) << 16));
            constants[2] = 0;
            constants[3] = 0;

            return constantsArray;
        }
    }
}
