# Code Review & Cleanup Notes

This document records the cleanup pass over the project-owned code, the changes that
were applied, and the higher-risk items that were **deferred** at the time. It also
captures the broker / handshake design so the security goals are written down before
implementation.

> **Historical note (updated 2026-06-12).** This review predates two large removals:
> the RGB-app hardware-sync **plugin** (the C++ component reviewed below) was removed
> from the repo **2026-06-11** (personal-use only, not distributed — Register Broker is
> standalone), and the bridge's **HTTP feed** was removed **2026-06-09** (followed by
> the in-proc third-party hardware-monitoring library itself on 2026-06-11). Sections
> covering those components are kept as a record and marked **historical/resolved**;
> they describe code that no longer exists in this repo.

Scope note *(historical)*: upstream UI files and third-party hardware-library binaries
were out of scope. Edits targeted only the project's own code:
`BrokerSensorBridge/` and the (since-removed) BuildKit `source/…-NoAdmin/` C++ backend.

---

## Applied in this pass (verified where possible)

### `BrokerSensorBridge` (.NET) — builds clean (`dotnet build -c Release`)
- **Graceful shutdown.** `Ctrl+C` now cancels the poll loop and server and the process
  exits cleanly, instead of an uncancellable `while (true)` server loop
  and a fire-and-forget poll task. (`Program.cs`) *(Still true; shutdown now also rides
  the Windows-Service `stoppingToken`, releasing the driver handle on SCM stop.)*
- **`--once` runs before binding the listener** — diagnostics no longer briefly open
  a socket. *(Historical: the HTTP listener was removed 2026-06-09; `--once` now prints
  one named-catalog JSON reading via the driver and exits — no socket exists at all.)*
- **`Cache-Control: no-store`** on every response. *(Historical/obsolete: applied to the
  HTTP responses, which were removed 2026-06-09 — there is no HTTP surface anymore.)*

### Hardware-sync plugin (C++) — safe, inspection-verified fixes *(HISTORICAL — component removed 2026-06-11)*

> The plugin was removed from the repo 2026-06-11; these fixes shipped in the
> personal-use build before removal and are kept here as a record.

- Plugin header: `ui` is now `= nullptr`. It was uninitialized, so
  `Unload()` before `GetWidget()` dereferenced an indeterminate pointer (UB).
- Plugin main source:
  - Removed the dead `if(can_load)` immediately after `can_load = true;`.
  - **De-duplicated** the doubled `RegisterDeviceListChangeCallback` /
    `UnregisterDeviceListChangeCallback` calls (the callback was registered twice,
    so it fired twice per device-list change).
  - `Unload()` now null-guards `ui` before `StopAll()` / unregister.
- `HardwareMeasure.cpp`: removed a duplicate `#include <algorithm>`.

---

## Deferred — recommended at the time *(ALL RESOLVED/OBSOLETE — components removed)*

These were real quality/performance wins, held back because they altered the hot path
of working sensor code that couldn't be compiled/tested in the review environment.
**None remain applicable:** P1–P4 targeted the plugin's `WinSensors.cpp` HTTP/WMI
paths — the HTTP client path was deleted 2026-06-09 and the whole plugin left the repo
2026-06-11. P5's stale launchers/packages were archived in the 2026-06-09/06-10 tooling
consolidations.

### P1 — `WinSensors.cpp`: collapse duplicated hardware-monitor-HTTP parsing *(obsolete — HTTP path deleted, then plugin removed)*
The discover/poll pair contained a near-identical `/data.json` walk (find `"SensorId"`,
locate object bounds, extract `Type`/`SensorId`/`Text`/`RawValue`). The recommendation
was to extract one shared parse helper; the entire HTTP client was instead removed.

### P2 — `WinSensors.cpp`: reuse one WinHTTP session *(obsolete — WinHTTP code deleted)*
`HttpGetUtf8()` opened a fresh `WinHttpOpen` session on every poll (once/sec). Moot:
the plugin became HTTP-free 2026-06-09, then was removed.

### P3 — `WinSensors.cpp`: factor the WMI `Sensor` enumeration *(obsolete — plugin removed)*
The discover/poll pair shared WMI query + `IEnumWbemClassObject` iteration boilerplate;
a `ForEachWmiSensor(service, callback)` helper was recommended.

### P4 — `WinSensors.cpp`: name the magic temperature bounds *(obsolete — plugin removed)*
`-50.0 … 200.0` and the `"2"` sensor-type literal recurred across discover/poll.

### P5 — repo hygiene *(resolved)*
- Root-duplicate VBS/CMD launchers were archived to `scripts\_archive_old` in the
  2026-06-09 tooling consolidation.
- The parallel plugin package tree left the repo with the plugin removal (2026-06-11).

---

## Broker / auth design (now implemented — summary)

> The shipped design is documented in [`ARCHITECTURE.md`](ARCHITECTURE.md) and
> [`IMPLEMENTATION.md`](IMPLEMENTATION.md). The summary
> below is retained for context and **updated to the shipped protocol v2** (the
> shared-secret challenge/response originally sketched here was dropped).

Target: a scoped, hardened evolution of `BrokerSensorBridge` that exposes PC sensors
(and scoped SMBus reads, plus a separate RGB write path) to non-admin consumers without
leaking a usable surface to passive scanners. **As of 2026-06-08 this is implemented and
hardware-validated** (and since 2026-06-09 packaged as the LocalSystem Register Broker
services).

1. **Privilege boundary.** The broker is the only component that touches privileged
   sensor/SMBus surfaces. Reads are a **named catalog** (no raw I/O, no client
   addressing); the write path is a separate, brick-guarded, named-device service — so a
   compromised consumer cannot escalate through it.
2. **Identity auth before data (protocol v2 — no shared secret).** A client connects to
   the fixed well-known pipe `\\.\pipe\SensorBroker` and is authenticated by **pipe DACL
   + peer-process identity + Authenticode signer-thumbprint pin**. Unauthorized peers are
   dropped with no reply (nothing to fingerprint). Nothing rides TCP — the legacy
   loopback HTTP feed was removed 2026-06-09, and the legacy bulk `sensors` op
   2026-06-11; the pipe + named catalog is the only surface.
3. **Abuse guardrails (implemented).** Per-session token-bucket rate limiting, a bounded
   session table, short-lived session tokens, strict frame validation, and an audit log
   of every connect / auth decision / op→result.
4. **Scope tokens.** Capability scopes — `sensors:read`, `smbus:read`, and `rgb:write`
   (write-only control service) — so a consumer is granted only what it needs.

> These requirements are now realized in `BrokerSensorBridge` (`BrokerControlServer.cs`,
> `ClientAuthorization.cs`, `PeerIdentity.cs`, `PeerSignature.cs`, `BrokerPolicy.cs`,
> `SensorCatalog.cs`, `RgbCatalog.cs`) — see `IMPLEMENTATION.md` for how each works.
