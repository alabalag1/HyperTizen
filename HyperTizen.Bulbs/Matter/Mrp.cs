using System;

namespace HyperTizen.Bulbs.Matter
{
    /// <summary>Message Reliability Protocol constants and backoff (Core spec §4.12.2, §4.12.8).</summary>
    internal static class Mrp
    {
        public const int MaxTransmissions = 5;
        public const double BackoffBase = 1.6, BackoffJitter = 0.25, BackoffMargin = 1.1;
        public const int BackoffThreshold = 1;

        /// <summary>SESSION_ACTIVE_INTERVAL. ponytail: the active interval for every message, Sigma1 included; mains-powered
        /// bulbs are never sleepy, so the idle interval (500 ms) and the peer's own Sigma2 parameters are not used.</summary>
        public static readonly TimeSpan ActiveInterval = TimeSpan.FromMilliseconds(300);

        [ThreadStatic] private static Random _rng;

        /// <param name="sendCount">transmissions already made of this message, 0 for the first</param>
        public static TimeSpan Backoff(int sendCount, TimeSpan baseInterval)
        {
            var rng = _rng ?? (_rng = new Random());
            double f = BackoffMargin * Math.Pow(BackoffBase, Math.Max(0, sendCount - BackoffThreshold)) * (1 + rng.NextDouble() * BackoffJitter);
            return TimeSpan.FromTicks((long)(baseInterval.Ticks * f));
        }
    }
}
