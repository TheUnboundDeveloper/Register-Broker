# RgbAudioReactive

Audio-reactive RGB for the Register Broker. A **standalone, non-admin** consumer: it
captures audio and streams per-LED frames to whatever zones the broker exposes, over the
public control pipe (`\\.\pipe\BrokerControl`, protocol v2). It does **not** touch the
signed driver or the broker's privileged surface — it's exactly what any third-party app
does, which is why music sync lives here and not in the broker (see
`docs/CLIENT-PROTOCOL.md` §6: animation is the consumer's job).

Two sources, same program — the only difference is which WASAPI endpoint is opened:

- `--source=mic` — reacts to the **microphone** (default recording device).
- `--source=output` — reacts to **what's playing on your speakers/headphones** via
  render-endpoint loopback (default playback device).

## Build & run

```powershell
cd "C:\Users\natha\AppData\Roaming\VSC\Register Broker\RgbAudioReactive"
dotnet build -c Release

# react to system audio with a rainbow FFT visualiser (default)
.\bin\Release\net10.0-windows\win-x64\RgbAudioReactive.exe --source=output --mode=spectrum

# react to the mic as a VU meter
.\bin\Release\net10.0-windows\win-x64\RgbAudioReactive.exe --source=mic --mode=level
```

Ctrl+C stops it and blacks out the zones.

## Options

| Flag | Meaning | Default |
|---|---|---|
| `--source=mic\|output` | microphone, or system output (loopback) | `output` |
| `--mode=level\|spectrum` | VU meter, or rainbow FFT visualiser | `spectrum` |
| `--bands=N` | spectrum frequency bands (1–64) | `12` |
| `--fps=N` | target frame rate (auto-capped to the rate limit) | auto |
| `--devices=a,b,c` | restrict to these zone ids | every zone `rgb.list` reports |

## Notes

- **Zone discovery is automatic** — it drives every device `rgb.list` returns. Use
  `--devices` to narrow it. DRAM zones work out of the box; the USB-HID zones (motherboard
  header `mb.argb0`, Razer and other peripherals) are gated by `AllowHidRgb` on the control
  service, which is **on by default** — set it `false` there to opt out.
- **Rate limit:** the control service allows 120 ops/s (burst 240) per identity, and each
  zone is one op per frame. The tool auto-caps FPS to `110 / zoneCount` so all zones together
  stay under the limit; `--fps` can only lower it. Unchanged frames (e.g. during silence) are
  skipped to save ops.
- **Modes:** *Level* fills the LEDs as a green→yellow→red meter from overall loudness (works
  on any LED count). *Spectrum* spreads the frequency bands across the strip — hue by position,
  brightness by band energy (best on multi-LED zones).
- Close any other lighting software (vendor RGB suites, etc.) first — only one app should
  drive the lights at a time.
