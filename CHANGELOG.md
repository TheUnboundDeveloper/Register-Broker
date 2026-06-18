# Changelog

All notable changes to Register Broker are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[Semantic Versioning](https://semver.org/).

Versioning note: the **release version** (this file, the `BrokerSensorBridge`
assembly version, git tags) and the **pipe protocol version** (currently `2`, sent
in the client hello — see `docs/CLIENT-PROTOCOL.md` §8) are independent. New sensors
and additive ops do not bump the protocol version.

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
  OpenRazer/OpenRGB use (Naga = iface 0, Cynosa = iface 2; usage `0x01:0x02`, 91-byte feature
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
