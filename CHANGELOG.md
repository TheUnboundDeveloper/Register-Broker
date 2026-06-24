# Changelog

All notable changes to Register Broker are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[Semantic Versioning](https://semver.org/).

Versioning note: the **release version** (this file, the `BrokerSensorBridge`
assembly version, git tags) and the **pipe protocol version** (currently `2`, sent
in the client hello — see `docs/CLIENT-PROTOCOL.md` §8) are independent. New sensors
and additive ops do not bump the protocol version.

## [Unreleased]

### Added — read-only GPU sensors (AMD · NVIDIA · Intel; user-mode, opt-in)

- **GPU telemetry is now served as `gpu.*` sensors** over the normal `sensor.list` /
  `sensor.read` / `sensor.readall` ops, non-admin, alongside the CPU/board sensors:
  `gpu.temp` (edge), `gpu.temp.hotspot`, `gpu.temp.mem`, `gpu.fan` (RPM), `gpu.fan.pct`,
  `gpu.power` (W), `gpu.clock.core`, `gpu.clock.mem` (MHz), `gpu.usage` (%). The Reference
  Console's previously-empty **GPU Temperature** card auto-populates (no Console change).
- **Broker-only, no driver change / re-sign.** A discrete GPU's thermals are not an SMBus
  device, so this is a **user-mode vendor-API backend** in the broker (minimal P/Invoke, no new
  NuGet dep), not a kernel backend. Same reduced-assurance shape as the USB-HID RGB path, but
  **strictly read-only** (no GPU write op exists). One `IGpuSensorProvider` seam, one `IsUserMode`
  `ChannelBackendDef` (prefix `gpu.`); `GpuSensorProvider.TryCreate` probes AMD → NVIDIA → Intel
  and uses whichever vendor library answers, so the right provider is picked automatically.
- **Three vendors implemented:**
  - **AMD** (`Gpu/AdlInterop.cs`, ADL PMLog via `atiadlxx.dll`) — **HW-validated on a Radeon
    RX 7900 XTX** (RDNA3): edge/hotspot/mem temps, fan, clocks, utilization read real, physically
    consistent values (PMLog indices anchored on hardware by `BUS_LANES = 16`). `gpu.power`/
    `ASIC_POWER` isn't populated by the current RDNA3 driver's PMLog set → reports not-available
    rather than a guessed value.
  - **NVIDIA** (`Gpu/NvmlGpuProvider.cs`, NVML via `nvml.dll`) — temp, fan %, power, core/mem
    clock, utilization. **Built, HW-unvalidated** (no NVIDIA GPU on the dev box).
  - **Intel** (`Gpu/LevelZeroGpuProvider.cs`, Level Zero **Sysman** via `ze_loader.dll`) — edge +
    memory temp, GPU + memory clock, fan RPM (power/utilization are counter deltas, deferred).
    **Built, HW-unvalidated**; every call degrades gracefully (a wrong descriptor tag hides one
    metric, never crashes).
- **Opt-in, off by default:** `AllowGpuSensors` (default `false`), `--allow-gpu-sensors`, or
  `Install-SensorBrokerService.ps1 -WithGpuSensors`. Calibration labels added; `DecoderRegistry`
  cites the vendor sources; 8 new selftest gates (`SELFTEST PASS`). Session-0 (LocalSystem)
  validation for the GPU path still pending. Details: `docs/GPU-SENSOR-SUPPORT.md`.

## [1.6.0] — 2026-06-22 — device-aware brick-guard + RGB device expansion + USB-HID RGB on by default

> Building out RGB device coverage ported from the public hardware register maps (facts only —
> our own code; Gigabyte/ITE stay retired).
> **Status at tagging:** broker selftest green; the new USB-HID peripherals + SMBus RGB families are
> HW-UNVALIDATED on this dev box (which owns none of the hardware). The device-aware kernel
> brick-guard requires an elevated WDK rebuild + re-sign + redeploy to go live — **not yet
> redeployed**; the live `publish\` services run the prior build until then.

### Changed — USB-HID RGB transport now on by default (`AllowHidRgb`)

- **`AllowHidRgb` now defaults to `true`** (was `false`). The USB-HID RGB transport — MSI Mystic
  Light motherboard headers plus all the board-independent peripherals (Logitech, SteelSeries,
  Corsair, HyperX, Razer, …) — is enabled out of the box, since nearly every host running RGB
  control wants it and a fresh `-WithRgbControl` install otherwise surfaced only the DRAM zones.
  The C# default (`BridgeConfig.AllowHidRgb`) and the shipped `appsettings.json` (source +
  `publish\`) all now carry `true`. Set `"AllowHidRgb": false` for the previous stricter posture.
- **Posture note:** this is a deliberate default-assurance change. The transport is still
  user-mode and reduced-assurance (no kernel brick-guard; bounded by the broker's baked report
  builder + a per-device USB id/usage pin) and its blast radius is the RGB controller only.
  `SECURITY.md`, `ARCHITECTURE.md`, `CLIENT-PROTOCOL.md`, `REFERENCE.md`, the user/RGB guides and
  the device-coverage doc were updated to match. No protocol-version change; broker behaviour for
  an already-`AllowHidRgb`-enabled install is unchanged.

### Added — device-aware kernel brick-guard (the architectural keystone)

- **The SMBus write brick-guard is now per-device-class.** A bounded write names a
  `BROKER_RGB_WRITE_CLASS` (appended `DeviceClass` field, zero-fills to the legacy ENE windows for
  old callers); the kernel `g_RgbWriteProfiles` table (`Smbus.c`) permits ONLY that class's address
  window(s). This is strictly TIGHTER than the previous single shared window — a DRAM-RGB write can
  no longer reach a GPU/ENE address and vice-versa, and an unknown class is refused outright. The
  per-device maps live in SIGNED KERNEL CODE, never data. Broker mirror (`RgbCatalog.SmbusClassAllows`)
  refuses an out-of-window zone at load. New `RgbWriteClass` (C# mirror) + `RgbZone.WriteClass` +
  `RgbTransport.Smbus`. Driver files touched: `inc/SmbusBrokerProtocol.h`, `Smbus.c`, `Driver.c`.

### Added — SMBus DRAM RGB families (facts ported from public register maps)

Each family gets its own tightly-scoped `BROKER_RGB_WRITE_CLASS` + kernel window (overlaps across
classes are fine — the guard only matches the named class). All detection matches a positive
signature **at the RGB address** (never via SPD, which the kernel refuses to read), preserving the
anti-brick SPD guard. Board-independent detection in `RgbRegistry.DetectSmbusDram` (first family to
claim an address wins; read-only signatures checked before any identify-writes). HW-UNVALIDATED.

- **Corsair Vengeance RGB Pro / Dominator** (newer per-LED, `corsair.dram{n}`) — `CorsairDram` class
  (0x58–0x5F): DIRECT `[ledCount, RGB…, CRC-8]` block frame, device-info VID/PID/protocol probe,
  per-model LED table.
- **Corsair Vengeance** (original single-color, `corsair.ven{n}`) — `CorsairVenDram` class (0x58–0x5F
  only; the device's 0x18–0x1F alias is **refused** because it overlaps the JC42 DIMM-temp range).
- **Crucial Ballistix** (`crucial.dram{n}`, 8 LEDs) — `CrucialDram` class (0x20–0x27 + 0x39–0x3C):
  ENE-style indirection, planar R/G/B block writes, ramp + "Micron" signature.
- **T-Force Xtreem** (`tforce.dram{n}`, 15 LEDs) — `XtreemDram` class (0x70–0x78 + 0x39–0x3D): ENE
  controller, folded-strip LED remap, R-B-G order, ramp signature.

Device classes/windows are also **reserved** for **Kingston Fury** (`FuryDram`, 0x58–0x67) and
**HyperX** / **Patriot Viper** (`HyperXDram` 0x27 / `ViperDram` 0x77). HyperX and Patriot are
**deliberately not auto-detected**: their identity lives in the SPD bytes the kernel refuses to read,
so a safe detector would conflict with the SPD-refusal guardrail (documented gap, not an oversight).

13 new RGB selftest gates total (device-class isolation, CRC-8, packet/planar layout, folded-strip
remap, the JC42-alias refusal, per-family windows).

### Added — motherboard SMBus RGB (host SMBus, DMI-vendor gated)

- **ASRock Polychrome / ASR RGB** (`mb.asrock`, `AsrockMb` class, 0x6A): one device, three firmware
  variants auto-detected from the major byte at reg 0x00 (ASR 0x01 / Polychrome V1 0x02 / V2 0x03);
  drives a solid whole-board STATIC color across the populated zones (read from the 0x33 config).
- **EVGA ACX30** (`mb.evga`, `EvgaMb` class, 0x28): unlock/lock-bracketed byte writes, with NVRAM-wear
  dedup (the controller persists every update to flash).

Both are **gated on the board's DMI manufacturer** so they are never probed at 0x6A/0x28 on other
boards. 2 new selftest gates.

### Scope decisions (architecture, not backlog)

- **GPU SMBus RGB (~20 controllers) is OUT of scope.** Drives GPU RGB over the **GPU's own
  I2C bus** (via the display driver / NVAPI; `REGISTER_I2C_PCI_DETECTOR`, `is_amd_gpu_i2c_bus`), not
  the motherboard SMBus host controller this driver implements. Supporting it would mean a whole new
  GPU-I2C/NVAPI subsystem (likely GPU MMIO) — breaking the "driver stays narrow / no physical memory"
  guardrails. Dropped as an architecture mismatch.
- **MSI Super-I/O RGB (NCT6795/6797) is DEFERRED as a security-sensitive driver change, not shipped
  blind.** It writes the Nuvoton Super-I/O **config space over port I/O** (logical-device select 0x07,
  RGB logdev 0x12, regs 0xE0–0xFF). The `0x07` selector is the danger surface — the *same* primitive
  reaches the fan/voltage/temperature logical devices — so a safe kernel guard must police the *value*
  written to 0x07 (restrict to {0x09, 0x12}), not just register indices. It is also NCT6795/6797-only
  and the dev box (MS-7C92) isn't in its board table (it uses the HID path), so it cannot be HW-
  validated here. The full register map + the required value-gating are captured for a future
  deliberate, validated, re-signed driver change (mirrors the inert NCT6687 EC RGB placeholder).

Still pending: Kingston Fury controller, MSI SIO-RGB driver bring-up, and the USB-HID peripheral maps.

### Added — USB-HID peripherals (user-mode, gated by `AllowHidRgb` — now on by default)

- **HID output-report transport** (`HidDevice.SetOutputReport` → `HidD_SetOutputReport`): like the
  existing feature-report path it goes through an IOCTL, so it works on the zero-access handle the
  OS-owned input collections force — no WriteFile/elevated access. This unlocks the large class of
  peripherals that use output reports (`hid_write`) rather than feature reports (the prior Razer/MSI
  paths). No new dependency.
- **AMD Wraith Prism CPU cooler** (`amd.wraithprism`, VID 0x2516 / PID 0x0051, vendor collection on
  interface 1): 17 LEDs (Logo, Fan, 15-LED ring), DIRECT per-LED mode (enable 0x41/0x80/0x03 + apply
  0x51/0x28, then 0xC0 `{id,R,G,B}` packets). Board-independent (matched by USB id), reduced assurance
  (user-mode, no kernel guard). New `RgbZoneKind.Cooler`. 3 selftest gates on the packet builders.

- **Logitech G-series** (VID 0x046D): **G203 Lightsync** mouse (`logi.g203`, 3 LEDs, DIRECT writes) +
  **G810 / G910 / G610 / G512 / G Pro keyboards** (`logi.g810`/`g910`/`gpro`/…, whole-keyboard SOLID
  color via the firmware STATIC mode — no per-key table needed). 2 selftest gates.
- **SteelSeries mice** (VID 0x1038): **Rival 3** (`ss.rival3`, cmd 0x05) + **Aerox 3 / Aerox 5**
  (`ss.aerox3`/`ss.aerox5`, cmd 0x21, zone-based, software-mode enable 0x2D). 2 selftest gates.
- **HID input-report read** (`HidDevice.GetInputReport` → `HidD_GetInputReport`): the request/response
  read path some vendor collections need. Unlocked Corsair iCUE V2.
- **Corsair iCUE V2** (VID 0x1B1C, iface 1, usage 0xFF42) — **14 fixed-LED-count devices**: 11 mice
  (Dark Core SE/Pro SE, Harpoon, Ironclaw, Katar Pro/V2/XT, M55, M65 Ultra/Wireless, M75), 2 mousepads
  (MM700, MM700 3XL), and the K55 keyboard. DIRECT streaming (render-mode SW → lighting-control →
  Start/Stop-Transaction-bracketed BLK_W1/BLK_WN), with both color encodings (CTRL2 triplet default,
  CTRL1 planar via a best-effort init probe). The **matrix keyboards (K60/K70/K95/K100) are excluded**
  — their LED count is keyboard-layout-dependent (needs a runtime ANSI/ISO query). New
  `RgbZoneKind.Mousepad`. 3 selftest gates (both buffer encodings + BLK framing).

Full coverage rationale — what's in, what's out, and the change each exclusion would need — is in
**`docs/RGB-DEVICE-COVERAGE.md`**. The large remaining HID set (per-key keyboards, the un-specced
long tail) is catalogued there with reasons.

### Added — more HID table-porting (zone-based, fixed geometry)

- **SteelSeries** keyboards/mice: **Apex 3** 8-zone (`ss.apex3tkl`) + T-zone (`ss.apex3`); **Apex OG/350**
  5-zone RGBA (`ss.apexog`); **Sensei / Rival 310** 2-zone (`ss.sensei`).
- **NZXT Lift** mouse (`nzxt.lift`, 6 LEDs, output report, remapped slot order).
- **Roccat** mice: **Kone Aimo** (`roccat.koneaimo`, 11 LEDs, feature report) + **Kone Pro**
  (`roccat.konepro`, 2 LEDs, direct feature + control enable).

Deferred (in the coverage doc): **SteelSeries Rival 600** (anomalous report-id framing — no 0x00
prefix), **NZXT Hue 2** channel controllers (LED count discovered at runtime, not fixed), **Roccat
Vulcan** keyboards (per-key, layout-dependent, brick-sensitive two-interface init), and the SteelSeries
Apex per-key keyboards (key tables). 7 new selftest gates (now 65 RGB gates total, 0 failures).

### Added — long-tail HID (clean, fixed-geometry)

- **Redragon mice** (`redragon.mouse`, VID 0x04D9, 10 PIDs, 1 LED): register-write `0xF3`@0x0449 + apply
  `0xF1`, flash-write de-duplicated.
- **Cooler Master MP750** mousepad (`cm.mp750`, 1 LED, single static output packet).

New deferrals catalogued in the coverage doc, each with its enabling change:
- **HyperX** peripherals (Pulsefire mice, Alloy keyboards) + **Thermaltake Riing Quad** — require a
  **keepalive refresh thread** (firmware reverts to its stored effect in 50 ms–3 s without a periodic
  resend; the broker has no such mechanism). This is the single biggest class-wide unlock.
- **Sinowealth / Glorious** (Model O/D, ZET, Genesis) — almost all do a **flash read-modify-write** per
  color (wear), with **PID collisions** and an explicit upstream **brick warning** (PIDs 0x0016/0x0049
  are reused by Redragon and bricked them). Against the no-brick posture without a stronger ID gate.
- **Thermaltake Riing** (classic/Trio) — LED count per channel is **user-configured**, not fixed, and
  the detector pins no interface/usage (needs `--hid-scan`).
- **Cooler Master** mice/GPU/keyboards — intricate flow-control/apply sequences (several offsets
  agent-flagged for re-confirmation) and per-model key tables.

2 new selftest gates (now 67 RGB gates total, 0 failures).

### Added — keepalive refresh loop (infrastructure) + the HyperX family

- **Keepalive infrastructure.** `IRgbController` gained `KeepaliveIntervalMs` (default 0 = the device
  holds its color) and `Refresh()` (default no-op) as **default interface methods** — zero churn on
  existing controllers. The control service runs one background refresh loop (`RgbKeepaliveLoopAsync`)
  that re-sends each keepalive device's last frame on its interval. Keepalive devices are always
  volatile (no flash write), so refreshing is wear-free; `Refresh()` is serialized with `rgb.set`
  inside each controller's lock. **Corsair K55** retrofitted (30 s interval).
- **HyperX peripherals** (VID 0x0951 Kingston / 0x03F0 HP), all keepalive-driven:
  - **Mice:** Pulsefire FPS Pro/Core (1 LED), Raid (2), Haste (1), Dart (2, holds — no keepalive),
    Surge (33, planar strip + logo).
  - **Keyboards:** Alloy FPS RGB (106, channel-scatter), Alloy Origins (107) / Origins 60 (71) /
    Origins 65 (77) / Elite 2 (128) (4-byte-group streaming with skip-index blanks), Eve 1800 (10
    zones) and Origins 2 65 (74) (report-id 0x44 path).
  - **Deferred:** Alloy Elite (uncertain extended-LED scatter tables) and Alloy Origins Core (runtime
    keyboard-layout query).

8 new selftest gates (now **75 RGB gates total, 0 failures**).

### Added — more long-tail HID (fixed geometry)

- **SteelSeries Rival 100 / Rival 300** legacy mice (`ss.rival100` 1 zone / `ss.rival300` 2 zones,
  DIRECT-effect latch + per-zone color). (Rival 600 stays excluded — anomalous report framing.)
- **Cooler Master mice** MM530 (3 LEDs) / MM711 / MM720 / MM730 (2 LEDs) (`cm.mm5xx`/`cm.mm7xx`):
  init (0x41/0x80) + DIRECT packet (seed 0x51/0xA8) with family-specific zone byte-offsets.

### Decided — MSI Super-I/O RGB removed from scope

Confirmed it is **not duplicative of, nor additive to, the USB-HID Mystic Light path** the dev box
uses: older MSI boards wire RGB to the Nuvoton **Super-I/O** (NCT6795/6797) and have **no** Mystic
Light HID controller; newer boards (incl. MS-7C92, which has an NCT6687D, not a 6795/6797) use the HID
controller and don't wire RGB to the Super-I/O. The two are **mutually exclusive by board generation**,
and the Super-I/O path is **less** capable (4-bit/channel, single whole-board color, no per-LED). It
can't run or be validated on the dev hardware and benefits only third-party older-MSI boards, so it is
**dropped from the project** by decision (it was never an architecture or feasibility blocker).

2 new selftest gates (now **77 RGB gates total, 0 failures**).

### Added — per-key keyboard unlock, ROG Ally, Lian Li hub

- **Per-key keyboards (structural unlock).** `IRgbController` keyboards can now drive true per-key
  color. Implemented for **Logitech G810 / G910 / G610 / G512 / G Pro** (upgraded from solid color):
  the per-key (zone, HID-usage) table is generated per layout (117/117/94 LEDs); LEDs stream 14 per
  report-0x12 frame, flushed on zone change, then a report-0x11 commit latches. The SteelSeries Apex
  (112/111-key) and Asus TUF/Strix per-key tables are **extracted and queued** (mechanical follow-up).
- **ASUS ROG Ally / Ally X** (`asus.rogally`, 4 LEDs, feature reports with the "ASUS Tech.Inc." init).
- **Lian Li Uni Hub SL V2 / AL V2 / SL V2 v0.5** (`lianli.unihub.slv2`, VID 0x0CF2): per-channel
  START/COLOR/COMMIT, R,B,G wire order, the R+B+G>460 power limiter. A hub's LED count is
  **user-configured** (fans × 16), not device-read, so this drives a **1-fan-per-channel baseline**
  (safe, never overruns); multi-fan + the AL/SL/SL-Infinity variants are queued (need a per-channel
  fan-count config). 6 new selftest gates (now **81 RGB gates total, 0 failures**).

### Verified — driver compiles clean (kernel changes)

The kernel `.c` changes from the device-aware brick-guard work (`inc/SmbusBrokerProtocol.h`, `Smbus.c`,
`Driver.c`) were **compile-checked under `/W4 /WX`** with the WDK (SDK 10.0.26100, KMDF 1.35) — clean,
0 warnings. The full rebuild + test-sign + redeploy is an **elevated** operation (it must stop the
link-locking `BrokerSmbus` service) and has not been run yet; the live `publish\` services are still
the prior build until an elevated `Build-All.ps1 -Bridge -Driver` + `Install-SensorBrokerService.ps1`.

### Decided — MSI Super-I/O RGB removed from scope

## [1.5.1] — 2026-06-20

### Changed — whole repository now targets .NET 10

- **The broker (`BrokerSensorBridge`) and the `RgbAudioReactive` consumer moved from
  `net8.0-windows` to `net10.0-windows`**, matching the Reference Console (already .NET 10). The
  repository now builds entirely on the **.NET 10 SDK** — the .NET 8 SDK/runtime is no longer
  required and can be uninstalled. `Microsoft.Extensions.Hosting.WindowsServices` bumped to
  `10.0.9` to match. No source/behavioural change: the broker selftest passes unchanged and the
  pipe protocol version is untouched.
- Build scripts (`Build-BrokerSensorBridge.ps1` default TFM, `Sign-BrokerSensorBridge.ps1`
  fallback paths) and CI/release/CodeQL workflows (`setup-dotnet` → `10.0.x`) updated to match.
- **Deployed:** the `publish\` broker has been re-published on .NET 10 and re-signed with the
  `Broker Test Cert` (same thumbprint as before; republish wiped the prior Authenticode
  signature). Selftest — including the signature gates — passes against the redeployed binary.
  The .NET 8 SDK/runtime can now be removed.

## [1.5.0] — 2026-06-19

### Added — Fan PWM duty telemetry (read-only) [NCT668x]

- **The broker now serves each NCT668x fan header's current PWM duty** as read-only sensors
  `nct6687d.pwm.0`–`.7` (`%`), decoded from the EC duty byte at `0x160 + i`
  (`raw/255·100`, `NctPwmPercent`; ported from mainline `nct6683.c` / Fred78290/nct6687d). This
  is **telemetry only — there is no fan-write path anywhere in the driver**: reading the duty
  cannot change a fan's speed or mode. Driving fans (speed/function) would be a separate,
  deliberately thermal-safety-gated future step.
- Rides the **existing** Super-I/O read IOCTL as a new `kind` — **no new IOCTL, no new
  capability bit, no protocol-version change.** Kernel adds one read-only branch (`SuperioNct.c`)
  gated by the same `CAP_SUPERIO`; the NCT6775 bank-select family does not expose PWM yet.
- Broker: new `nct6687d.pwm.*` channels (`ChannelRegistry`), `NctPwmPercent` decode, default
  labels `Fan {i} PWM`, and selftest gates (EC chip lights PWM; bank-select family does not;
  `0xFF → 100 %`). Selftest green; driver compiles `/W4 /WX` clean.
- **HW-validated on the dev box (NCT6687D):** `nct6687d.fan.3` = 4137 RPM at `nct6687d.pwm.3`
  = 27 % (the lone populated SIO header — a small M.2 fan), read non-admin through the broker;
  unpopulated headers report their configured default duty. This is the broker+driver change
  that bumps the release to **1.5.0**.

### Added — Reference Console + demonstrator tooling (ships in 1.5.0)

These changes add a first-party **test/validation interface** and demonstrator tooling to the
repository. They do not themselves change the broker or kernel driver (the GUI is a *consumer*
and is not versioned separately yet) — they ride along in the 1.5.0 release, whose version bump
is driven by the fan-PWM telemetry feature above.

### Added — Reference Console, the first-party demonstrator GUI (now in the repo)

- **The Reference Console is now committed** under `Test_GUI/ReferenceConsole/` (previously a
  gitignored sandbox). It is a first-party, **non-admin desktop application** (.NET 10 +
  Avalonia 12) that drives the entire framework through nothing but the public pipe protocol —
  the test/validation interface that shows the broker model is functional, safe, and effective
  end to end.
  - **Sensors tab** — live `sensor.readall` polling; the session reports `elevated=False`.
  - **RGB tab** — a client-side effect engine (Static, Temperature-reactive, Rainbow,
    Breathing, Comet, Twinkle, Aurora, Manual per-LED, Audio Spectrum) that renders frames in
    the client and streams them through the existing `rgb.set` op. **No broker or driver
    change** — the broker stays a pure transport; consumers do the animation.
  - **Diagnostics tab** — granted scopes, ping/latency, raw protocol log.
  - `Broker.Client/` is a portable, dependency-free port of the wire format; the UI references
    Avalonia + NAudio (audio mode) via NuGet (both MIT). Built separately from the broker, though
    the whole repository now targets **.NET 10** (see below).
- **`RgbAudioReactive/` is now committed too** — a standalone, **non-admin** .NET 10 console
  consumer (NAudio, MIT) that captures microphone or system-output (loopback) audio and streams
  per-LED frames to every zone `rgb.list` reports, over the public control pipe. Like the
  console's Audio Spectrum effect, it lives outside the broker (animation is the consumer's job)
  and drives the lights exactly as any third-party app would. So a downloader gets the GUI, the
  full effect engine, **and** a ready-to-run music-sync tool.
- **Docs:** new [`docs/REFERENCE-CONSOLE.md`](docs/REFERENCE-CONSOLE.md); the console (and the
  RgbAudioReactive tool) are now featured in the README, the User Guide, ARCHITECTURE (Layer 4),
  and INTEGRATING. Build output (`bin/`, `obj/`) remains ignored. No change to the broker,
  driver, or any shipped binary — this is a repository/tooling milestone.

### Added — Reference Console: session persistence, branding, and effect polish

- **Settings persistence.** The console now remembers your setup between runs. The global
  FPS / sensor-refresh / poll-interval knobs and, per RGB device, the assigned effect, every
  parameter value, drive state, gradient stops, and hand-painted per-LED colours are saved to
  `%APPDATA%\RegisterBroker\ReferenceConsole\settings.json` on close/disconnect and restored on
  the next **Connect**. UI convenience only — it stores no addresses, scopes, or anything the
  broker acts on.
- **App icon / branding.** An **RC** logo (a dark "chip" badge with a red→green→blue gradient
  monogram) is embedded in the executable and set as the window icon, so it appears in the title
  bar and in the taskbar while running.
- **Footer note** clarifies the console is a test/validation interface and that the core project
  is the framework providing the data.

### Fixed — Reference Console effects

- **Audio "Level" mode no longer pegs the strip at full.** Level is now derived as the RMS of the
  smoothed spectrum bands (the same perceptually-tuned signal Spectrum mode uses) rather than the
  near-saturated raw loudness, so the gain / noise-floor / smoothing knobs behave identically in
  both modes and the lights actually track loudness.
- **Temperature effect fades between colours instead of snapping.** The sensor updates coarsely
  and slowly, which made the colour switch like a relay; the rendered colour now eases toward the
  target with a new **Fade (s)** knob (default 0.35 s — soft but quick; 0 = instant).
- **Colour quick-picks everywhere.** The Temperature gradient-stop editor gained the same basic
  colour swatches the other effects' colour pickers already had.

(All console-only — no broker, driver, or shipped-binary change; broker stays **1.4.1**.)

## [1.4.1] — 2026-06-18

### Fixed — stale control session could block reconnection (long-lived clients silently stopped)

- **Bug: a control-session timeout on a still-open connection prevented the client from
  reconnecting.** Sessions on the `BrokerControl` pipe carried a hard 10-minute lifetime that was
  never refreshed by activity. When it lapsed, the broker rejected each subsequent op with a
  `deny` frame **but left the pipe open** — so a long-lived consumer (e.g. an RGB application
  holding one control connection) kept replaying its now-dead session token indefinitely and never
  re-authenticated. The symptom was control going silently dead after a while, recoverable only by
  restarting the consumer process; sensors and hardware writes were otherwise healthy.
- **Fix 1 — sliding expiry:** a session's lifetime is now refreshed on every authorized op and the
  window raised to 24 hours, so a continuously-used connection never expires; only one that goes
  genuinely idle (no op for 24h) ages out and is pruned.
- **Fix 2 — close-on-stale-session:** an op carrying an unknown or expired session token now causes
  the broker to **close the connection** instead of replying `deny`. The client sees an ordinary
  transport drop, and its existing reconnect path re-authenticates and resends transparently — the
  reconnection contract lives entirely on the broker, with no special-case handling required of any
  consumer.
- Broker-only change (no driver/kernel change, no re-sign). Selftest adds two gates: the session
  TTL is the 24h sliding window, and a stale/expired token closes the connection rather than
  replying.

## [1.4.0] — 2026-06-18

### Added — AMD CPU core & SoC voltage (SVI2 telemetry)

- **Two new SMU sensors: `smu.cpu.vcore` (VDDCR_CPU) and `smu.soc.voltage` (VDDCR_SOC)**, read
  from the CPU's SVI2 telemetry-plane registers over the existing named-SMN read path — **no new
  IOCTL, no SMU mailbox, and no physical-memory access**. They are ordinary `sensor.readall` /
  `sensor.read` channels served non-admin through the broker.
- Served only on CPU models whose telemetry-plane addresses are known and identical (AMD Matisse
  17h/0x71 and Vermeer 19h/0x21, e.g. the Ryzen 5000 desktop line); on any other model the rails
  are simply **absent** (the kernel returns NotImplemented and the broker omits them) — never a
  wrong-register read. Validated on Ryzen 7 5800X3D (Vermeer); Matisse is built but unvalidated.
- Register facts (SVI plane SMN addresses, base `0x0005A000`; decode `V = 1.550 − 0.00625·code`)
  ported from the **zenpower** driver (GPL-2.0) and cross-checked against the Linux k10temp
  voltage patch — provenance in `THIRD-PARTY-NOTICES.md`. The `--smu-read` DevProbes bring-up tool
  now prints the SVI voltages; selftest gains the decode/clamp regression and a `smu.cpu.vcore`
  scope gate.

### Not included (by design)

- **CPU package power (PPT) and per-core/effective clocks are intentionally not exposed.** Unlike
  voltages, those live only in the SMU **PM table**, which requires writing the SMU mailbox and
  **reading a physical-memory region the SMU DMAs the table into** — outside Register Broker's
  bounded, no-physical-memory driver design. See the README note. This may be revisited (as a
  separate, opt-in driver) if it is frequently requested.

## [1.3.0] — 2026-06-17

### Added — MSI Mystic Light per-LED DIRECT mode for addressable headers (JRAINBOW)

- **Addressable headers (`RgbZoneKind.MbArgb`) are now driven per-LED in DIRECT mode** (HID report
  `0x53`, 725-byte frame) instead of the 185-byte sync-static packet. RGB is written literally per LED
  with the firmware effect engine disabled, so **brightness is linear** (fixes the brightness "fold"
  /double-ramp on the 185-byte path) and **real per-LED color/gradients/effects** work — `SetLeds` is
  no longer collapsed to a single color. Validated on MSI MPG B550I (MS-7C92): solid colors confirmed
  on JRAINBOW.
- Direct mode is engaged with a faithful reconstruction of the public protocol's enable packet — a
  fully-formed 185-byte `0x52` report whose `on_board_led` zone carries the device-wide per-LED master
  flags — then per-zone `0x53` frames stream the colors (`MysticLightHidController.BuildDirectModeEnable`
  / `BuildPerLedFrame`). Ported as facts from the public Mystic Light protocol; **broker-only, no
  kernel driver change or re-sign** (the USB-HID path is user-mode).
- New per-zone selectors `RgbZone.HidPerLedHdr1` / `HidPerLedHdr2` in the signed board map
  (JRAINBOW1 = 4/0). The MSI B550I `mb.argb0` zone is relabeled **"MSI JRAINBOW"**.
- New DevProbes bring-up tool `--mystic-perled` (find a board's per-zone selector on the strip), and
  selftest gains per-LED frame/enable gates. (Also fixed a pre-existing compile break in the gated
  `--smu-read` probe so the DevProbes build builds again.)

## [1.2.1] — 2026-06-17

### Fixed — MSI Mystic Light JRAINBOW (`mb.argb0`) brightness ramp & flicker

- **Addressable headers now write the full `RainbowZoneData` layout.** On a JRAINBOW / JARGB
  (`RgbZoneKind.MbArgb`) zone the controller previously wrote only the 10-byte `ZoneData` and never
  set the trailing **`cycle_or_led_num` byte at +10** — the LED/render count the firmware sync engine
  needs. Left at a stale value it produced a **double-brightness ramp** and **dim-and-recover flicker
  on color change**. Cross-checking the public Mystic Light protocol showed `RainbowZoneData` simply
  *extends* `ZoneData` with that one trailing byte (the field offsets are not shifted — the earlier
  assumption was wrong); the controller now writes it (the zone's LED count, clamped 1..200) for
  `MbArgb` zones, while the `ZoneData` path for non-addressable zones is unchanged. HW-validated on
  MSI MPG B550I (MS-7C92): the dimming/flicker artifact is gone.
- **No per-frame re-assert.** A consumer driving `rgb.set` at frame rate was re-sending the whole
  185-byte packet every frame, restarting the firmware effect and fighting its sync engine. The
  controller now caches the last color and **suppresses an identical re-send**, so a held color is
  written exactly once.
- `--selftest` gains four Mystic Light gates (rainbow `ZoneData` fields; the +10 LED-count byte;
  a non-rainbow zone leaving +10 untouched; LED-count clamp 1..200). Broker-only change — no kernel
  driver rebuild or re-sign.

### Known limitation

- `mb.argb0` remains **static, whole-zone** color. Per-LED spatial effects and the smoother
  transitions of a native vendor driver require the MSI per-LED direct frame (report `0x53` /
  725-byte protocol), which is not yet implemented — see `CLAUDE.md` Open items.

## [1.2.0] — 2026-06-17

### Added — Razer Chroma peripheral RGB (USB-HID, board-independent)

- **Razer Chroma keyboards/mice over USB-HID.** A new `UsbHidRazer` transport
  (`Rgb/RazerHidController.cs`) drives Razer peripherals (VID `0x1532`) as ordinary broker
  devices — they appear in `rgb.list` and take `rgb.set` (whole-device or per-LED) exactly
  like any other zone. Ships with Razer Naga Trinity (PID `0x0067`, 3 zones) and Razer Cynosa
  Chroma (PID `0x022A`, 6×22). The "extended matrix" command protocol (90-byte report,
  transaction id, command class/id, XOR checksum, custom-frame + apply-custom) is reproduced
  as public protocol facts, cross-checked against the OpenRazer Linux driver (GPL-2.0); only
  RGB commands are issued — device mode / macros are never touched. Provenance in
  `THIRD-PARTY-NOTICES.md`.
- **Board-independent registration.** Unlike the board-zone transports, Razer devices are not
  tied to the DMI board profile — `RgbRegistry.Build` enumerates them by vendor id and binds the
  90-byte command collection, identified by the **(USB interface number + HID usage)** tuple
  OpenRazer use (Naga = iface 0, Cynosa = iface 2; usage `0x01:0x02`, 91-byte feature
  report) — a device exposes several collections per interface, so the usage disambiguates the
  command one from the consumer/system collections. `HidDevice` now parses the interface number
  from the Windows device path, exposes the HID usage page/usage, and **opens with a zero-access
  fallback** so OS-held input collections (where Razer keyboards/mice expose RGB) are reachable
  for feature reports (HidD_GetFeature/SetFeature work on a 0-access handle — what hidapi does).
  Each enumerated Razer interface is logged (PID / interface / usage / feature length), and
  `--hid-scan --vid=1532` reports the same, for bring-up. Same opt-in gate (`AllowHidRgb`) and
  reduced assurance (user-mode, no kernel brick-guard) as the Mystic Light path (whose R/W open is
  unchanged); the broker's baked report builder is the only write boundary.
- New `RgbZoneKind` values **`keyboard`** / **`mouse`** surfaced in `rgb.list` (additive; pipe
  protocol stays v2 — older clients unaffected).
- `--selftest` gains five Razer gates (custom-frame header/args, CRC = XOR[3..88], apply-custom,
  known-model geometry); the packet builders are pure/internal so the brittle wire math is
  asserted without a device.

✅ **Hardware-validated 2026-06-17** on the dev box: Razer Naga Trinity (iface 0) and Cynosa
Chroma (iface 2) both enumerate and light via the broker, driven by a non-admin client — no
"software mode" handshake needed, custom frames take effect directly. The zero-access HID open
was the key: the command collections are owned by the OS HID input stack and only open for
feature reports under a 0-access handle.

## [1.1.1] — 2026-06-17

Security and robustness hardening from a full-repo code review. No new features;
backward-compatible (pipe protocol stays v2). Deployed and hardware-validated on the
dev box (selftest PASS, 33-sensor non-admin catalog, MSI HID RGB).

### Security

- **Client signer pin now matches SHA-256** in addition to SHA-1. `AllowedClientSigners`
  may list either a 40-hex (SHA-1) or 64-hex (SHA-256) thumbprint; pinning on SHA-256 is
  recommended (SHA-1 is collision-weak). Existing SHA-1 allowlists keep working.
- **Pre-auth connection throttle** on the control channel: new connections are rate-limited
  before any signature verification, so a connect-flood cannot spin `WinVerifyTrust` before a
  session exists.

### Fixed

- The RGB registry disposes opened MSI HID interfaces that no zone binds, instead of holding
  the handles open until shutdown.
- The interactive (console) shutdown path drops its `Console.CancelKeyPress` subscription when
  shutdown fires.
- `Install-SensorBrokerService.ps1` checks the `sc.exe create` exit code for the kernel driver
  (the old service is removed first, so a silent create failure — e.g. a lingering
  `DELETE_PENDING` handle — no longer leaves no driver service, notably under `-NoStart`).

### Changed

- Driver block-write copy length uses an explicit clamp instead of the `min()` macro (removes a
  header-availability dependency; behavior identical).
- NCT6791+ HWM-enable and I/O-space-unlock bit-flips are logged (`DbgPrintEx`) only when they
  actually change a bit — visibility for that HW-unvalidated family during bring-up.
- `Install-SensorBrokerService.ps1` accepts SHA-256 signer thumbprints and writes
  `appsettings.json` with `ConvertTo-Json -Depth 32` (was 8) so a deep config section is never
  silently truncated.

## [1.1.0] — 2026-06-13

### Added — motherboard-header RGB (board-aware, multi-transport)

- **Board-aware RGB catalog.** `RgbCatalog` is now a DMI-matched board profile of *zones*
  (`Rgb/RgbZone.cs`), each tagged with a `RgbZoneKind` (dram / mb12v / mbargb) and a
  `RgbTransport`. Adding a board/zone is a **broker-only** change — the kernel exposes only
  stable, class-wide write windows, so no driver recompile/re-sign.
- **MSI Mystic Light over USB-HID** for addressable motherboard headers (JRAINBOW). The
  185-byte `FeaturePacket` is ported from the public protocol; whole-device packet seeded via
  `HidD_GetFeature` so editing one zone preserves the others. **Opt-in** (`AllowHidRgb`,
  default off) and **reduced assurance** (user-mode, no kernel brick-guard). USB **product-id
  pin** (`HidProductId`) drives only the intended controller. ✅ hardware-validated on
  MSI MPG B550I GAMING EDGE MAX WIFI (PID `0x7C92`).
- **NCT6687 EC 12V header (JRGB)** path: new brick-guarded `IOCTL_BROKER_SUPERIO_RGB_WRITE`
  (op `0x806`) + `CAP_SUPERIO_RGB`. Wired and bounded but **inert** (`SuperioRgbImplemented`
  off) until the EC RGB register window is hardware-validated.
- **`--hid-scan`** — read-only USB-HID discovery (enumerate a vendor id, print PID + feature
  length) to find and pin a controller. Minimal Win32 HID interop, no new dependency.
- **Broker-side window assertion** mirroring the kernel guard: a zone whose baked address is
  outside the kernel RGB window is refused at load (defense in depth).
- Docs: `RGB-COMMANDS.md` (command syntax for users), `RGB-BOARD-BRINGUP.md` (collect the data
  to add a board).

### Changed

- `rgb.list` gains additive `kind` and `transport` fields per device (protocol stays v2;
  older clients unaffected).
- RGB zone labels are settable via calibration by zone id (addresses-free, like sensors).
- `Install-SensorBrokerService.ps1` gains **`-WithHidRgb`** — enables the USB-HID RGB transport
  at install time (writes `AllowHidRgb=true`, implies `-WithRgbControl`), instead of a manual
  `appsettings.json` edit.

## [1.0.0] — 2026-06-12

First public release.

### The framework

- **`BrokerSmbus`** — narrow, brick-guarded KMDF kernel driver: bounded named-register
  IOCTLs only (SMBus transfer, SMU read, Super-I/O read, guarded RGB write incl. atomic
  1–32-byte block write, backend enumeration). No physical memory, MSRs, or arbitrary
  port I/O — by design, permanently.
- **`SensorBroker`** — LocalSystem sensor service on `\\.\pipe\SensorBroker`: named
  sensor catalog (`sensor.list` / `sensor.read` / `sensor.readall`), protocol v2.
  Clients name logical ids, never addresses.
- **`BrokerControl`** — separate write-only RGB service on `\\.\pipe\BrokerControl`
  (`rgb.list` / `rgb.set`, whole-device or per-LED). Per-LED ENE/Aura DRAM RGB drivable
  by a non-admin client.
- **Auth & policy** — pipe DACL + peer-process identity + optional Authenticode
  signer-thumbprint pin; per-session rate limiting (30/60 sensor, 120/240 control),
  bounded session table (32 / 8 per identity), unconditional audit log with size-cap
  rotation.

### Hardware support at release

- ✅ Hardware-validated: AMD SMU CPU temperature (Tctl + per-CCD), Nuvoton NCT6687D
  (board temps / fans / voltages), JC42 DIMM temperatures, AMD FCH (KERNCZ) SMBus.
- 🟡 Implemented from proven register sources, awaiting community validation:
  NCT6683 / NCT6686 (EC family), the NCT6775 bank-select family
  (6779/6791/6792/6793/6795/6796/6797/6798), Intel i801 SMBus (read path).
- Detection is table-driven (kernel backend registries + `ENUM_BACKENDS` IOCTL,
  broker `ChannelRegistry` / `DecoderRegistry`); all backends auto-detect and stay
  inert when their hardware is absent.

### Notable pre-release history (condensed)

- 2026-06-12 — detector/backend registry; community docs (`CONTRIBUTING-CHIPSET.md`,
  `TESTING.md`); zero third-party hardware libraries confirmed.
- 2026-06-11 — atomic RGB block write + once-per-controller latch (fixed crawl/blink);
  NCT668x siblings + NCT6775 family added; ITE/Gigabyte path retired (design record:
  `docs/GIGABYTE-SUPPORT.md`); renamed **Register Broker**; AGPL-3.0 + Commercial
  Exception license.
- 2026-06-09/10 — Windows-Service packaging live-validated; HTTP surface removed
  (pipes only); data-driven board calibration (labels/scales as data, never addresses).
- 2026-06-07/08 — broker architecture locked; protocol v2 identity auth; first
  hardware validation pass; policy hardening.

[1.2.0]: https://github.com/TheUnboundDeveloper/Register-Broker/releases/tag/v1.2.0
[1.1.1]: https://github.com/TheUnboundDeveloper/Register-Broker/releases/tag/v1.1.1
[1.1.0]: https://github.com/TheUnboundDeveloper/Register-Broker/releases/tag/v1.1.0
[1.0.0]: https://github.com/TheUnboundDeveloper/Register-Broker/releases/tag/v1.0.0
