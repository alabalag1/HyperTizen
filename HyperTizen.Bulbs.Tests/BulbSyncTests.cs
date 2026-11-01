using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HyperTizen.Bulbs;
using Xunit;

namespace HyperTizen.Bulbs.Tests
{
    /// <summary>Virtual time. Waiters complete inline inside Advance (plain TCS, and BulbSync uses
    /// ConfigureAwait(false)), so every test runs single-threaded and deterministically.
    /// Anything that completes tasks must run under DetachContext: with xUnit's SynchronizationContext
    /// installed, the runtime refuses to inline continuations and queues them to the thread pool.</summary>
    sealed class FakeClock
    {
        public double Now;
        readonly List<(double Due, TaskCompletionSource<bool> Tcs)> _waiters = new();

        public Task Delay(double ms, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
            var tcs = new TaskCompletionSource<bool>();
            _waiters.Add((Now + ms, tcs));
            ct.Register(() => tcs.TrySetCanceled(ct));
            return tcs.Task;
        }

        public static void DetachContext() => SynchronizationContext.SetSynchronizationContext(null);

        public void Advance(double ms)
        {
            DetachContext();
            double end = Now + ms;
            for (;;)
            {
                _waiters.RemoveAll(w => w.Tcs.Task.IsCompleted);
                int best = -1;
                for (int i = 0; i < _waiters.Count; i++)   // earliest due; ties in registration order
                    if (_waiters[i].Due <= end && (best < 0 || _waiters[i].Due < _waiters[best].Due)) best = i;
                if (best < 0) break;
                var w = _waiters[best];
                _waiters.RemoveAt(best);
                Now = Math.Max(Now, w.Due);
                w.Tcs.TrySetResult(true);
            }
            Now = end;
        }
    }

    sealed record Call(string Method, ulong Node, int Value, int Sat, int Tenths, double At);

    sealed class FakeTransport : IBulbTransport
    {
        public readonly List<Call> Calls = new();
        public Func<string, double> DelayMs = _ => 0;    // by method: "hue", "level", "ct"
        public Func<ulong, bool> Fails = _ => false;
        public int Cancelled;
        readonly FakeClock _clock;
        public FakeTransport(FakeClock clock) { _clock = clock; }

        public Task<bool> MoveToHueAndSaturation(ulong node, int hue, int sat, int tenths, CancellationToken ct) => Do("hue", node, hue, sat, tenths, ct);
        public Task<bool> MoveToLevelWithOnOff(ulong node, int level, int tenths, CancellationToken ct) => Do("level", node, level, 0, tenths, ct);
        public Task<bool> MoveToColorTemperature(ulong node, int mireds, int tenths, CancellationToken ct) => Do("ct", node, mireds, 0, tenths, ct);

        async Task<bool> Do(string m, ulong node, int value, int sat, int tenths, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add(new Call(m, node, value, sat, tenths, _clock.Now));
            double d = DelayMs(m);
            if (d > 0)
                try { await _clock.Delay(d, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { Cancelled++; throw; }
            return !Fails(node);
        }
    }

    public class BulbSyncTests
    {
        static readonly Rgb Red = new Rgb(255, 0, 0), Black = new Rgb(0, 0, 0);
        static readonly TimeSpan Deadline = TimeSpan.FromSeconds(3);

        readonly FakeClock _clock = new FakeClock();
        readonly FakeTransport _t;
        readonly List<string> _log = new();
        Tuning _tuning = Tuning.Default;
        readonly BulbSync _sync;

        public BulbSyncTests()
        {
            _t = new FakeTransport(_clock);
            var bulbs = new[] { new BulbEntry(1, "upper", new string[0]), new BulbEntry(2, "lower", new string[0]) };
            _sync = new BulbSync(_t, bulbs, () => _tuning, _log.Add, () => _clock.Now, _clock.Delay);
        }

        Task Stop() { FakeClock.DetachContext(); return _sync.StopToWarmWhiteAsync(Deadline); }

        IEnumerable<Call> Of(ulong node, string method) => _t.Calls.Where(c => c.Node == node && c.Method == method);

        void AssertEndsWarmWhite(ulong node)
        {
            var last = _t.Calls.Where(c => c.Node == node).TakeLast(2).ToList();
            Assert.Equal(new[] { ("level", 127, 10), ("ct", 370, 10) }, last.Select(c => (c.Method, c.Value, c.Tenths)));
        }

        [Fact]
        public void Black_HoldsColour_DimsToMinLevel()
        {
            _sync.SetFrame(Red, Red);
            var run = _sync.RunAsync(CancellationToken.None);
            _clock.Advance(2000);
            Assert.Equal(254, Of(1, "level").Last().Value);
            _sync.SetFrame(Black, Black);
            _clock.Advance(5000);
            foreach (ulong n in new ulong[] { 1, 2 })
            {
                Assert.Single(Of(n, "hue"));
                Assert.Equal(5, Of(n, "level").Last().Value);
            }
            Assert.False(run.IsCompleted);
        }

        [Fact]
        public void NoFrame_SendsNothing()
        {
            _sync.RunAsync(CancellationToken.None);
            _clock.Advance(2000);
            Assert.Empty(_t.Calls);
        }

        [Fact]
        public void StaticFrame_StopsSending()
        {
            _sync.SetFrame(new Rgb(200, 120, 40), new Rgb(30, 90, 220));
            _sync.RunAsync(CancellationToken.None);
            _clock.Advance(5000);
            int n = _t.Calls.Count;
            Assert.True(n > 0);
            _clock.Advance(2000);
            Assert.Equal(n, _t.Calls.Count);
        }

        [Fact]
        public void Fade_TracksCycleTime()
        {
            // node 2 (lower): colour and level both change every cycle, so a cycle is two calls
            var dim = new Rgb(0, 0, 60);
            _tuning = Tuning.Default with { SmoothMs = 0 };
            _sync.RunAsync(CancellationToken.None);
            for (int i = 0; i < 40; i++) { _sync.SetFrame(i % 2 == 0 ? Red : dim, Red); _clock.Advance(100); }
            Assert.All(Of(2, "level").TakeLast(5), c => Assert.Equal(2, c.Tenths));     // fast bulb: 100 ms cycle

            _t.DelayMs = _ => 120;
            int from = _t.Calls.Count;
            for (int i = 0; i < 40; i++) { _sync.SetFrame(i % 2 == 0 ? Red : dim, Red); _clock.Advance(240); }
            var slow = _t.Calls.Skip(from).Where(c => c.Node == 2).TakeLast(6).ToList();
            Assert.Equal(6, slow.Count);
            Assert.All(slow, c => Assert.Equal(3, c.Tenths));                            // 2 x 120 ms cycle
        }

        [Fact]
        public void OneBulbFailing_OtherKeepsSyncing()
        {
            _t.Fails = n => n == 1;
            _sync.RunAsync(CancellationToken.None);
            for (int i = 0; i < 12; i++) { var c = i % 2 == 0 ? Red : new Rgb(0, 0, 255); _sync.SetFrame(c, c); _clock.Advance(500); }

            Assert.Contains(Of(2, "hue"), c => c.At > 5000);
            Assert.Contains(Of(2, "level"), c => c.At > 5000);
            int failedCycles = Of(1, "level").Count();      // node 1 never confirms, so every cycle retries level
            Assert.True(failedCycles > 40, $"{failedCycles}");
            Assert.Equal((failedCycles + 19) / 20, _log.Count(l => l.Contains("node 1")));
            Assert.DoesNotContain(_log, l => l.Contains("node 2"));
        }

        [Fact]
        public void StopDuringInFlightSend_EndsWarmWhite()
        {
            _t.DelayMs = m => m == "hue" ? 500 : 0;
            _sync.SetFrame(Red, Red);
            var run = _sync.RunAsync(CancellationToken.None);
            _clock.Advance(250);                              // both colour calls in flight
            Assert.Equal(0, _t.Cancelled);

            var stop = Stop();
            _clock.Advance(100);
            Assert.True(stop.IsCompletedSuccessfully);
            Assert.Equal(2, _t.Cancelled);
            AssertEndsWarmWhite(1);
            AssertEndsWarmWhite(2);
            Assert.False(_sync.IsRunning);
            Assert.True(run.IsCompletedSuccessfully);

            int n = _t.Calls.Count;
            _clock.Advance(2000);
            Assert.Equal(n, _t.Calls.Count);
        }

        [Fact]
        public void TuningChange_AppliesWithoutRestart()
        {
            _sync.SetFrame(Black, Black);
            _sync.RunAsync(CancellationToken.None);
            _clock.Advance(2000);
            Assert.Equal(5, Of(1, "level").Last().Value);
            _tuning = _tuning with { MinLevel = 20 };
            _clock.Advance(1000);
            Assert.Equal(20, Of(1, "level").Last().Value);
            Assert.Equal(20, Of(2, "level").Last().Value);
        }

        [Fact]
        public void SentValues_AreClamped()
        {
            _tuning = Tuning.Default with { MinLevel = 0 };   // maps black to level 0, which a bulb rejects
            _sync.SetFrame(Black, Black);
            _sync.RunAsync(CancellationToken.None);
            _clock.Advance(1000);
            Assert.Equal(1, Of(1, "level").Last().Value);
        }

        [Fact]
        public void StopWhenNotRunning_SendsNothing()
        {
            Assert.True(Stop().IsCompletedSuccessfully);
            Assert.Empty(_t.Calls);

            _sync.SetFrame(Red, Red);
            _sync.RunAsync(CancellationToken.None);
            _clock.Advance(1000);
            Assert.True(_sync.IsRunning);
            Assert.True(Stop().IsCompletedSuccessfully);
            int n = _t.Calls.Count;

            Assert.True(Stop().IsCompletedSuccessfully);
            _clock.Advance(1000);
            Assert.Equal(n, _t.Calls.Count);
        }

        [Fact]
        public void Restart_RefusedWhileStopping_WorksAfter()
        {
            _sync.SetFrame(Red, Red);
            _sync.RunAsync(CancellationToken.None);
            _clock.Advance(1000);
            _t.DelayMs = _ => 100;
            var stop = Stop();
            Assert.IsType<InvalidOperationException>(Record.Exception(() => { _sync.RunAsync(CancellationToken.None); }));   // throws synchronously
            _clock.Advance(1000);
            Assert.True(stop.IsCompletedSuccessfully);

            _t.DelayMs = _ => 0;
            int n = _t.Calls.Count;
            _sync.SetFrame(new Rgb(0, 0, 255), new Rgb(0, 0, 255));
            _sync.RunAsync(CancellationToken.None);
            _clock.Advance(1000);
            Assert.True(_t.Calls.Count > n);
            Assert.True(Stop().IsCompletedSuccessfully);
            Assert.Equal(2, Of(1, "ct").Count());
            AssertEndsWarmWhite(1);
        }

        [Fact]
        public void StopTwice_SendsWarmWhiteOnce()
        {
            _sync.SetFrame(Red, Red);
            _sync.RunAsync(CancellationToken.None);
            _clock.Advance(2000);
            _t.DelayMs = _ => 100;                            // warm white takes a while

            var a = Stop();
            var b = Stop();
            Assert.False(a.IsCompleted);
            _clock.Advance(1000);
            Assert.True(a.IsCompletedSuccessfully);
            Assert.True(b.IsCompletedSuccessfully);
            foreach (ulong n in new ulong[] { 1, 2 })
            {
                Assert.Single(Of(n, "level"), c => c.Value == 127);
                Assert.Single(Of(n, "ct"));
                AssertEndsWarmWhite(n);
            }
        }
    }
}
