# Bulb Sync on the TV: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task by task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** HyperTizen samples the screen, turns the left edge into two bulb colours and drives both Ledvance Matter bulbs directly. The owner can switch sync on and off and change the tuning values from the TV app. There is no Mac or HyperHDR at runtime.

**Architecture:**
- **A new plain .NET library, `HyperTizen.Bulbs` (netstandard2.0),** holds everything that doesn't need Tizen: the colour chain, the smoothing, the bulb mapping, a minimal Matter controller and the send loop. It's developed and tested with `dotnet test` on the Mac, against fixtures and the real bulbs.
- **The Tizen service** references it, feeds it the 64×48 NV12 frames it already produces, and replaces the HyperHDR flatbuffer output.
- **Crypto comes from BouncyCastle**, so it doesn't depend on whatever OpenSSL Tizen ships.

**Tech stack:**
- C# with `netstandard2.0` (library) and `tizen90` (service)
- `BouncyCastle.Cryptography`
- xUnit on `net10.0` for the tests
- Node and matter.js in `~/git/bulb-sync` for the one-time Mac tools
- TizenBrew web module for the UI

**Spec:** `docs/superpowers/specs/2026-11-01-matter-bulb-sync-design.md`. Read it first. This plan argues from it.

## Global constraints

- **Bulbs:**
  - node 1 = upper-left zone, node 2 = lower-left zone, endpoint 1
  - Matter operational UDP port 5540
  - known addresses `fe80::dafc:92ff:fed2:91c5` (node 1) and `fe80::dafc:92ff:fed2:8796` (node 2)
- **Commands:** `optionsMask` 0, `optionsOverride` 0, `transitionTime` in tenths of a second. Four commands only:
  - `MoveToHueAndSaturation` (ColorControl 0x0300, cmd 0x06)
  - `MoveToLevelWithOnOff` (LevelControl 0x0008, cmd 0x04)
  - `MoveToColorTemperature` (0x0300, cmd 0x0A)
  - `MoveToLevel` is no longer used: the spec's call means sync turns the bulbs on.
- **Defaults (`Tuning`):** Gamma 1, SmoothMs 300, MinLevel 5 (range 1–254), Black 10 (range 0–255).
- **Saved settings (Preference keys):** `bulbGamma`, `bulbSmoothMs`, `bulbMinLevel`, `bulbBlack`, next to the existing `enabled`.
- **Warm white on stop:** level 127, 370 mireds (≈2700 K). Used both when sync is switched off and when the TV goes to standby.
- **HyperHDR version:** the installed HyperHDR is **22.0.0** (from `/Applications/hyperhdr.app/Contents/Info.plist`). Read the source at tag `v22.0.0`.
- **Solutions and SDKs:**
  - Only `HyperTizen.Bulbs` (netstandard2.0) is referenced from `HyperTizen.sln` and `HyperTizen.csproj`.
  - The `net10.0` test and tool projects live in a separate `HyperTizen.Bulbs.sln`, so the owner's Tizen build (the VS Code Tizen extension, bundled SDK 9.0.304) never sees them.
  - Verify the Tizen build with the extension's build, not with Homebrew `dotnet build`.
- **HyperHDR reference settings:**
  - gamma 1.5 per channel, all other adjustments identity
  - `HybridRgbInterpolator` with `time_ms` 150, `antiFlickeringFilter` true, `updateFrequency` 50, `y_limit` 0.03, stiffness 150, damping 26
  - LED 0 = lower-left, area x 0–0.05, y 0.5–1; LED 1 = upper-left, area x 0–0.05, y 0–0.5; "advanced" mapping
- **Same controller identity.** The TV uses the matter.js shell's controller identity, so the Mac bridge (`~/git/bulb-sync/bridge.mjs`) must be stopped whenever the TV or the Mac test tools talk to the bulbs. Stop it with `pkill -f bridge.mjs`, then check `pgrep -f nodejs-shell` is empty.
- **Private keys never leave their two homes:** `~/.matter/shell-1` and the gitignored `HyperTizen/res/bulbs.json`. Never commit them and never log them. Never distribute a `.tpk` that has them bundled in.
- **Git:** work on branch `bulbs-on-tv` in `~/git/HyperTizen`. Commit freely. Never push (owner's instruction).
- **Keep HyperHDR installed on the Mac** until the owner signs off at the end (Task 13).

**Deviations from the approved spec**, to be confirmed by the owner before execution:

- **Credentials are bundled into the package** as `res/bulbs.json` (gitignored) instead of being pushed to the app data directory.
  - **Reason:** I expect retail Tizen not to allow `sdb push` into another app's data directory. That's unverified.
  - **Consequence:** the built `.tpk` carries the bulbs' private key. It must stay on this TV and must not be shared.
- **The black threshold goes up to 254, not 255.** At 255 the brightness curve divides by zero.

## Review Focus

These are the five failures most likely to bite in daily use that the spec implies but doesn't spell out. Each has a test in the owning task.

1. **A bulb unreachable, at startup or mid-run** (switched off at the wall, Wi-Fi drop). The other bulb keeps syncing. The missing one reconnects by itself when it's back. No crash, and at most one warning every 20 failures. Tests: Task 10 `OneBulbFailing_OtherKeepsSyncing`, Task 9 reconnect step.
2. **A bulb reboots and loses its session.** The next command after MRP gives up re-runs CASE, and sync carries on. Test: Task 9 `SessionLost_NextInvokeReconnects`.
3. **Bad settings from the UI** (non-numeric, MinLevel 0 or 300, Gamma 0 or negative, Black > 254). These are rejected, the previous value stays, and nothing bad is saved. Tests: Task 6 `Tuning_RejectsBadValues`, Task 11 SetConfig step.
4. **Sync switched off, or the TV going to standby, while a colour command is in flight** (including fast on/off toggling). The last thing each bulb receives is warm white. Test: Task 10 `StopDuringInFlightSend_EndsWarmWhite`.
5. **Missing or corrupt `bulbs.json`.** The service and its UI still run, sync logs one clear error, and nothing crashes. Tests: Task 7 `Load_MissingOrCorrupt_Throws_CredentialsException`, Task 11 startup step.

**Owner-present steps.** These need the owner at the TV or watching the bulbs:
- Task 1 steps 4–5
- Task 2 step 3
- Task 9 step 5
- Task 11 step 6
- Task 12 step 3
- Task 13 step 2

Run them in the main session with the owner, whatever execution method is used. A subagent stops before them and hands back.

---

### Task 1: Risk probes on the real TV (spike, gate)

This is throwaway code that answers spec risks 1, 2 and 5 before anything is built. It needs the owner at the TV.

**Files:**
- Create: `HyperTizen/Probe.cs` (deleted at the end of this task)
- Modify: `HyperTizen/HyperTizen_App.cs` (temporary call)
- Create (Mac, not committed): `~/git/bulb-sync/probe-listen.mjs`

- [ ] **Step 1: Commit the existing uncommitted work first.** Create the branch, then commit the `SemaphoreSlim` send gate in `Networking.cs` and the raw-sample log line on their own.
  ```bash
  git checkout -b bulbs-on-tv
  git add HyperTizen/Networking.cs HyperTizen/Capture/PixelSamplingCaptureMethod.cs
  git commit -m "fix: serialize frame sends; temp raw sample log"
  ```
  Add `HyperTizen/out/` and `HyperTizen/res/bulbs.json` to `.gitignore` in the same commit.
- [ ] **Step 2: Write `Probe.Run()`.** It does two things:
  - **mDNS check:** sends an mDNS PTR query for `_matter._tcp.local` to `[ff02::fb]:5353` on every IPv6-capable interface, then logs every answer: the interface name and scope id, plus the AAAA addresses received.
  - **Standby hook:** registers a `Display.StateChanged` handler that, on `Off`, sends one UDP datagram `"display-off <utc ms>"` to the Mac (IP from `Preference` key `probeHost`, port 5599) every 100 ms for 5 s, and logs each send result.
- [ ] **Step 3: Write `probe-listen.mjs`** on the Mac. It listens on UDP 5599 and prints each datagram with its arrival time.
- [ ] **Step 4: Build, install, and record the results.** With the owner:
  1. Start the service and read the log viewer for the mDNS answers.
  2. Press the remote's power button with `probe-listen.mjs` running.
  3. Cold boot the TV (unplug for 30 s).
  4. Bring it back from standby.

  For both 3 and 4, record whether the service came back without the owner opening anything.
- [ ] **Step 5: Gate.** Write the results into a new section "Probe results" at the end of this plan file:
  - interface name and scope id
  - whether both bulbs answered mDNS
  - how many "display-off" datagrams arrived, and over what time span
  - whether autostart works after a cold boot and after standby

  **Stop and report to the owner instead of continuing if any of these hold:**
  - neither bulb answered mDNS
  - zero datagrams arrived after `Off`
  - autostart fails

  Those cases change the design.
- [ ] **Step 6: Delete `Probe.cs` and its call, and commit the results.**
  ```bash
  git commit -am "docs: TV probe results"
  ```

### Task 2: Reference recording from the live Mac pipeline

This captures how today's chain turns raw samples into LED colours, so the C# port can be checked against numbers and not just by eye.

**Files:**
- Create (Mac, not committed): `~/git/bulb-sync/record-reference.mjs`
- Create: `HyperTizen.Bulbs.Tests/fixtures/reference.json`

**Interfaces:**
- **Produces:** `reference.json`, a JSON array of rows `{ "raw": [[r,g,b] ×16], "led0": [r,g,b], "led1": [r,g,b] }`.
  - `raw` is the 16 10-bit samples, in `_capturedPoints` order.
  - `led0` and `led1` are HyperHDR's 8-bit udpraw output for lower-left and upper-left.
  - Only steady states are recorded, so smoothing doesn't matter here.

- [ ] **Step 1: Write `record-reference.mjs`.** It does three things:
  - connects to the TV log WebSocket (`ws://<TV>:45678`) and parses `PixelSampling raw:` lines
  - binds UDP 127.0.0.1:5568 (the bridge must be stopped)
  - each time a raw line arrives, waits until the udpraw values have not changed by more than 1 for 1 s, then appends a row
- [ ] **Step 2: Shorten the raw-log interval for recording.** Temporarily make the raw-log line in `PixelSamplingCaptureMethod.cs` fire every 20 frames instead of every 100. Install it.
- [ ] **Step 3: Record at least 40 rows with the owner.** The owner plays a YouTube colour test sequence covering:
  - full black
  - near-black (RGB 4–20)
  - grey steps
  - pure R, G, B, C, M, Y at 100% and 25%
  - skin tones
  - one dark film scene
- [ ] **Step 4: Commit and restore.** Copy the output to `HyperTizen.Bulbs.Tests/fixtures/reference.json` (it contains no secrets) and commit it. Restore the 100-frame interval. Restart `bridge.mjs` afterwards so the owner still has a working setup.

### Task 3: Library and test project, NV12 encoder moved into it

**Files:**
- Create: `HyperTizen.Bulbs/HyperTizen.Bulbs.csproj` (`netstandard2.0`, `LangVersion` latest, package `BouncyCastle.Cryptography`)
- Create: `HyperTizen.Bulbs/SampleImage.cs`, `HyperTizen.Bulbs/Rgb.cs`
- Create: `HyperTizen.Bulbs/IsExternalInit.cs`. This is the `System.Runtime.CompilerServices.IsExternalInit` polyfill. The later `record` types need it on netstandard2.0, or the build fails with CS0518.
- Create: `HyperTizen.Bulbs.Tests/HyperTizen.Bulbs.Tests.csproj` (`net10.0`, xUnit, references the library, copies `fixtures/**`)
- Create: `HyperTizen.Bulbs.sln` (the library, the tests, and later `tools/BulbProbe`)
- Create: `HyperTizen.Bulbs.Tests/SampleImageTests.cs`
- Modify: `HyperTizen/HyperTizen.csproj` (ProjectReference to the library only), `HyperTizen.sln` (add the library only), `HyperTizen/Capture/PixelSamplingCaptureMethod.cs:663-892`

**Interfaces:**
- **Produces:**
  - `public readonly struct Rgb10 { public readonly int R, G, B; }`
  - `public readonly struct Rgb { public readonly double R, G, B; }`, with channels 0–255
  - `public static class SampleImage { public const int Width = 64, Height = 48; public static (byte[] Y, byte[] UV) ToNv12(Rgb10[] samples); }`. `samples` is the 16 points in `_capturedPoints` order.

- [ ] **Step 1: Write the failing tests** in `SampleImageTests`:
  - `AllBlack_GivesY0_UV128`: every `Y == 0` and every `UV == 128`
  - `AllWhite_GivesY255`: every left-edge `Y` in columns 0–2 is 255
  - `MatchesOriginalEncoder`: the encoder's output for the first fixture row is byte-for-byte equal to `fixtures/nv12-golden.bin`

  To create `nv12-golden.bin`, run the *old* `ConvertColorsToNV12` once from a scratch test copy before deleting it.
- [ ] **Step 2: Run them and confirm they fail.**
  ```bash
  dotnet test HyperTizen.Bulbs.Tests
  ```
  Expected: compile failure (`SampleImage` not found).
- [ ] **Step 3: Move `ConvertColorsToNV12` and `ScaleTo8Bit` into `SampleImage.ToNv12` unchanged.** `PixelSamplingCaptureMethod` then maps its `Color[]` to `Rgb10[]` and calls `SampleImage.ToNv12`. Nothing else in the capture changes.
- [ ] **Step 4: Run the tests and confirm they pass.** Run `dotnet test HyperTizen.Bulbs.Tests` (expect PASS, 3 tests), then build the Tizen project with the owner's VS Code Tizen extension build (Release). Expect a clean build that produces the `.tpk`.
- [ ] **Step 5: Commit.**
  ```bash
  git commit -m "refactor: move NV12 encoder into HyperTizen.Bulbs"
  ```

### Task 4: Colour chain (NV12 → two zone colours, as HyperHDR does it)

**Files:**
- Create: `HyperTizen.Bulbs/ColourChain.cs`
- Test: `HyperTizen.Bulbs.Tests/ColourChainTests.cs`

**Interfaces:**
- **Consumes:** `SampleImage`, `Rgb10`, `Rgb` (Task 3)
- **Produces:** `public static class ColourChain { public static (Rgb Lower, Rgb Upper) LeftZones(byte[] y, byte[] uv, int width, int height); }`. The output is the same 8-bit LED values HyperHDR sends over udpraw, as doubles, before smoothing.

- [ ] **Step 1: Write the failing test** `ReproducesHyperHdrReference`. For each row in `reference.json`:
  ```csharp
  var (y, uv) = SampleImage.ToNv12(row.Raw);
  var (lower, upper) = ColourChain.LeftZones(y, uv, 64, 48);
  AssertClose(row.Led0, lower, tol: 3); AssertClose(row.Led1, upper, tol: 3);
  ```
  Add `Black_IsZero`: an all-black frame gives exactly (0, 0, 0) for both zones.
- [ ] **Step 2: Run it and confirm it fails.**
  ```bash
  dotnet test --filter ColourChainTests
  ```
  Expected: FAIL (`ColourChain` not found).
- [ ] **Step 3: Implement `LeftZones`.**
  1. Decode NV12 to RGB exactly as HyperHDR 22.0.0 does for flatbuffer NV12 input with HDR off. Read it from HyperHDR's source (the flatbuffer server's NV12 path and the `lut_lin_tables.3d` table for no tone mapping) and implement the formula and range handling.

     **Fallback** if the table isn't a closed formula you can reproduce: extract a 33×33×33 subsampled YUV→RGB table from the installed `lut_lin_tables.3d` on the Mac (a one-off script). Embed it as a resource and interpolate trilinearly. Either way, the ±3 gate in the test decides.
  2. Average the pixels in each LED area (x 0–0.05, y 0.5–1 for Lower and 0–0.5 for Upper) with HyperHDR's "advanced" mapping rule.
  3. Apply HyperHDR's per-channel gamma 1.5 the way its adjustment code does.
- [ ] **Step 4: Run it and confirm it passes.** If some rows miss by more than 3, fix the decode. Don't widen the tolerance.
- [ ] **Step 5: Commit.**
  ```bash
  git commit -m "feat: HyperHDR-equivalent colour chain"
  ```

### Task 5: Smoothing (HyperHDR interpolator, then the bridge's fade)

**Files:**
- Create: `HyperTizen.Bulbs/HybridSmoother.cs`, `HyperTizen.Bulbs/Ema.cs`
- Test: `HyperTizen.Bulbs.Tests/SmoothingTests.cs`

**Interfaces:**
- **Produces:**
  - `public sealed class HybridSmoother { public HybridSmoother(int timeMs = 150, bool antiFlicker = true, double yLimit = 0.03, int stiffness = 150, int damping = 26); public void SetTarget(Rgb lower, Rgb upper, double nowMs); public (Rgb Lower, Rgb Upper) Tick(double nowMs); }`. The caller ticks it at 50 Hz.
  - `public static class Ema { public static Rgb Step(Rgb current, Rgb target, double dtMs, int smoothMs); }`. With `smoothMs <= 0` it returns the target. Otherwise it returns `current + (target - current) * (1 - exp(-dt/smoothMs))`, exactly as in `bridge.mjs`.

- [ ] **Step 1: Write the failing tests:**
  - `Ema_ZeroSmooth_ReturnsTarget`
  - `Ema_OneTimeConstant_Reaches63Percent`: from 0 towards 255 with dt = 300 and smoothMs = 300 gives 161 ± 1
  - `Hybrid_StepSettlesWithinTime`: a step from black to (255, 0, 0), ticked every 20 ms, is within 2 of the target by 150 ms + 40 ms and never exceeds 255
  - `Hybrid_AntiFlicker_HoldsTinyJitter`: alternating targets ±2 around a mid-grey change the output by less than 2 over 1 s
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.** `HybridSmoother` ports HyperHDR 22's `HybridRgbInterpolator` behaviour (the timing, the anti-flicker filter and `y_limit`) from its source. Keep only the code paths these settings use.
- [ ] **Step 4: Run them and confirm they pass.**
- [ ] **Step 5: Commit.**
  ```bash
  git commit -m "feat: smoothing (HybridRgbInterpolator port + EMA)"
  ```

### Task 6: Bulb mapping and tuning

**Files:**
- Create: `HyperTizen.Bulbs/BulbMapper.cs`, `HyperTizen.Bulbs/Tuning.cs`
- Test: `HyperTizen.Bulbs.Tests/BulbMapperTests.cs`

**Interfaces:**
- **Produces:**
  - `public readonly struct MatterColour { public readonly int? Hue, Sat; public readonly int Level; }`. `Hue` and `Sat` are null when the screen counts as black.
  - `public sealed record Tuning(double Gamma = 1, int SmoothMs = 300, int MinLevel = 5, int Black = 10) { public static readonly Tuning Default; public static bool TryParse(string key, string value, Tuning current, out Tuning updated); }`
    - Keys are the Preference keys from Global constraints.
    - Valid ranges: Gamma 0.2–5, SmoothMs 0–5000, MinLevel 1–254, Black 0–254.
    - It parses with invariant culture.
  - `public static class BulbMapper { public static MatterColour ToMatter(Rgb rgb, Tuning t); public static bool ColourChanged(MatterColour? prev, MatterColour next); public static bool LevelChanged(int? prev, int next); public static int HueDiff(int a, int b); }`. This is a straight port of `rgbToMatter`, `colourChanged` (hue > 3 or sat > 6), `levelChanged` (> 3) and `hueDiff` from `~/git/bulb-sync/bridge.mjs`.

- [ ] **Step 1: Write the failing tests.** Port the `bridge.mjs` self-check with defaults:
  - `[0,0,0]` gives `Hue` null, `Sat` null, `Level` 5
  - `[10,0,0].Level == 5`
  - `[255,0,0]` gives (0, 254, 254)
  - `[0,0,255].Hue == 169`
  - `[255,255,255].Sat == 0`
  - `HueDiff(2, 252) == 4`

  Plus three more tests:
  - `Gamma_BelowOne_BrightensMidtones`: level for `[128,0,0]` with Gamma 0.7 is higher than with Gamma 1
  - `ColourChanged_NullHue_IsFalse`
  - `Tuning_RejectsBadValues`: `TryParse` returns false and leaves `current` unchanged for `("bulbMinLevel","0")`, `("bulbMinLevel","300")`, `("bulbGamma","0")`, `("bulbGamma","abc")` and `("bulbBlack","255")`, and returns true for `("bulbGamma","0.7")`
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement the port.**
- [ ] **Step 4: Run them and confirm they pass.**
- [ ] **Step 5: Commit.**
  ```bash
  git commit -m "feat: bulb mapping and tuning"
  ```

### Task 7: Credential export (Mac) and loading (library)

**Files:**
- Create (Mac, not committed): `~/git/bulb-sync/export-credentials.mjs`
- Create: `HyperTizen.Bulbs/MatterCredentials.cs`
- Test: `HyperTizen.Bulbs.Tests/MatterCredentialsTests.cs`, `fixtures/credentials-fake.json` (made-up keys)

**Interfaces:**
- **Produces** a `bulbs.json` format, all binary values base64:
  ```json
  { "fabricId": "<decimal>", "nodeId": "<decimal>", "rootPublicKey": "…", "rootCert": "…(Matter TLV)",
    "operationalCert": "…(Matter TLV)", "privateKey": "…(32-byte raw P-256 scalar)",
    "ipk": "…(16-byte operational IPK)",
    "bulbs": [ { "nodeId": "1", "zone": "upper", "addresses": ["fe80::dafc:92ff:fed2:91c5"] },
               { "nodeId": "2", "zone": "lower", "addresses": ["fe80::dafc:92ff:fed2:8796"] } ] }
  ```
- **Produces** `public sealed class MatterCredentials { public ulong FabricId, NodeId; public byte[] RootPublicKey, RootCert, OperationalCert, PrivateKey, Ipk; public IReadOnlyList<BulbEntry> Bulbs; public static MatterCredentials Load(string path); }`, plus `public sealed record BulbEntry(ulong NodeId, string Zone, string[] Addresses);` and `public sealed class CredentialsException : Exception`.

- [ ] **Step 1: Write the exporter.** `export-credentials.mjs` reads `~/.matter/shell-1/fabrics.fabrics` and `nodes.peer*` (for operational addresses) by decoding matter.js's storage encoding. It writes `~/git/HyperTizen/HyperTizen/res/bulbs.json`.

  The operational IPK is `operationalIdentityProtectionKey`. It must print only field names and lengths, never values.
- [ ] **Step 2: Run the exporter and check what it wrote.**
  ```bash
  node export-credentials.mjs
  ```
  Expected: it reports `privateKey 32`, `ipk 16` and two bulbs, and `git status` in HyperTizen does not show `res/bulbs.json`.
- [ ] **Step 3: Write the failing tests:**
  - `Load_ParsesFakeFile`: all fields decode and there are two bulbs
  - `Load_MissingOrCorrupt_Throws_CredentialsException`: covers a missing file, invalid JSON, a missing `privateKey`, and a `privateKey` that isn't 32 bytes
- [ ] **Step 4: Implement `Load`** with Newtonsoft.Json, which is already a dependency of the service. Run the tests and confirm they pass.
- [ ] **Step 5: Commit** the library code and fake fixture only.
  ```bash
  git commit -m "feat: matter credentials loading"
  ```

### Task 8: Matter building blocks (TLV, message frame, session crypto)

**Files:**
- Create: `HyperTizen.Bulbs/Matter/Tlv.cs`, `Matter/MessageCodec.cs`, `Matter/SessionCrypto.cs`
- Create (Mac, not committed): `~/git/bulb-sync/gen-vectors.mjs`
- Test: `HyperTizen.Bulbs.Tests/MatterCodecTests.cs`, `fixtures/matter-vectors.json`

**Interfaces:**
- **Produces** (all `internal`, with `InternalsVisibleTo` the tests):
  - **TLV:** `TlvWriter` (`StartStructure(tag)`, `Put(tag, ulong|long|bool|byte[]|string)`, `EndContainer()`, `ToArray()`) and `TlvReader` (`Next()`, `Tag`, `Type`, `GetUInt()`, `GetBytes()`, `EnterContainer()`, `ExitContainer()`), with context and anonymous tags only
  - **Message framing:** `MessageCodec.EncodeSecured(ushort sessionId, uint counter, byte[] payloadHeaderAndBody, byte[] key, ulong sourceNodeId)` → `byte[]` and `MessageCodec.DecodeSecured(byte[] datagram, byte[] key, out MessageHeader header)` → `byte[]`, with AES-128-CCM, a 16-byte tag, and the nonce built per Matter Core spec §4.8.1. `ProtocolHeader` encode and decode (exchange flags, opcode, exchange id, protocol id, ack counter).
  - **Session crypto:** `SessionCrypto.Hkdf(byte[] ikm, byte[] salt, byte[] info, int len)`, `EcdhP256(byte[] priv, byte[] peerPub)`, `SignP256(byte[] priv, byte[] msg)` (raw r||s), `VerifyP256(byte[] pub, byte[] msg, byte[] sig)`, `Sha256` and `HmacSha256`, all on BouncyCastle

- [ ] **Step 1: Generate test vectors on the Mac.** `gen-vectors.mjs` uses matter.js's own codecs (`@matter/types` TLV, `@matter/protocol` message codec, `@matter/general` crypto) to produce:
  - TLV encodings of an `InvokeRequest` for each of the 4 commands
  - one encrypted message with a fixed key, nonce inputs and payload
  - an HKDF output and an ECDH shared secret from fixed made-up keys
  - one ECDSA signature that verifies

  Write them to `fixtures/matter-vectors.json`.
- [ ] **Step 2: Write the failing tests.** Each vector gets one test asserting byte equality: `Tlv_InvokeRequest_MatchesMatterJs` (4 cases), `Message_Encrypt_MatchesMatterJs`, `Message_Decrypt_RoundTrip`, `Message_TamperedTag_Throws`, `Hkdf_MatchesMatterJs`, `Ecdh_MatchesMatterJs`, and `Verify_MatterJsSignature`.
- [ ] **Step 3: Implement** until they pass.
- [ ] **Step 4: Commit.**
  ```bash
  git commit -m "feat: matter TLV, message codec, crypto"
  ```

### Task 9: Matter controller (CASE, reliable messaging, command invoke)

**Files:**
- Create: `HyperTizen.Bulbs/Matter/CaseInitiator.cs`, `Matter/Mrp.cs`, `Matter/MatterController.cs`, `HyperTizen.Bulbs/IBulbTransport.cs`
- Create: `tools/BulbProbe/BulbProbe.csproj` (`net10.0` console) and `Program.cs`
- Test: `HyperTizen.Bulbs.Tests/MatterControllerTests.cs`

**Interfaces:**
- **Consumes:** Task 7 `MatterCredentials` and the Task 8 building blocks
- **Produces:**
  ```csharp
  public interface IBulbTransport {
    Task<bool> MoveToHueAndSaturation(ulong node, int hue, int sat, int tenths, CancellationToken ct);
    Task<bool> MoveToLevelWithOnOff(ulong node, int level, int tenths, CancellationToken ct);
    Task<bool> MoveToColorTemperature(ulong node, int mireds, int tenths, CancellationToken ct);
  }
  public sealed class MatterController : IBulbTransport, IDisposable {
    public MatterController(MatterCredentials creds, string interfaceScope /* from Task 1, e.g. "wlan0" */, Action<string> log);
  }
  ```
  - The constructor does no I/O.
  - Each node gets one CASE session, created lazily on the first command, from the address list in `bulbs.json`.
  - A result of `false` means the command wasn't confirmed: no status response, or MRP gave up. On `false`, drop that node's session so the next call re-runs CASE.
  - Calls to the same node are serialized. Different nodes run in parallel.

- [ ] **Step 1: Write the failing unit tests.** They use a fake UDP loop: an in-memory responder the test drives.
  - `Mrp_RetransmitsUntilAck`: a lost first send is followed by a retransmit, then success
  - `Mrp_GivesUpAfterMaxRetries_ReturnsFalse`
  - `SessionLost_NextInvokeReconnects`: after a `false`, the next invoke starts a new Sigma1
  - `Invoke_EncodesCommandPath`: the sent `InvokeRequest` has endpoint 1, cluster 0x0300, command 0x06 and the expected fields
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement** per Matter Core spec 1.3:
  - CASE Sigma1/2/3 initiator (§4.14.2), with `destinationId` computed from IPK, root public key, fabric id and peer node id, and the peer's certificate verified against `RootCert`. No session resumption.
  - MRP (§4.12) with the spec's default intervals, plus standalone acks for responses
  - IM `InvokeRequest` / `InvokeResponse` (Interaction Model §8), where only a status of SUCCESS counts as `true`
- [ ] **Step 4: Run the unit tests and confirm they pass.**
- [ ] **Step 5: Run `BulbProbe` against the real bulbs** with the owner watching. Stop the Mac bridge first (see Global constraints).
  ```bash
  dotnet run --project tools/BulbProbe -- ~/git/HyperTizen/HyperTizen/res/bulbs.json en0
  ```
  It sets node 1 to red, then node 2 to blue, each with a 2 s fade (`tenths` = 20), then both to warm white. Then it sends 200 alternating hues to node 1 back to back and prints the commands per second.

  Expected:
  - The owner sees the fades.
  - The rate is at least 16 per second (spec risk 4).
  - Every command returns `true`.

  If the bulbs jump instead of fading, stop and report: the spec's fade approach depends on this working.
- [ ] **Step 6: Restart `bridge.mjs` and commit.**
  ```bash
  git commit -m "feat: minimal matter controller"
  ```

### Task 10: Send loop (the bridge's behaviour, plus warm white on stop)

**Files:**
- Create: `HyperTizen.Bulbs/BulbSync.cs`
- Test: `HyperTizen.Bulbs.Tests/BulbSyncTests.cs` (with a `FakeTransport` that records calls, can delay, and can fail per node)

**Interfaces:**
- **Consumes:** `IBulbTransport` (Task 9), `ColourChain` output (Task 4), `HybridSmoother` and `Ema` (Task 5), `BulbMapper` and `Tuning` (Task 6), `BulbEntry` (Task 7)
- **Produces:**
  ```csharp
  public sealed class BulbSync {
    public BulbSync(IBulbTransport transport, IReadOnlyList<BulbEntry> bulbs, Func<Tuning> tuning, Action<string> log);
    public void SetFrame(Rgb lower, Rgb upper);          // called per capture frame (pre-smoothing)
    public Task RunAsync(CancellationToken ct);           // smoother tick 50 Hz + one send loop per bulb at ≤10 Hz
    public bool IsRunning { get; }
    public Task StopToWarmWhiteAsync(TimeSpan deadline);  // only if IsRunning: cancels sends, then level 127 + 370 mireds, 1 s fade, both bulbs in parallel; otherwise no-op
  }
  ```

  `StopToWarmWhiteAsync` is idempotent. A second call while one is in progress awaits the first. It never sends anything when sync wasn't running, so turning the TV off with sync disabled can't switch bulbs on.

  The per-bulb loop mirrors `bridge.mjs`:
  - EMA with `tuning().SmoothMs` on real elapsed time
  - `fade = clamp(ceil(cycleMs / 100), 2, 5)`, where `cycleMs` is an EMA (0.3 weight) of busy-loop durations, starting at 300
  - colour and level tracked separately
  - a colour is sent only when `ColourChanged`
  - level via `MoveToLevelWithOnOff`
  - one warning per 20 consecutive failures per node

- [ ] **Step 1: Write the failing tests** (time is driven by an injectable clock):
  - `Black_HoldsColour_DimsToMinLevel`: red, then black. There's no colour call after the first, and the level ends at 5.
  - `StaticFrame_StopsSending`: after convergence there are 0 calls in 2 s.
  - `Fade_TracksCycleTime`: with a fake per-call delay of 120 ms, `tenths` settles at 3.
  - `OneBulbFailing_OtherKeepsSyncing`: node 1 always fails, node 2 still gets updates and there is one warning per 20 failures.
  - `StopDuringInFlightSend_EndsWarmWhite`: stop during a delayed colour call. The last call per node is level 127 plus 370 mireds.
  - `TuningChange_AppliesWithoutRestart`: switching MinLevel from 5 to 20 while the screen is black takes the level to 20.
  - `StopWhenNotRunning_SendsNothing`: `StopToWarmWhiteAsync` before `RunAsync`, or after a completed stop, makes 0 transport calls.
  - `StopTwice_SendsWarmWhiteOnce`: two concurrent stops give exactly one warm-white pair per node.
- [ ] **Step 2: Run them and confirm they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run them and confirm they pass.** Then run the whole suite with `dotnet test` and expect everything to pass.
- [ ] **Step 5: Commit.**
  ```bash
  git commit -m "feat: bulb send loop"
  ```

### Task 11: Wire it into the Tizen service

**Files:**
- Modify: `HyperTizen/HyperionClient.cs:104-300` (startup), `:409-580` (capture loop), `:612-700` (Stop)
- Modify: `HyperTizen/HyperTizen_App.cs` (`Display_StateChanged`, startup)
- Modify: `HyperTizen/WebSocket/WebSocket.cs:387-485` (`SetConfiguration`)
- Modify: `HyperTizen/HyperTizen.csproj` (bundle `res/bulbs.json`)
- Delete or stop using: the flatbuffer send path (`Networking.SendImageAsync`, `SendRegister`, RPC-server and SSDP discovery at startup). Leave the files if removing them is invasive. The output path must simply not run.

**Interfaces:**
- **Consumes:** Tasks 4, 7, 9 and 10
- **Produces** a new static, `App.Bulbs` (`BulbSync`, or null when credentials failed to load), and `Globals.Instance.Tuning` (a `Tuning` loaded from Preference at startup)

- [ ] **Step 1: Startup.**
  1. Load `bulbs.json` from `Application.Current.DirectoryInfo.Resource`. On `CredentialsException`, log one error and post a TV notification ("Bulb sync: credentials missing"), and keep the service and the WebSockets running without sync.
  2. Build `Tuning` from the Preference keys, falling back to defaults for any key that is missing or invalid.
  3. Create `MatterController` using the interface scope from Task 1, and `BulbSync`.
- [ ] **Step 2: Split "user wants sync" from "loop is running".** Today `Globals.Instance.Enabled` does both jobs:
  - `Stop()` clears it (`HyperionClient.cs:618`), and so do the error paths at 205, 397 and 577.
  - `Start()` force-sets it ("Auto-enabling capture").
  - Waking from standby only works because of that override.

  Make the split:
  - **What the user wants** is the persisted `enabled`, held in `App.Configuration.Enabled`. Only `SetConfig enabled` changes it.
  - **Whether the loop runs** is a new `HyperionClient.IsRunning` flag. `Start`, `Stop` and the error paths use it in place of `Globals.Instance.Enabled`. Remove the auto-enable override.
  - **On startup, and on `Display_StateChanged(Normal)`,** call `Start()` only if `App.Configuration.Enabled`.
  - **Defaults:** the first-install default stays `"false"` (`HyperTizen_App.cs`). Change `Globals`' fallback to `"false"` too, so the defaults no longer conflict.
- [ ] **Step 3: Feed the frames.** In the capture loop, replace `Networking.SendImageAsync` with:
  ```csharp
  var (lower, upper) = ColourChain.LeftZones(captureResult.YData, captureResult.UVData, captureResult.Width, captureResult.Height);
  App.Bulbs?.SetFrame(lower, upper);
  ```
  Start `App.Bulbs.RunAsync` with the capture loop and cancel it with the capture loop.
- [ ] **Step 4: Warm white on stop, in one place only.** At the start of `HyperionClient.Stop()`, await `App.Bulbs.StopToWarmWhiteAsync(TimeSpan.FromSeconds(2))`.
  - Both the `enabled` false path and the `Display_StateChanged` `Off` branch already call `Stop()`, so that covers both.
  - `StopToWarmWhiteAsync` is a no-op when sync wasn't running.
  - Use the timing measured in Task 1 to confirm that 2 s fits inside the window when the network is still up. If it doesn't, shrink the deadline.
- [ ] **Step 5: Tuning from the UI.** In `SetConfiguration`, add cases for the four `bulb*` keys:
  - If `Tuning.TryParse` succeeds, set `Globals.Instance.Tuning` and fall through to the existing `Preference.Set`.
  - If it fails, log a warning and `return` before `Preference.Set`, so a bad value is never saved.

  Add a `bulbReset` key that deletes the four Preference keys and restores `Tuning.Default`.
- [ ] **Step 6: Build and install on the TV, with the Mac bridge stopped.** Use the existing `controls.html` on the Mac to send `SetConfig enabled true`, then `false`.

  Expected:
  - The bulbs follow the screen while enabled.
  - Disabling fades them to warm white.
  - Pressing the remote's power button also leaves them warm white.
  - Sending `bulbMinLevel` = `300` is logged as rejected, and `ReadConfig bulbMinLevel` still shows the old value.
- [ ] **Step 7: Commit.**
  ```bash
  git commit -m "feat: HyperTizen drives Matter bulbs directly"
  ```

### Task 12: TV app controls

**Files:**
- Modify: `HyperTizenUI/index.html`, `HyperTizenUI/js/wsClient.js`, `HyperTizenUI/main.css`
- Modify: `controls.html` (the same four fields plus reset, so they can be tested from the Mac before anything is pushed)

**Interfaces:**
- **Consumes:** the WebSocket events `ReadConfig` and `SetConfig`, with keys `enabled`, `bulbGamma`, `bulbSmoothMs`, `bulbMinLevel`, `bulbBlack` and `bulbReset` (Task 11)

- [ ] **Step 1: Add a "Bulb sync" section** to `index.html`:
  - the existing on/off control, relabelled "Bulb sync"
  - four number fields with these labels and steps:
    - "Dark-scene brightness (gamma)": step 0.1, 0.2–5
    - "Fade time (ms)": step 50, 0–5000
    - "Glow on black (1–254)": step 1
    - "Black threshold (0–254)": step 1
  - a "Reset to defaults" button

  All of it is reachable with the remote's arrow keys, using the existing focus handling at `index.html:195`. Left and right step the value, and each change sends `SetConfig` straight away. On connect, `ReadConfig` fills each field, falling back to the defaults when the key is missing.
- [ ] **Step 2: Add the same fields to `controls.html`.**
- [ ] **Step 3: Verify from the Mac.** Open `controls.html` in a browser, change Glow on black to 20 while the TV shows black, and watch the bulbs brighten. Restart the TV and check the value is still 20. Press Reset and check it goes back to 5.

  The TV app itself can only be checked once the owner chooses to push the fork. Note that in the final report.
- [ ] **Step 4: Commit.**
  ```bash
  git commit -m "feat: bulb sync controls in TV app"
  ```

### Task 13: Acceptance on the TV and cleanup

- [ ] **Step 1: Remove the temporary raw-sample log line.** Delete `PixelSampling raw` from `PixelSamplingCaptureMethod.cs`.
- [ ] **Step 2: Check the TV against the spec with the owner.** The Mac bridge is stopped and HyperHDR is quit. Check each of these:
  1. The bulbs follow the screen with no visible stepping, compared side by side with memory of the Mac setup.
  2. A black screen holds the colour at a dim glow.
  3. Near-black scenes don't flicker.
  4. Sync off leads to warm white.
  5. Remote power-off leads to warm white.
  6. After a cold boot with sync left on, opening the HyperTizen app once starts syncing. Autostart at boot isn't required; the owner accepted this.
     - Waking from standby with sync on resumes syncing.
     - Waking from standby or a cold boot with sync off leaves the bulbs alone.
     - Turning the TV off with sync off sends nothing, so bulbs that are off stay off.
  7. A changed setting survives a restart.
  8. The TV feels as responsive as before.

  Write pass or fail for each into the plan's "Probe results" section.
- [ ] **Step 3: Owner sign-off.** Only on that sign-off may the owner retire HyperHDR and `bridge.mjs` on the Mac. Don't do it unasked.
- [ ] **Step 4: Commit.**
  ```bash
  git commit -m "chore: remove temp logging; acceptance results"
  ```

## Probe results

Recorded 2026-11-01 from Task 1 (probe build on the real TV).

**Network (mDNS over IPv6)**
- Interface `wlan0`, IPv6 scope id 3. Replies come back unicast, so no multicast join and no port 5353 binding is needed.
- Both bulbs answered mDNS, each with an AAAA record, port 5540:
  - node 1: `fe80::dafc:92ff:fed2:91c5` / 192.168.0.10
  - node 2: `fe80::dafc:92ff:fed2:8796` / 192.168.0.11
- Operational instance names: `10DCB99FEC339D2C-0000000000000001` and `10DCB99FEC339D2C-0000000000000002`.
- Each device advertises several fabrics. Match the exact instance name, not just `_matter._tcp`.
- Node 2's reply is split over 2 datagrams. Merge datagrams from the same source.
- A third Matter device at `fe80::dafc:92ff:fed2:9459` also answers. Ignore it.

**Standby**
- All 50 "display-off" datagrams arrived over the full 5 s after `Off`. The network stays up for at least 5 s, and the service keeps running in standby.

**Wake**
- `Display Normal` restarted capture by itself.

**Cold boot**
- The service does NOT start by itself. The owner accepted opening the app once after a cold boot (spec goal 3 and Task 13 check 6 are updated accordingly).

## Acceptance results (2026-11-01, on the TV, with the owner)

Build `1574848`, plus the standalone control app (`bdc57ba`).

| Check | Result |
|---|---|
| 1. Bulbs follow the screen with no visible stepping | PASS: "follows smoothly" |
| 2. Black screen holds the colour at a dim glow | Not checked separately. Same code path as the tuned Mac bridge; MinLevel is now always reached |
| 3. Near-black scenes don't flicker | Not checked separately. Covered by check 1 viewing |
| 4. Sync off → warm white | PASS |
| 5. Remote power-off → warm white | PASS |
| 6. Cold boot, then open the app once → syncing | PASS after the fix below. Settings and `enabled` survived the power cycle |
| 7. A changed setting survives a restart | PASS. `bulbMinLevel` 20 kept; 300 rejected and not saved; reset restores the defaults |
| 8. TV responsiveness | No issue reported |
| Wake from standby resumes sync | PASS |

**Found during acceptance:** the pinwheel tile in My Apps was the HyperTizen *service* package. A service has no UI, so Samsung shows "Please wait a moment and try again" when it's opened. TizenBrew was never installed on this TV, so the control app never existed there.

**Fix (owner's choice: standalone, no push):** `HyperTizenUI` is now packaged as its own wgt (`6jwjAZfoVq.HyperTizenUI`), installed from the Mac with the HyperTizen signing profile. When opened, it starts the service itself (`js/service.js`) and talks to it on the TV's own LAN address (from `tizen.systeminfo`). A web app can't reach the service on `127.0.0.1`, and the CSP needs explicit ports (`ws://*:*`), since `ws://*` only allows port 80. Until 2026-11-01 13:28 neither worked, so the app's buttons did nothing. Sync only started because opening the app launched the service. The owner confirmed the app shows its controls and sync starts after the service was stopped.

To rebuild and reinstall the control app from `HyperTizenUI/`:
1. Copy the files to an empty folder.
2. Run `tizen package -t wgt -s HyperTizen -- <folder>`.
3. Run `tizen install -n <folder>/HyperTizen.wgt -s 192.168.0.4:26101`.

**Optional checks not run** (the owner skipped them):
- pause for about 10 minutes, then power off (tests the keepalive)
- power-cycle one bulb at the wall during a static scene, then power off (tests a dead session)

## Known follow-ups (parked during review)

- **N1.** On Normal→Off→Normal during Start's init, a wake can be lost and sync stays off until it's re-enabled. Fix: in `HyperionClient.StopInner`, move the `_isRunning` wait out of the `_cancellationTokenSource != null` branch.
- **N2.** Start's generation snapshot depends on thread-pool dispatch. A sub-millisecond Off→Normal→Off burst could leave capture running with the TV off. Fix: take the generation snapshot in the `Display_StateChanged` handler and pass it to `Start`.
- **Warm-white timing.** Warm white on a session that dies just before power-off uses about 3.9 s of the 4 s deadline, and the 2-transmission fail-fast hasn't been tried on the real bulbs. Run the two optional checks listed above.
- **Pre-existing in `HyperionClient`.** The Stop timeout disposes the token source under a live loop. A Stop during init races the capture-method cleanup. `BroadcastLog` calls `.Wait()` under a lock, which can stall the loops.
- **Minor polish.** The README still says "Start/Stop Capture". The gamma field allows only 1 decimal place. A refused Start still broadcasts "started".
