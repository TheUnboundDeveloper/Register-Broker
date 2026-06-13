# Getting Started

Register Broker is a **framework with no GUI**. You build two pieces (a kernel driver and a broker service), install them, and then any non-admin program — including the bundled command-line client — reads sensors and drives RGB over a named pipe.

> **Honest status.** This is a working, hardware-validated project, **not yet a polished consumer install**. The kernel driver is currently **dev/test-signed**, so it only loads on a machine with **test-signing on** and **Memory Integrity (HVCI) off**. Running on a normal, locked-down user machine needs production driver signing — see [Architecture & Security](Architecture-and-Security#help-wanted-production-signing).

---

## 1. What you need

- **Windows x64** (Windows 10/11)
- **.NET 8 SDK** — `winget install Microsoft.DotNet.SDK.8`
- **WDK 10.0.26100** — `winget install Microsoft.WindowsWDK.10.0.26100` — plus Visual Studio (or Build Tools) with the C++ x64 toolset
- For the driver to load today: **test-signing enabled** and **Memory Integrity off**

The driver is **required** — every sensor and RGB path goes through `BrokerSmbus`. There are no third-party hardware libraries.

---

## 2. Build

From the repo root, in an **elevated** PowerShell:

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force

# 1) Build the broker (.NET; default when no switch is given):
.\scripts\Build-All.ps1 -Clean

# 2) Build the kernel driver (auto-test-signs the .sys):
.\scripts\Stop-BrokerServices.ps1     # a LOADED driver locks the .sys
.\scripts\Build-All.ps1 -Driver       # -> BrokerSmbusDriver\x64\Release\BrokerSmbus.sys
bcdedit /set testsigning on            # one-time, then REBOOT
```

The broker publishes to `publish\BrokerSensorBridge\`. Normal builds deliberately **exclude** the raw hardware probes — that's correct for a usable build.

---

## 3. Install the services

```powershell
# Sensor broker + driver only:
.\scripts\Install-SensorBrokerService.ps1

# …or also install the write-only RGB control service:
.\scripts\Install-SensorBrokerService.ps1 -WithRgbControl
```

This registers three auto-starting services:

| Service | Display name | What it does | Pipe |
|---|---|---|---|
| `BrokerSmbus` | Register Broker SMBus Driver | the kernel driver | — |
| `SensorBroker` | Register Broker Sensor Service | reads sensors, serves them | `\\.\pipe\SensorBroker` |
| `BrokerControl` (optional) | Register Broker RGB Control | RGB writes only | `\\.\pipe\BrokerControl` |

The installer runs a **signature preflight** — it refuses to tear down the running services if the new `.sys` is unsigned or tampered. Confirm `SensorBroker` shows **Running**; if it didn't start, the driver almost certainly didn't load (test-signing / Secure Boot / Memory Integrity).

To remove everything: `.\scripts\Uninstall-SensorBrokerService.ps1`.

---

## 4. Read sensors (as a non-admin user)

Open a **normal, non-elevated** terminal. The broker exe is a GUI-subsystem app, so let `cmd` own the output redirect:

```powershell
cd "publish\BrokerSensorBridge"

# List the catalog:
cmd /c ".\BrokerSensorBridge.exe --client --op=sensor.list > out.txt 2>&1"
type out.txt

# Read one sensor by name:
cmd /c ".\BrokerSensorBridge.exe --client --op=sensor.read --id=cpu.temp > out.txt 2>&1"
type out.txt          # -> cpu.temp = 56.6 °C
```

You name a **logical sensor** (`cpu.temp`, `board.vrm.temp`, `fan3`) — never a hardware address. The `elevated=False` line in the output is the proof: a non-admin process just read privileged sensor data. Use `--op=sensor.readall` for polling the whole catalog in one operation.

---

## 5. Control RGB (as a non-admin user)

Requires the control service (`-WithRgbControl`) and a driver with the write capability.

```powershell
cd "publish\BrokerSensorBridge"

# What can I control?
cmd /c ".\BrokerSensorBridge.exe --client --control --op=rgb.list > out.txt 2>&1"
type out.txt          # -> ram0, ram1, …

# Set a device to green (RRGGBB hex):
cmd /c ".\BrokerSensorBridge.exe --client --control --op=rgb.set --device=ram0 --color=00FF00 > out.txt 2>&1"
type out.txt          # -> rgb.set OK: ram0 -> #00FF00
```

You name a **device** (`ram0`), never an address. The kernel refuses to write anywhere outside the known RGB range, so it cannot corrupt your RAM's SPD.

> **RGB scope:** DRAM (ENE/Aura over SMBus, validated) and MSI Mystic Light motherboard ARGB headers (USB-HID, opt-in via `AllowHidRgb`). It's **colors only** — effects (breathing, rainbow, music sync) are the consumer app's job. GPUs and AIOs are not supported.

---

## 6. Building your own client

The named-pipe protocol is small and language-agnostic — a named pipe plus a JSON parser, no crypto, no secret to manage. See the [Client Protocol](Client-Protocol) page for the full wire contract and a minimal client flow.

---

## Troubleshooting (common ones)

| Symptom | Fix |
|---|---|
| `out.txt` is empty | You used PowerShell's `>` on a GUI-subsystem app. Use `cmd /c "… > out.txt 2>&1"`. |
| "Connect failed (is the broker running?)" | `SensorBroker` isn't running; check `Get-Service BrokerSmbus` started. |
| Broker service won't start | The driver didn't load — confirm test-signing on, Memory Integrity off, rebooted. |
| RGB ops all `deny` | Control service not installed (`-WithRgbControl`), or driver lacks the write capability. |

Service logs (LocalSystem) live under `C:\Windows\System32\config\systemprofile\AppData\Local\BrokerSensorBridge\` (`bridge.log` + `audit.log`).
