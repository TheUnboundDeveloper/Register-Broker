# Register Broker — Documentation

This folder is the front door to the project's documentation. Start with the guide
that matches what you want to do.

## I want to…

| Goal | Read |
|---|---|
| **Use it** — read my PC sensors / control my RGB as a normal (non-admin) user | [USER-GUIDE.md](USER-GUIDE.md) |
| **See it work** — the first-party demonstrator GUI (Reference Console) and how to run it | [REFERENCE-CONSOLE.md](REFERENCE-CONSOLE.md) |
| **Sync RGB to music** — the standalone audio-reactive consumer (mic / system output) | [`../RgbAudioReactive/README.md`](../RgbAudioReactive/README.md) |
| **Drive RGB** — the rgb.list / rgb.set command syntax and examples | [RGB-COMMANDS.md](RGB-COMMANDS.md) |
| **Integrate it** — add sensor/RGB support to my own application (with code samples) | [INTEGRATING.md](INTEGRATING.md) |
| **Contribute** — build it, add a sensor or RGB device, send a change | [CONTRIBUTING.md](CONTRIBUTING.md) |
| **Add my board's RGB** — collect the data (DMI, HID PID, zones) to add a board profile | [RGB-BOARD-BRINGUP.md](RGB-BOARD-BRINGUP.md) |
| **Port a chipset** — add support for a new Super-I/O / SMBus host chip | [CONTRIBUTING-CHIPSET.md](CONTRIBUTING-CHIPSET.md) |
| **Test on my hardware** — validate an unvalidated chipset and report results | [TESTING.md](TESTING.md) |
| **Understand how it's built** — the layers, the trust boundaries, the data flow | [ARCHITECTURE.md](ARCHITECTURE.md) |
| **Read the internals** — how auth, the pipe protocol, the driver IOCTLs, and the decode actually work | [IMPLEMENTATION.md](IMPLEMENTATION.md) |
| **Look something up** — what a config key, scope, status code, or service name means | [REFERENCE.md](REFERENCE.md) |
| **Bring up new hardware** — use the dev build and raw probes | [DEV-GUIDE.md](DEV-GUIDE.md) |
| **Ship it for real** — Windows driver signing + running as a service in a user environment | [SIGNING-AND-DEPLOYMENT.md](SIGNING-AND-DEPLOYMENT.md) |

## What this project is, in one paragraph

**Register Broker** — the Universal Low-Level Hardware Access Framework — lets **non-admin**
applications read privileged PC sensors (CPU/board temperatures, fans, voltages) and drive **RGB
lighting** (RAM, motherboard) **without** loading a vulnerable kernel driver like WinRing0
into every app and **without** running the whole stack as administrator. It does this with
a small **broker**: one narrowly-privileged service reads the hardware once, behind a
**narrow, validated kernel driver**, and hands the data and control to ordinary apps over an
**authenticated local named pipe**. The thesis is proven on real hardware — a non-admin client
reads Ryzen/board temps and changes RAM color through the elevated broker, no WinRing0
involved. Licensed under **AGPL-3.0 with Commercial Exception** — improvements are shared
back to the community, but commercial use is available under a separate license.

## The three pieces

```
  Your app / any client   (non-admin)
            │  named pipe (authenticated, scoped)
            ▼
  SensorBroker / BrokerControl (the broker — runs as a service)
            │  narrow IOCTLs (bounded transactions only)
            ▼
  BrokerSmbus (the kernel driver — the only Ring-0 surface)
            │
            ▼
  CPU SMU · Super-I/O · SMBus (RAM/board RGB)
```

1. **`BrokerSmbus`** — a minimal kernel driver exposing *only* bounded, validated sensor
   reads and brick-guarded RGB writes. Not WinRing0: no arbitrary memory/MSR/port I/O.
2. **`BrokerSensorBridge`** (the broker) — the only process that talks to the driver.
   Authenticates callers, enforces scopes, rate-limits, audits, and exposes a **named
   catalog** (clients name `cpu.temp`/`ram0`, never an address). Runs as `SensorBroker`
   (sensor reads) and `BrokerControl` (RGB writes) Windows services.
3. **Any non-admin client** — a consumer that connects to the broker's named pipe and
   speaks the authenticated protocol. Two are included: the `BrokerSensorBridge.exe --client`
   CLI, and the **[Reference Console](REFERENCE-CONSOLE.md)** — a first-party .NET 10 /
   Avalonia GUI that reads sensors and drives RGB through the broker with no elevation.

## Authoritative design docs (deeper than these guides)

These predate the guides and remain the source of truth for design decisions:

- [`CLIENT-PROTOCOL.md`](CLIENT-PROTOCOL.md) — the named-pipe wire contract (the integration surface).
- [`SENSOR-MAP.md`](SENSOR-MAP.md) — the named sensor catalog (baked-in locations).
- [`SENSOR-CHIPSET-INVENTORY.md`](SENSOR-CHIPSET-INVENTORY.md) — hardware support by chipset and vendor.
- [`CALIBRATION-AND-REGISTRY-PLAN.md`](CALIBRATION-AND-REGISTRY-PLAN.md) — data-driven board calibration (labels/scales as data) + the plan to scale to many chips.
- [`CODE-REVIEW.md`](CODE-REVIEW.md) — review findings and design notes.

> Target platform is **Windows x64**. Built with **.NET 8** (broker) and **WDK 10.0.26100**
> (KMDF kernel driver). The optional [Reference Console](REFERENCE-CONSOLE.md) GUI builds
> separately with the **.NET 10 SDK** (+ Avalonia).
