# Changelog

All notable changes to Register Broker are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[Semantic Versioning](https://semver.org/).

Versioning note: the **release version** (this file, the `BrokerSensorBridge`
assembly version, git tags) and the **pipe protocol version** (currently `2`, sent
in the client hello — see `docs/CLIENT-PROTOCOL.md` §8) are independent. New sensors
and additive ops do not bump the protocol version.

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

[1.1.0]: https://github.com/REPLACE-OWNER/register-broker/releases/tag/v1.1.0
[1.0.0]: https://github.com/REPLACE-OWNER/register-broker/releases/tag/v1.0.0
