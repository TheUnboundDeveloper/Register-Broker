# BrokerSmbus — the Register Broker kernel driver

A small **non-PnP KMDF** driver that exposes one control device,
`\\.\BrokerSmbus` (SDDL = SYSTEM+Admins, sequential dispatch), speaking the IOCTL
contract in [`inc/SmbusBrokerProtocol.h`](inc/SmbusBrokerProtocol.h). Its entire
purpose is **bounded, validated hardware transactions** on behalf of the user-mode
broker (`BrokerSensorBridge`): SMBus reads, named SMU/Super-I/O sensor reads, and
brick-guarded SMBus writes for RGB. It deliberately exposes **no** physical-memory
mapping, **no** MSR access, and **no** arbitrary port I/O — that narrow surface is
what makes it *not* WinRing0. See
[`../docs/BROKER-DESIGN.md`](../docs/BROKER-DESIGN.md) for the full design.

> **Status (2026-06-12).** IOCTLs: `INFO`, `XFER` (read byte/word/block), `SMU_READ`,
> `SUPERIO_READ`, and `WRITE` (byte/word/**block** — block is op 5, 1..32 bytes via the
> appended `Length`+`Block[32]` fields; the legacy 24-byte prefix is still accepted for
> byte/word). The in-kernel brick-guard allows writes **only** to `0x70–0x77` /
> `0x39–0x3A`.
>
> - **AMD FCH (`SmbusAmd.c`) — HARDWARE-VALIDATED**, including block write and the
>   two-phase completion poll (KERNCZ gate: device `0x790B` rev ≥ `0x51` or `0x780B`
>   rev ≥ `0x59`; 4-way port mux). Ported from Linux `i2c-piix4`.
> - **AMD SMU (`SmuAmd.c`) — HARDWARE-VALIDATED** (Ryzen Tctl + per-CCD, named sensors,
>   addresses baked in-kernel; ported from `k10temp`/`zenpower`).
> - **Nuvoton Super-I/O** — `SuperioNct.c` (NCT668x EC family: NCT6683/6686/6687D —
>   **6687D hardware-validated**, the others built-unvalidated) + `SuperioNct6775.c`
>   (NCT6775 bank-select family: 6779/6791–6798 — **built, hardware-unvalidated**).
> - **Intel i801 (`SmbusIntel.c`) — written, NOT hardware-validated.**
>
> The `INFO` IOCTL reports the detected vendor + capabilities (visible in the broker log).
> Untested kernel bus I/O can wedge the SMBus and force a hard power cycle — first reads
> on new hardware go against SPD EEPROM (`0x50`, non-destructive) per
> [`BRINGUP-AMD-FCH.md`](BRINGUP-AMD-FCH.md) §6 / [`BRINGUP-i801.md`](BRINGUP-i801.md).

## Files

| File | Purpose |
|---|---|
| `inc/SmbusBrokerProtocol.h` | Shared IOCTL contract (driver + broker must stay in sync) |
| `Driver.c` | DriverEntry, runs detection once, control-device creation, IOCTL dispatch |
| `SmbusController.h` | Vendor enum, controller model, detect/dispatch prototypes |
| `SmbusDetect.c` | **Auto-detection**: PCI scan → vendor → vendor dispatch |
| `SmbusAmd.c` | AMD FCH (PIIX4/SB800/KERNCZ) base discovery + read/write (**hardware-validated**) |
| `SmbusIntel.c` | Intel i801 base discovery + read (written, unvalidated) |
| `SmuAmd.c` | AMD SMU named-sensor reads over SMN (Ryzen CPU temps) |
| `SuperioNct.c` | Nuvoton NCT668x EC family (NCT6683/6686/6687D) + Super-I/O dispatch |
| `SuperioNct6775.c` | Nuvoton NCT6775 bank-select family (6779/6791–6798) |
| `Smbus.c` / `Smbus.h` | Vendor-agnostic validation + in-kernel brick-guard, then dispatch |
| `BrokerSmbus.vcxproj` | KMDF project (MSBuild route; needs the WDK VS extension) |
| `scripts/*.ps1` | Build / test-sign / install / uninstall |

(`SuperioIte.c` — ITE IT87xx — was archived to `_archive_gigabyte\` on 2026-06-11;
its `KIND_ITE` wire value stays reserved.)

## Prerequisites

- Visual Studio (or Build Tools) with the **C++ x64** workload (auto-located via `vswhere`)
- **Windows Driver Kit (WDK)** — `winget install Microsoft.WindowsWDK.10.0.26100`
- A **dev/lab machine** with **Secure Boot OFF** (test-signed drivers will not load
  under Secure Boot)

## Dev workflow (lab box)

```powershell
# 0) If the driver is LOADED, the .sys at the build output is link-locked (LNK1104)
#    AND the kernel service's ImagePath points at it -- stop the stack first:
..\scripts\Stop-BrokerServices.ps1

# 1) Build. The MSBuild route (Build-Driver.ps1) fails with "MSB8020 ...
#    WindowsKernelModeDriver10.0 ... cannot be found" unless the WDK's Visual Studio
#    extension (WDK.vsix) is installed — the winget WDK package ships headers/libs/
#    tools but not the VS project-system toolset. Use the direct cl/link build:
.\scripts\Build-Driver-DirectLink.ps1     # -> x64\Release\BrokerSmbus.sys
#    The built .sys is test-signed AUTOMATICALLY (Sign-Driver-TestCert.ps1) unless
#    -NoSign. Never leave the output unsigned — the kernel service loads the .sys
#    from this path, and an unsigned binary fails the next service start (error 577).
#    (Sign-Driver-TestCert.ps1 needs elevation only for the one-time LocalMachine
#    trust import; signing itself works non-elevated.)

# 2) Enable test signing (elevated, one-time) and reboot
bcdedit /set testsigning on
#    ...reboot...

# 3) Load it (elevated). Normally the full-stack installer does this:
..\scripts\Install-SensorBrokerService.ps1      # driver + broker services (+ -WithRgbControl)
#    ...or driver-only, for isolated bring-up:
.\scripts\Install-Driver.ps1

# 4) Remove it (elevated)
.\scripts\Uninstall-Driver.ps1
```

## Vendor status — AMD validated, Intel pending

Detection routes to the right vendor file. **Both vendors are required** (universal
tool):

- **AMD FCH** (PIIX4/SB800/Family 17h+) — **HARDWARE-VALIDATED** (the dev box is AMD):
  [`SmbusAmd.c`](SmbusAmd.c). Base via PM ports `0xCD6/0xCD7` (smb_en `0x00`), 4-way
  port mux (PM index `0x02`), KERNCZ gating on device-id + revision, PIIX4
  byte/word/block read **and** block-write sequence with the two-phase completion
  poll, ported from Linux `i2c-piix4`. Validated against SPD (`0x50`, byte 2 = `0x0C`
  DDR4) and live ENE RGB writes. [`BRINGUP-AMD-FCH.md`](BRINGUP-AMD-FCH.md) is the
  historical checklist.
- **Intel i801**: [`SmbusIntel.c`](SmbusIntel.c) — `DiscoverBuses()` (SMB I/O base from
  PCI BAR4) and `Read()` are **written but NOT hardware-validated** (no Intel box yet).
  Validate per [`BRINGUP-i801.md`](BRINGUP-i801.md).

`DiscoverBuses()` populates `Controller->Buses[]` / `BusCount`; the `INFO` IOCTL
reports `BusCount`/capabilities and the broker gates its catalog on them. No edits to
`Driver.c` / `Smbus.c` are needed for a new vendor — they're vendor-agnostic.

## Production note

Test signing is lab-only. A distributable driver that loads under HVCI / Secure Boot
requires an **EV code-signing certificate + Microsoft attestation signing**. Budget
and process are tracked in [`../docs/BROKER-DESIGN.md`](../docs/BROKER-DESIGN.md) §7 and §9.
