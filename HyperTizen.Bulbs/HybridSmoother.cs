using System;

namespace HyperTizen.Bulbs
{
    /// <summary>
    /// HyperHDR 22 smoothing for two LEDs: InfiniteHybridRgbInterpolator (a damped spring per channel)
    /// followed by LedDevice::write's anti-flickering hold. Input and output are unrounded 0-255;
    /// round with ColourChain.ToDevice8Bit afterwards. No clock inside: tick it at updateFrequency (50 Hz).
    /// </summary>
    public sealed class HybridSmoother
    {
        // FINISH_COMPONENT_THRESHOLD / VELOCITY_THRESHOLD, InfiniteHybridRgbInterpolator.cpp:138-139
        const double FinishThreshold = 0.0013732906 / 10, VelocityThreshold = 0.0005;
        // ponytail: the source's lower clamp is minimalBacklight (0.0039 here); ColourChain already
        // lifts inputs to it, so 0 is used. Add a floor parameter if the spring ever undershoots it.
        const double Floor = 0;

        readonly double _stiffness, _damping;
        readonly bool _antiFlicker;
        readonly double[] _cur = new double[6], _tgt = new double[6], _vel = new double[6];
        double[] _held;            // LedDevice::_lastFinityLedValues
        bool _hasColours, _complete = true;
        double _lastUpdate;

        /// <param name="timeMs">Accepted for config parity, unused: HybridRgb stores it in _targetTime, which nothing reads.</param>
        /// <param name="yLimit">Accepted for config parity, unused: HybridRgb doesn't override setMaxLuminanceChangePerFrame.</param>
        public HybridSmoother(int timeMs = 150, bool antiFlicker = true, double yLimit = 0.03, int stiffness = 150, int damping = 26)
        {
            _antiFlicker = antiFlicker;
            _stiffness = Math.Max(0.1, stiffness);   // setSpringiness
            _damping = Math.Max(0.1, damping);
        }

        /// <summary>setTargetColors with smoothingFactor 0 (schema default): the first call snaps.</summary>
        public void SetTarget(Rgb lower, Rgb upper, double nowMs)
        {
            Store(_tgt, lower, upper);
            if (!_hasColours)
            {
                _hasColours = true;
                _lastUpdate = nowMs;
                Array.Copy(_tgt, _cur, 6);
                Array.Clear(_vel, 0, 6);
                _complete = true;
            }
            else _complete = false;
        }

        public (Rgb Lower, Rgb Upper) Tick(double nowMs)
        {
            if (!_hasColours) return (default, default);
            Update(nowMs);

            var output = new double[6];
            for (int i = 0; i < 6; i++) output[i] = Math.Max(Floor, Math.Min(_cur[i], 1)); // getCurrentColors

            if (_antiFlicker)
            {
                if (_held == null) _held = output;
                else
                    for (int led = 0; led < 6; led += 3)
                    {
                        double d = 0;
                        for (int c = led; c < led + 3; c++) d = Math.Max(d, Math.Abs(output[c] - _held[c]) * 255);
                        if (d > 0.49) Array.Copy(output, led, _held, led, 3);
                    }
                output = (double[])_held.Clone();
            }
            return (new Rgb(output[0] * 255, output[1] * 255, output[2] * 255),
                    new Rgb(output[3] * 255, output[4] * 255, output[5] * 255));
        }

        /// <summary>updateCurrentColors, InfiniteHybridRgbInterpolator.cpp:127-177.</summary>
        void Update(double nowMs)
        {
            if (_complete) { _lastUpdate = nowMs; return; }

            double dt = Math.Max(0.001, Math.Min(nowMs - _lastUpdate, 100)) * 0.001;
            _lastUpdate = nowMs;
            _complete = true;
            for (int led = 0; led < 6; led += 3)
            {
                double maxDiff = 0, maxVel = 0;
                for (int c = led; c < led + 3; c++)
                {
                    maxDiff = Math.Max(maxDiff, Math.Abs(_tgt[c] - _cur[c]));
                    maxVel = Math.Max(maxVel, Math.Abs(_vel[c]));
                }
                if (maxDiff < FinishThreshold && maxVel < VelocityThreshold)
                {
                    Array.Copy(_tgt, led, _cur, led, 3);
                    Array.Clear(_vel, led, 3);
                    continue;
                }
                _complete = false;
                for (int c = led; c < led + 3; c++)
                {
                    double acc = _stiffness * (_tgt[c] - _cur[c]) - _damping * _vel[c];
                    _vel[c] += acc * dt;           // semi-implicit Euler: velocity first
                    _cur[c] += _vel[c] * dt;
                    if (_cur[c] < Floor || _cur[c] > 1)
                    {
                        _vel[c] = 0;
                        _cur[c] = Math.Max(Floor, Math.Min(_cur[c], 1));
                    }
                }
            }
        }

        static void Store(double[] a, Rgb lower, Rgb upper)
        {
            a[0] = lower.R / 255; a[1] = lower.G / 255; a[2] = lower.B / 255;
            a[3] = upper.R / 255; a[4] = upper.G / 255; a[5] = upper.B / 255;
        }
    }
}
