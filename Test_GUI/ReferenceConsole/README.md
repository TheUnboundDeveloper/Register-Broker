# Register Broker — Reference Console

A first-party, **non-admin** desktop client that drives the Register Broker over its
two well-known pipes. It is a transparent instrument that talks the broker wire protocol
directly — nothing sits between the broker and the truth — and a clean, end-to-end
demonstration of everything the framework exposes (sensors and full RGB control) from an
ordinary user-mode process, without elevation. It is the project's reference consumer and
its proof that the broker model is functional, safe, and effective.

> Stack: **.NET 10** + **Avalonia 12** (cross-platform UI). The broker it connects to is
> Windows-only, so the connection target is Windows even though the UI framework isn't.

## Layout

| Project | What it is |
|---|---|
| `Broker.Client/` | Portable, dependency-free port of the broker wire format: 4-byte BE length + UTF-8 JSON frames, hello/ok identity handshake, scoped `{token,op}` requests. Owns `RgbColor` and the typed ops (`SensorReadAllAsync`, `RgbListAsync`, `RgbSetLedsAsync`, …). Pipe I/O is serialized so streamed effect frames never interleave with manual sends. |
| `ReferenceConsole/` | The Avalonia app. One window, three tabs. `Effects/` holds the client-side effect engine. |
| `ReferenceConsole.slnx` | Solution (new XML format; .NET 10 default). |
| `global.json` | Pins the .NET 10 SDK (`10.0.301`) so this tree builds on 10 while the broker stays on its .NET 8 SDK. |

## Build & run

```powershell
# .NET 10 SDK required (runtime alone is not enough). Installed via:
#   winget install Microsoft.DotNet.SDK.10
cd "C:\Users\<you>\AppData\Roaming\VSC\Register Broker\Test_GUI\ReferenceConsole"
dotnet build -c Debug
.\ReferenceConsole\bin\Debug\net10.0\ReferenceConsole.exe
```

Click **Connect**. The console opens two sessions: `SensorBroker` (scope `sensors:read`)
and `BrokerControl` (scope `rgb:write`). The peer-identity/signature gate is audit-only by
default, so the unsigned console connects; if `RequireAuthorizedClient` is ever turned on,
this exe's Authenticode signer would need to be pinned.

## Tabs

- **Sensors** — `sensor.readall` grid, live-poll toggle with adjustable interval, in-place row
  updates (no flicker), per-read latency.
- **RGB** — the effect engine (below).
- **Diagnostics** — ping/latency, granted scopes, raw log.

## The effect engine (RGB tab)

The broker already serves **per-LED** frames: `rgb.set` accepts a `colors:[RRGGBB,…]` array
routed to `IRgbController.SetLeds`. So every effect is a **client-side renderer** that fills a
per-LED buffer each frame; the engine streams it through that existing op. **No broker or
driver change is involved** — the broker stays a pure transport.

`EffectEngine` runs a background loop while connected. Each device can be assigned its own
effect and toggled with **Drive this device**; the loop renders every enabled device per tick,
dedupes unchanged frames (rate-limit friendly), refreshes the sensor cache every 0.75 s (for
the Temperature effect), and starts/stops audio capture automatically. **Apply to all devices**
clones the current effect (same type, same parameter values, independent per-device state) onto
every device and drives them together; **Stop all** disables them. Global **FPS** is adjustable
(default 20 — safe under the control service's 120/240 ops/s limit).

### Effects

| Effect | Demonstrates | Key live parameters |
|---|---|---|
| Static | whole-device colour | colour |
| **Temperature** | colour-by-sensor (auto-enables Sensors live poll) | sensor picker, cold/hot temps, cold/hot colours, brightness |
| Rainbow | animation | speed, spread, saturation, brightness |
| Breathing | animation | colour, Hz, min/max brightness |
| Comet | per-LED chase animation | colour, background, speed, tail |
| Twinkle | random per-LED sparkles igniting and fading over a background | background, twinkle colour, density, fade, random-colours |
| **Aurora** | an original effect — drifting curtains of light blended from three palette colours | speed, wave scale, brightness, colours A/B/C |
| **Manual per-LED** | click LEDs to paint individually | brush colour (+ Fill / Clear) |
| **Audio Spectrum** | WASAPI loopback → FFT bands | **reactive factor**, **smoothing**, noise floor, low/high colour, Spectrum/Level mode |

### Fully-configurable parameters

Every effect declares a typed `EffectParam` list (`Slider` / `Color` / `Toggle` / `Choice`).
The UI builds the controls generically (`BuildParamControl`), so **any** knob is adjustable
live with zero per-effect UI code — including the audio **reactive factor** and **smoothing**.
Adding a parameter to an effect is a one-line change in that effect; the UI picks it up.

### Adding an effect

1. Implement `IEffect` in `Effects/BuiltinEffects.cs` (declare params in the ctor, fill the
   LED buffer in `Render`).
2. Add its name to `EffectNames` and a case in `CreateEffect` (both in `MainWindow.axaml.cs`).

## Dependencies / notes

- **NAudio 2.2.1** (console only) — WASAPI loopback capture for the audio mode. Windows-only,
  but loopback is non-admin, so the whole console stays non-admin. The FFT is hand-rolled
  (no extra FFT dependency). Audio mode only lights while the system is actually playing sound.
- `Broker.Client` has **no** third-party dependencies.
- MSI `mb.argb0` per-LED goes through the broker's already-validated `SetLeds` path.
