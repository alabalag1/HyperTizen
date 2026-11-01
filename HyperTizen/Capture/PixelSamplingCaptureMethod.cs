using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using HyperTizen.Bulbs;
using Tizen.System;

namespace HyperTizen.Capture
{
    /// <summary>
    /// Pixel sampling capture method using libvideoenhance.so
    /// Samples individual pixels from screen edges for ambient lighting
    /// Adapted from original HyperTizen Capturer.cs to use NV12/FlatBuffers format
    /// </summary>
    public class PixelSamplingCaptureMethod : ICaptureMethod
    {
        private bool _isInitialized = false;
        private Condition _condition;

        // Track which API variant and library path works
        private string _workingVariant = null; // T6, T7, T9A, T9B, T9C, T9 (ppi_ve_*)
        private string _workingLibPath = null; // SO, SO0 (only .so and .so.0 exist on Tizen 9)

        // Pre-calculated pixel coordinates (calculated once during initialization)
        private struct PixelCoordinate
        {
            public int X;
            public int Y;
        }
        private PixelCoordinate[] _pixelCoordinates = null;

        // 16-point sampling grid (normalized coordinates 0.0-1.0)
        // 4 points per edge for better color representation
        // With synchronized batching, we achieve 40 FPS even with 16 points
        private CapturePoint[] _capturedPoints = new CapturePoint[] {
            // Top edge (4 points) - left to right
            new CapturePoint(0.20, 0.05),
            new CapturePoint(0.40, 0.05),
            new CapturePoint(0.60, 0.05),
            new CapturePoint(0.80, 0.05),
            // Right edge (4 points) - top to bottom
            new CapturePoint(0.95, 0.20),
            new CapturePoint(0.95, 0.40),
            new CapturePoint(0.95, 0.60),
            new CapturePoint(0.95, 0.80),
            // Bottom edge (4 points) - right to left
            new CapturePoint(0.80, 0.95),
            new CapturePoint(0.60, 0.95),
            new CapturePoint(0.40, 0.95),
            new CapturePoint(0.20, 0.95),
            // Left edge (4 points) - bottom to top
            new CapturePoint(0.05, 0.80),
            new CapturePoint(0.05, 0.60),
            new CapturePoint(0.05, 0.40),
            new CapturePoint(0.05, 0.20)
        };

        public string Name => "Pixel Sampling";
        public CaptureMethodType Type => CaptureMethodType.PixelSampling;

        #region P/Invoke Declarations

        // ===== Tizen 6 API (cs_ve_* prefix) - Test all library paths =====

        // Library: /usr/lib/libvideoenhance.so
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "cs_ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition_T6_SO(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "cs_ve_set_rgb_measure_position")]
        private static extern int MeasurePosition_T6_SO(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "cs_ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T6_SO(int index, out Color color);

        // Library: /usr/lib/libvideoenhance.so.0
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "cs_ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition_T6_SO0(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "cs_ve_set_rgb_measure_position")]
        private static extern int MeasurePosition_T6_SO0(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "cs_ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T6_SO0(int index, out Color color);

        // ===== Tizen 7 API (ve_* prefix) - Test all library paths =====

        // Library: /usr/lib/libvideoenhance.so
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition_T7_SO(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ve_set_rgb_measure_position")]
        private static extern int MeasurePosition_T7_SO(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T7_SO(int index, out Color color);

        // Library: /usr/lib/libvideoenhance.so.0
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition_T7_SO0(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ve_set_rgb_measure_position")]
        private static extern int MeasurePosition_T7_SO0(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T7_SO0(int index, out Color color);

        // ===== Tizen 9+ API variant A (tizen_ve_* prefix) - Test all library paths =====

        // Library: /usr/lib/libvideoenhance.so
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "tizen_ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition_T9A_SO(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "tizen_ve_set_rgb_measure_position")]
        private static extern int MeasurePosition_T9A_SO(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "tizen_ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T9A_SO(int index, out Color color);

        // Library: /usr/lib/libvideoenhance.so.0
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "tizen_ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition_T9A_SO0(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "tizen_ve_set_rgb_measure_position")]
        private static extern int MeasurePosition_T9A_SO0(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "tizen_ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T9A_SO0(int index, out Color color);

        // ===== Tizen 9+ API variant B (samsung_ve_* prefix) - Test all library paths =====

        // Library: /usr/lib/libvideoenhance.so
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "samsung_ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition_T9B_SO(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "samsung_ve_set_rgb_measure_position")]
        private static extern int MeasurePosition_T9B_SO(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "samsung_ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T9B_SO(int index, out Color color);

        // Library: /usr/lib/libvideoenhance.so.0
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "samsung_ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition_T9B_SO0(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "samsung_ve_set_rgb_measure_position")]
        private static extern int MeasurePosition_T9B_SO0(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "samsung_ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T9B_SO0(int index, out Color color);

        // ===== Tizen 9+ API variant C (no prefix) - Test all library paths =====

        // Library: /usr/lib/libvideoenhance.so
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "get_rgb_measure_condition")]
        private static extern int MeasureCondition_T9C_SO(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "set_rgb_measure_position")]
        private static extern int MeasurePosition_T9C_SO(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T9C_SO(int index, out Color color);

        // Library: /usr/lib/libvideoenhance.so.0
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "get_rgb_measure_condition")]
        private static extern int MeasureCondition_T9C_SO0(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "set_rgb_measure_position")]
        private static extern int MeasurePosition_T9C_SO0(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T9C_SO0(int index, out Color color);

        // ===== Tizen 9 API (ppi_ve_* prefix) - CONFIRMED via analysis =====

        // Library: /usr/lib/libvideoenhance.so
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ppi_ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition_T9_SO(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ppi_ve_set_rgb_measure_position")]
        private static extern int MeasurePosition_T9_SO(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ppi_ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T9_SO(int index, out Color color);

        // Library: /usr/lib/libvideoenhance.so.0
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ppi_ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition_T9_SO0(out Condition condition);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ppi_ve_set_rgb_measure_position")]
        private static extern int MeasurePosition_T9_SO0(int index, int x, int y);
        [DllImport("/usr/lib/libvideoenhance.so.0", CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "ppi_ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel_T9_SO0(int index, out Color color);

        #endregion

        #region Native Structs

        /// <summary>
        /// Color struct for 10-bit RGB values (0-1023)
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct Color
        {
            public int R;
            public int G;
            public int B;
        }

        /// <summary>
        /// Condition struct containing screen parameters and sampling configuration
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct Condition
        {
            public int ScreenCapturePoints;  // Max number of points that can be sampled simultaneously
            public int PixelDensityX;         // Pixel density in X direction
            public int PixelDensityY;         // Pixel density in Y direction
            public int SleepMS;               // Milliseconds to sleep between position set and pixel read
            public int Width;                 // Screen width in pixels
            public int Height;                // Screen height in pixels
        }

        /// <summary>
        /// Capture point with normalized coordinates (0.0-1.0)
        /// </summary>
        public struct CapturePoint
        {
            public CapturePoint(double x, double y)
            {
                this.X = x;
                this.Y = y;
            }

            public double X;
            public double Y;
        }

        #endregion

        /// <summary>
        /// Check if pixel sampling library is available
        /// </summary>
        public bool IsAvailable()
        {
            Helper.Log.Write(Helper.eLogType.Debug, "PixelSampling: Checking availability...");

            // Check if library file exists
            if (!System.IO.File.Exists("/usr/lib/libvideoenhance.so"))
            {
                Helper.Log.Write(Helper.eLogType.Debug, "PixelSampling: libvideoenhance.so not found");
                return false;
            }

            Helper.Log.Write(Helper.eLogType.Debug, "PixelSampling: Library found, available");
            return true;
        }

        /// <summary>
        /// Test pixel sampling by attempting to get screen condition
        /// </summary>
        public bool Test()
        {
            if (!IsAvailable())
                return false;

            try
            {
                Helper.Log.Write(Helper.eLogType.Info, "PixelSampling: Testing capture...");

                bool success = GetCondition();

                if (success)
                {
                    // Pre-calculate coordinates during test
                    PreCalculateCoordinates();

                    Helper.Log.Write(Helper.eLogType.Info,
                        $"PixelSampling Test: SUCCESS - Screen: {_condition.Width}x{_condition.Height}, " +
                        $"Points: {_condition.ScreenCapturePoints}, Sleep: {_condition.SleepMS}ms");
                    _isInitialized = true;
                    return true;
                }
                else
                {
                    Helper.Log.Write(Helper.eLogType.Warning, "PixelSampling Test: GetCondition failed");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Error, $"PixelSampling Test exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get screen condition parameters from VideoEnhance library
        /// Tests ALL combinations of API variants and library paths systematically
        /// </summary>
        private bool GetCondition()
        {
            Helper.Log.Write(Helper.eLogType.Info,
                "PixelSampling: Testing ALL combinations of entry points and library paths...");

            // Test Tizen 6 (cs_ve_*) with existing library paths
            if (TryVariant(
                () => MeasureCondition_T6_SO(out _condition),
                (idx, x, y) => MeasurePosition_T6_SO(idx, x, y),
                MeasurePixel_T6_SO,
                "T6", "SO", "cs_ve_*", ".so")) return true;
            if (TryVariant(
                () => MeasureCondition_T6_SO0(out _condition),
                (idx, x, y) => MeasurePosition_T6_SO0(idx, x, y),
                MeasurePixel_T6_SO0,
                "T6", "SO0", "cs_ve_*", ".so.0")) return true;

            // Test Tizen 7 (ve_*) with existing library paths
            if (TryVariant(
                () => MeasureCondition_T7_SO(out _condition),
                (idx, x, y) => MeasurePosition_T7_SO(idx, x, y),
                MeasurePixel_T7_SO,
                "T7", "SO", "ve_*", ".so")) return true;
            if (TryVariant(
                () => MeasureCondition_T7_SO0(out _condition),
                (idx, x, y) => MeasurePosition_T7_SO0(idx, x, y),
                MeasurePixel_T7_SO0,
                "T7", "SO0", "ve_*", ".so.0")) return true;

            // Test Tizen 9+ variant A (tizen_ve_*) with existing library paths
            if (TryVariant(
                () => MeasureCondition_T9A_SO(out _condition),
                (idx, x, y) => MeasurePosition_T9A_SO(idx, x, y),
                MeasurePixel_T9A_SO,
                "T9A", "SO", "tizen_ve_*", ".so")) return true;
            if (TryVariant(
                () => MeasureCondition_T9A_SO0(out _condition),
                (idx, x, y) => MeasurePosition_T9A_SO0(idx, x, y),
                MeasurePixel_T9A_SO0,
                "T9A", "SO0", "tizen_ve_*", ".so.0")) return true;

            // Test Tizen 9+ variant B (samsung_ve_*) with existing library paths
            if (TryVariant(
                () => MeasureCondition_T9B_SO(out _condition),
                (idx, x, y) => MeasurePosition_T9B_SO(idx, x, y),
                MeasurePixel_T9B_SO,
                "T9B", "SO", "samsung_ve_*", ".so")) return true;
            if (TryVariant(
                () => MeasureCondition_T9B_SO0(out _condition),
                (idx, x, y) => MeasurePosition_T9B_SO0(idx, x, y),
                MeasurePixel_T9B_SO0,
                "T9B", "SO0", "samsung_ve_*", ".so.0")) return true;

            // Test Tizen 9+ variant C (no prefix) with existing library paths
            if (TryVariant(
                () => MeasureCondition_T9C_SO(out _condition),
                (idx, x, y) => MeasurePosition_T9C_SO(idx, x, y),
                MeasurePixel_T9C_SO,
                "T9C", "SO", "no prefix", ".so")) return true;
            if (TryVariant(
                () => MeasureCondition_T9C_SO0(out _condition),
                (idx, x, y) => MeasurePosition_T9C_SO0(idx, x, y),
                MeasurePixel_T9C_SO0,
                "T9C", "SO0", "no prefix", ".so.0")) return true;

            // Test Tizen 9 actual API (ppi_ve_*) - CONFIRMED via library analysis
            if (TryVariant(
                () => MeasureCondition_T9_SO(out _condition),
                (idx, x, y) => MeasurePosition_T9_SO(idx, x, y),
                MeasurePixel_T9_SO,
                "T9", "SO", "ppi_ve_*", ".so")) return true;
            if (TryVariant(
                () => MeasureCondition_T9_SO0(out _condition),
                (idx, x, y) => MeasurePosition_T9_SO0(idx, x, y),
                MeasurePixel_T9_SO0,
                "T9", "SO0", "ppi_ve_*", ".so.0")) return true;

            // All combinations failed
            Helper.Log.Write(Helper.eLogType.Error,
                "PixelSampling: ALL 12 combinations failed (6 entry point variants × 2 library paths)");
            Helper.Log.Write(Helper.eLogType.Error,
                "PixelSampling: libvideoenhance.so does not support RGB pixel sampling on this Tizen version");
            return false;
        }

        /// <summary>
        /// Try a specific API variant + library path combination
        /// Tests ALL 3 entry points to ensure complete API surface exists
        /// </summary>
        private delegate int MeasurePixelDelegate(int index, out Color color);

        private bool TryVariant(
            Func<int> conditionFunc,
            Func<int, int, int, int> positionFunc,
            MeasurePixelDelegate pixelFunc,
            string variant,
            string libPath,
            string entryPrefix,
            string libSuffix)
        {
            try
            {
                Helper.Log.Write(Helper.eLogType.Debug,
                    $"PixelSampling: Testing {variant} ({entryPrefix}) with libvideoenhance{libSuffix}");

                // Test 1: MeasureCondition
                int conditionResult = conditionFunc();
                if (conditionResult < 0)
                {
                    Helper.Log.Write(Helper.eLogType.Debug,
                        $"PixelSampling: {variant}/{libPath} condition returned error {conditionResult}");
                    return false;
                }

                // Test 2: MeasurePosition (validate entry point exists with dummy coordinates)
                int positionResult = positionFunc(0, 0, 0);
                // Position may fail if called before proper setup, but entry point should exist
                Helper.Log.Write(Helper.eLogType.Debug,
                    $"PixelSampling: {variant}/{libPath} position entry point exists (result: {positionResult})");

                // Test 3: MeasurePixel (validate entry point exists)
                Color dummyColor;
                int pixelResult = pixelFunc(0, out dummyColor);
                // Pixel may fail if no position set yet, but entry point should exist
                Helper.Log.Write(Helper.eLogType.Debug,
                    $"PixelSampling: {variant}/{libPath} pixel entry point exists (result: {pixelResult})");

                // Success - all three entry points exist and condition succeeded
                _workingVariant = variant;
                _workingLibPath = libPath;
                Helper.Log.Write(Helper.eLogType.Info,
                    $"PixelSampling: ✓ SUCCESS - All 3 entry points validated for {variant} ({entryPrefix}) with libvideoenhance{libSuffix}");
                Helper.Log.Write(Helper.eLogType.Debug, $"PixelSampling: Condition result: {conditionResult}");
                LogConditionDetails();
                return true;
            }
            catch (EntryPointNotFoundException ex)
            {
                Helper.Log.Write(Helper.eLogType.Debug,
                    $"PixelSampling: {variant}/{libPath} entry point not found: {ex.Message}");
                return false;
            }
            catch (DllNotFoundException ex)
            {
                Helper.Log.Write(Helper.eLogType.Debug,
                    $"PixelSampling: {variant}/{libPath} library file not found: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Debug,
                    $"PixelSampling: {variant}/{libPath} exception: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Helper method to log condition details
        /// </summary>
        private void LogConditionDetails()
        {
            Helper.Log.Write(Helper.eLogType.Info,
                $"PixelSampling: Condition - Width: {_condition.Width}, Height: {_condition.Height}, " +
                $"Points: {_condition.ScreenCapturePoints}, PixelDensity: {_condition.PixelDensityX}x{_condition.PixelDensityY}, " +
                $"Sleep: {_condition.SleepMS}ms");
        }

        /// <summary>
        /// Call correct MeasurePosition variant based on working combination
        /// </summary>
        private int CallMeasurePosition(int index, int x, int y)
        {
            string key = $"{_workingVariant}_{_workingLibPath}";
            switch (key)
            {
                case "T6_SO": return MeasurePosition_T6_SO(index, x, y);
                case "T6_SO0": return MeasurePosition_T6_SO0(index, x, y);
                case "T7_SO": return MeasurePosition_T7_SO(index, x, y);
                case "T7_SO0": return MeasurePosition_T7_SO0(index, x, y);
                case "T9A_SO": return MeasurePosition_T9A_SO(index, x, y);
                case "T9A_SO0": return MeasurePosition_T9A_SO0(index, x, y);
                case "T9B_SO": return MeasurePosition_T9B_SO(index, x, y);
                case "T9B_SO0": return MeasurePosition_T9B_SO0(index, x, y);
                case "T9C_SO": return MeasurePosition_T9C_SO(index, x, y);
                case "T9C_SO0": return MeasurePosition_T9C_SO0(index, x, y);
                case "T9_SO": return MeasurePosition_T9_SO(index, x, y);
                case "T9_SO0": return MeasurePosition_T9_SO0(index, x, y);
                default:
                    throw new InvalidOperationException($"Unknown variant: {key}");
            }
        }

        /// <summary>
        /// Call correct MeasurePixel variant based on working combination
        /// </summary>
        private int CallMeasurePixel(int index, out Color color)
        {
            string key = $"{_workingVariant}_{_workingLibPath}";
            switch (key)
            {
                case "T6_SO": return MeasurePixel_T6_SO(index, out color);
                case "T6_SO0": return MeasurePixel_T6_SO0(index, out color);
                case "T7_SO": return MeasurePixel_T7_SO(index, out color);
                case "T7_SO0": return MeasurePixel_T7_SO0(index, out color);
                case "T9A_SO": return MeasurePixel_T9A_SO(index, out color);
                case "T9A_SO0": return MeasurePixel_T9A_SO0(index, out color);
                case "T9B_SO": return MeasurePixel_T9B_SO(index, out color);
                case "T9B_SO0": return MeasurePixel_T9B_SO0(index, out color);
                case "T9C_SO": return MeasurePixel_T9C_SO(index, out color);
                case "T9C_SO0": return MeasurePixel_T9C_SO0(index, out color);
                case "T9_SO": return MeasurePixel_T9_SO(index, out color);
                case "T9_SO0": return MeasurePixel_T9_SO0(index, out color);
                default:
                    throw new InvalidOperationException($"Unknown variant: {key}");
            }
        }

        /// <summary>
        /// Pre-calculate pixel coordinates from normalized positions
        /// Called once during initialization to avoid repeated calculations
        /// </summary>
        private void PreCalculateCoordinates()
        {
            _pixelCoordinates = new PixelCoordinate[_capturedPoints.Length];

            for (int i = 0; i < _capturedPoints.Length; i++)
            {
                // Convert normalized coordinates to pixel coordinates
                int x = (int)(_capturedPoints[i].X * (double)_condition.Width) - _condition.PixelDensityX / 2;
                int y = (int)(_capturedPoints[i].Y * (double)_condition.Height) - _condition.PixelDensityY / 2;

                // Clamp coordinates to valid screen bounds
                x = (x >= _condition.Width - _condition.PixelDensityX) ?
                    _condition.Width - (_condition.PixelDensityX + 1) : x;
                y = (y >= _condition.Height - _condition.PixelDensityY) ?
                    (_condition.Height - _condition.PixelDensityY + 1) : y;

                // Ensure coordinates are not negative
                x = Math.Max(0, x);
                y = Math.Max(0, y);

                _pixelCoordinates[i].X = x;
                _pixelCoordinates[i].Y = y;
            }

            Helper.Log.Write(Helper.eLogType.Info,
                $"PixelSampling: Pre-calculated {_pixelCoordinates.Length} pixel coordinates");
        }

        /// <summary>
        /// Sample pixel colors from predefined screen positions
        /// OPTIMIZED: Sets ALL positions first, then ONE sleep, then reads ALL pixels
        /// This ensures all sampling happens at the same moment for temporal consistency
        /// </summary>
        private int _frameCount = 0;
        private Color[] GetColors()
        {
            Color[] colorData = new Color[_capturedPoints.Length];

            if (_condition.ScreenCapturePoints == 0)
            {
                Helper.Log.Write(Helper.eLogType.Error, "PixelSampling: ScreenCapturePoints is 0");
                return colorData;
            }

            // PHASE 1: Set ALL measurement positions first (no delays between batches)
            int i = 0;
            while (i < _capturedPoints.Length)
            {
                // Set positions for this batch
                for (int j = 0; j < _condition.ScreenCapturePoints && i < _capturedPoints.Length; j++)
                {
                    // Use pre-calculated pixel coordinates
                    int x = _pixelCoordinates[i].X;
                    int y = _pixelCoordinates[i].Y;

                    // Set the measurement position
                    int res = CallMeasurePosition(j, x, y);

                    if (res < 0)
                    {
                        Helper.Log.Write(Helper.eLogType.Error,
                            $"PixelSampling: MeasurePosition failed for point {i} at ({x}, {y}) with error {res}");
                    }

                    i++;
                }
            }

            // PHASE 2: Single sleep after ALL positions are set
            // This ensures all measurements happen at approximately the same time
            if (_condition.SleepMS > 0)
            {
                Thread.Sleep(_condition.SleepMS);
            }

            // PHASE 3: Read ALL pixel colors in batches
            i = 0;
            while (i < _capturedPoints.Length)
            {
                // Read pixels for this batch
                for (int j = 0; j < _condition.ScreenCapturePoints && i < _capturedPoints.Length; j++)
                {
                    Color color;
                    int res = CallMeasurePixel(j, out color);

                    if (res < 0)
                    {
                        Helper.Log.Write(Helper.eLogType.Error,
                            $"PixelSampling: MeasurePixel failed for point {i} with error {res}");
                        // Use black as fallback
                        color.R = 0;
                        color.G = 0;
                        color.B = 0;
                    }
                    else
                    {
                        // Validate color data (10-bit values should be 0-1023)
                        bool invalidColorData = color.R > 1023 || color.G > 1023 || color.B > 1023 ||
                                                color.R < 0 || color.G < 0 || color.B < 0;

                        if (invalidColorData)
                        {
                            Helper.Log.Write(Helper.eLogType.Warning,
                                $"PixelSampling: Invalid color data at point {i}: R={color.R}, G={color.G}, B={color.B}");
                            // Clamp to valid range
                            color.R = Math.Max(0, Math.Min(1023, color.R));
                            color.G = Math.Max(0, Math.Min(1023, color.G));
                            color.B = Math.Max(0, Math.Min(1023, color.B));
                        }
                    }

                    colorData[i] = color;
                    i++;
                }
            }

            // ponytail: raw dump every 100 frames to check the value range on this firmware; remove once settled
            if (_frameCount++ % 100 == 0)
            {
                var parts = new string[colorData.Length];
                for (int k = 0; k < colorData.Length; k++)
                    parts[k] = $"{colorData[k].R},{colorData[k].G},{colorData[k].B}";
                Helper.Log.Write(Helper.eLogType.Info, "PixelSampling raw: " + string.Join(" ", parts));
            }

            return colorData;
        }

        /// <summary>
        /// Capture screen using pixel sampling
        /// </summary>
        public CaptureResult Capture(int width, int height)
        {
            try
            {
                // Initialize if not already done
                if (!_isInitialized)
                {
                    if (!GetCondition())
                    {
                        return CaptureResult.CreateFailure("PixelSampling: Failed to get condition");
                    }

                    // Pre-calculate all pixel coordinates once
                    PreCalculateCoordinates();

                    _isInitialized = true;
                }

                // Sample pixels from screen
                Color[] colors = GetColors();

                if (colors == null || colors.Length == 0)
                {
                    return CaptureResult.CreateFailure("PixelSampling: No colors captured");
                }

                // Convert to NV12 format
                var samples = new Rgb10[colors.Length];
                for (int i = 0; i < colors.Length; i++)
                    samples[i] = new Rgb10(colors[i].R, colors[i].G, colors[i].B);
                var (yData, uvData) = SampleImage.ToNv12(samples);

                // Return success with 64x48 sampled image
                return CaptureResult.CreateSuccess(yData, uvData, 64, 48);
            }
            catch (Exception ex)
            {
                return CaptureResult.CreateFailure($"PixelSampling exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Clean up resources
        /// </summary>
        public void Cleanup()
        {
            _isInitialized = false;
            Helper.Log.Write(Helper.eLogType.Debug, "PixelSampling: Cleaned up");
        }
    }
}
