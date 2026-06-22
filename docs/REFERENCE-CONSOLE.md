# Reference Console — the first-party demonstrator GUI

The **Reference Console** is a first-party, **non-admin** desktop application that drives the
Register Broker over its two well-known pipes. It is the project's flagship proof that the
broker model is **functional, safe, and effective**: an ordinary user-mode process, with no
elevation and no kernel driver of its own, reads every sensor in the catalog and drives every
RGB device the broker exposes — sensors, DRAM RGB, motherboard ARGB headers, and Razer
peripherals — through the same authenticated pipe protocol any third-party app would use.

It lives in the repository under [`Test_GUI/ReferenceConsole/`](../Test_GUI/ReferenceConsole/)
and is a *consumer* of the framework, not part of the privileged stack. It ships no hardware
code; it only speaks the wire protocol ([CLIENT-PROTOCOL.md](CLIENT-PROTOCOL.md)).

> **Why it exists.** A command-line client proves the protocol works; a GUI proves the model is
> *usable*. The console is a transparent instrument — it talks the broker wire format directly,
> with nothing sitting between the broker and the truth — so what you see on screen is exactly
> what a non-admin process can obtain through the broker.

---

## What it demonstrates

| The framework promise | How the console proves it |
|---|---|
| Non-admin sensor access | The **Sensors** tab live-polls `sensor.readall`; the session reports `elevated=False`. |
| Non-admin RGB control | The **RGB** tab drives DRAM, MSI ARGB headers, and Razer peripherals via `rgb.set`. |
| Per-LED control is real | The **Manual per-LED** and **Comet** effects paint individual LEDs through `rgb.set`'s `colors:[…]` array. |
| The broker is a pure transport | Every animation is rendered **client-side** and streamed as solid-color frames; no broker or driver change is involved. |
| Scoped, audited, rate-limited | Two sessions open with distinct scopes (`sensors:read`, `rgb:write`); the **Diagnostics** tab shows granted scopes, ping latency, and a raw log. |

---

## Stack & requirements

The console is **separate from the broker build** — it has its own solution and a cross-platform
UI framework, though the whole repository now targets **.NET 10** (the broker included).

- **.NET 10 SDK** (`10.0.301`, pinned by `Test_GUI/ReferenceConsole/global.json`) — the runtime
  alone is not enough. Install with `winget install Microsoft.DotNet.SDK.10`.
- **Avalonia 12** (cross-platform UI; MIT) — pulled via NuGet.
- **NAudio 2.2.1** (console only; MIT) — WASAPI loopback capture for the audio-reactive effect.
  Loopback is non-admin, so the whole console stays non-admin. The FFT is hand-rolled (no extra
  dependency). Audio mode only lights while the system is actually playing sound.
- `Broker.Client` (the portable wire-protocol port) has **no** third-party dependencies.
- A running Register Broker stack (the `SensorBroker` service, plus `BrokerControl` for RGB) on
  the target Windows machine — see the [User Guide](USER-GUIDE.md).

The UI framework is cross-platform, but the broker it connects to is Windows-only, so the
connection target is Windows regardless.

---

## Build & run

```powershell
# .NET 10 SDK required (runtime alone is not enough):
#   winget install Microsoft.DotNet.SDK.10
cd "Test_GUI\ReferenceConsole"
dotnet build -c Debug
.\ReferenceConsole\bin\Debug\net10.0\ReferenceConsole.exe
```

Click **Connect**. The console opens two sessions: `SensorBroker` (scope `sensors:read`) and
`BrokerControl` (scope `rgb:write`). The peer-identity / signature gate is audit-only by default,
so the unsigned console connects; if `RequireAuthorizedClient` is ever turned on, this exe's
Authenticode signer would need to be pinned (see [SIGNING-AND-DEPLOYMENT.md](SIGNING-AND-DEPLOYMENT.md)).

> **RGB note.** Motherboard headers and peripherals use the USB-HID transports, gated by
> `AllowHidRgb` — which is **on by default**, so those devices appear automatically when present.
> Set `AllowHidRgb: false` in the control service's `appsettings.json` for the stricter posture
> (see [RGB-BOARD-BRINGUP.md](RGB-BOARD-BRINGUP.md) §9). Close other lighting software before
> driving the same hardware.

---

## Layout

| Project | What it is |
|---|---|
| `Broker.Client/` | Portable, dependency-free port of the broker wire format: 4-byte BE length + UTF-8 JSON frames, hello/ok identity handshake, scoped `{token,op}` requests. Owns `RgbColor` and the typed ops (`SensorReadAllAsync`, `RgbListAsync`, `RgbSetLedsAsync`, …). Pipe I/O is serialized so streamed effect frames never interleave with manual sends. |
| `ReferenceConsole/` | The Avalonia app. One window, three tabs. `Effects/` holds the client-side effect engine. |
| `ReferenceConsole.slnx` | Solution (new XML format; .NET 10 default). |
| `global.json` | Pins the .NET 10 SDK (`10.0.301`) for this tree. The broker also targets .NET 10. |

## Tabs

- **Sensors** — `sensor.readall` grid, live-poll toggle with adjustable interval, in-place row
  updates (no flicker), per-read latency.
- **RGB** — the client-side effect engine (below).
- **Diagnostics** — ping / latency, granted scopes, raw log.

## The effect engine (RGB tab)

The broker already serves **per-LED** frames: `rgb.set` accepts a `colors:[RRGGBB,…]` array routed
to `IRgbController.SetLeds`. So every effect is a **client-side renderer** that fills a per-LED
buffer each frame and streams it through that existing op — **no broker or driver change is
involved**. This is the point: animation is the consumer's job, and the console proves a consumer
can do it richly while the broker stays a pure, auditable transport.

`EffectEngine` runs a background loop while connected. Each device can be assigned its own effect
and toggled with **Drive this device**; the loop renders every enabled device per tick, dedupes
unchanged frames (rate-limit friendly), refreshes the sensor cache every 0.75 s (for the
Temperature effect), and starts/stops audio capture automatically. **Apply to all devices** clones
the current effect onto every device; **Stop all** disables them. Global **FPS** is adjustable
(default 20 — safe under the control service's 120/240 ops/s limit).

**Session persistence.** The console remembers your setup between runs. The global FPS /
sensor-refresh / poll-interval knobs and, per device, the assigned effect, every parameter value,
drive state, gradient stops, and hand-painted per-LED colours are saved to
`%APPDATA%\RegisterBroker\ReferenceConsole\settings.json` on close/disconnect and restored on the
next **Connect**. It is UI convenience only — it stores no addresses, scopes, or anything the
broker acts on.

| Effect | Demonstrates | Key live parameters |
|---|---|---|
| Static | whole-device colour | colour |
| Temperature | colour-by-sensor, with a soft fade between colours (auto-enables Sensors live poll) | sensor picker, temp→colour stops (add/remove, colour quick-picks), brightness, fade |
| Rainbow | animation | speed, spread, saturation, brightness |
| Breathing | animation | colour, Hz, min/max brightness |
| Comet | per-LED chase animation | colour, background, speed, tail |
| Twinkle | random per-LED sparkles igniting and fading over a background | background, twinkle colour, density, fade rate, random-colours toggle |
| Aurora | an original effect — drifting curtains of light from three blended palette colours | speed, wave scale, brightness, colours A / B / C |
| Manual per-LED | click LEDs to paint individually | brush colour (+ Fill / Clear) |
| Audio Spectrum | WASAPI loopback → FFT bands (Level mode tracks overall loudness from the same tuned bands) | reactive factor, smoothing, noise floor, low/high colour, Spectrum/Level mode |

Every effect declares a typed `EffectParam` list (`Slider` / `Color` / `Toggle` / `Choice`); the UI
builds the controls generically, so any knob is adjustable live with zero per-effect UI code.

### Adding an effect

1. Implement `IEffect` in `Effects/BuiltinEffects.cs` (declare params in the ctor, fill the LED
   buffer in `Render`).
2. Add its name to `EffectNames` and a case in `CreateEffect` (both in `MainWindow.axaml.cs`).

---

## Where it fits

- Wire protocol the console speaks → [CLIENT-PROTOCOL.md](CLIENT-PROTOCOL.md)
- Adding broker support to your *own* app → [INTEGRATING.md](INTEGRATING.md)
- Running the broker stack the console connects to → [USER-GUIDE.md](USER-GUIDE.md)
- RGB command surface the effect engine drives → [RGB-COMMANDS.md](RGB-COMMANDS.md)
- Standalone music-sync consumer (a second first-party demonstrator) → [`RgbAudioReactive/`](../RgbAudioReactive/README.md)
