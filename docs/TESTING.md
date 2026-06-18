# Hardware validation guide

How to test Register Broker on your board and report results — the step-by-step for
the community validation campaign. Several backends are register-correct ports from
proven sources but have **never run on real silicon**; one tester with the right board
moves a 🟡 to a ✅ for everyone.

## What needs validation

| Backend | Boards that have it | Status |
|---|---|---|
| **Nuvoton NCT6775 family** (6779/6791/6792/6793/6795/6796/6797/6798) | most ASUS, ASRock, EVGA, Biostar; many Gigabyte | 🟡 implemented, unvalidated |
| **Nuvoton NCT6683 / NCT6686** (EC family) | many MSI (NCT6687D is ✅ validated) | 🟡 implemented, unvalidated |
| **Intel i801 SMBus host** | any Intel chipset board | ⬜ written, unvalidated (read path) |
| **AMD SMU SVI2 voltages** (`smu.cpu.vcore`, `smu.soc.voltage`) | Ryzen Matisse (17h/0x71, Zen 2) / Vermeer (19h/0x21, Zen 3) desktop | ✅ Vermeer (5800X3D) · 🟡 Matisse (built, unvalidated) |

Full status detail: [SENSOR-CHIPSET-INVENTORY.md](SENSOR-CHIPSET-INVENTORY.md).

**Is it safe?** Sensor validation is **read-only** — the failure mode of an unvalidated
read is a wrong number on your screen, never hardware damage. The only write path in
the whole stack is the DRAM-RGB window, gated in the kernel, and it is not part of
sensor testing. The one real caveat is below.

## The caveat: test signing

Until production code-signing lands, the kernel driver is **test-signed**: your machine
must run with Windows *test-signing mode* enabled (and Memory Integrity / HVCI off,
Secure Boot off). That is a real reduction of your machine's security posture — use a
bench/secondary box if you have one, and turn it back off afterward:

```powershell
bcdedit /set testsigning on    # elevated, then reboot (off: /set testsigning off)
```

## Setup (≈15 minutes)

You need: Windows 11 x64 · .NET 8 SDK · Visual Studio Build Tools (C++ x64 workload) ·
WDK (`winget install Microsoft.WindowsWDK.10.0.26100`).

```powershell
git clone <repo> ; cd "Register Broker"
.\scripts\Build-All.ps1 -Driver -Bridge          # elevated; driver auto-test-signs
.\scripts\Install-SensorBrokerService.ps1        # elevated; refuses an unsigned driver
```

Details and troubleshooting: [USER-GUIDE.md](USER-GUIDE.md).

## Validation steps

Run all client steps from a **normal, non-elevated** terminal in
`publish\BrokerSensorBridge` (use the `cmd /c` redirect — the exe has no console):

1. **Identify what was detected.**
   ```powershell
   cmd /c ".\BrokerSensorBridge.exe --calibration > calib.txt 2>&1"
   ```
   Note the board DMI identity, the detected Super-I/O **chip id**, and which channels
   resolved. The service log
   (`C:\Windows\System32\config\systemprofile\AppData\Local\BrokerSensorBridge\bridge.log`,
   admin to read) also prints the registry view, e.g.
   `Detected backends: AMD FCH (0x790B), AMD SMU (0x1921), NCT668x EC (0xD592) | registered: …`.

2. **Read everything.**
   ```powershell
   cmd /c ".\BrokerSensorBridge.exe --client --op=sensor.readall > readall.txt 2>&1"
   ```
   Every channel should return a value or an honest status — no hangs, no service crash.

3. **Compare against a known-good monitor.** Run [HWiNFO](https://www.hwinfo.com/)
   side-by-side and compare:
   - temperatures: agree **to the degree** (sensor naming may differ — match by value
     and behavior, not label);
   - fan RPM: agree within a few RPM at steady state;
   - voltages: compare the obvious rails (+12V, +5V, VCore). A raw pin reading that is
     an exact divisor of HWiNFO's value (e.g. 1.024 V vs 12.29 V = ×12) is a missing
     **calibration scale**, not a wrong register — report it as such; that's data, not code.

4. **Exercise it.** Put some load on the system (a stress test or a game) and confirm
   temps/fans in `sensor.readall` track HWiNFO's movement over a few minutes.

## Reporting

Open an issue on the **Hardware Validation Campaign** tracking issue (or a new issue
titled `Validation: <board model>`) with:

```
Board:        <vendor + exact model, e.g. ASUS ROG STRIX B550-F GAMING>
DMI identity: <from --calibration>
Chip id:      <from --calibration, e.g. 0xD42A>
Driver log:   <the "Detected backends: …" line>
CPU / RAM:    <model + DIMM count>

Result table (broker value vs HWiNFO value, one row per sensor):
  nct6775.temp.0   38.0 °C   vs  HWiNFO "Motherboard" 38 °C    MATCH
  nct6775.fan.1    644 RPM   vs  HWiNFO "CPU Fan" 645 RPM      MATCH
  nct6775.volt.2   3.392 V   vs  HWiNFO "+3.3V" 3.392 V        MATCH
  ...

Anomalies: <wrong values, missing channels, errors in readall.txt, log excerpts>
Files:     attach calib.txt + readall.txt
```

A *failed* validation is exactly as valuable as a pass — "chip id 0xD42A detected but
all temps read 0" pinpoints a register-map fact to re-check against the Linux source.

## For contributors porting a NEW chip

This guide validates existing backends. To add a chip that isn't covered at all,
follow [CONTRIBUTING-CHIPSET.md](CONTRIBUTING-CHIPSET.md) — and then run this same
validation procedure on the result.
