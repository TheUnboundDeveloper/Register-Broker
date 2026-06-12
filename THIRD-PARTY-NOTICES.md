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
| Nuvoton NCT668x | `drivers/hwmon/nct6683.c` | NCT6683/6686/6687D Super-I/O sensors |
| Nuvoton NCT6775 | `drivers/hwmon/nct6775-core.c`, `nct6775-platform.c` | NCT6775-family Super-I/O sensors + IO-space-lock |
| DIMM thermal | `drivers/hwmon/jc42.c` | JC42 / TSE2004av DIMM temperature decode |

The Linux kernel is licensed **GPL-2.0**. https://www.kernel.org

## RGB control

The ENE/Aura DRAM direct-color SMBus protocol (pointer + per-LED block writes)
is a publicly-documented hardware protocol; it is reproduced as a hardware
protocol fact in original code (`BrokerSensorBridge/Rgb/`). The kernel driver's
single SMBus write path is bounded by an in-kernel address allow-list (the
"brick guard"), so only RGB-controller address windows are ever writable.

## Build / runtime dependencies

- **.NET 8** runtime / base class library — MIT (Microsoft).
- **Microsoft.Extensions.Hosting.WindowsServices** (NuGet) — MIT (Microsoft);
  the Windows-Service host. The only third-party package the broker references.

## Summary

| Component | Origin | License of the distributed code |
|---|---|---|
| `BrokerSensorBridge/`, `BrokerSmbusDriver/`, `scripts/`, `docs/` | original (hardware register facts reproduced) |  AGPL-3.0 + Commercial Exceptions Clause (see `LICENSE`) |
| Linux kernel reference drivers | facts only — not distributed | n/a (no code included) |
