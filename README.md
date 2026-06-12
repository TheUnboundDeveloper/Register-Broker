# Register Broker

**Universal Low-Level Hardware Access Framework** — secure, controlled, audited access
to PC sensors and RGB hardware for **non-admin** applications, with no vulnerable
kernel drivers (no WinRing0) and no per-app elevation.

One narrow, signed kernel driver and one authenticated broker service read the
hardware **once**; any number of unprivileged clients consume named, pre-mapped data
over a local pipe. Clients name a *logical register* (`smu.cpu.temp`, `ram0`) —
**never an address** — and cannot scan, probe, or write outside the broker's baked,
kernel-enforced map.

```
 non-admin clients (any app speaking the pipe protocol)
        │  \\.\pipe\SensorBroker        \\.\pipe\BrokerControl
        │  sensor.list / read / readall  rgb.list / rgb.set
        ▼                                      ▼
 ┌──────────────────────────┐   ┌──────────────────────────────┐
 │ SensorBroker service     │   │ BrokerControl service        │
 │ (LocalSystem)            │   │ (LocalSystem, write-only)    │
 │  auth: DACL + peer       │   │  same gate + rgb:write scope │
 │  identity + signer pin   │   │                              │
 │  rate limit · sessions   │   │  per-LED atomic block frames │
 │  audit log · catalog +   │   │                              │
 │  calibration data        │   │                              │
 └────────────┬─────────────┘   └──────────────┬───────────────┘
              │        \\.\BrokerSmbus (SYSTEM+Admins only)
              ▼                                ▼
 ┌─────────────────────────────────────────────────────────────┐
 │ BrokerSmbus kernel driver (non-PnP KMDF, sequential, narrow) │
 │  bounded reads: SMBus · AMD SMU · Super-I/O (named registers)│
 │  bounded writes: SMBus block ≤32 B, in-kernel address        │
 │  allow-list ("brick guard": RGB windows only, never SPD)     │
 └─────────────────────────────────────────────────────────────┘
              │ SMBus / SMN / LPC port I/O
              ▼
         hardware (CPU SMU · Super-I/O EC · DIMMs · RGB controllers)
```

## Why

Today every monitoring/RGB tool ships its own kernel driver (often WinRing0 — signed,
vulnerable, on Microsoft's block list) and runs elevated. Register Broker replaces
that model with a **register database** approach: trusted, audited code knows *where*
registers live; calibration **data** (JSON, no addresses) maps them to human labels
per board; clients get values by **id** through an authenticated, rate-limited,
audited broker. Privilege is scoped to one small service instead of every consumer.

## What works today

**33-sensor catalog** served to non-admin clients over authenticated pipe; per-LED RGB fully drivable.

| Capability | Mechanism | Status |
|---|---|---|
| CPU die temp + per-CCD (Ryzen) | AMD SMU Tctl + CCD reads over SMN | ✅ hardware-validated |
| Board temps / fans / voltages | Nuvoton NCT6683 EC (0xC730) | 🟡 implemented, HW-unvalidated |
| Board temps / fans / voltages | Nuvoton NCT6686 EC (0xD440) | 🟡 implemented, HW-unvalidated |
| Board temps / fans / voltages | Nuvoton NCT6687D EC (0xD590) | ✅ hardware-validated |
| Board temps / fans / voltages | Nuvoton NCT6775 family (6779, 6791–6798) | 🟡 implemented, HW-unvalidated |
| DIMM temperatures | JC42 / TSE2004av over SMBus | ✅ hardware-validated |
| Per-LED DRAM RGB (non-admin!) | ENE/Aura (block write, 1–32 B atomic) | ✅ hardware-validated |
| AMD SMBus host (FCH KERNCZ) | SMBus sequential controller | ✅ hardware-validated |
| Intel SMBus host (i801) | SMBus sequential controller | ⬜ implemented, HW-unvalidated |

Full inventory: [docs/SENSOR-CHIPSET-INVENTORY.md](docs/SENSOR-CHIPSET-INVENTORY.md) · Read the inventory for complete per-backend chipset details.

## RGB status (read this before expecting your build to light up)

RGB support is deliberately narrow today:

- **Supported hardware: ENE/Aura-protocol DRAM over SMBus only** (validated on
  G.Skill DDR4 modules). That is the one controller family the in-kernel write
  allow-list ("brick guard") covers. Motherboard headers, GPUs, AIOs, and USB/HID
  controllers are **not** supported.
- **Colors only, no effects.** `rgb.set` writes static per-LED colors atomically.
  Animation, breathing, rainbow, music sync — any effect — is the **consumer's job**:
  render frames client-side and send colors at your own rate (the control service
  allows 120 ops/s, burst 240). The broker will never host an effects engine.
- **Extensible by interface.** New controller families implement
  `IRgbController` ([BrokerSensorBridge/Rgb/IRgbController.cs](BrokerSensorBridge/Rgb/IRgbController.cs));
  the reference implementation is the ENE/Aura controller. Every new write target
  also requires a matching in-kernel allow-list entry — write capability is never
  data-driven. See [docs/CONTRIBUTING.md](docs/CONTRIBUTING.md) §5.
- **Future:** broader controller coverage is planned as a separate, license-isolated
  sidecar process (post-1.0) — see [docs/BROKER-ROADMAP.md](docs/BROKER-ROADMAP.md).

## Security model (the point of the project)

- **Narrow Ring 0.** The driver exposes a handful of bounded IOCTLs — named-register
  reads and one ≤32-byte SMBus write path allow-listed **in the kernel** to RGB
  controller address windows. No physical memory, no MSRs, no arbitrary port I/O.
- **No client addressing.** The catalog of readable sensors and drivable devices is
  baked into trusted code; calibration files can relabel/rescale a reading but can
  never reach hardware.
- **Authenticated, audited, throttled.** Peer-process identity + Authenticode signer
  pin (no shared secret), token-bucket rate limits (30 ops/s burst 60; control service
  120/240), max 32 sessions (8 per identity), and an audit log of every connect, auth
  decision, and operation.
- **Register-fact provenance.** Every register map and access sequence is reproduced
  from documented hardware facts cross-checked against open-source reference drivers
  — never invented. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Quick start (dev box)

```powershell
# Build everything (elevated: republishing stops the running services)
.\scripts\Build-All.ps1 -Driver -Clean

# Register the kernel driver + services (elevated; refuses an unsigned driver)
.\scripts\Install-SensorBrokerService.ps1 -WithRgbControl

# Consume from a NORMAL (non-admin) shell — the exe is a WinExe, so redirect:
.\publish\BrokerSensorBridge\BrokerSensorBridge.exe --client --op=sensor.readall > out.txt 2>&1
.\publish\BrokerSensorBridge\BrokerSensorBridge.exe --client --control --op=rgb.set --device=ram0 --color=00FF00 > out.txt 2>&1
```

Details: [docs/USER-GUIDE.md](docs/USER-GUIDE.md) (run it) ·
[docs/DEV-GUIDE.md](docs/DEV-GUIDE.md) (build it) ·
[docs/CLIENT-PROTOCOL.md](docs/CLIENT-PROTOCOL.md) (integrate with it).

## Known limitations (not yet done)

- **Hardware validation pending** for Intel i801 SMBus host, entire NCT6775 family
  (6779, 6791–6798), and NCT6683/6686 EC siblings (6687D is validated). Want to help?
  See [docs/TESTING.md](docs/TESTING.md).
- **Production code signing** (EV certificate + attestation) — currently test-signed.
  Hardened client authentication (`RequireAuthorizedClient`) is off by default; will
  require production cert + pinned signer thumbprint.
- **SMU PM-table metrics** (CPU power, clocks, voltage) — separate mailbox mechanism,
  deliberately deferred.
- **RGB scope**: ENE/Aura DRAM only, colors only — see [RGB status](#rgb-status-read-this-before-expecting-your-build-to-light-up).

## Documentation map

| Doc | What it covers |
|---|---|
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | the four layers, end to end |
| [docs/BROKER-DESIGN.md](docs/BROKER-DESIGN.md) | threat model, auth, scopes, driver strategy |
| [docs/CLIENT-PROTOCOL.md](docs/CLIENT-PROTOCOL.md) | the named-pipe wire protocol for consumers |
| [docs/SENSOR-MAP.md](docs/SENSOR-MAP.md) | every served id and where it comes from |
| [docs/SENSOR-CHIPSET-INVENTORY.md](docs/SENSOR-CHIPSET-INVENTORY.md) | chipset coverage by vendor + status |
| [docs/CALIBRATION-AND-REGISTRY-PLAN.md](docs/CALIBRATION-AND-REGISTRY-PLAN.md) | data-driven calibration + scaling plan |
| [docs/SIGNING-AND-DEPLOYMENT.md](docs/SIGNING-AND-DEPLOYMENT.md) | test vs production signing, hardened install |
| [docs/IMPLEMENTATION.md](docs/IMPLEMENTATION.md) / [docs/REFERENCE.md](docs/REFERENCE.md) | internals + full reference |
| [docs/CONTRIBUTING.md](docs/CONTRIBUTING.md) | guardrails for adding chips/sensors |
| [docs/CONTRIBUTING-CHIPSET.md](docs/CONTRIBUTING-CHIPSET.md) | full chipset-porting walkthrough |
| [docs/TESTING.md](docs/TESTING.md) | hardware validation guide (community testers) |

## License

**AGPL-3.0 with Commercial Exception** — free for personal and internal use with
copyleft sharing of improvements; commercial/proprietary use available under a
separate commercial license. See [LICENSE](LICENSE) and
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
