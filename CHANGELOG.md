# Changelog

All notable changes to Register Broker are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[Semantic Versioning](https://semver.org/).

Versioning note: the **release version** (this file, the `BrokerSensorBridge`
assembly version, git tags) and the **pipe protocol version** (currently `2`, sent
in the client hello — see `docs/CLIENT-PROTOCOL.md` §8) are independent. New sensors
and additive ops do not bump the protocol version.

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

[1.0.0]: https://github.com/REPLACE-OWNER/register-broker/releases/tag/v1.0.0
