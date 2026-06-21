# User Guide — reading sensors & controlling RGB without admin

This guide gets you from a fresh checkout to **a non-admin program reading your CPU/board
temperatures and changing your RAM's RGB color**, with the privileged work isolated in one
small service.

> **Honest status (read this first).** This is a working, hardware-validated project, **not
> yet a polished consumer install.** Two things matter for you:
> 1. The everyday path in this guide is a small command-line client
>    (`BrokerSensorBridge.exe --client …`); other applications consume the same named-pipe
>    protocol ([CLIENT-PROTOCOL.md](CLIENT-PROTOCOL.md)). There **is** also a first-party
>    GUI — the **[Reference Console](REFERENCE-CONSOLE.md)** (.NET 10 + Avalonia) — that reads
>    sensors and drives RGB through the broker with no elevation; it's the demonstrator, built
>    separately (see that doc for requirements).
> 2. The kernel driver is currently **dev/test-signed**, which means it only loads on a
>    machine with **test-signing on** (and HVCI/Memory Integrity off). Running on a normal,
>    locked-down user machine needs **production driver signing** — that work and its
>    requirements are described in [SIGNING-AND-DEPLOYMENT.md](SIGNING-AND-DEPLOYMENT.md).
>
> So today this is usable on a **developer/enthusiast machine you control**. The end goal is
> a properly-signed install that runs anywhere; we're building toward it.

---

## 1. What you need

- **Windows x64** (Windows 10/11).
- **.NET 10 SDK** (to build the broker) — `winget install Microsoft.DotNet.SDK.10`.
- **WDK 10.0.26100** (to build the driver) — `winget install Microsoft.WindowsWDK.10.0.26100`
  — plus Visual Studio (or Build Tools) with the C++ x64 toolset.
- For the driver to load today: **test-signing enabled** and **Memory Integrity off** (see
  the status note above).

The driver is **required**: every sensor and RGB path goes through `BrokerSmbus` (CPU temps
via SMU, board temps/fans/voltages via Super-I/O, DIMM temps and RGB via SMBus). There are
no third-party hardware libraries — no kernel-driver-based monitoring DLLs to vendor.

---

## 2. Build

From the repo root, in an **elevated** PowerShell (publishing the broker stops the running
services, and the service steps need admin):

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force

# 1) Build the broker (.NET; default when no switch is given):
.\scripts\Build-All.ps1 -Clean

# 2) Build the kernel driver (auto-test-signs the .sys; pass -NoSign to skip):
.\scripts\Stop-BrokerServices.ps1            # a LOADED driver locks the .sys (link fails otherwise)
.\scripts\Build-All.ps1 -Driver              # -> BrokerSmbusDriver\x64\Release\BrokerSmbus.sys
bcdedit /set testsigning on                  # one-time, then REBOOT
```

The broker publishes to `publish\BrokerSensorBridge\`. (`Build-All.ps1` wraps
`Build-BrokerSensorBridge.ps1` and `BrokerSmbusDriver\Build-Driver-DirectLink.ps1`, which you
can also run individually.)

> A normal build deliberately **excludes** the raw hardware probes — that's correct for a
> usable build. You don't need them. (They're a developer-only bring-up tool; see
> [DEV-GUIDE.md](DEV-GUIDE.md).)

---

## 3. Install the services

One installer registers everything as auto-starting Windows services:

```powershell
# Sensor broker + driver only:
.\scripts\Install-SensorBrokerService.ps1

# …or also install the write-only RGB control service:
.\scripts\Install-SensorBrokerService.ps1 -WithRgbControl
```

This creates:

| Service | Display name | What it does | Pipe |
|---|---|---|---|
| `BrokerSmbus` | Register Broker SMBus Driver | the kernel driver | — |
| `SensorBroker` | Register Broker Sensor Service | reads sensors, serves them | `\\.\pipe\SensorBroker` |
| `BrokerControl` (optional) | Register Broker RGB Control | RGB writes only | `\\.\pipe\BrokerControl` |

The installer runs a **signature preflight** first: it refuses to tear down the existing
services if the new `.sys` is unsigned or tampered (so a bad rebuild can't leave you with no
services at all). It prints a status table — confirm `SensorBroker` shows **Running**. If it
didn't start, the driver almost certainly didn't load (test-signing/Secure-Boot); the
installer prints an explicit message when that happens.

For a hardened install, `-Deploy` copies the broker + driver into `%ProgramFiles%\SensorBroker`
(admin-only writable) and registers the services from there, instead of running them out of
the user-writable `publish\` tree.

To remove everything later: `.\scripts\Uninstall-SensorBrokerService.ps1`.

---

## 4. Read sensors (as a non-admin user)

Open a **normal, non-elevated** terminal. Because the broker exe has no console window, let
`cmd` own the output redirect so you can see it:

```powershell
cd "publish\BrokerSensorBridge"

# List the sensors the broker offers (the "catalog"):
cmd /c ".\BrokerSensorBridge.exe --client --op=sensor.list > out.txt 2>&1"
type out.txt

# Read one sensor by name:
cmd /c ".\BrokerSensorBridge.exe --client --op=sensor.read --id=cpu.temp > out.txt 2>&1"
type out.txt          # -> cpu.temp = 56.6 °C
```

You name a **logical sensor** (`cpu.temp`, `board.vrm.temp`, `fan3`) — never a hardware
address. The full list of names is in [SENSOR-MAP.md](SENSOR-MAP.md). The `elevated=False`
line in the output is the proof: a non-admin process just read privileged sensor data.

`--op=ping` is a quick "is the broker alive?" check; `--op=sensor.readall` reads every
catalog sensor in one operation (what a polling consumer should use). `--calibration` is an
offline, no-admin inspector that prints your board's DMI identity and the resolved catalog
(labels/scales come from `calibration.default.json` plus an optional
`C:\ProgramData\SensorBroker\calibration.user.json`).

---

## 5. Control RGB (as a non-admin user)

Requires the control service (`-WithRgbControl` at install) and a driver with the write
capability.

> **RGB scope today:** DRAM modules (ENE/Aura over SMBus, validated on G.Skill DDR4), **motherboard
> ARGB headers** (MSI Mystic Light over USB-HID — opt-in, validated on MSI B550I), **and Razer Chroma
> peripherals** (keyboards/mice over USB-HID — opt-in, board-independent, validated on Naga Trinity
> + Cynosa Chroma). The 12V JRGB header via the NCT6687 EC is wired but inert pending validation. GPUs and AIOs are
> not supported. It's **colors only**: effects (breathing, rainbow, music sync) are the consumer
> app's job — render frames and send `rgb.set` updates at your own rate. The broker hosts no
> effects engine. Full command reference: [RGB-COMMANDS.md](RGB-COMMANDS.md); full scope
> statement: the "RGB status" section of the main [README](../README.md).
>
> Motherboard headers (`mb.argb0`) are **off by default** — enable `AllowHidRgb` in the control
> service's `appsettings.json` (see [RGB-BOARD-BRINGUP.md](RGB-BOARD-BRINGUP.md) §9) and close
> other RGB apps (OpenRGB / MSI Center) before driving them.

```powershell
cd "publish\BrokerSensorBridge"

# What can I control?
cmd /c ".\BrokerSensorBridge.exe --client --control --op=rgb.list > out.txt 2>&1"
type out.txt          # -> ram0, ram1, …

# Set a device to green (RRGGBB hex):
cmd /c ".\BrokerSensorBridge.exe --client --control --op=rgb.set --device=ram0 --color=00FF00 > out.txt 2>&1"
type out.txt          # -> rgb.set OK: ram0 -> #00FF00
```

Again, you name a **device** (`ram0`), never an address. The broker holds a baked map of
which device is which controller; the kernel refuses to write anywhere outside the known RGB
range (so it cannot corrupt your RAM's SPD).

---

## 6. Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| `out.txt` is empty | You ran the WinExe under PowerShell's `>` redirect. Use `cmd /c "… > out.txt 2>&1"` (PowerShell doesn't reliably capture a GUI-subsystem app's stdout). |
| "Connect failed (is the broker running?)" | `SensorBroker` service isn't running. `Get-Service SensorBroker`; check the driver started (`Get-Service BrokerSmbus`). |
| Broker service won't start | The driver didn't load. Confirm `bcdedit` shows test-signing on, Memory Integrity is off, and you rebooted after `Sign-Driver-TestCert.ps1`. |
| "Broker closed the connection without authorizing" | Enforcement is on and your client isn't allowlisted/signed. Either run audit-only (default) or add your client's signer/path (see [REFERENCE.md](REFERENCE.md) → `RequireAuthorizedClient`). |
| `sensor.list` shows fewer sensors than expected | The driver didn't report a capability — CPU-SMU/Super-I/O/SMBus sensors only appear when the driver advertises them and the chip is detected. All sensors come through the driver; check `Get-Service BrokerSmbus` and the bridge log. |
| RGB ops all `deny` | The control service isn't installed (`-WithRgbControl`) or the driver lacks the write capability. |
| `°C` shows as mojibake | Cosmetic console codepage issue; the data is correct. PowerShell's `type` (Get-Content) renders it fine; raw `cmd type` may not. |

**Where the logs are.** When the broker runs as a service (LocalSystem) its logs are under
the *service* profile, not yours:
`C:\Windows\System32\config\systemprofile\AppData\Local\BrokerSensorBridge\bridge.log`
(and `audit.log` beside it — every connect, auth decision, and operation).

---

## 7. What's safe, and what isn't (yet)

- **Safe:** the broker is the only elevated piece; your apps stay non-admin. Clients can only
  name pre-mapped sensors/devices — no scanning, no raw addresses. The driver refuses RGB
  writes to anything outside the known RGB range (SPD is protected in the kernel).
- **Not production-ready:** the driver is test-signed (lab machines only), and enforcement of
  *which* clients may connect defaults to **audit-only** (logs everyone, blocks no one). To
  lock it to specific signed clients, see `RequireAuthorizedClient` in
  [REFERENCE.md](REFERENCE.md). The road to a properly-signed, lock-anywhere install is
  [SIGNING-AND-DEPLOYMENT.md](SIGNING-AND-DEPLOYMENT.md).
