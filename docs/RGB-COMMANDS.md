# RGB commands & syntax

How to drive RGB with the bundled client (`BrokerSensorBridge.exe --client`). This is the
**command-line** reference — how to put a command together and what each part means. For the raw
wire protocol (for building your own app), see [`CLIENT-PROTOCOL.md`](CLIENT-PROTOCOL.md); to add a
new board's zones, see [`RGB-BOARD-BRINGUP.md`](RGB-BOARD-BRINGUP.md).

RGB lives on the **control service** (`\\.\pipe\BrokerControl`). Everything below is **non-admin** —
no elevation needed.

---

## 1. Quick start

```powershell
# list what you can control
.\publish\BrokerSensorBridge\BrokerSensorBridge.exe --client --control --op=rgb.list

# set DIMM 0 to green
.\publish\BrokerSensorBridge\BrokerSensorBridge.exe --client --control --op=rgb.set --device=ram0 --color=00FF00
```

> **Capturing output:** the broker is a windowed app (WinExe). A bare PowerShell `>` redirect can
> capture *nothing*. To save/inspect output, wrap it in `cmd /c`:
> ```powershell
> cmd /c ".\publish\BrokerSensorBridge\BrokerSensorBridge.exe --client --control --op=rgb.list > out.txt 2>&1"
> Get-Content out.txt
> ```
> Run straight (no redirect) and it prints to the console fine; you only need `cmd /c` when saving to a file.

---

## 2. Anatomy of a command

```
BrokerSensorBridge.exe  --client  --control  --op=<operation>  [ --device=<id> ]  [ --color=<RRGGBB> ]
                        \_______/  \_______/  \______________/  \_____________________________________/
                         required   required    which op           op-specific arguments
                        (client    (RGB pipe;
                         mode)      omit = sensors)
```

| Flag | Meaning | Default |
|---|---|---|
| `--client` | Run as a client (connect to the broker). **Required.** | — |
| `--control` | Talk to the **RGB** control pipe. Omit it and you're on the sensor pipe. | sensor pipe |
| `--op=<op>` | The operation (see §3). | `rgb.list` (with `--control`) |
| `--device=<id>` | Target zone id for `rgb.set` (from `rgb.list`). | `ram0` |
| `--color=<RRGGBB>` | Color for `rgb.set`, 6 hex digits, no `#`. | `00FF00` |
| `--scopes=<list>` | Scopes requested at handshake (comma-separated). | `rgb:write` |

Order of flags doesn't matter. Anything the server doesn't recognise comes back as a `deny`.

---

## 3. Operations

### `rgb.list` — discover controllable zones
```powershell
... --client --control --op=rgb.list
```
Prints each zone's id, label, and LED count, e.g.:
```
RGB devices (3):
  ram0     GSkill RGB (DIMM 0) [5 LEDs]
  ram1     GSkill RGB (DIMM 1) [5 LEDs]
  mb.argb0 Front ARGB Fans (JRAINBOW) [60 LEDs]
```
The **id** (`ram0`, `mb.argb0`) is what you pass to `--device`. On the wire each zone also carries a
`kind` (`dram`/`mb12v`/`mbargb`) and `transport` (`smbusene`/`superioec`/`usbhid`) for apps that want
to group them; the CLI display omits those.

### `rgb.set` — set a zone to one color
```powershell
... --client --control --op=rgb.set --device=<id> --color=<RRGGBB>
```
Sets the whole zone to one solid color. Success prints `rgb.set OK: <id> #<color>`. The color is
applied immediately; there is no "apply" step.

### `ping` — check the service is alive
```powershell
... --client --control --op=ping        ->  pong (broker alive)
```

---

## 4. Color format

Six hex digits, `RRGGBB`, **no `#`**:

| Color | Code | | Color | Code |
|---|---|---|---|---|
| Red | `FF0000` | | White | `FFFFFF` |
| Green | `00FF00` | | Warm white | `FFA040` |
| Blue | `0000FF` | | Off | `000000` |
| Yellow | `FFFF00` | | Purple | `800080` |
| Cyan | `00FFFF` | | Orange | `FF6000` |

---

## 5. Cookbook

```powershell
# set the path once for brevity
$rb = ".\publish\BrokerSensorBridge\BrokerSensorBridge.exe"

# everything red
& $rb --client --control --op=rgb.set --device=ram0     --color=FF0000
& $rb --client --control --op=rgb.set --device=ram1     --color=FF0000
& $rb --client --control --op=rgb.set --device=mb.argb0 --color=FF0000

# turn the motherboard ARGB header off
& $rb --client --control --op=rgb.set --device=mb.argb0 --color=000000

# a simple breathing loop on the DIMMs (effects are client-side — see §7)
foreach ($v in 0,64,128,192,255,192,128,64) {
    $hex = '{0:X2}0000' -f $v
    & $rb --client --control --op=rgb.set --device=ram0 --color=$hex
    Start-Sleep -Milliseconds 80
}
```

---

## 6. Enabling the motherboard-header zones

DRAM zones (`ram0`/`ram1`) work out of the box. The **motherboard headers** are gated:

- **`mb.argb0` (USB-HID JRAINBOW)** is opt-in. It only appears in `rgb.list` when the **control
  service** has `AllowHidRgb` enabled. Easiest: install with **`-WithHidRgb`**
  (`.\scripts\Install-SensorBrokerService.ps1 -WithHidRgb`) — it writes the flag for you. Or set
  `"AllowHidRgb": true` in `publish\BrokerSensorBridge\appsettings.json` and restart the service.
  (The `--allow-hid-rgb` CLI flag affects only the *service* process, not a client invocation.)
  Close OpenRGB / MSI Center before driving it. Full steps:
  [`RGB-BOARD-BRINGUP.md`](RGB-BOARD-BRINGUP.md) §5 / §9.
- **`mb.jrgb0` (12V EC header)** stays inert until the NCT6687 EC RGB register window is validated and
  enabled in the driver — see the bring-up guide §6.

If a zone isn't in `rgb.list`, it isn't enabled — there's nothing to "set."

---

## 7. What `rgb.set` does *not* do

- **No effects.** `rgb.set` writes a solid color and nothing else — there is no breathing/rainbow/
  music op. Animation is the **client's** job: render frames and send `rgb.set` at your own cadence
  (the control service allows 120 ops/s, burst 240, so frame updates aren't rate-limited). This keeps
  the privileged surface tiny.
- **No per-LED from the CLI.** The bundled client sends one color per zone. The wire protocol *does*
  support a per-LED `colors` array (see `CLIENT-PROTOCOL.md` §6) for custom apps — but note the
  motherboard USB-HID zone currently collapses per-LED to the lead color (per-LED streaming for that
  header is a future item). DRAM per-LED is fully supported on the wire.

---

## 8. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `rgb.set denied/failed: {"type":"deny"}` | unknown `--device` (not in `rgb.list`), bad `--color`, or no `rgb:write` | run `rgb.list` first; use an id it shows; check the color is 6 hex digits |
| `mb.argb0` missing from `rgb.list` | `AllowHidRgb` off on the service, or `publish\` not rebuilt | enable `AllowHidRgb` (§6) and restart; redeploy the broker |
| `Connect failed (is the broker running?)` | control service stopped | start `BrokerControl` (the RGB control service) |
| Output file is empty | WinExe + bare `>` redirect | use `cmd /c "... > file 2>&1"` (§1) |
| Color set "OK" but LEDs don't change | another RGB app is overwriting frames | close OpenRGB / MSI Center / Dragon Center |
| `Authorization denied by the broker` | client-identity gate is on and this binary isn't authorized | run the shipped client, or authorize it (see `SECURITY` / installer flags) |
