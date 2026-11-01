// Drives the real bulbs through MatterController. Usage: BulbProbe <bulbs.json> <interface, e.g. en0>
// Prints command results and timings only: never keys or certificates.
using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HyperTizen.Bulbs;
using HyperTizen.Bulbs.Matter;

var creds = MatterCredentials.Load(args[0]);
var sw = Stopwatch.StartNew();
using var c = new MatterController(creds, args[1], m => Console.WriteLine($"  [{sw.ElapsedMilliseconds,6} ms] {m}"));
var ct = CancellationToken.None;

async Task Step(string what, Func<Task<bool>> cmd)
{
    var t = sw.ElapsedMilliseconds;
    bool ok = await cmd();
    Console.WriteLine($"{what}: {ok} ({sw.ElapsedMilliseconds - t} ms)");
}

const int Fade = 20; // 2 s
await Step("node 1 on, full level", () => c.MoveToLevelWithOnOff(1, 254, 0, ct));
await Step("node 2 on, full level", () => c.MoveToLevelWithOnOff(2, 254, 0, ct));
await Step("node 1 red, 2 s fade", () => c.MoveToHueAndSaturation(1, 0, 254, Fade, ct));
await Task.Delay(2500);
await Step("node 2 blue, 2 s fade", () => c.MoveToHueAndSaturation(2, 169, 254, Fade, ct));
await Task.Delay(2500);
await Task.WhenAll(
    Step("node 1 warm white, 2 s fade", () => c.MoveToColorTemperature(1, 370, Fade, ct)),
    Step("node 2 warm white, 2 s fade", () => c.MoveToColorTemperature(2, 370, Fade, ct)));
await Task.Delay(2500);

const int N = 200;
var results = new StringBuilder();
int okCount = 0;
var t0 = sw.Elapsed;
for (int i = 0; i < N; i++)
{
    bool ok = await c.MoveToHueAndSaturation(1, i % 2 == 0 ? 0 : 85, 254, 1, ct);
    results.Append(ok ? '.' : 'x');
    if (ok) okCount++;
}
var secs = (sw.Elapsed - t0).TotalSeconds;
Console.WriteLine($"rate test, node 1, {N} alternating hues ('.' ok, 'x' failed):");
for (int i = 0; i < N; i += 50) Console.WriteLine("  " + results.ToString(i, Math.Min(50, N - i)));
Console.WriteLine($"rate test: {okCount}/{N} ok in {secs:F2} s = {N / secs:F1} commands/s");
await Step("node 1 warm white", () => c.MoveToColorTemperature(1, 370, 0, ct));
