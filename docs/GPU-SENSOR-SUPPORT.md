# GPU Sensors — Feasibility Investigation (read-only sources)

**Status: investigated, recommended DEFERRED (architecture mismatch).** This is a findings
record, not a committed feature. Question asked: *can the Register Broker framework add GPU
temperature (and similar) as read-only sensor sources, served non-admin like the existing
CPU/board sensors?*

Short answer: **not through the current narrow SMBus / Super-I/O driver.** A modern discrete
GPU does not expose its temperature as a plain SMBus device the broker can name and read.
Reaching it means a GPU-vendor API or GPU MMIO, which crosses the framework's hard guardrails.
The clean path, if it is ever wanted, is a **separate opt-in sidecar service** — exactly the
shape already chosen for the deferred SMU PM-table metrics.

---

## How sensors reach the broker today

Every served sensor rides one of three **named, bounded** transports, with register maps baked
into signed kernel code (never in data):

| Transport | Backend | Examples |
|---|---|---|
| AMD SMN (PCI config) | SMU | `smu.cpu.temp`, per-CCD, `smu.cpu.vcore` |
| Super-I/O (LPC/EC) | NCT668x / NCT6775 | board temps, fans, voltages, PWM duty |
| System SMBus | JC42 | `dimm.*` DIMM temps (0x18–0x1F) |

Adding a backend = a kernel probe+read file with a chip-id gate, a descriptor row in the
backend registry, a decoder in `SensorDecode.cs`, and a channel in `ChannelRegistry.cs`. The
driver only ever exposes `{kind, index}` reads — never an address from the client.
(`BrokerSmbusDriver/SmbusDetect.c`, `BrokerSensorBridge/Sensors/ChannelRegistry.cs`,
`SensorDecode.cs`.)

## The guardrails that decide this

From `CLAUDE.md` and `docs/ARCHITECTURE.md`:

> **The driver stays narrow.** Bounded, validated, named-register IOCTLs only — never physical
> memory, MSRs, or arbitrary port I/O. Register maps live in signed code, never in data.

The **SMU PM-table** deferral (Open items #3) is the governing precedent: package power and
per-core clocks were *not* added because they need the SMU mailbox plus a physical-memory read
of a DMA'd table — and the explicit decision was that if revisited it must be a **separate
opt-in driver+service**, leaving the core driver's assurance untouched. GPU access is the same
class of problem.

## Why GPU temperature is not an SMBus read

- **NVIDIA / AMD / Intel discrete GPUs** keep the thermal sensor on-package, managed by the GPU
  firmware. It is reported through vendor APIs — **NVML/NVAPI** (NVIDIA), **ADL/AMDSMI** (AMD),
  Level-Zero/IGCL (Intel) — not as a device at a motherboard-SMBus address (0x18–0x1F, 0x50–0x7F).
- The GPU *does* have an I2C/SMBus controller, but it is the card's **own private bus** behind
  PCIe, reachable only via the display driver / GPU MMIO. This repo already drew that line for
  **GPU RGB**: `CHANGELOG.md` records GPU SMBus RGB as out of scope precisely because it lives on
  the GPU's own I2C bus via NVAPI, "likely GPU MMIO — breaking the driver-stays-narrow / no
  physical memory guardrails."
- A handful of boards publish an **EC-sampled "GPU temperature"** over Super-I/O. That is a
  board-EC proxy value, not the GPU's real sensor; where present it already surfaces as a normal
  `nct6687d.temp.*` channel and needs no GPU-specific work.

So the three ways to get a real GPU temperature — vendor API, GPU MMIO, or display-driver-mediated
I2C — all require either loading vendor libraries or physical-memory/MMIO access. None fit the
narrow kernel driver.

## Options & recommendation

| Option | Verdict |
|---|---|
| **A.** Read GPU temp over the existing SMBus/Super-I/O backends | ❌ No hardware path on modern GPUs. |
| **B.** Add a GPU-vendor API into the broker/driver | ❌ Violates no-physical-memory / driver-stays-narrow. |
| **C.** Separate opt-in **GPU sensor sidecar** (user-mode, vendor APIs: NVML/NVAPI/ADL) feeding the broker pipe | 🟡 Feasible, *reduced assurance*; deferred — mirrors the SMU PM-table decision. |

**Recommendation: defer (Option C if ever pursued).** A user-mode sidecar that calls NVML/NVAPI/
ADL and republishes readings over the broker's named pipe would keep the signed driver and its
assurance posture completely untouched — the driver is the real security boundary, and the
BrokerControl-from-SensorBroker split is the existing precedent for adding a capability as its
own service. It would ship explicitly labelled reduced-assurance (no kernel brick-guard, depends
on vendor libraries / running GPU driver). This is a meaningful new subsystem, not a backend row,
so it is out of scope for a routine sensor addition.

## Reference Console note

The dashboard's **GPU Temperature** card is aspirational scaffolding: it scans the catalog for a
`gpu` temperature and currently finds none, so it reads `—`. As of this task the **Add box** menu
is sensor-derived, so a removed GPU card is offered back only when a GPU temperature is actually
detected — i.e. it stays out of the menu until/unless a source like the sidecar above exists. No
change to the broker or driver was made for GPU support; this document is the investigation
deliverable.
