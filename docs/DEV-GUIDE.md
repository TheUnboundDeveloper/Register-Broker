# Developer Guide — dev builds & hardware bring-up

> ⚠️ **This document is for development on a machine you control.** Everything here uses the
> **dev build** of the broker, which compiles in raw hardware-addressing tools that are
> deliberately **excluded from any normal/deployment build**. A dev build is for bringing up
> new hardware and debugging the kernel path — **never ship one.**

For the production story (signing, deploying to user machines) see
[SIGNING-AND-DEPLOYMENT.md](SIGNING-AND-DEPLOYMENT.md).

---

## 1. What "dev mode" is (and isn't)

"Dev mode" here means exactly one thing: the **raw hardware probes** —
`--smbus-read`, `--smu-read`, `--superio-read`, `--ene-read`, `--ene-set`. They open the
kernel driver **directly** (bypassing the broker, its auth, and its named catalog) and take
**raw** bus/address/command (`--ene-set` does a raw RGB write). They are how the named catalog
gets built: you probe real hardware to find/validate an address, then bake it into the
catalog so end-user clients never need to address anything.

They are **compile-time gated** behind `#if BROKER_DEV_PROBES`:

- A **normal build excludes the code entirely** — passing `--smbus-read` to a deployment
  binary does nothing (it falls through to the broker). There is no raw-addressing code in a
  shipped binary to unlock or exploit.
- A **dev build** (`-p:DevProbes=true`) compiles them in **and** prints a loud `DEV BUILD`
  banner at startup and in `--selftest`, so a dev build can never be mistaken for a
  deployment build.

The build flag *is* the security boundary — not a runtime secret. `--client`, `--selftest`,
and `--once` are **not** dev-gated (they're not raw-addressing surfaces).

---

## 2. Producing a dev build

```powershell
# via the build script (warns that it's not a deployment build):
.\scripts\Build-BrokerSensorBridge.ps1 -DevProbes

# or directly:
dotnet build  BrokerSensorBridge\BrokerSensorBridge.csproj -c Release -p:DevProbes=true -o <dir>
dotnet publish BrokerSensorBridge\BrokerSensorBridge.csproj -c Release -p:DevProbes=true -o <dir>
```

Confirm you got a dev build: `BrokerSensorBridge.exe --selftest` prints the
`*** DEV BUILD … ***` banner before `SELFTEST PASS`.

The probes open `\\.\BrokerSmbus` directly, so they require an **elevated** shell and the
driver loaded (the in-kernel guards still apply — a probe can't write to SPD either).

---

## 3. Dev machine prerequisites

The dev box runs with relaxed driver-loading so a test-signed driver can load:

- **Memory Integrity / HVCI: OFF**, **Secure Boot: OFF**.
- **Test-signing: ON** (`bcdedit /set testsigning on`, reboot).
- **WDK 10.0.26100** installed via winget (the `.vcxproj`/MSBuild route fails `MSB8020`
  without the WDK VS extension — use `Build-Driver-DirectLink.ps1`, which locates Visual
  Studio via `vswhere`).

Build + load the driver:

```powershell
.\BrokerSmbusDriver\scripts\Build-Driver-DirectLink.ps1     # -> x64\Release\BrokerSmbus.sys, AUTO-test-signed (-NoSign to skip)
.\BrokerSmbusDriver\scripts\Install-Driver.ps1              # sc create type=kernel + start (elevated)
```

> `Sign-Driver-TestCert.ps1` runs automatically after the build. It only needs elevation for
> the **one-time** LocalMachine trust import — signing itself works non-elevated (test-signing
> mode loads the driver either way).
>
> The kernel service's ImagePath points at the build output, and a **loaded** driver
> link-locks that `.sys` — the rebuild fails with `LNK1104`. Stop the stack first
> (`.\scripts\Stop-BrokerServices.ps1`). Same for the broker exe if a broker is running.
> **Never leave the build output unsigned** — the service would fail to start (error 577).

---

## 4. The probes

All are elevated, and all redirect via `cmd` to capture WinExe stdout.

### `--smbus-read` — raw SMBus read
```
--smbus-read --bus=0 --addr=0x50 --cmd=0x02 [--size=byte|word|block] [--len=N]
```
SPD byte 2 reads `0x0C` on DDR4 / `0x12` on DDR5 — the canonical "is the read path alive"
check. Walk `--bus=0..3` to find which segment your DIMMs are on.

### `--smu-read` — AMD CPU temperature register
```
--smu-read [--tctl-offset=<°C>]
```
Reads the raw SMU reported-temp register and applies the `k10temp` decode. Compare to HWiNFO.

### `--superio-read` — Nuvoton Super-I/O temps + fans
```
--superio-read
```
Walks all temp/fan indices on the detected Super-I/O chip (NCT668x EC family or the NCT6775
bank-select family) and prints decoded values, so you can **map indices to board labels**
against HWiNFO before baking them into the calibration data.

### `--ene-read` — RGB bring-up, non-destructive
```
--ene-read
```
Proves the write path's brick-guard (a write to SPD `0x50` must come back `Forbidden`), does
an SPD page-0 reset, scans the primary buses, and reads the ENE device-name string at the
DRAM RGB addresses (`0x39/0x3A`). Run this **before** ever writing a color.

### `--ene-set` — RGB write (destructive-ish: changes your lights)
```
--ene-set --color=RRGGBB [--leds=N]
```
Writes a color to the ENE DRAM controllers and commits. Watch the RAM change.

> **USB-HID RGB discovery is *not* a DevProbe.** Motherboard ARGB headers (MSI Mystic Light) are
> found with `--hid-scan` in a **normal** build (read-only — enumerate a USB vendor's HID
> interfaces, print PID + feature-report length). It doesn't touch the kernel driver. Full board
> RGB bring-up: [RGB-BOARD-BRINGUP.md](RGB-BOARD-BRINGUP.md).

---

## 5. Hardware bring-up workflow (new board / new sensor)

This is the proven recipe — it's how CPU-temp, board-temps, and RGB were brought up:

1. **Prove the read path** with `--smbus-read` on SPD (`0x50`, non-destructive). Find the bus
   with your DIMMs. Never scan `0x36/0x37` (flips the SPD page).
2. **Map the source.** For a sensor: read the raw register with the relevant probe
   (`--smu-read` / `--superio-read`) and compare to a trusted tool (HWiNFO) to the degree.
   For RGB: `--ene-read` to confirm the controller address and brick-guard.
3. **Port the decode** (raw → units) from a proven source (`k10temp`, `nct6683`/`nct6687d`,
   JEDEC SPD — Linux-kernel references reproduced as hardware facts, see
   `THIRD-PARTY-NOTICES.md`; the ENE protocol is a publicly documented hardware protocol,
   reproduced as register facts). **Do not invent register encodings.**
4. **Bake it in.** Add the address to the kernel (a new named `BROKER_SMU_SENSOR` /
   Super-I/O index, or an RGB window), add the decode + catalog entry broker-side
   (see [CONTRIBUTING.md](CONTRIBUTING.md) §4–5).
5. **Verify through the broker** (non-dev path): `--client --op=sensor.read --id=<new>` /
   `--client --control --op=rgb.set` — confirm a non-admin client sees it, with the raw probe
   no longer needed.
6. **Rebuild without `DevProbes`** for the deployment artifact.

Detailed controller checklists: [`../BrokerSmbusDriver/BRINGUP-AMD-FCH.md`](../BrokerSmbusDriver/BRINGUP-AMD-FCH.md)
(AMD, primary) and [`../BrokerSmbusDriver/BRINGUP-i801.md`](../BrokerSmbusDriver/BRINGUP-i801.md)
(Intel, pending hardware).

---

## 6. Safety rules for bring-up

- **SPD is sacred.** Reads of `0x50–0x57` are fine; a *write* there can brick a DIMM. The
  kernel guard refuses RGB-window-only — keep it that way; widen deliberately and never into
  SPD / page-select (`0x36/0x37`) / DIMM-temp (`0x18–0x1F`).
- **Live SMBus can wedge the bus.** Close other RGB/monitoring apps (HWiNFO, vendor RGB
  tools) during bring-up — concurrent bus masters corrupt state. The driver serializes its own
  IOCTLs but can't stop another app's driver.
- **Page hygiene.** A broad scan that touches `0x37` latches DDR4 SPD to page 1 (persists
  across warm reboot). Address `0x36` to force page 0 first; `--ene-read` does this for you.
- **Bring up reads before writes**, always.
