using System;

namespace HyperTizen.Bulbs
{
    public static class SampleImage
    {
        public const int Width = 64, Height = 48;

        /// <summary>
        /// Convert sampled pixel colors to NV12 format using BT.2020 color space
        /// Creates a virtual 64x48 image with sampled colors mapped to screen edges
        /// Uses BT.2020 coefficients for HDR10+ compatibility
        /// </summary>
        public static (byte[] Y, byte[] UV) ToNv12(Rgb10[] colors)
        {
            const int width = 64;
            const int height = 48;

            // Allocate NV12 buffers
            byte[] yData = new byte[width * height];
            byte[] uvData = new byte[width * height / 2]; // UV plane is half the size

            // Create virtual RGB image (same logic as original ToImage method)
            byte[] rgbImage = new byte[width * height * 3]; // RGB888

            // Initialize with black
            for (int i = 0; i < rgbImage.Length; i++)
            {
                rgbImage[i] = 0;
            }

            // 16-point color mapping (4 points per edge)
            // colors[0-3]   = Top edge (left to right)
            // colors[4-7]   = Right edge (top to bottom)
            // colors[8-11]  = Bottom edge (right to left)
            // colors[12-15] = Left edge (bottom to top)

            // Top edge (colors 0-3) with linear interpolation
            for (int x = 0; x < 64; x++)
            {
                // Determine which segment this x falls into (4 segments)
                float segmentPos = (x / 63.0f) * 3.0f; // 0.0 to 3.0
                int segment = Math.Min(2, (int)segmentPos); // 0, 1, or 2
                float t = segmentPos - segment; // Position within segment (0.0 to 1.0)

                byte r, g, b;
                if (segment == 0) // colors[0] to colors[1]
                {
                    r = (byte)((1 - t) * ScaleTo8Bit(colors[0].R) + t * ScaleTo8Bit(colors[1].R));
                    g = (byte)((1 - t) * ScaleTo8Bit(colors[0].G) + t * ScaleTo8Bit(colors[1].G));
                    b = (byte)((1 - t) * ScaleTo8Bit(colors[0].B) + t * ScaleTo8Bit(colors[1].B));
                }
                else if (segment == 1) // colors[1] to colors[2]
                {
                    r = (byte)((1 - t) * ScaleTo8Bit(colors[1].R) + t * ScaleTo8Bit(colors[2].R));
                    g = (byte)((1 - t) * ScaleTo8Bit(colors[1].G) + t * ScaleTo8Bit(colors[2].G));
                    b = (byte)((1 - t) * ScaleTo8Bit(colors[1].B) + t * ScaleTo8Bit(colors[2].B));
                }
                else // segment == 2, colors[2] to colors[3]
                {
                    r = (byte)((1 - t) * ScaleTo8Bit(colors[2].R) + t * ScaleTo8Bit(colors[3].R));
                    g = (byte)((1 - t) * ScaleTo8Bit(colors[2].G) + t * ScaleTo8Bit(colors[3].G));
                    b = (byte)((1 - t) * ScaleTo8Bit(colors[2].B) + t * ScaleTo8Bit(colors[3].B));
                }

                for (int y = 0; y < 4; y++)
                {
                    int idx = (y * width + x) * 3;
                    rgbImage[idx + 0] = r;
                    rgbImage[idx + 1] = g;
                    rgbImage[idx + 2] = b;
                }
            }

            // Bottom edge (colors 8-11) with linear interpolation (right to left)
            for (int x = 0; x < 64; x++)
            {
                // Determine which segment this x falls into (4 segments, reversed)
                float segmentPos = (x / 63.0f) * 3.0f; // 0.0 to 3.0
                int segment = Math.Min(2, (int)segmentPos); // 0, 1, or 2
                float t = segmentPos - segment; // Position within segment (0.0 to 1.0)

                byte r, g, b;
                if (segment == 0) // colors[11] to colors[10]
                {
                    r = (byte)((1 - t) * ScaleTo8Bit(colors[11].R) + t * ScaleTo8Bit(colors[10].R));
                    g = (byte)((1 - t) * ScaleTo8Bit(colors[11].G) + t * ScaleTo8Bit(colors[10].G));
                    b = (byte)((1 - t) * ScaleTo8Bit(colors[11].B) + t * ScaleTo8Bit(colors[10].B));
                }
                else if (segment == 1) // colors[10] to colors[9]
                {
                    r = (byte)((1 - t) * ScaleTo8Bit(colors[10].R) + t * ScaleTo8Bit(colors[9].R));
                    g = (byte)((1 - t) * ScaleTo8Bit(colors[10].G) + t * ScaleTo8Bit(colors[9].G));
                    b = (byte)((1 - t) * ScaleTo8Bit(colors[10].B) + t * ScaleTo8Bit(colors[9].B));
                }
                else // segment == 2, colors[9] to colors[8]
                {
                    r = (byte)((1 - t) * ScaleTo8Bit(colors[9].R) + t * ScaleTo8Bit(colors[8].R));
                    g = (byte)((1 - t) * ScaleTo8Bit(colors[9].G) + t * ScaleTo8Bit(colors[8].G));
                    b = (byte)((1 - t) * ScaleTo8Bit(colors[9].B) + t * ScaleTo8Bit(colors[8].B));
                }

                for (int y = 44; y < 48; y++)
                {
                    int idx = (y * width + x) * 3;
                    rgbImage[idx + 0] = r;
                    rgbImage[idx + 1] = g;
                    rgbImage[idx + 2] = b;
                }
            }

            // Left edge (colors 12-15) with linear interpolation (bottom to top)
            for (int y = 0; y < 48; y++)
            {
                // Determine which segment this y falls into (4 segments)
                float segmentPos = (y / 47.0f) * 3.0f; // 0.0 to 3.0
                int segment = Math.Min(2, (int)segmentPos); // 0, 1, or 2
                float t = segmentPos - segment; // Position within segment (0.0 to 1.0)

                byte r, g, b;
                if (segment == 0) // colors[15] to colors[14]
                {
                    r = (byte)((1 - t) * ScaleTo8Bit(colors[15].R) + t * ScaleTo8Bit(colors[14].R));
                    g = (byte)((1 - t) * ScaleTo8Bit(colors[15].G) + t * ScaleTo8Bit(colors[14].G));
                    b = (byte)((1 - t) * ScaleTo8Bit(colors[15].B) + t * ScaleTo8Bit(colors[14].B));
                }
                else if (segment == 1) // colors[14] to colors[13]
                {
                    r = (byte)((1 - t) * ScaleTo8Bit(colors[14].R) + t * ScaleTo8Bit(colors[13].R));
                    g = (byte)((1 - t) * ScaleTo8Bit(colors[14].G) + t * ScaleTo8Bit(colors[13].G));
                    b = (byte)((1 - t) * ScaleTo8Bit(colors[14].B) + t * ScaleTo8Bit(colors[13].B));
                }
                else // segment == 2, colors[13] to colors[12]
                {
                    r = (byte)((1 - t) * ScaleTo8Bit(colors[13].R) + t * ScaleTo8Bit(colors[12].R));
                    g = (byte)((1 - t) * ScaleTo8Bit(colors[13].G) + t * ScaleTo8Bit(colors[12].G));
                    b = (byte)((1 - t) * ScaleTo8Bit(colors[13].B) + t * ScaleTo8Bit(colors[12].B));
                }

                for (int x = 0; x < 3; x++)
                {
                    int idx = (y * width + x) * 3;
                    rgbImage[idx + 0] = r;
                    rgbImage[idx + 1] = g;
                    rgbImage[idx + 2] = b;
                }
            }

            // Right edge (colors 4-7) with linear interpolation (top to bottom)
            for (int y = 0; y < 48; y++)
            {
                // Determine which segment this y falls into (4 segments)
                float segmentPos = (y / 47.0f) * 3.0f; // 0.0 to 3.0
                int segment = Math.Min(2, (int)segmentPos); // 0, 1, or 2
                float t = segmentPos - segment; // Position within segment (0.0 to 1.0)

                byte r, g, b;
                if (segment == 0) // colors[4] to colors[5]
                {
                    r = (byte)((1 - t) * ScaleTo8Bit(colors[4].R) + t * ScaleTo8Bit(colors[5].R));
                    g = (byte)((1 - t) * ScaleTo8Bit(colors[4].G) + t * ScaleTo8Bit(colors[5].G));
                    b = (byte)((1 - t) * ScaleTo8Bit(colors[4].B) + t * ScaleTo8Bit(colors[5].B));
                }
                else if (segment == 1) // colors[5] to colors[6]
                {
                    r = (byte)((1 - t) * ScaleTo8Bit(colors[5].R) + t * ScaleTo8Bit(colors[6].R));
                    g = (byte)((1 - t) * ScaleTo8Bit(colors[5].G) + t * ScaleTo8Bit(colors[6].G));
                    b = (byte)((1 - t) * ScaleTo8Bit(colors[5].B) + t * ScaleTo8Bit(colors[6].B));
                }
                else // segment == 2, colors[6] to colors[7]
                {
                    r = (byte)((1 - t) * ScaleTo8Bit(colors[6].R) + t * ScaleTo8Bit(colors[7].R));
                    g = (byte)((1 - t) * ScaleTo8Bit(colors[6].G) + t * ScaleTo8Bit(colors[7].G));
                    b = (byte)((1 - t) * ScaleTo8Bit(colors[6].B) + t * ScaleTo8Bit(colors[7].B));
                }

                for (int x = 61; x < 64; x++)
                {
                    int idx = (y * width + x) * 3;
                    rgbImage[idx + 0] = r;
                    rgbImage[idx + 1] = g;
                    rgbImage[idx + 2] = b;
                }
            }

            // Convert RGB to NV12 using BT.2020 color space (HDR10+ compatible)
            // Y plane
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int rgbIdx = (y * width + x) * 3;
                    byte r = rgbImage[rgbIdx + 0];
                    byte g = rgbImage[rgbIdx + 1];
                    byte b = rgbImage[rgbIdx + 2];

                    // BT.2020 Y = 0.2627R + 0.678G + 0.0593B
                    int yVal = (int)(0.2627 * r + 0.678 * g + 0.0593 * b);
                    yData[y * width + x] = (byte)Math.Max(0, Math.Min(255, yVal));
                }
            }

            // UV plane (interleaved, subsampled 2x2)
            for (int y = 0; y < height; y += 2)
            {
                for (int x = 0; x < width; x += 2)
                {
                    // Sample 2x2 block
                    int rgbIdx = (y * width + x) * 3;
                    byte r = rgbImage[rgbIdx + 0];
                    byte g = rgbImage[rgbIdx + 1];
                    byte b = rgbImage[rgbIdx + 2];

                    // BT.2020 U = -0.1396R - 0.36037G + 0.5B + 128
                    // BT.2020 V = 0.5R - 0.4598G - 0.0402B + 128
                    int uVal = (int)(-0.1396 * r - 0.36037 * g + 0.5 * b + 128);
                    int vVal = (int)(0.5 * r - 0.4598 * g - 0.0402 * b + 128);

                    int uvIdx = (y / 2) * width + x;
                    uvData[uvIdx + 0] = (byte)Math.Max(0, Math.Min(255, uVal)); // U
                    uvData[uvIdx + 1] = (byte)Math.Max(0, Math.Min(255, vVal)); // V
                }
            }

            return (yData, uvData);
        }

        /// <summary>
        /// Convert 10-bit color value (0-1023) to 8-bit (0-255) using proper scaling
        /// Uses scaling rather than clamping to preserve color accuracy
        /// </summary>
        private static byte ScaleTo8Bit(int value)
        {
            // Scale 10-bit (0-1023) to 8-bit (0-255)
            return (byte)Math.Min(255, value * 255 / 1023);
        }
    }
}
