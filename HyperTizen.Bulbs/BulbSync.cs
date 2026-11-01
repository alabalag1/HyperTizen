using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HyperTizen.Bulbs
{
    /// <summary>
    /// Today's chain on the TV: SetFrame -> HybridSmoother (50 Hz) -> 8-bit (what HyperHDR sent over udpraw)
    /// -> per-bulb send loop ported from bulb-sync/bridge.mjs (EMA, mapping, change thresholds, at most 10 Hz).
    /// Every await uses ConfigureAwait(false): the tests rely on continuations running inline.
    /// </summary>
    public sealed class BulbSync
    {
        const double TickMs = 20, CycleMinMs = 100;     // HyperHDR updateFrequency 50 Hz; bridge MAX_HZ 10
        const int WarmLevel = 127, WarmMireds = 370, WarmTenths = 10;
        // Nothing confirmed for this long on a static scene: re-send the level, so a dead session (bulb reboot,
        // eviction) is found and re-established while running, not at power-off when warm white needs it.
        const double KeepaliveMs = 30000;

        readonly IBulbTransport _transport;
        readonly IReadOnlyList<BulbEntry> _bulbs;
        readonly Func<Tuning> _tuning;
        readonly Action<string> _log;
        readonly Func<double> _now;
        readonly Func<double, CancellationToken, Task> _delay;

        readonly object _frameLock = new object();
        HybridSmoother _smoother = new HybridSmoother();
        bool _hasFrame, _hasOutput;
        Rgb _lower, _upper;                              // latest 8-bit output, guarded by _frameLock

        readonly object _gate = new object();
        volatile bool _running;
        CancellationTokenSource _sendCts;
        Task _loops, _stopTask;
        readonly HashSet<ulong> _driven = new HashSet<ulong>();   // nodes sent a command this run; lock on itself

        /// <param name="nowMs">Clock in ms; defaults to a Stopwatch.</param>
        /// <param name="delay">Delay in ms; defaults to Task.Delay.</param>
        public BulbSync(IBulbTransport transport, IReadOnlyList<BulbEntry> bulbs, Func<Tuning> tuning, Action<string> log,
            Func<double> nowMs = null, Func<double, CancellationToken, Task> delay = null)
        {
            _transport = transport; _bulbs = bulbs; _tuning = tuning; _log = log;
            if (nowMs == null) { var sw = Stopwatch.StartNew(); nowMs = () => sw.Elapsed.TotalMilliseconds; }
            _now = nowMs;
            _delay = delay ?? ((ms, ct) => Task.Delay(TimeSpan.FromMilliseconds(ms), ct));
        }

        public bool IsRunning => _running;

        /// <summary>Called per capture frame with ColourChain's unrounded output.</summary>
        public void SetFrame(Rgb lower, Rgb upper)
        {
            lock (_frameLock) { _smoother.SetTarget(lower, upper, _now()); _hasFrame = true; }
        }

        /// <summary>Runs until <paramref name="ct"/> is cancelled or StopToWarmWhiteAsync is called; completes normally either way.
        /// Cancelling <paramref name="ct"/> just stops; call StopToWarmWhiteAsync for the warm white.</summary>
        public Task RunAsync(CancellationToken ct)
        {
            CancellationTokenSource cts;
            lock (_gate)
            {
                if (_running) throw new InvalidOperationException("BulbSync is already running");
                // _running drops when the loops exit, before warm white is sent: await the stop first
                if (_stopTask != null && !_stopTask.IsCompleted) throw new InvalidOperationException("BulbSync stop in progress");
                _running = true;
                _stopTask = null;
                lock (_driven) _driven.Clear();
                // ponytail: the linked CTS is never disposed (Stop may still cancel it); one small leak per run.
                _sendCts = cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _loops = Task.WhenAll(new[] { TickLoop(cts.Token) }.Concat(_bulbs.Select(b => BulbLoop(b, cts.Token))));
            }
            return Finish(cts);
        }

        async Task Finish(CancellationTokenSource cts)
        {
            try { await _loops.ConfigureAwait(false); }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
            finally
            {
                // Forget this run's frame, so the next run can't replay a scene from before the stop.
                // Reset here rather than in RunAsync: a SetFrame made before RunAsync is fresh and must count.
                lock (_frameLock) { _smoother = new HybridSmoother(); _hasFrame = _hasOutput = false; }
                _running = false;
            }
        }

        /// <summary>No-op unless running. Cancels in-flight sends, then fades to warm white every bulb that was
        /// sent a command this run (sync never switches on a bulb it didn't drive).
        /// A second call while one is in progress gets the same task.</summary>
        public Task StopToWarmWhiteAsync(TimeSpan deadline)
        {
            lock (_gate)
            {
                if (_stopTask != null) return _stopTask;
                if (!_running) return Task.CompletedTask;
                return _stopTask = StopCore(deadline);
            }
        }

        async Task StopCore(TimeSpan deadline)
        {
            var dl = new CancellationTokenSource();
            var expired = _delay(deadline.TotalMilliseconds, dl.Token);
            try
            {
                _sendCts.Cancel();
                // wait for the loops, so a colour from a loop mid-iteration can't land after the warm white
                if (await Task.WhenAny(_loops, expired).ConfigureAwait(false) == expired)
                    _log("bulbs: send loops did not stop before the deadline");
                ulong[] driven;
                lock (_driven) driven = _driven.ToArray();
                var all = Task.WhenAll(driven.Select(n => WarmWhite(n, dl.Token)));
                if (await Task.WhenAny(all, expired).ConfigureAwait(false) == expired)
                    _log("bulbs: warm white did not finish before the deadline");
            }
            finally
            {
                dl.Cancel();    // stops the deadline timer and any warm white still in flight
                dl.Dispose();
                _running = false;
            }
        }

        async Task WarmWhite(ulong node, CancellationToken ct)
        {
            try
            {
                // One retry each while the deadline allows (ct is cancelled once it passes): a silently evicted
                // session fails the first invoke and is dropped, so the retry re-runs CASE.
                // failFast: a dead session gives up after ~0.7 s, not ~4 s, so the retry's CASE fits the deadline
                bool level = await Retry(() => _transport.MoveToLevelWithOnOff(node, WarmLevel, WarmTenths, ct, failFast: true), ct).ConfigureAwait(false);
                bool temp = await Retry(() => _transport.MoveToColorTemperature(node, WarmMireds, WarmTenths, ct, failFast: true), ct).ConfigureAwait(false);
                if (!level || !temp) _log($"node {node}: warm white not confirmed");
            }
            catch (Exception e) { _log($"node {node}: warm white failed: {e.Message}"); }
        }

        static async Task<bool> Retry(Func<Task<bool>> call, CancellationToken ct) =>
            await call().ConfigureAwait(false) || (!ct.IsCancellationRequested && await call().ConfigureAwait(false));

        async Task TickLoop(CancellationToken ct)
        {
            for (;;)
            {
                lock (_frameLock)
                    if (_hasFrame)
                    {
                        var (l, u) = _smoother.Tick(_now());
                        _lower = ColourChain.ToDevice8Bit(l);
                        _upper = ColourChain.ToDevice8Bit(u);
                        _hasOutput = true;
                    }
                await _delay(TickMs, ct).ConfigureAwait(false);
            }
        }

        Rgb? Latest(string zone)
        {
            lock (_frameLock)
            {
                if (!_hasOutput) return null;
                return zone == "upper" ? _upper : zone == "lower" ? _lower
                    : new Rgb((_lower.R + _upper.R) / 2, (_lower.G + _upper.G) / 2, (_lower.B + _upper.B) / 2);
            }
        }

        /// <summary>bridge.mjs send loop, one bulb.</summary>
        async Task BulbLoop(BulbEntry bulb, CancellationToken ct)
        {
            ulong node = bulb.NodeId;
            Rgb? smooth = null;
            MatterColour? colour = null;
            int? level = null;
            int failures = 0;
            double cycleMs = 300, last = _now(), confirmed = last;
            for (;;)
            {
                ct.ThrowIfCancellationRequested();
                double t0 = _now(), dt = t0 - last;
                last = t0;
                var tuning = _tuning();
                int fade = Clamp((int)Math.Ceiling(cycleMs / 100), 2, 5);
                int sent = 0;
                var raw = Latest(bulb.Zone);
                if (raw != null)
                {
                    smooth = smooth == null ? raw : Ema.Step(smooth.Value, raw.Value, dt, tuning.SmoothMs);
                    var next = BulbMapper.ToMatter(smooth.Value, tuning);
                    bool ok = true;
                    if (BulbMapper.ColourChanged(colour, next))
                    {
                        if (await Send(node, () => _transport.MoveToHueAndSaturation(node, Clamp(next.Hue.Value, 0, 254),
                                Clamp(next.Sat.Value, 0, 254), Clamp(fade, 0, 65534), ct), ct).ConfigureAwait(false))
                        { colour = next; confirmed = _now(); }
                        else ok = false;
                        sent++;
                    }
                    // Deviation from the bridge: its >3 threshold can strand a fade-out at MinLevel + 1..3,
                    // so the floor itself is always sent.
                    bool levelDue = BulbMapper.LevelChanged(level, next.Level) || (next.Level == tuning.MinLevel && level != next.Level);
                    bool keepalive = !levelDue && sent == 0 && level != null && t0 - confirmed >= KeepaliveMs;
                    if (levelDue || keepalive)
                    {
                        int lv = levelDue ? next.Level : level.Value;    // a keepalive re-sends the current level
                        if (await Send(node, () => _transport.MoveToLevelWithOnOff(node, Clamp(lv, 1, 254),
                                Clamp(fade, 0, 65534), ct), ct).ConfigureAwait(false))
                        { level = lv; confirmed = _now(); }
                        else ok = false;
                        sent++;
                    }
                    ct.ThrowIfCancellationRequested();   // a stop is not a failure
                    if (!ok) { if (++failures % 20 == 1) _log($"node {node}: command failed ({failures})"); }
                    else failures = 0;
                }
                double wait = CycleMinMs - (_now() - t0);
                if (wait > 0) await _delay(wait, ct).ConfigureAwait(false);
                if (sent > 0) cycleMs += (_now() - t0 - cycleMs) * 0.3;
            }
        }

        /// <summary>A throwing transport counts as a failed command; cancellation propagates.</summary>
        async Task<bool> Send(ulong node, Func<Task<bool>> call, CancellationToken ct)
        {
            lock (_driven) _driven.Add(node);   // an attempt counts: an in-flight command may still land
            try { return await call().ConfigureAwait(false); }
            catch (Exception) when (!ct.IsCancellationRequested) { return false; }
        }

        static int Clamp(int v, int lo, int hi) => Math.Max(lo, Math.Min(hi, v));
    }
}
