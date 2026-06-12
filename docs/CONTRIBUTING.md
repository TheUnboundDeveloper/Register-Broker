# Contributing

How to build the project, make a change cleanly, and add the two things people most often
want to add — a **new sensor** and a **new RGB device**.

> This tree is two source trees (the .NET broker, the C kernel driver) plus PowerShell
> tooling. "Send a change" means: keep the diff small and reviewable, follow the
> per-tree conventions below, and keep the security guardrails (§3) intact — read them
> before touching anything.
>
> Adding hardware support? The deep walkthrough is
> [CONTRIBUTING-CHIPSET.md](CONTRIBUTING-CHIPSET.md).

---

## 1. Build environments

| Component | Toolchain | Build command |
|---|---|---|
| **Broker** (`BrokerSensorBridge/`, C# .NET 8) | .NET 8 SDK | `.\scripts\Build-BrokerSensorBridge.ps1` (or `.\scripts\Build-All.ps1`, elevated — publishing stops the services) |
| **Driver** (`BrokerSmbusDriver/`, C / KMDF) | WDK 10.0.26100 + VS Build Tools | `.\BrokerSmbusDriver\scripts\Build-Driver-DirectLink.ps1` (auto-test-signs; `-NoSign` to skip) |

There are **no third-party DLLs to vendor** — the broker has no external hardware libraries;
every sensor/RGB access goes through the `BrokerSmbus` driver.

Quick inner-loop for the broker without disturbing an installed service (which locks the
exe): build to a scratch dir and run the self-test there:

```powershell
dotnet build BrokerSensorBridge\BrokerSensorBridge.csproj -c Release -o $env:TEMP\rbroker
& $env:TEMP\rbroker\BrokerSensorBridge.exe --selftest     # in-proc auth/scope/rate/calibration tests
```

The driver's MSBuild `.vcxproj` route fails (`MSB8020`) unless the WDK's VS extension is
installed — **use `Build-Driver-DirectLink.ps1`** (direct cl/link against the WDK). A
**loaded** driver link-locks the build output `.sys` (the kernel service's ImagePath points
at it) — run `.\scripts\Stop-BrokerServices.ps1` before rebuilding the driver.

---

## 2. Repository layout (where to edit)

| Path | What it is | Edit here? |
|---|---|---|
| `BrokerSensorBridge/` | the broker (C#) | ✅ |
| `BrokerSmbusDriver/` | the kernel driver (C) + sign/install scripts | ✅ |
| `scripts/` | build / install / lifecycle tooling | ✅ |
| `docs/`, `*.md` (root) | documentation | ✅ |

A map of the broker's C# files and the driver's C files is in
[IMPLEMENTATION.md](IMPLEMENTATION.md) §"File map".

---

## 3. The guardrails (do not break these)

These are the project's reason for existing; a change that violates one will be rejected:

1. **No kernel driver or admin requirement on the consumer path.** Elevation is
   allowed *only* inside the broker/driver. Consumers stay non-admin and talk to the
   broker over the named pipes. (No reintroducing WinRing0.)
2. **The kernel driver stays narrow.** Any new capability is a *specific, bounded, in-kernel-
   validated* IOCTL — never physical-memory mapping, MSR, or arbitrary port I/O. Port
   hardware sequences from proven sources (Linux `i2c-*`, `k10temp`, `nct6687d` — reproduced
   as hardware facts, see `THIRD-PARTY-NOTICES.md`); don't invent register encodings. Bring
   up on SPD (`0x50`, read-only) first.
3. **Clients name things, never address them.** Sensors and RGB devices are exposed by
   *logical name* from a baked catalog. No client op may accept a bus/address/register. (This
   is what makes the broker safe to expose to non-admin callers.)
4. **Addresses are baked broker-side; writes are brick-guarded in-kernel.** The kernel refuses
   any write outside the known RGB address window regardless of what the broker sends.
5. **Dev-only raw tooling is compile-time gated.** The raw probes live behind
   `#if BROKER_DEV_PROBES` and never ship in a normal build. See [DEV-GUIDE.md](DEV-GUIDE.md).
6. **Catalog `id` strings are persistence keys.** Clients save sensor/device selections by
   id (`nct6687d.volt.0`, `ram0`). Keep ids stable across releases or saved client configs
   break; legacy semantic ids resolve via the built-in alias map.

---

## 4. How to add a new **sensor**

There are two cases.

### 4a. The chip is already supported — you just need a label/scale
Board-specific labeling and scaling are **data, not code**: raw channels (stable ids like
`nct6687d.volt.0`) are decoded in `BrokerSensorBridge/Sensors/` (`RawChannel.cs`,
`SensorDecode.cs`) and labeled by calibration data (`calibration.default.json`, loader
`Sensors/Calibration.cs`; users can override with
`C:\ProgramData\SensorBroker\calibration.user.json` — see `calibration.user.example.json`).
Calibration entries carry **labels + scales only, no addresses**. To support a new board on a
known chip, add/extend a calibration entry — nothing to do in the driver.

### 4b. The reading needs the kernel driver (a new privileged source)
Follow the proven template (this is exactly how CPU-temp and board-temp were added);
the full walkthrough with templates and checklists is
[CONTRIBUTING-CHIPSET.md](CONTRIBUTING-CHIPSET.md):

1. **(driver)** Add a *named* sensor to the IOCTL contract — extend the relevant enum in
   [`BrokerSmbusDriver/inc/SmbusBrokerProtocol.h`](../BrokerSmbusDriver/inc/SmbusBrokerProtocol.h)
   (e.g. a new `BROKER_SMU_SENSOR` value), **bake the hardware address in the kernel**, and
   return the **raw register bytes** — no interpretation in Ring 0. Validate the index/bounds
   in-kernel. Mirror the byte layout in `BrokerSensorBridge/Smbus/SmbusTypes.cs`.
2. **(broker)** Add a read method or extend an existing one in
   `BrokerSensorBridge/Smbus/SmbusDriverBackend.cs` to issue the IOCTL and return the raw value.
3. **(broker)** Implement the **decode** (raw → units) in user mode — *ported from a proven
   source*, never invented. Put it next to the existing decodes
   (`Sensors/SensorDecode.cs`).
4. **(broker)** Add a catalog entry (4c) so clients can name it.

### 4c. Register it in the catalog
Add a `RawChannel` to `ChipChannels` in `BrokerSensorBridge/Sensors/RawChannel.cs` with: a
stable **raw id** (e.g. `smu.cpu.temp`, `nct6687d.volt.0`), a default unit, an availability
predicate (gate on the right capability/chip detection), and a read that calls the backend
and applies the decode (`Sensors/SensorDecode.cs`). `SensorCatalog` assembles the served
entries from the raw channels plus calibration data (labels/scales —
`calibration.default.json`; legacy semantic ids resolve via the alias map). Document the new
id in [`SENSOR-MAP.md`](SENSOR-MAP.md). Clients pick
it up automatically via `sensor.list` — no client change needed.

---

## 5. How to add a new **RGB device**

For a device on an **already-allowed** controller address window
(`BROKER_SMBUS_RGB_ADDR_*` / `BROKER_SMBUS_DRAM_ADDR_*` in the IOCTL header), just add a
`RgbDevice` to `BrokerSensorBridge/RgbCatalog.cs`:

```csharp
new RgbDevice("ram2", "DRAM RGB (DIMM 2)", bus: 0, address: 0x3B, ledCount: 5),
```

If the new device sits **outside** the kernel's allowed write window, you must *deliberately*
widen the window in the IOCTL header **and** the in-kernel guard
(`BrokerSmbusWriteAddressAllowed` in `BrokerSmbusDriver/Smbus.c`) — and you must be sure the
new range never overlaps SPD (`0x50–0x57`), the SPD page-select (`0x36/0x37`), or DIMM temp
sensors (`0x18–0x1F`). Treat widening the brick-guard as a security change: justify it, bound
it tightly, and test against SPD first. The LED write sequence itself lives in
`BrokerSensorBridge/Smbus/EneController.cs` (the ENE/Aura protocol is a publicly documented
hardware protocol, reproduced as register facts; per-LED frames go out as atomic 3-byte block
writes via the driver's `WriteBlock` op).

---

## 6. Conventions

- **C#:** nullable enabled, `internal` by default, file-scoped namespaces. Match the existing
  boxed banner-comment style on types.
- **C (driver):** match the existing boxed `/*---*\` header style; validate every
  IOCTL field before use; snapshot `METHOD_BUFFERED` input before zeroing the response.
- **PowerShell:** check native-exe exit codes (`$LASTEXITCODE`) — `sc.exe` etc. don't throw on
  failure; write BOM-less UTF-8 when editing JSON config.
- **Polling cadence is 1 s** in the broker; use the existing
  `PollMilliseconds` knob rather than adding timers.

---

## 7. Testing your change

- **`--selftest`** (broker): exercises the auth gate, scope enforcement, the session/rate
  limiter, the signature path, the calibration regression, and the chipset detection gates
  **in-process, with no hardware** — runs on any machine and
  must stay green. Add a self-test case when you add a control op or auth rule.
- **Driver / hardware paths** can only be validated on real silicon. Bring up read paths on
  **SPD (`0x50`, non-destructive) first**, then the specific sensor; compare temps against a
  known tool (HWiNFO) to the degree — the full procedure and report template are in
  [TESTING.md](TESTING.md). The dev probes ([DEV-GUIDE.md](DEV-GUIDE.md)) exist for
  exactly this.
- **Live broker test:** start the broker, run `--client --op=sensor.list` / `sensor.read` from
  a non-admin shell, and confirm `elevated=False`.

---

## 8. Submitting a change

- Keep each change scoped to one concern; keep the diff minimal and reviewable.
- Update the docs you touched (`SENSOR-MAP.md` for new sensors, `REFERENCE.md` for new config
  keys/ops, `CLIENT-PROTOCOL.md` for protocol changes).
- Run `--selftest` and confirm it passes; note what hardware validation you did (or couldn't).
- For protocol changes, keep the wire format/auth model stable — prefer **additive** ops and
  catalog ids (that's the designed extension path).
- For anything touching the driver's IOCTL surface or the brick-guard, call it out explicitly
  for security review (`/security-review` covers the pending diff).
