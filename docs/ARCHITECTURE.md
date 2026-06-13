# Architecture

How the system is structured, where the privilege boundary is, and how a request flows from
a non-admin app to the hardware and back. For the line-level "how does this actually work,"
see [IMPLEMENTATION.md](IMPLEMENTATION.md); for the trust boundary and what counts as a
vulnerability, see [`SECURITY.md`](../SECURITY.md).

---

## 1. The core idea

Reading some PC sensors (Ryzen CPU temperature) and **all** RGB-over-SMBus control are
**Ring-0 operations** on Windows — there is no user-mode API. The traditional approach
(vendor RGB suites, hardware-monitor apps) is to load **WinRing0**, a kernel driver that
hands *any* caller arbitrary physical memory / MSR / port I/O, and to run the whole stack
elevated. WinRing0 is on Microsoft's vulnerable-driver block list.

**Register Broker** (the Universal Low-Level Hardware Access Framework) replaces that with
a **broker pattern**:

- **One** small, elevated process (the broker) does the privileged work, behind
- **one** narrow kernel driver that exposes *only* bounded, validated transactions, and
- exposes the results to **many** non-admin consumers over an **authenticated local channel**
  where they name *logical things* (`smu.cpu.temp`, `ram0`) instead of addressing hardware.

The win: elevation is scoped to one auditable process instead of every RGB app, and the
Ring-0 surface is a tiny, reviewable IOCTL set instead of "give me any memory."

---

## 2. The layers

```
 ┌──────────────────────────────────────────────────────────────────────────┐
 │ LAYER 4 — Consumers (non-admin, medium integrity)                          │
 │   • any tool speaking the client protocol                                  │
 │   • BrokerSensorBridge.exe --client   (the reference consumer)            │
 └───────────────┬──────────────────────────────────────────────────────────┘
                 │  named pipe, length-prefixed JSON, scoped + authenticated
                 │  (sensors:read / smbus:read on SensorBroker;
                 │   rgb:write on BrokerControl)
   ══════════════╪═══════════════ PRIVILEGE BOUNDARY ═══════════════════════════
                 ▼
 ┌──────────────────────────────────────────────────────────────────────────┐
 │ LAYER 3 — Broker (BrokerSensorBridge, elevated / LocalSystem service)      │
 │   • the ONLY holder of the driver handle                                   │
 │   • authenticates peers, enforces scopes, rate-limits, audits              │
 │   • named catalogs: SensorCatalog (reads), RgbCatalog (writes)             │
 │   • user-mode decode of raw register values (k10temp, nct6683/nct6775,     │
 │     JEDEC JC42) + data-driven board calibration (labels/scales, no addrs)  │
 └───────────────┬──────────────────────────────────────────────────────────┘
                 │  DeviceIoControl — bounded IOCTLs only (INFO/XFER/SMU/SUPERIO/WRITE)
                 ▼
 ┌──────────────────────────────────────────────────────────────────────────┐
 │ LAYER 2 — Kernel driver (BrokerSmbus, non-PnP KMDF, SYSTEM+Admins device) │
 │   • validates every IOCTL field; bakes hardware addresses in-kernel        │
 │   • SMBus XFER (read), SMU read, Super-I/O read, brick-guarded SMBus write │
 │   • NO physical-memory map, NO MSR, NO arbitrary port I/O                   │
 └───────────────┬──────────────────────────────────────────────────────────┘
                 ▼
 ┌──────────────────────────────────────────────────────────────────────────┐
 │ LAYER 1 — Hardware                                                          │
 │   • AMD SMU (SMN via PCI cfg) — CPU temperature (Tctl + per-CCD)           │
 │   • Nuvoton Super-I/O (LPC/EC) — board temps + fans + voltages             │
 │     (NCT668x EC family; NCT6775 bank-select family)                        │
 │   • SMBus host controller (AMD FCH / Intel i801) — RAM/board RGB + SPD     │
 └──────────────────────────────────────────────────────────────────────────┘
```

---

## 3. Components and responsibilities

### Layer 2 — `BrokerSmbus` (kernel driver)
A minimal **non-PnP KMDF** driver. One control device, `\\.\BrokerSmbus`, with an SDDL that
admits only SYSTEM and Administrators. Its **entire** surface is the IOCTL contract in
[`inc/SmbusBrokerProtocol.h`](../BrokerSmbusDriver/inc/SmbusBrokerProtocol.h):

- `INFO` — version, bus count, capability bits, vendor, detected Super-I/O chip id.
- `XFER` — one bounded read-only SMBus transaction (byte/word/block ≤ 32).
- `SMU_READ` — a **named** SMU sensor (Tctl + per-CCD); the kernel bakes in the SMN address
  and returns the raw 32-bit register.
- `SUPERIO_READ` — a **named** `{kind, index}` Super-I/O sensor (temp/fan/voltage); the EC
  register is baked in.
- `SMBUS_WRITE` — a bounded byte/word/**block** write (block = 1..32 bytes in one atomic bus
  transaction), **brick-guarded in-kernel** to the RGB address windows only.

The driver returns **raw register bytes**; it never interprets values. It owns the host
controller and serializes all IOCTLs (the WDF queue is sequential — load-bearing, since the
SMBus/SMU/EC share firmware state). Detection (`SmbusDetect.c`) scans PCI and dispatches to
`SmbusAmd.c` (FCH PIIX4/SB800) or `SmbusIntel.c` (i801). `SmuAmd.c`, `SuperioNct.c`
(NCT668x EC family) and `SuperioNct6775.c` (NCT6775 bank-select family) implement the named
sensor sources; each is auto-detect-gated and inert when its chip is absent.

### Layer 3 — `BrokerSensorBridge` (broker)
A GUI-less .NET 8 `WinExe`, normally run as a **LocalSystem Windows service**. It is the
**only** process that opens the driver device. Responsibilities:

- **Authentication** — every connecting pipe client is identified by peer-process identity +
  (optionally) an Authenticode signer-thumbprint pin; the pipe DACL keeps out other users.
- **Authorization** — capability **scopes** (`sensors:read`, `smbus:read`, `rgb:write`);
  ungranted ops are denied uniformly.
- **Catalogs** — `SensorCatalog` maps logical ids → backend reads + decode; `RgbCatalog` maps
  device names → baked `(bus, address)`. Clients only ever see names.
- **Decode** — raw register → engineering units, in user mode, reproduced from Linux-hwmon
  register facts (k10temp, nct6683 lineage, nct6775, jc42 — see `THIRD-PARTY-NOTICES.md`).
- **Calibration** — board-specific labels/scales/offsets are **data**
  (`calibration.default.json` + an optional user override in `ProgramData\SensorBroker`),
  keyed by board DMI; calibration can rename/rescale/hide a channel but never address
  hardware. Legacy semantic ids (`cpu.temp`, `board.12v.volt`) resolve via a built-in alias
  map.
- **Service-grade guardrails** — per-session token-bucket rate limiting, a bounded session
  table (with a per-identity cap), and a dedicated audit log.

It has **no third-party hardware libraries** — every sensor/RGB access goes through the
project's own driver (LibreHardwareMonitor and HidSharp were fully removed 2026-06-11). It
also runs as a **second instance** for RGB: `--control` serves a separate write-only pipe
so the sensor broker never holds the write capability.

### Layer 4 — Consumers
Any process that speaks the [client protocol](CLIENT-PROTOCOL.md). The reference consumer is
`BrokerSensorBridge.exe --client`; any non-admin tool can integrate the same way. (A
personal-use RGB-app integration proved the path end-to-end before being removed from the
repo on 2026-06-11 — the framework itself is standalone and consumer-agnostic.)

---

## 4. The trust boundary

```
   non-admin / medium integrity         │        elevated / LocalSystem
   ─────────────────────────────────────┼───────────────────────────────────
   consumers                            │   broker  ──IOCTL──▶  kernel driver
        │  connect to a named pipe      │
        └──────────────────────────────▶│  pipe DACL + peer identity + signer pin
                                         │  scope check → op → rate-limit → audit
```

Everything to the **left** is unprivileged and untrusted. The boundary is the named pipe.
Crossing it requires, in order: (1) passing the pipe **DACL**, (2) passing the broker's
**identity/signer** gate, (3) a valid **session token**, (4) a **granted scope** for the op,
(5) the **rate limiter**. Every step is audited. A consumer can name only what the catalogs
expose — it cannot enumerate hardware, supply an address, or reach a capability the broker
doesn't offer on that pipe.

Within the elevated side, a second boundary is the **IOCTL contract**: even the broker can
only ask the driver for bounded, named, in-kernel-validated transactions. The driver does not
trust the broker — it re-validates every field and enforces the write brick-guard itself.

The trust boundary — and the attack surface each layer defends — is summarized in
[`SECURITY.md`](../SECURITY.md).

---

## 5. Request flow — `sensor.read cpu.temp`

```
client                 broker                         driver            hardware
  │  hello(sensors:read)  │                             │                  │
  │──────────────────────▶│ auth(peer id/signer)         │                  │
  │   ok(token)           │ (DACL already passed)        │                  │
  │◀──────────────────────│                             │                  │
  │  read(token,cpu.temp) │                             │                  │
  │──────────────────────▶│ token? scope? rate-limit?    │                  │
  │                       │ catalog: cpu.temp→SMU_READ   │                  │
  │                       │────────IOCTL_SMU_READ───────▶│ bake SMN addr     │
  │                       │                             │──PCI cfg 0x60/64─▶│
  │                       │                             │◀──raw 0x69BB0000──│
  │                       │◀────────raw 32-bit──────────│                  │
  │                       │ k10temp decode → 56.6 °C     │                  │
  │  data(56.6,°C)        │ audit(op→result)             │                  │
  │◀──────────────────────│                             │                  │
```

The client named `cpu.temp` (a legacy alias the broker resolves to the stable raw id
`smu.cpu.temp` via the calibration alias map). The address (`SMN 0x00059800` via PCI config
`0x60/0x64`) lives only in the kernel; the decode (`Tctl` formula) lives only in the broker;
the consumer sees just a number and a unit. `sensor.readall` returns the whole catalog in one
rate-limited op.

`rgb.set ram0 00FF00` is the same shape on the control pipe: `rgb:write` scope → `RgbCatalog`
resolves `ram0` → baked `(bus 0, 0x39)` → `IOCTL_SMBUS_WRITE` → in-kernel brick-guard confirms
the address is in an RGB window → ENE write sequence (each LED's 3 color bytes go out as one
atomic block write; DIRECT mode + APPLY are latched once per controller).

---

## 6. Why it's structured this way (key decisions)

| Decision | Rationale |
|---|---|
| **Named pipe, not TCP** | OS ACLs, no remote reachability, and the broker can read the peer's process identity/signature. There is **no** TCP/HTTP surface (the legacy HTTP feed was removed). |
| **Identity auth, no shared secret** | A per-user secret file fits a per-user app, not a system service; binding to *who the binary is* (DACL + identity + signer pin) is the stronger, service-appropriate anchor and has nothing to leak/rotate. |
| **Driver returns raw bytes; broker decodes** | Keeps the Ring-0 surface minimal and dumb; all model-specific logic (and bugs) stay in user mode where they're cheap to fix. |
| **Named catalogs, no client addressing** | A consumer that can't supply an address can't scan the bus or steer a write — the catalog *is* the allowlist. |
| **In-kernel brick-guard** | The kernel refuses writes outside the RGB window regardless of the broker, so a broker bug can't brick a DIMM's SPD. |
| **Two services (sensor vs control)** | The sensor broker never holds the write capability; RGB write is an opt-in, separately-scoped process. |
| **Sequential IOCTL queue** | SMBus/SMU/EC share firmware state; serializing all IOCTLs is the single hardware lock. |
| **RGB is one thin layer; effects are consumer-side** | The broker writes colors (`rgb.set`), nothing more. Animation lives in the consumer, which renders frames and streams per-LED updates — so the privileged surface never grows an effects engine, and every consumer can have its own. Broader controller coverage is planned as a separate license-isolated sidecar process (a deferred post-1.0 design), not as broker code. |

---

## 7. Current state & what's missing

Working and hardware-validated (AMD dev box): the full read path (CPU via SMU incl. per-CCD,
board via NCT6687D Super-I/O, DIMM temps + SPD over SMBus), non-admin per-LED RGB write, the
identity-authenticated pipe, and the Windows-service packaging.

Built but **not hardware-validated**: the **Intel i801** SMBus backend and the **NCT6775
bank-select Super-I/O family** (NCT6683/6686 are register-identical to the validated 6687D
but also unvalidated) — community testing welcome, see [TESTING.md](TESTING.md). The ITE
IT87xx / Gigabyte USB-HID backends were retired 2026-06-11 after expert corrections and are
no longer in the tree (the `KIND_ITE` wire value stays reserved so it is never reused).

Not yet done: **production driver signing** (today's driver is test-signed — lab machines
only), flipping `RequireAuthorizedClient` on by default (still audit-only), SMU PM-table
metrics (power/clocks), and SMBus writes outside the RGB windows (deliberately refused). The
productionization path is [SIGNING-AND-DEPLOYMENT.md](SIGNING-AND-DEPLOYMENT.md); release
history is in [`CHANGELOG.md`](../CHANGELOG.md).
