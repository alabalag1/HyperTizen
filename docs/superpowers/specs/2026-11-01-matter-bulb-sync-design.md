# Bulb sync on the TV: HyperTizen drives Matter bulbs directly

Status: draft for approval (2026-11-01)

## Intent

Today the ambient bulb sync needs a Mac running all the time:

Samsung S90D (HyperTizen pixel sampling) → HyperHDR on the Mac → `~/git/bulb-sync/bridge.mjs` → matter.js shell → two Ledvance Matter bulbs.

The owner wants the Mac out of the loop. The TV should sample the screen, turn it into bulb colours and send them to the bulbs itself. The only thing the owner wants to do day to day is switch sync on or off from the HyperTizen app on the TV. They also want the tuning values adjustable there, saved on the TV.

**Success** means everything described in this document works as described. The owner said explicitly that anything short of that counts as failure. In particular: the bulbs follow the screen as smoothly as the tuned Mac setup does today (`bridge.mjs` as of 2026-10-31), and they reliably return to warm white when sync stops.

## Goals

1. **Runs entirely on the TV.** HyperTizen (the C# Tizen service) does the whole pipeline. No HyperHDR, no Mac, no other computer at runtime.
2. **Same look as today.** The output matches the current tuned pipeline:
   - **Zones:** node 1 = upper-left zone, node 2 = lower-left zone.
   - **HyperHDR's colour processing:** gamma 1.5 per channel, all other adjustments neutral (see "Reference behaviour").
   - **The bridge's behaviour:**
     - smoothing in the send loop, run on elapsed time
     - each fade lasting as long as the gap before the next command
     - hue held when the screen is near-black
     - a continuous brightness curve with a floor
     - colour and brightness tracked separately
3. **Turn sync on or off from the TV app.** This reuses the app's existing `enabled` setting. The choice persists: if sync was on, it resumes by itself when the TV wakes from standby.

   After a cold boot (unplugged, power cut, firmware reboot), the owner opens the HyperTizen app once. The owner accepted this on 2026-11-01, after a probe showed the service doesn't start at boot.
4. **Tune from the TV app.** Four values with today's defaults:
   - Dark-scene curve (gamma), default 1
   - Fade time constant, default 300 ms
   - Glow on black (minimum level 1–254), default 5
   - Black threshold (brightest channel, 0–255), default 10

   Each change applies immediately and is saved on the TV. A "reset to defaults" button restores all four.
5. **Warm white when sync stops.** When the owner switches sync off, and when the TV turns off or goes to standby, both bulbs fade to 50% brightness (level 127) at 2700 K (≈370 mireds).
6. **One-time setup from the Mac.** A script on the Mac exports the existing pairing credentials from `~/.matter/shell-1` into a file the TV app loads. After that, the Mac is not needed.

## Non-goals

- Pairing or commissioning bulbs from the TV, or adding more bulbs or zones through the UI. New bulbs get paired on the Mac and the credentials re-exported.
- Support for bulbs on Thread, or anything other than these two Wi-Fi Matter bulbs.
- A general-purpose Matter library. Build only what these two bulbs need.
- Still sending to HyperHDR at the same time, or a UI choice between "HyperHDR mode" and "bulb mode".
- Controls beyond the on/off switch and the four tuning values.
- Sudden power loss (unplugged, breaker, outage). The bulbs may stay on their last colour. The owner accepted this.
- Keeping the HyperHDR output path. This is the owner's own fork, not meant for upstream, so the Matter output simply replaces it.

## Anti-goals (would make this a failure even if it technically works)

- The bulbs left on the last screen colour after sync stops or the TV turns off (sudden power loss excepted).
- Visible stepping or jitter: the problem fixed on the Mac on 2026-10-31 coming back.
- Black frames turning the bulbs white. Near-black frames flickering between random hues.
- Making the TV itself sluggish, or breaking the existing HyperTizen app and log viewer.
- Settings that reset on restart.

## Facts the builder needs

### Network and bulbs

- **Both bulbs are on Wi-Fi.** The Mac reaches them on IPv6 link-local addresses, operational port 5540:
  - node 1: `fe80::dafc:92ff:fed2:91c5`
  - node 2: `fe80::dafc:92ff:fed2:8796`

  Both addresses are derived from the bulbs' hardware (MAC) address, so they should be stable. They also advertise `_matter._tcp` over mDNS.
- **Commands the bridge sends today,** endpoint 1, `optionsMask`/`optionsOverride` 0:
  - ColorControl `MoveToHueAndSaturation`: hue and sat 0–254, `transitionTime` in tenths of a second
  - LevelControl `MoveToLevel`: level 1–254, `transitionTime`
- **Warm white needs one more command:** ColorControl `MoveToColorTemperature`.
- **Measured throughput** through the matter.js shell is about 16 commands per second, one at a time.

### Pairing credentials

The bulbs are paired to the matter.js shell's fabric, and are also still in Apple Home. Everything needed to act as that controller is in `~/.matter/shell-1`:

- **`fabrics.fabrics`:**
  - `fabricId`
  - `nodeId`
  - `rootNodeId`
  - `rootCert`
  - `operationalCert` (Matter TLV)
  - `keyPair` (P-256)
  - `identityProtectionKey`
  - `operationalIdentityProtectionKey`
  - `rootVendorId`
- **`certificates.*`:** the root CA material.

The bulbs' access control lists grant the shell's `nodeId`, so the TV must present that same identity (same operational cert and key). **Consequence:** the Mac bridge and the TV must not run at the same time.

These files hold private keys. Don't log them, and don't commit them to git.

### HyperTizen today

- **Runtime:** a .NET `service-application` (`tizen90` target) with internet privilege, using Tizen `Preference` for persisted settings (`enabled`, `rpcServer`, `diagnosticMode`).
- **Capture:** `Capture/PixelSamplingCaptureMethod.cs` samples 16 edge points. The left edge is x=0.05 at y=0.20, 0.40, 0.60, 0.80. Values are 10-bit (white ≈ 934, 922, 792).
- **Current output:** frames go to HyperHDR as flatbuffers (`Networking.cs`, `HyperionClient.cs`).
- **UI:** `HyperTizenUI` is a TizenBrew web module. It talks to the service over a control WebSocket on port 45677, using `ReadConfig`/`SetConfig` events backed by `Preference`, plus a log WebSocket on port 45678. TizenBrew installs the UI module from a GitHub repository.
- **Uncommitted work:** a `SemaphoreSlim` send gate in `Networking.cs`, and a temporary "PixelSampling raw" log line in `PixelSamplingCaptureMethod.cs`.

### Reference behaviour

The current behaviour, which the port must reproduce, is defined by two things.

**1. `~/git/bulb-sync/bridge.mjs`.** Read it. It has a self-check (`node bridge.mjs test`), and those cases should carry over as C# tests.

**2. HyperHDR's settings,** applied before the bridge:
- adjustment gamma 1.5; all other adjustments identity
- smoothing `HybridRgbInterpolator`, 150 ms, anti-flicker on, `updateFrequency` 50
- the LED layout: 2 LEDs on the left edge, LED 0 = lower-left, LED 1 = upper-left

What happens between the sampled pixels and the bridge today, from a read of `PixelSamplingCaptureMethod.cs` and HyperHDR's settings database:

- **On the TV:**
  - HyperTizen converts 10-bit values to 8-bit (`*255/1023`).
  - It paints the samples into a 64×48 image. The left-edge samples land in columns 0–2.
  - It encodes that image as NV12 with BT.2020 coefficients.
- **In HyperHDR:**
  - HyperHDR decodes the NV12 image through its colour lookup table (`lut_lin_tables.3d`).
  - It averages LED areas x 0–0.05, with y 0.5–1 for LED 0 and y 0–0.5 for LED 1, using the "advanced" mapping.
  - Then it applies gamma 1.5 and the smoothing.

`BLACK` and `MIN_LEVEL` were tuned against the result of this chain. The colour decode (full vs limited range in particular) therefore has to be reproduced from HyperHDR's source, not guessed.

**How "same look" is checked (designer's call):**
1. Before removing anything, record a reference trace from the live Mac pipeline: the sampled points and the bridge's commands, over a set of test clips (black, near-black, bright saturated colours, a dark scene, fast cuts).
2. The C# port must reproduce that trace closely.
3. Then the owner compares the bulbs by eye.

HyperHDR and `bridge.mjs` stay installed on the Mac until the owner signs off. The Mac bridge is stopped whenever the TV talks to the bulbs, because both use the same controller identity.

## Decisions made (by the owner)

- Sync on/off lives in the TV app. It persists and starts automatically.
- Today's tuning values are the defaults. They can be changed and saved on the TV, if that isn't hard. A small numeric settings section counts as "not hard".
- Stop behaviour: 50% brightness, 2700 K, on both switch-off and TV-off.
- Using the Mac once to copy the pairing keys is fine.
- Success = everything here works as described.
- Power cuts are out of scope.
- This is for the owner's own fork. Nothing gets pushed to GitHub for now.

  Because TizenBrew installs `HyperTizenUI` from GitHub, the TV app's new controls can't reach the TV until the owner chooses to push. Until then, test the same control WebSocket events from `controls.html` in a browser on the Mac. The service package (`.tpk`) is installed directly, as today.

## Calls made by the designer (the owner can override them)

- **Same controller identity.** Reuse the shell's controller identity rather than minting a new one. A new one would need its own access entries on the bulbs.
- **Plain controls.** Plain numeric fields stepped with the remote, plus "reset to defaults".
- **Settings names** in `Preference`, alongside the existing keys: `bulbGamma`, `bulbSmoothMs`, `bulbMinLevel`, `bulbBlack`.
- **Credentials on the TV:** delivered as one JSON file pushed to the app's data directory during setup.
- **Bulbs are switched on when sync uses them.**
  - Starting sync turns the bulbs on, even if they were switched off in Apple Home: use `MoveToLevelWithOnOff`, not `MoveToLevel`.
  - The warm-white fade on stop also leaves them on.
  - This adds `MoveToLevelWithOnOff` to the command list.

## Left to the builder

- **Code structure.** Keep the Matter client small and separable, so it can be developed and tested with `dotnet` on the Mac against the real bulbs before going near the TV (.NET 10 SDK is installed on the Mac).
- **The Matter subset.** Implement only:
  - CASE session establishment, with optional resumption
  - message encryption and counters
  - MRP acks and retransmits
  - Interaction Model `InvokeRequest` for the three commands

  Crypto choices are part of this. `AesCcm` on Tizen depends on the platform's OpenSSL, so a managed fallback such as BouncyCastle may be needed.
- **Finding the bulbs.** mDNS discovery versus stored link-local addresses with the TV's interface scope, or both.
- **Concurrency and pacing** across the two bulbs.
- **TV-off detection.** How HyperTizen learns the TV is turning off or going to standby while the network still works. See risk 2.

## Risks to check first, on the real TV, before building the rest

1. **Can the TV reach the bulbs?** It needs IPv6 link-local and/or mDNS from the HyperTizen service to the bulbs on port 5540. If it can't, the approach fails.
2. **Is there a warning before standby?** If HyperTizen gets no shutdown or standby notice while the network is still up, warm white on TV-off needs another trigger. Goal 5 isn't met until this works reliably.
3. **Crypto on Tizen.** P-256 ECDH and ECDSA, HKDF-SHA256 and AES-CCM must all be available.
4. **Throughput.** Talking to the bulbs directly needs to be at least as fast as the shell's ~16 commands per second, without starving pixel sampling.
5. **Autostart.** Today only the UI module starts the service (`HyperTizenUI/js/service.js` loops on `tizen.application.launch`). That makes goal 3 depend on TizenBrew running the module after a cold boot and after standby. Check this on the TV.

   Also, `HyperionClient.cs` currently turns capture on at startup whatever the `enabled` setting says, and the `enabled` defaults conflict between files. Both need fixing.
6. **Pixel-sampling flicker.** The README says it flickers. Today HyperHDR's smoothing and the bridge's smoothing hide it. With HyperHDR gone, the TV has to provide all of that smoothing.
