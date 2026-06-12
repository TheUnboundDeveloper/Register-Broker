# Reference — definitions

Look-up tables for every name, key, code, and value in the system. For how they fit together
see [ARCHITECTURE.md](ARCHITECTURE.md) / [IMPLEMENTATION.md](IMPLEMENTATION.md).

---

## Services

| Service name | Display name | Type | Account | Purpose |
|---|---|---|---|---|
| `BrokerSmbus` | Register Broker SMBus Driver | kernel driver | — | the Ring-0 surface; loaded via `sc create type=kernel` |
| `SensorBroker` | Register Broker Sensor Service | Win32 service | LocalSystem | sensor broker; serves `\\.\pipe\SensorBroker`; depends on `BrokerSmbus` |
| `BrokerControl` | Register Broker RGB Control | Win32 service (optional) | LocalSystem | write-only RGB control; serves `\\.\pipe\BrokerControl` |

## Pipes

| Pipe | Served by | Scopes offered | Ops |
|---|---|---|---|
| `\\.\pipe\SensorBroker` | sensor broker | `sensors:read` (+ `smbus:read` if a driver backs it) | `ping`, `sensor.list`, `sensor.read`, `sensor.readall` |
| `\\.\pipe\BrokerControl` | control service | `rgb:write` | `ping`, `rgb.list`, `rgb.set` |

Kernel device: `\\.\BrokerSmbus` (opened only by the broker; SDDL admits SYSTEM + Admins).

## Capability scopes

| Scope | Meaning | Offered on |
|---|---|---|
| `sensors:read` | read the named sensor catalog | broker pipe (always) |
| `smbus:read` | the driver-backed reads are available | broker pipe (only when the driver reports `CAP_READ`) |
| `rgb:write` | set named RGB devices | control pipe (only when `allowRgbWrite` **and** the driver reports `CAP_WRITE`) |

The sensor broker **never** offers `rgb:write`. (The legacy bulk `sensors` op — the old
third-party-monitor tree payload — was removed; the catalog ops are the whole surface.)

## Client operations (wire)

Full request/response shapes are in [`CLIENT-PROTOCOL.md`](CLIENT-PROTOCOL.md). Summary:

| Op | Args | Returns | Scope |
|---|---|---|---|
| `ping` | — | `pong` | any |
| `sensor.list` | — | `{sensors:[{id,label,unit}]}` | `sensors:read` |
| `sensor.read` | `id` | `{value,unit}` / `error` / `deny` | `sensors:read` |
| `sensor.readall` | — | every catalog sensor's value, **one rate-limited op** | `sensors:read` |
| `rgb.list` | — | `{devices:[{id,label,leds}]}` | `rgb:write` |
| `rgb.set` | `device`, `color` (`RRGGBB`) **or** `colors` (per-LED array) | `{device}` / `error` / `deny` | `rgb:write` |

`{"type":"deny"}` is uniform and uninformative by design (bad token, ungranted scope, unknown
id/device, or rate-limited — indistinguishable).

## Catalog ids (this dev box; see `SENSOR-MAP.md`)

Public ids are the **stable raw channel ids** (the persistence keys); labels come from board
calibration data. Each group is auto-detect-gated — it appears only when its chip is present.

| Sensor id (raw) | Source | Unit |
|---|---|---|
| `smu.cpu.temp` | AMD SMU (Tctl/Tdie) | °C |
| `smu.ccd.0` … `smu.ccd.7` | AMD SMU per-CCD (valid-bit-gated) | °C |
| `nct6687d.temp.0` … `nct6687d.temp.5` | NCT668x EC Super-I/O | °C |
| `nct6687d.fan.0` … `nct6687d.fan.7` | NCT668x EC Super-I/O | RPM |
| `nct6687d.volt.0` … `nct6687d.volt.14` | NCT668x EC Super-I/O | V |
| `nct6775.temp.0-5`, `nct6775.fan.0-6`, `nct6775.volt.0-15` | NCT6775-family Super-I/O (hardware-unvalidated) | °C / RPM / V |
| `dimm.0` … `dimm.7` | SMBus JC42.4 / TSE2004av (`0x18+slot`) | °C — *listed only when a DIMM thermal sensor is detected at that slot* |

Legacy semantic ids resolve via a **built-in alias map** (so saved consumer selections keep
working): `cpu.temp` → `smu.cpu.temp`, `cpu.ccd{n}.temp` → `smu.ccd.{n}`,
`board.cpu.temp`/`board.system.temp`/`board.vrm.temp`/… → `nct6687d.temp.{n}`,
`board.12v.volt`/`board.5v.volt`/… → `nct6687d.volt.{n}`, `fan{n}` → `nct6687d.fan.{n}`,
`dimm{n}.temp` → `dimm.{n}`.

| RGB device id | Baked location | LEDs |
|---|---|---|
| `ram0` | bus 0, addr `0x39` | 5 |
| `ram1` | bus 0, addr `0x3A` | 5 |

Ids are stable identifiers; new hardware adds new ids (clients discover them via `*.list`).

---

## `appsettings.json` keys

Lives beside `BrokerSensorBridge.exe`. Loaded at startup; a file that exists but won't parse
makes the broker **fail closed** (`RequireAuthorizedClient=true`) with a loud log.

| Key | Type | Default | Meaning |
|---|---|---|---|
| `LogFile` | string | `%LOCALAPPDATA%\BrokerSensorBridge\bridge.log` | diagnostic log path (env vars expanded) |
| `AuditLogFile` | string | `%LOCALAPPDATA%\BrokerSensorBridge\audit.log` | security audit log path |
| `RequireAuthorizedClient` | bool | `false` | `false` = audit-only (log every client, allow all); `true` = only allowlisted/signed clients may connect |
| `AllowedClientPaths` | string[] | `[]` | full client image paths authorized when enforcing (for self-built/unsigned clients) |
| `AllowedClientSigners` | string[] | `[]` | Authenticode signer SHA-1 thumbprints authorized when enforcing (the stronger pin) |
| `MaxOpsPerSecond` | double | `30.0` | per-session token-bucket refill rate (control service floors this at 120) |
| `RateBurst` | double | `60.0` | per-session token-bucket capacity (control service floors this at 240) |
| `MaxSessions` | int | `32` | bounded session table size |
| `MaxSessionsPerIdentity` | int | `8` | max concurrent sessions per client identity |

> Under LocalSystem, `%LOCALAPPDATA%` resolves to
> `C:\Windows\System32\config\systemprofile\AppData\Local\…`, **not** your user profile.

## Exe modes / CLI flags

| Flag | Mode |
|---|---|
| *(none)* | run the sensor broker (console; or under SCM if launched as a service) |
| `--service` | force the SCM-hosted path (the installer uses this in the service binPath) |
| `--control` | run the write-only RGB control service instead of the sensor broker |
| `--client` | act as a non-admin consumer (the reference client) |
| `--client --control` | consume the control pipe |
| `--op=<name>` | client op: `ping` / `sensor.list` / `sensor.read` / `sensor.readall` / `rgb.list` / `rgb.set` (default: `sensor.list`, or `rgb.list` with `--control`) |
| `--id=<id>` | sensor id for `sensor.read` |
| `--device=<id>` `--color=<RRGGBB>` | args for `rgb.set` |
| `--scopes=<a,b>` | scopes to request in the client hello |
| `--once` | print one named-catalog JSON snapshot read straight from the driver and exit (needs elevation to open the device) |
| `--calibration [--user=<path>]` | print the detected board DMI + resolved catalog (no driver, no pipe, no admin) |
| `--selftest` | in-process auth/scope/rate/signature/calibration/chipset-gate tests (no hardware) |
| `--smbus-read` / `--smu-read` / `--superio-read` / `--ene-read` / `--ene-set` | **dev-only** raw probes — present only in a `-p:DevProbes=true` build (see [DEV-GUIDE.md](DEV-GUIDE.md)) |

The exe is a `WinExe` (no console window): `--client` / `--once` / `--calibration` /
`--selftest` output is visible only when **redirected** (`> out.txt`) or run from `cmd`/Bash.

## Build properties (MSBuild `-p:`)

| Property | Default | Effect |
|---|---|---|
| `DevProbes` | `false` | `true` defines `BROKER_DEV_PROBES` → compiles in the raw probes + a `DEV BUILD` banner. **Never `true` for a deployment build.** |
| `SelfContained` | `false` | publish a self-contained .NET app |
| `TargetFramework` | `net8.0-windows` | TFM (dev override only) |

---

## IOCTL contract (`SmbusBrokerProtocol.h`)

`BROKER_SMBUS_PROTOCOL_VERSION = 1`, `BROKER_SMBUS_MAX_BLOCK = 32`.

| IOCTL (function code) | Purpose |
|---|---|
| `IOCTL_BROKER_SMBUS_INFO` (0x800) | version / bus count / capabilities / vendor / detected Super-I/O chip id (`SuperioChipId`, appended — older 48-byte readers keep working) |
| `IOCTL_BROKER_SMBUS_XFER` (0x801) | bounded read-only SMBus transaction |
| `IOCTL_BROKER_SMU_READ` (0x802) | read a baked-in named SMU sensor (raw 32-bit) |
| `IOCTL_BROKER_SUPERIO_READ` (0x803) | read a baked-in named Super-I/O sensor (raw) |
| `IOCTL_BROKER_SMBUS_WRITE` (0x804) | brick-guarded byte/word/block write (RGB windows only) |

### Op codes (`BROKER_SMBUS_OP`)
`ReadByte=0`, `ReadWord=1`, `ReadBlock=2`, `WriteByte=3`, `WriteWord=4`, `WriteBlock=5`
(writes valid only on `…_WRITE`). `WriteBlock` carries 1..32 bytes in one atomic bus
transaction via the appended `Length` + `Block[32]` request fields; byte/word requests may
still send only the original 24-byte V1 prefix (`BROKER_SMBUS_WRITE_REQUEST_V1_SIZE`).

### Status codes (`BROKER_SMBUS_STATUS`)
| Value | Name | Meaning |
|---|---|---|
| 0 | `Ok` | success |
| 1 | `NotImplemented` | driver present, that controller path not wired |
| 2 | `BadRequest` | failed field validation (version/op/length/bus index) |
| 3 | `BusError` | hardware transaction failed |
| 4 | `Forbidden` | blocked by the in-kernel address guard |
| 100 | `Unavailable` | *(broker-side only)* no driver present |

### Capability bits (`Capabilities`)
| Bit | Name | Set when |
|---|---|---|
| `0x1` | `CAP_READ` | SMBus read path implemented |
| `0x2` | `CAP_SMU` | AMD SMU CPU-temp path present (CPUID-gated) |
| `0x4` | `CAP_SUPERIO` | a supported Super-I/O chip detected (NCT668x EC or NCT6775 family) |
| `0x8` | `CAP_WRITE` | brick-guarded SMBus write path present |

### Vendor (`Vendor`)
`0` unknown, `1` Intel (i801), `2` AMD (FCH).

### Named sensors
- `BROKER_SMU_SENSOR`: `BrokerSmuCpuTemp = 0` (Tctl), `BrokerSmuCcd0Temp = 1` …
  `BrokerSmuCcd7Temp = 8` (per-CCD, valid-bit `0x800` checked broker-side).
- `BROKER_SUPERIO_KIND`: `BrokerSuperioTemp = 0`, `BrokerSuperioFan = 1`,
  `BrokerSuperioVoltage = 2`. Index is bounded per kind *per detected backend*
  (NCT668x EC: 7 temps / 8 fans / 16 volts; NCT6775 family: 6 / 7 / 16).

### Super-I/O backend family (`BROKER_SUPERIO_KIND_*`)
`NONE = 0`, `NCT = 1` (NCT668x EC family: chip ids masked `0xFFF0` — NCT6683 `0xC730`,
NCT6686 `0xD440`, NCT6687D `0xD590`), `ITE = 2` (**reserved** — the ITE backend was archived
2026-06-11 to `_archive_gigabyte\`; never reuse the wire value), `NCT6775 = 3` (bank-select
family: NCT6779/6791/6792/6793/6795/6796/6797/6798).

### Protected vs writable SMBus addresses
| Range | Status |
|---|---|
| `0x70–0x77`, `0x39–0x3A` | **writable** (RGB controller windows) |
| `0x50–0x57` (SPD), `0x36/0x37` (SPD page-select), `0x18–0x1F` (DIMM temp) | **refused in-kernel** |
| any other ≤ `0x7F` | readable; not writable |

---

## Glossary

| Term | Meaning |
|---|---|
| **Broker** | the elevated process (`BrokerSensorBridge`) that fronts the driver and serves non-admin clients |
| **Catalog** | the broker's baked map of logical names → hardware (`SensorCatalog`, `RgbCatalog`); the client-visible allowlist |
| **Brick-guard** | the in-kernel write-address allowlist that prevents writes to SPD/protected ranges |
| **SMU / SMN** | AMD System Management Unit / its register network, reached via root-complex PCI config — source of CPU temperature |
| **Super-I/O** | the board's environment-controller chip (temps/fans/voltages), reached over LPC — NCT668x EC family (NCT6683/6686/6687D) or NCT6775 bank-select family |
| **FCH / i801** | AMD Fusion Controller Hub / Intel's SMBus host controller |
| **SPD** | the DIMM's serial-presence-detect EEPROM (`0x50`+) — must never be written |
| **Signer pin** | authorizing a client by the exact SHA-1 thumbprint of its Authenticode signing cert |
| **Scope** | a capability token (`sensors:read` etc.) granted in the session, checked per op |
| **HVCI / Memory Integrity** | the hypervisor-enforced code-integrity feature that blocks unsigned/blocklisted drivers |
