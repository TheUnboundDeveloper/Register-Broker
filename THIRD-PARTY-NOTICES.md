# Third-Party Notices & Provenance

The framework (the broker + kernel driver) is original code. It **reproduces
hardware register maps and access sequences** that are publicly documented and
were verified against open-source reference drivers. This file documents that
provenance and the legal position the project relies on.

> Not legal advice. If you intend to distribute this commercially, have counsel
> confirm the facts-vs-expression position below against the actual source.

## The legal position (facts vs. expression)

- **Hardware facts are not copyrightable.** Register addresses, bit layouts,
  command/bank-select sequences, and decode formulas are facts about the
  hardware (and are documented in vendor datasheets). The framework reproduces
  these facts faithfully — "ported verbatim" in this repo means *the register
  values are reproduced exactly*, not *that source code was copied*.
- **No third-party source code or binaries are distributed** with the framework.
  The driver and broker are independently written (KMDF C / .NET C#) with their
  own structure; reference drivers were used to learn and cross-check *what the
  registers are*, not to copy implementation expression.
- **What would create an obligation:** copying the literal code expression
  (source structure, comments, unique implementation choices) of another
  project. The framework aims to avoid this; a diff review of a given file
  against the references below is how to confirm it before commercial release.

## Referenced sources — Linux kernel (GPL-2.0)

Used as references for hardware register maps / access sequences. Reproduced as
facts; **no kernel source code is included or distributed.**

| Subsystem | Linux reference driver | Used for |
|---|---|---|
| AMD FCH SMBus (PIIX4/SB800) | `drivers/i2c/busses/i2c-piix4.c` | SMBus host transactions (read + RGB write) |
| Intel SMBus | `drivers/i2c/busses/i2c-i801.c` | Intel i801 SMBus path (written, unvalidated) |
| AMD CPU temperature | `drivers/hwmon/k10temp.c` | SMU/SMN Tctl + per-CCD temperature decode |
| AMD CPU voltage (SVI2) | `zenpower` (out-of-tree hwmon) | SVI2 telemetry-plane SMN addresses + voltage decode (`AmdSviVoltageV`) |
| Nuvoton NCT668x | `drivers/hwmon/nct6683.c` | NCT6683/6686/6687D Super-I/O sensors |
| Nuvoton NCT6775 | `drivers/hwmon/nct6775-core.c`, `nct6775-platform.c` | NCT6775-family Super-I/O sensors + IO-space-lock |
| DIMM thermal | `drivers/hwmon/jc42.c` | JC42 / TSE2004av DIMM temperature decode |

The Linux kernel is licensed **GPL-2.0**. https://www.kernel.org

**zenpower** is an independent out-of-tree Linux hwmon driver (**GPL-2.0**;
https://github.com/ocerman/zenpower and the Zen-3 fork). Only the AMD SVI2
voltage-telemetry register facts — the per-CPU-model telemetry-plane SMN addresses
(base `0x0005A000`) and the voltage code→volts decode (`1.550 − 0.00625·code`) — are
reproduced, **as facts, in original code** (`SmuAmd.c` bakes the addresses; the broker
decodes). The facts were cross-checked against the Linux `k10temp` "core/SoC voltages"
patch. No zenpower source is included or distributed. AMD SVI2 **current/power**
telemetry is deliberately **not** reproduced (it needs a board-dependent telemetry factor).

## RGB control

The ENE/Aura DRAM direct-color SMBus protocol (pointer + per-LED block writes)
is a publicly-documented hardware protocol; it is reproduced as a hardware
protocol fact in original code (`BrokerSensorBridge/Rgb/`). The kernel driver's
single SMBus write path is bounded by an in-kernel address allow-list (the
"brick guard"), so only RGB-controller address windows are ever writable.

The MSI Mystic Light USB-HID feature-report layout and the **Razer Chroma
"extended matrix" USB-HID command protocol** (90-byte report, transaction id,
command class/id, XOR checksum, custom-frame + apply-custom commands) are
likewise reproduced as publicly-documented hardware protocol facts in original
C# (`BrokerSensorBridge/Rgb/MysticLightHidController.cs`,
`RazerHidController.cs`). The Razer command facts were cross-checked against the
**OpenRazer** Linux kernel driver (`drivers/hid/razer/` family, GPL-2.0); only
the register/command values are reproduced — no source code is copied or
distributed. These USB-HID paths are user-mode and outside the kernel
brick-guard, so they are opt-in (`AllowHidRgb`) and reduced-assurance; the
broker's baked report builder is the only write boundary.

## Build / runtime dependencies

### Broker + driver (the privileged stack)

- **.NET 8** runtime / base class library — MIT (Microsoft).
- **Microsoft.Extensions.Hosting.WindowsServices** (NuGet) — MIT (Microsoft);
  the Windows-Service host. The only third-party package the broker references.

### First-party demonstrator consumers (not part of the privileged stack)

The **Reference Console** (`Test_GUI/ReferenceConsole/`, the demonstrator GUI) and
**RgbAudioReactive** (`RgbAudioReactive/`, the standalone music-sync tool) are first-party
*consumers* of the broker — they speak only the public pipe protocol and are **not** bound by
the broker's zero-third-party-library rule. They reference permissively-licensed NuGet packages
(restored at build time, not vendored):

- **.NET 8 / .NET 10** runtime / base class library — MIT (Microsoft). RgbAudioReactive targets
  .NET 8; the Reference Console targets .NET 10.
- **Avalonia** (and `Avalonia.Desktop` / `Avalonia.Themes.Fluent` / `Avalonia.Fonts.Inter`
  / `AvaloniaUI.DiagnosticsSupport`, the last debug-only) — MIT (Avalonia community);
  cross-platform UI framework for the Reference Console. https://avaloniaui.net
- **NAudio** — MIT (Mark Heath & contributors); WASAPI microphone + render-endpoint loopback
  capture for the audio-reactive paths (used by RgbAudioReactive and the console's Audio Spectrum
  effect). https://github.com/naudio/NAudio
- The console's `Broker.Client/` and all of RgbAudioReactive's own code have **no** third-party
  dependencies beyond the above.

No third-party source is vendored in either tree; these are NuGet package references.

## Summary

| Component | Origin | License of the distributed code |
|---|---|---|
| `BrokerSensorBridge/`, `BrokerSmbusDriver/`, `scripts/`, `docs/` | original (hardware register facts reproduced) |  AGPL-3.0 + Commercial Exceptions Clause (see `LICENSE`) |
| `Test_GUI/ReferenceConsole/`, `RgbAudioReactive/` | original first-party consumers (Avalonia/NAudio referenced via NuGet, MIT) | AGPL-3.0 + Commercial Exceptions Clause (see `LICENSE`) |
| Linux kernel reference drivers | facts only — not distributed | n/a (no code included) |
