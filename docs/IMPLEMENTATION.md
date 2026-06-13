# Implementation Reference

The line-level "how it actually works." Pairs with [ARCHITECTURE.md](ARCHITECTURE.md) (the
shape) and [REFERENCE.md](REFERENCE.md) (the definitions). The wire protocol itself is
specified in [`CLIENT-PROTOCOL.md`](CLIENT-PROTOCOL.md) — this doc covers the *server/
driver* internals behind it.

---

## File map

### Broker — `BrokerSensorBridge/` (C#, .NET 8)

| File | Responsibility |
|---|---|
| `Program.cs` | Entry point + arg routing; the broker body (`RunBrokerAsync`), the control-service body (`RunControlServiceAsync`), the `--once`/`--calibration` one-shots, logging + audit sinks, config (`BridgeConfig`), the `--selftest`, and the (gated) dev probes. |
| `ServiceHost.cs` | Hosts the broker/control body under the Windows SCM (`Microsoft.Extensions.Hosting.WindowsServices` — the only NuGet dependency; no third-party hardware libraries since 2026-06-11). |
| `BrokerControlServer.cs` | The named-pipe server: accept loop, pipe DACL, per-connection handshake, session table, rate limiting, op dispatch, frame I/O. |
| `BrokerControlClient.cs` | The reference non-admin consumer (`--client`). |
| `ClientAuthorization.cs` | The auth decision: path/signer allowlist matching → `AuthDecision`. |
| `PeerIdentity.cs` | Resolves the connecting client's PID → real image path. |
| `PeerSignature.cs` | `WinVerifyTrust` over the client image → signer thumbprint/subject. |
| `BrokerProtocol.cs` | Protocol constants: version, pipe names. |
| `BrokerPolicy.cs` | `BrokerPolicy` (rate/session limits) + the token-bucket `RateLimiter`. |
| `SensorCatalog.cs` | The served sensor catalog, assembled from raw channels + board calibration (labels/scales/hide — never addresses). |
| `Sensors/RawChannel.cs` | The raw channels: stable ids (`smu.cpu.temp`, `nct6687d.volt.0`, `dimm.0`…), availability gates, base reads. |
| `Sensors/SensorDecode.cs` | Raw register → engineering units (Linux-hwmon register facts: k10temp, nct6683 lineage, nct6775, jc42). |
| `Sensors/Calibration.cs` | Board DMI detect, `calibration.default.json` + user-override loading, the built-in legacy-id alias map. |
| `RgbCatalog.cs` | Named RGB device map → baked `(bus, address)`. |
| `Rgb/IRgbController.cs` / `Rgb/RgbRegistry.cs` / `Rgb/EneRgbController.cs` | Transport-agnostic RGB device seam + the auto-detect registry (devices appear only when their transport hardware is found). |
| `Smbus/SmbusDriverBackend.cs` | `DeviceIoControl` wrapper over the driver IOCTLs. |
| `Smbus/SmbusTypes.cs` | C# mirror of the IOCTL structs/enums + `ISmbusBackend`. |
| `Smbus/MockSmbusBackend.cs` | In-memory backend for `--selftest` (no hardware). |
| `Smbus/EneController.cs` | ENE/Aura DRAM RGB write sequence (a publicly documented hardware protocol, reproduced as register facts — see `THIRD-PARTY-NOTICES.md`). |

### Driver — `BrokerSmbusDriver/` (C, KMDF)

| File | Responsibility |
|---|---|
| `inc/SmbusBrokerProtocol.h` | The shared IOCTL contract (structs, enums, caps). **Mirror of `SmbusTypes.cs`.** |
| `Driver.c` | `DriverEntry`, device/queue creation, `EvtIoDeviceControl` (the IOCTL dispatch). |
| `Smbus.c` | Vendor-agnostic validation front-end + the read/write address guards. |
| `SmbusDetect.c` | PCI scan → pick AMD/Intel backend; per-bus dispatch. |
| `SmbusAmd.c` / `SmbusIntel.c` | FCH (PIIX4/SB800) and i801 transaction sequences. |
| `SmuAmd.c` | AMD SMU read over SMN (PCI cfg `0x60/0x64`) — Tctl + per-CCD. |
| `SuperioNct.c` | NCT668x EC family (NCT6683/6686/6687D) detect + EC page/index/data reads over LPC. |
| `SuperioNct6775.c` | NCT6775 bank-select family (NCT6779/6791/6792/6793/6795/6796/6797/6798): HWM bank-select window reads; IO-space-lock clear on 6791+ only. **Built, hardware-unvalidated.** |

---

## Authentication (broker side)

There is **no shared secret**. On each connection, before any frame is read,
`BrokerControlServer.HandleClientAsync` calls `ClientAuthorization.Authorize(pipeHandle)`,
which resolves and checks the peer in three layers:

1. **Pipe DACL** (set in `TryBuildPipeSecurity`). In the dev case (broker runs as the same
   user as its client) the DACL grants that user. In the **service** case (broker is
   LocalSystem) it grants SYSTEM/Admins full + **Authenticated Users** connect — no integrity
   label is set, because an unlabeled pipe is treated as *medium* integrity, which non-admin
   medium-IL clients can write to (an explicit label would also fail to apply, as the SYSTEM
   token doesn't hold `SeSecurityPrivilege` enabled).
2. **Peer-process identity** (`PeerIdentity.TryGetClient`): `GetNamedPipeClientProcessId` →
   `OpenProcess` → `QueryFullProcessImageNameW` to get the client's real image path.
3. **Authenticode signer pin** (`PeerSignature.TryGetSigner`): `WinVerifyTrust` over the image
   (rejecting unsigned/tampered/expired/revoked), then the signer's SHA-1 thumbprint.
   `CERT_E_UNTRUSTEDROOT` is accepted on purpose — the pin is an **exact thumbprint match**,
   so a dev test cert (or later an EV cert) authorizes without a machine-wide root install.

`Authorize` returns an `AuthDecision { Allowed, Who, How }`. Enforcement is policy-driven:

- `RequireAuthorizedClient = false` (default) → **audit-only**: every connect is logged with
  pid/image/signer but allowed.
- `RequireAuthorizedClient = true` → the peer must match **either** `AllowedClientPaths` (full
  image path) **or** `AllowedClientSigners` (thumbprint). Otherwise the pipe is closed with no
  reply (nothing to fingerprint).

> **Known residual:** a PID-reuse TOCTOU window exists between connect and identity query —
> accepted for local same-user IPC, recorded in `SECURITY.md` (known and accepted).

## Handshake, sessions, scopes

After auth, the client sends a `hello` declaring requested scopes. `TryProcessHello`
intersects them with `_allowedScopes` (what *this* pipe can back — the sensor broker never
includes `rgb:write`). An empty intersection is denied (no do-nothing sessions). On success a
256-bit session token (`RandomNumberGenerator.GetBytes(32)`) is created with a 10-minute
expiry and stored in a `ConcurrentDictionary`. The count-check-and-insert is done under a lock
so concurrent connections can't overshoot `MaxSessions` (32) or `MaxSessionsPerIdentity` (8).
Each op then checks: valid/unexpired token → granted scope
(`when session.Scopes.Contains(...)`) → rate limiter.

## Rate limiting

`RateLimiter` (in `BrokerPolicy.cs`) is a token bucket, **one per session**: `_tokens`
refills at `MaxOpsPerSecond` up to `RateBurst`, decrements by 1 per op, and returns `false`
(→ uniform `deny`) when empty. Defaults: 30 ops/s, burst 60 (the control service raises the
floor to 120/240 for per-LED frame traffic). Bulk reads stay cheap regardless:
`sensor.readall` returns the whole catalog as **one** rate-limited op.

## Frame I/O & DoS bounds

Every frame is a 4-byte big-endian length prefix + UTF-8 JSON (`ReadFrameAsync` /
`WriteFrameAsync`). Reads are capped at **64 KB** (control JSON is tiny). The pre-auth `hello`
read has a **10-second timeout** so a connected-but-silent client can't park a handler/pipe
instance (slowloris). On shutdown the accept loop stops and `RunAsync` **drains in-flight
handler tasks** (bounded 5 s) before returning, so the driver handle isn't disposed mid-IOCTL.

## Logging & audit

`Program.Log` (diagnostic) and `Program.Audit` (security trail) both run peer-derived strings
through `Sanitize()`, which strips control characters — a malicious client's cert *subject*
can contain CR/LF, and unsanitized it could forge/split audit lines. Config that exists but
fails to parse makes the broker **fail closed** (`RequireAuthorizedClient = true`) with a loud
log line, rather than silently reverting to open defaults.

---

## The driver IOCTLs

All five IOCTLs are `METHOD_BUFFERED` (the I/O manager copies small payloads through one
shared system buffer). The dispatch is in `Driver.c`'s `EvtIoDeviceControl`; the queue is
`WdfIoQueueDispatchSequential` (the single hardware lock across all backends).

**Critical pattern — snapshot before zero.** Because input and output alias the same system
buffer under `METHOD_BUFFERED`, each handler copies the request into a local *before* zeroing
the response (otherwise `RtlZeroMemory` wipes the request's `Version` → `BadRequest`). This
bit the project once; keep it.

| IOCTL | Request → Response | Validation |
|---|---|---|
| `INFO` | (none) → `{Version, BusCount, Capabilities, Vendor, BusInfo[8], SuperioChipId}` | response zeroed first (no stale-byte leak); `SuperioChipId` is appended so older 48-byte readers keep working |
| `XFER` | `{Version, Op, BusIndex, Address, Command, Length}` → `{Status, Length, Data[≤32]}` | `Version`, `Op ≤ ReadBlock`, `Length ≤ 32`, `BusIndex < BusCount`, address ≤ 0x7F |
| `SMU_READ` | `{Version, Sensor}` → `{Status, Raw}` | `Version`, `Sensor` index bounded (Tctl + 8 CCDs); SMN address baked in-kernel |
| `SUPERIO_READ` | `{Version, Kind, Index}` → `{Status, Raw}` | `Version`, `Kind` (temp/fan/voltage), `Index` bounded per kind *per detected backend*; EC/HWM register baked in-kernel |
| `SMBUS_WRITE` | `{Version, Op, BusIndex, Address, Command, Data, Length, Block[32]}` → `{Status}` | as XFER **plus** the brick-guard: address must be in an RGB window. `WriteBlock` (op 5) requires the full struct with `Length` 1..32; byte/word may legally truncate to the original 24-byte V1 prefix (`Length`/`Block` were appended) |

**Brick-guard** (`BrokerSmbusWriteAddressAllowed` in `Smbus.c`): a write is permitted *only*
to `0x70–0x77` or `0x39–0x3A` (the RGB controller windows). SPD (`0x50–0x57`), the SPD
page-select (`0x36/0x37`), and DIMM temp sensors (`0x18–0x1F`) are all refused **in the
kernel**, regardless of what the broker sends. (The guard gates the device *address*, not the
register/`Command` within it — the broker's baked `RgbCatalog` bounds which registers are
actually written.)

The C# side (`SmbusDriverBackend`) validates `bytesReturned` against the expected struct size
before trusting a response, so a truncated kernel reply is rejected rather than read as zeros.

**SMBus completion polling** (`SmbusAmd.c`): two phases — 25 µs busy-wait stalls
(`KeStallExecutionProcessor`, exact, safe at PASSIVE_LEVEL) for up to ~2 ms, then 250 µs
thread sleeps for up to ~48 ms before `BusError`. The old single-phase
`KeDelayExecutionThread(250 µs)` loop actually slept a timer tick (1–15.6 ms) per iteration,
costing milliseconds per transaction — the visible RGB frame crawl.

---

## Decode formulas (raw register → units)

The kernel returns raw bytes; the broker decodes them in `Sensors/SensorDecode.cs` —
**Linux-hwmon register facts** (k10temp, nct6683 lineage, nct6775, jc42 — see
`THIRD-PARTY-NOTICES.md`), never invented:

- **AMD CPU temperature** (`AmdCpuTctlC`, from `k10temp`/`zenpower`): the 32-bit
  reported-temp register's top bits give Tctl in 0.125 °C steps; `Tdie = Tctl − offset`
  (offset is 0 on most desktop Ryzen). Validated: `0x69BB0000` → 56.6 °C, matched HWiNFO.
- **AMD per-CCD temperature** (`AmdCcdTempC`, k10temp `ZEN_CCD_TEMP`): valid bit `0x800`
  gates presence; `(raw & 0x7FF) × 0.125 − 49 °C`.
- **NCT668x board temp** (`NctTempC`, from the Linux `nct6683` lineage):
  `temp = signedByte + 0.5 × halfBit`, where the raw value is `valueByte | (halfBit << 8)`.
- **NCT668x fan** (`NctFanRpm`): raw is a 16-bit RPM value, used directly.
- **NCT668x voltage** (`NctVoltageMv`): 16-bit EC reading in mV (per-rail scale applied by
  calibration data).
- **NCT6775-family voltage** (`Nct6775VoltageMv`): a single ADC byte at 8 mV/LSB (temps/fans
  reuse the NCT decode — identical packing).
- **DIMM temperature** (`Jc42TempC`, from Linux `jc42`): the JC42.4/TSE2004av temp
  register (reg `0x05`, read MSB-first) — bits 12:0 are a 13-bit two's-complement value at
  0.0625 °C/LSB; alarm flag bits 15:13 are masked. Read over the SMBus `XFER` path with the
  address (`0x18+slot`) baked broker-side and per-slot presence auto-probed (no phantom ids).

On top of the decode sits **board calibration** (`Sensors/Calibration.cs`): DATA keyed by
board DMI (`calibration.default.json`, plus an optional
`C:\ProgramData\SensorBroker\calibration.user.json` override — last file wins) supplying
labels, per-rail scales, offsets, and hide flags — never addresses. Public sensor ids are the
stable raw ids (`Sensors/RawChannel.cs`); legacy semantic ids resolve via the built-in alias
map. The `--calibration` inspector prints the detected board DMI + resolved catalog; the dev
box (MSI `MPG B550I GAMING EDGE MAX WIFI (MS-7C92)`) has an exact-match entry.

When you add a sensor, the decode goes here next to these — see
[CONTRIBUTING.md](CONTRIBUTING.md) §4.

---

## RGB frame path (control service)

`RgbRegistry` (transport-agnostic, built at startup over the `IRgbController` seam) registers
the ENE/Aura DRAM devices from `RgbCatalog` only when the driver reports `CAP_WRITE`. The
ENE write sequence (`Smbus/EneController.cs` — a publicly documented hardware protocol,
reproduced as register facts) is pointer-write (`cmd 0x00`, byte-swapped 16-bit register) then
data write. Two load-bearing details (the 2026-06-11 crawl/blink fix):

- **One atomic block write per LED**: each LED's 3 color bytes (R,B,G — the controller's byte
  order) go out as a single `WriteBlock` transaction (2 bus transactions per LED instead
  of 6) — no transient wrong-color mixes, far less bus time. If the kernel driver predates
  `WriteBlock` (`BadRequest` once, remembered), it degrades to per-byte writes.
- **DIRECT mode + APPLY latched once per controller**: re-latching per frame caused a visible
  blink. A failed frame clears the latch so the next frame re-latches (device reset / resume
  recovery).

Each controller instance is persistent + shared, with all bus sequences serialized per
controller so concurrent `rgb.set` calls can't interleave pointer/data pairs.

---

## Service hosting & lifecycle

`Program.Main` routes to `ServiceHost` when launched with `--service` or when
`WindowsServiceHelpers.IsWindowsService()` is true; otherwise it runs the same body on a
console path with a Ctrl+C token. `ServiceHost` builds a generic host with
`AddWindowsService` and a `BackgroundService` whose `ExecuteAsync` calls the broker body with
the host's `stoppingToken`. On SCM stop: the token cancels → the control loop and poll loop
end → `RunBrokerAsync`'s `finally` disposes `SmbusDriverBackend`, releasing `\\.\BrokerSmbus`
so the kernel service can unload. A 20 s shutdown window bounds the drain. The installer makes
`SensorBroker` depend on `BrokerSmbus`, so SCM start order is driver → broker.

---

## Dev probes (compile-time gated)

The raw bring-up probes (`--smbus-read`, `--smu-read`, `--superio-read`, `--ene-read`,
`--ene-set`) and their helpers are wrapped in `#if BROKER_DEV_PROBES`. A normal build
excludes them entirely; `-p:DevProbes=true` includes them and stamps a loud `DEV BUILD`
banner at startup and in `--selftest`. They open the driver **directly** and take raw
addresses — they exist only for hardware bring-up. See [DEV-GUIDE.md](DEV-GUIDE.md).
