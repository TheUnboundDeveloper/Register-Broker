# Adding your board: RGB & sensor profile bring-up

This is the **end-to-end walkthrough for collecting every piece of data Register Broker needs to
support a new motherboard** and turning it into a board profile. It is written so you can do it on
your own machine with the tools that ship in the box — no Wireshark, no kernel debugger required for
the common (USB-HID + DRAM) path.

A board profile is **signed code** (`BrokerSensorBridge/RgbCatalog.cs` for RGB,
`BrokerSensorBridge/calibration.default.json` for sensor/RGB *labels*). Adding one is a
**broker-only rebuild** — you never recompile or re-sign the kernel driver (the driver exposes only
stable, class-wide windows). Everything below produces values you paste into those two files.

> **Golden rule:** the broker maps *logical zones/ids → hardware*; clients only ever name the
> logical id. Addresses/registers live in signed code, **never** in the JSON. The JSON can only
> relabel/hide. If a step ever asks you to put an address in JSON, you're off the path.

---

## 0. What you're going to collect

Fill this worksheet in as you go (copy it into a scratch file):

```
BOARD
  DMI manufacturer        : ______________________________
  DMI product             : ______________________________

SENSORS
  Super-I/O chip id       : 0x____   (NCT668x EC family? NCT6775 family?)
  SMBus host vendor       : AMD FCH / Intel i801

DRAM RGB (ENE/SMBus)
  controller addresses    : 0x__ , 0x__      (LED count each: __)

MOTHERBOARD RGB (USB-HID, MSI Mystic Light)
  controller USB PID      : 0x____
  feature-report length   : ____   -> variant: 185 / 162 / 112
  header -> zone offset    : JRAINBOW1=31, JRGB=1, ...  (which header you want = which offset)

MOTHERBOARD RGB (NCT6687 EC) — advanced, optional
  RGB register window      : (left for a separate, validated effort)
```

## 1. Prerequisites

- The broker built and deployed (`scripts\Build-All.ps1`), services running. A normal,
  non-elevated PowerShell for the client; an elevated one only for reading the service log and
  stopping/starting services.
- **Close every vendor RGB app** before any write test — OpenRGB, MSI Center / Mystic Light,
  Dragon Center. They hold the controller open and stream frames; if they're running you can't tell
  whether *your* write worked. (Reading/enumeration is safe with them open; writing is not.)
- The binary is a **WinExe** (no console). To capture its output, redirect through `cmd /c`:
  `cmd /c ".\publish\BrokerSensorBridge\BrokerSensorBridge.exe <args> > out.txt 2>&1"` then
  `Get-Content out.txt`. (PowerShell's bare `>` can silently capture nothing.)

---

## 2. Board identity (DMI)

Everything keys off the board's DMI strings.

```powershell
cmd /c ".\publish\BrokerSensorBridge\BrokerSensorBridge.exe --calibration > calib.txt 2>&1"
Get-Content calib.txt
```

Copy the two strings from the `Board identity (DMI)` line **exactly** (including punctuation and the
`(MS-xxxx)` suffix) into the worksheet. These become the `Manufacturer`/`Product` of your
`RgbBoardProfile` and the `match` of your calibration `boards` entry.

`--calibration` also prints the **resolved sensor catalog** — every channel id/label/unit and
whether it's available. That's your sensor map; if labels are wrong or rails are mis-scaled, that's
what the calibration `boards` entry fixes (see `docs/CONTRIBUTING-CHIPSET.md` for sensor work).

## 3. Super-I/O chip & SMBus host (sensors)

In the **service startup log** (elevated shell) you'll see what the driver detected:

```powershell
Get-Content "C:\Windows\System32\config\systemprofile\AppData\Local\BrokerSensorBridge\bridge.log" -Tail 60
```

Look for:
```
[smbus] Detected backends: AMD FCH (0x790B), AMD SMU (0x1921), NCT668x EC (0xD592) | registered: ...
```
- The Super-I/O chip id (`0xD592` here) tells you the family (NCT668x EC vs NCT6775). Record it.
- The SMBus host vendor (AMD FCH / Intel i801) tells you which bus your DRAM RGB rides.

If your chip id isn't recognised, that's a *sensor* contribution — see `docs/CONTRIBUTING-CHIPSET.md`.
RGB bring-up below does not require it.

## 4. DRAM RGB (ENE/Aura over SMBus)

DRAM RGB is the one path that needs a **DevProbes** build (it pokes raw SMBus addresses, which the
normal build deliberately forbids). Build a dev binary to a temp dir — **never deploy it**:

```powershell
dotnet publish .\BrokerSensorBridge -c Release -p:DevProbes=true -o $env:TEMP\rb_dev
cmd /c "$env:TEMP\rb_dev\BrokerSensorBridge.exe --ene-read > ene.txt 2>&1"
Get-Content ene.txt
```

`--ene-read` is **non-destructive**: it proves the brick-guard (a write to SPD `0x50` must come back
`Forbidden`), resets the SPD page, scans the bus, and reads the ENE device-name string at the
candidate addresses. Record the addresses that return a sane ENE name (this board: `0x39`, `0x3A`)
and how many LEDs each stick has.

> The kernel only allows SMBus RGB writes inside `0x70–0x77` and `0x39–0x3A`. If your DRAM controller
> answers at a different address, the broker will *refuse* the zone at load (the window assertion) —
> that's the rare case that needs a deliberate kernel window-widen, not a profile edit.

Optional confirm (DevProbes, writes a color — RAM only, safe): `--ene-set --color=00FF00 --leds=5`.

## 5. Motherboard RGB over USB-HID (MSI Mystic Light) — the main path

This is the working motherboard-header path and needs **no DevProbes build**.

### 5a. Find the controller

```powershell
cmd /c ".\publish\BrokerSensorBridge\BrokerSensorBridge.exe --hid-scan > hid.txt 2>&1"
Get-Content hid.txt
```

Example (this board):
```
Found 2 interface(s) at VID 0x1462:
  PID 0x3FA4  featureReportLen=0     \\?\hid#vid_1462&pid_3fa4#...
  PID 0x7C92  featureReportLen=725   \\?\hid#vid_1462&pid_7c92#...
```
- The **RGB controller** is the one with a **non-zero feature-report length**. Here `0x7C92`
  (featureLen 725). The `featureReportLen=0` interface is a different MSI HID — ignore it.
- Tip: MSI RGB PIDs often encode the board model — `0x7C92` ↔ **MS-7C92**.
- Record the **PID** — you'll pin it so the broker drives only that device.

### 5b. Identify the protocol variant

`HidP_GetCaps` reports the device's **largest** feature report across all report ids, so a big number
(725) is the device max, not the packet size. Classify it the way the protocol does:

| feature-report length | Variant | Packet size sent | Implemented? |
|---|---|---|---|
| ≥ 185 | 185-byte (report id `0x52`) | 185 | ✅ yes |
| ≥ 162 | 162-byte | 162 | ⛔ not yet |
| ≥ 112 | 112-byte | 112 | ⛔ not yet |

725 ≥ 185 → **185-byte variant**. (If your board lands on 162/112, the controller needs that variant
ported — open an issue with the feature length.)

### 5c. Map your header to a zone offset

The 185-byte feature packet carries **every zone at a fixed byte offset**. Pick the offset for the
header your strip is plugged into:

| Header | Field | Byte offset (`HidZoneIndex`) |
|---|---|---|
| JRGB 1 (12V) | `j_rgb_1` | **1** |
| JRGB 2 (12V) | `j_rgb_2` | **174** |
| JRAINBOW 1 (5V ARGB) | `j_rainbow_1` | **31** |
| JRAINBOW 2 (5V ARGB) | `j_rainbow_2` | **42** |
| JPIPE 1 / 2 | `j_pipe_1` / `j_pipe_2` | 11 / 21 |
| On-board LEDs | `on_board_led[..9]` | 74, 84, 94 … 164 |

Each zone block is `effect[+0] R[+1] G[+2] B[+3] brightness[+4] … colorFlags[+8]`. The controller
writes a static color (effect = `MSI_MODE_STATIC`, colorFlags bit 7 = fixed color) and **seeds the
packet from the device's current state first**, so setting your zone never turns the others off.

> Not sure which physical header is which offset? Set one, watch which strip changes, adjust. Start
> with `j_rainbow_1` (31) for a 5V ARGB strip or `j_rgb_1` (1) for a 12V strip.

### 5d. (after first light) tune brightness

If the LEDs light but are dim/off, the one tunable is `StaticBrightnessFlags` in
`MysticLightHidController` (`(brightness << 2) | speed`). The default is near-max for the 5-bit
brightness field; raise/lower if needed.

## 6. Motherboard RGB over the NCT6687 EC — advanced / optional

The 12V JRGB header can alternatively be driven through the NCT6687 EC (kernel path, fully
brick-guarded). **This is not turn-key:** the NCT6687 RGB register window is not yet validated, so
the driver ships it **inert** (`SuperioRgbImplemented = FALSE`, `CAP_SUPERIO_RGB` off). Enabling it
is the *one* deliberate driver change in this whole guide and requires:
1. Confirming the EC RGB register page/offsets on hardware (a careful, no-brick effort).
2. Setting the validated `BROKER_NCT6687_RGB_ADDR_*` window **and** `SuperioRgbImplemented = TRUE`
   in `SuperioNctDetect` (`BrokerSmbusDriver/SuperioNct.c`).
3. Rebuilding + **re-signing** the driver (`Build-All.ps1 -Driver`) and re-validating.

Most boards are fully covered by USB-HID; pursue the EC path only if you specifically want the 12V
header without USB-HID. Until then, leave the `Mb12V` zone as-is — it's listed for parity but stays
inert.

---

## 7. Write the board profile

In `BrokerSensorBridge/RgbCatalog.cs`, add an `RgbBoardProfile` **above** the generic fallback. Fill
the standard zone vocabulary where the hardware exists (`Dram`, `Mb12V`, `MbArgb`) so the parity
selftest passes; omit a kind only if the board genuinely lacks it.

```csharp
new RgbBoardProfile(
    "<DMI manufacturer>",                 // from step 2, exact
    "<DMI product>",                      // from step 2, exact
    new[]
    {
        new RgbZone("ram0", "DRAM 0", RgbZoneKind.Dram, RgbTransport.SmbusEne, LedCount: 5, Bus: 0, Address: 0x39),
        new RgbZone("ram1", "DRAM 1", RgbZoneKind.Dram, RgbTransport.SmbusEne, LedCount: 5, Bus: 0, Address: 0x3A),

        // 12V header via EC — inert until the EC RGB window is validated (section 6).
        new RgbZone("mb.jrgb0", "12V Header (JRGB)", RgbZoneKind.Mb12V, RgbTransport.SuperioEc, LedCount: 1, EcAddress: 0x0F00),

        // Addressable header via USB-HID. HidProductId = the PID from 5a; HidZoneIndex = the offset from 5c.
        new RgbZone("mb.argb0", "ARGB Header (JRAINBOW)", RgbZoneKind.MbArgb, RgbTransport.UsbHid,
                    LedCount: 60, HidZoneIndex: 31, HidProductId: 0x____),
    }),
```

Rules the build enforces for you:
- **Window assertion** — every `SmbusEne` `Address` must be in `0x39–0x3A`/`0x70–0x77`; every
  `SuperioEc` `EcAddress` in the NCT6687 window. Out-of-window zones are refused at load (and fail
  the selftest), so you can't accidentally point a zone at SPD or a sensor bank.
- **Parity** — non-generic profiles should cover the standard kinds where the hardware exists.
- **Unique ids per profile**; `LedCount ≥ 1`.

## 8. Labels (optional, JSON — never addresses)

In `calibration.default.json`, under your board's `match`, relabel zones by id (and rails likewise):

```json
{ "rawId": "mb.argb0", "label": "Front ARGB Fans" },
{ "rawId": "mb.jrgb0", "label": "Case 12V Strip" }
```
`hidden: true` drops a zone from `rgb.list`. Labels only — there is no address field here, by design.

## 9. Build, deploy, validate

```powershell
# broker-only rebuild — NO driver recompile, NO re-sign (unless you did section 6)
.\scripts\Stop-BrokerServices.ps1            # elevated (releases the .exe lock)
.\scripts\Build-BrokerSensorBridge.ps1       # republish
.\scripts\Start-BrokerServices.ps1           # elevated
```

**Enable USB-HID on the service** (it's the *service* that registers `mb.argb0`, not the client —
`--allow-hid-rgb` on a client invocation does nothing). Easiest is to install with the switch:

```powershell
.\scripts\Install-SensorBrokerService.ps1 -WithHidRgb     # writes AllowHidRgb=true, implies -WithRgbControl
```

Or set `"AllowHidRgb": true` in `publish\BrokerSensorBridge\appsettings.json` **after** the republish
(the build overwrites it) and restart `BrokerControl`.

Then, from a **non-elevated** shell with **OpenRGB/MSI Center closed**:

```powershell
# confirm the zone shows up (no --allow-hid-rgb here — it's a service setting)
cmd /c ".\publish\BrokerSensorBridge\BrokerSensorBridge.exe --client --control --op=rgb.list > rgb.txt 2>&1"
Get-Content rgb.txt
# light the addressable header green
cmd /c ".\publish\BrokerSensorBridge\BrokerSensorBridge.exe --client --control --op=rgb.set --device=mb.argb0 --color=00FF00 > set.txt 2>&1"
```

Expected: `rgb.list` shows your zones with `kind`/`transport`; the `rgb.set` lights the header.

The service log should show the profile and the pinned HID device:
```
[rgb] board profile: <mfr> / <product>, N zone(s)
[rgb] Mystic Light HID: 1 device(s) at VID 0x1462 [candidate PIDs: 0x7C92] ...
[rgb] zone 'mb.argb0' -> HID PID 0x7C92 (pinned)
[rgb] registry: 3 device(s) [ram0, ram1, mb.argb0]
```

## 10. Safety & gotchas

- **Close vendor RGB software before writing.** USB-HID can't brick the board (you're talking to a
  controller designed to receive these reports), but two apps fighting = flicker + false test results.
- **USB-HID is reduced-assurance** (no kernel brick-guard) — that's why it's opt-in. The PID pin and
  the baked report builder are its boundaries; the window assertion does not apply to it.
- **SMBus/EC are kernel-guarded** — a profile can never make the driver write outside the RGB
  windows; the broker also refuses out-of-window zones up front.
- **Never deploy a DevProbes build** — it prints a loud `DEV BUILD` banner; it's for bring-up only.
- **WinExe output**: always capture via `cmd /c "... > file"`; a bare PowerShell `>` may capture
  nothing.

## 11. PR checklist

- [ ] DMI strings exact; profile above the generic fallback
- [ ] Addresses/PIDs in signed code (`RgbCatalog.cs`); JSON has labels only
- [ ] `--selftest` green (catalog validates, parity, window assertion, PID pin)
- [ ] Evidence: `--hid-scan` output, `rgb.list`/`rgb.set` proof per transport (or 🟡 if unvalidated)
- [ ] USB-HID zones noted as opt-in; no driver recompile required (unless you enabled the EC path)
