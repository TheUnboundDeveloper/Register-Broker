# GPU Sensors — read-only, user-mode (AMD ADL · NVIDIA NVML · Intel Level Zero)

**Status: implemented for AMD, NVIDIA, and Intel behind one vendor-agnostic seam; opt-in,
read-only.** GPU temperatures and related telemetry are served as `gpu.*` sensors through the
normal `sensor.list` / `sensor.read` / `sensor.readall` ops, non-admin, alongside the CPU/board
sensors. **No kernel driver change** — the GPU source is a user-mode vendor-API backend in the
broker, the same reduced-assurance shape as the USB-HID RGB transport.

**Validation:** AMD (ADL PMLog) is **HW-validated on an RX 7900 XTX**. NVIDIA (NVML) and Intel
(Level Zero Sysman) are **built but HW-unvalidated** — the dev box has no NVIDIA/Intel GPU — and
will be tested when that hardware is available. At runtime `GpuSensorProvider.TryCreate` probes
AMD → NVIDIA → Intel and uses whichever vendor library answers, so the right provider is selected
automatically per machine.

| Vendor | Library | Provider | Status |
|---|---|---|---|
| AMD | `atiadlxx.dll` (ADL PMLog) | `Gpu/GpuSensorProvider.cs` (`AmdAdlGpuProvider`) + `Gpu/AdlInterop.cs` | ✅ HW-validated (RX 7900 XTX) |
| NVIDIA | `nvml.dll` (NVML) | `Gpu/NvmlGpuProvider.cs` | 🟡 built, HW-unvalidated |
| Intel | `ze_loader.dll` (Level Zero **Sysman**) | `Gpu/LevelZeroGpuProvider.cs` | 🟡 built, HW-unvalidated |

Per-vendor metric coverage (a metric a vendor/library does not expose is gated out, not listed):

| Metric | AMD (PMLog) | NVIDIA (NVML) | Intel (L0 Sysman) |
|---|---|---|---|
| `gpu.temp` (edge) | ✅ | ✅ | ✅ |
| `gpu.temp.hotspot` | ✅ | — | — |
| `gpu.temp.mem` | ✅ | — | ✅ |
| `gpu.fan` (RPM) | ✅ | — | ✅ |
| `gpu.fan.pct` | ✅ | ✅ | — |
| `gpu.power` (W) | (driver-dependent) | ✅ | — (energy-counter delta, deferred) |
| `gpu.clock.core` | ✅ | ✅ | ✅ |
| `gpu.clock.mem` | ✅ | ✅ | ✅ |
| `gpu.usage` | ✅ | ✅ | — (activity-counter delta, deferred) |
| `gpu.voltage` | ✅ | — | — |

---

## Why GPU sensors are a user-mode source (not a driver backend)

A discrete GPU does **not** expose its temperature as a motherboard-SMBus device the broker can
name and read. The thermal sensors live on-package, managed by the GPU firmware, and are reported
through a **vendor user-mode API** — AMD **ADL** (`atiadlxx.dll`), NVIDIA NVML/NVAPI, Intel
Level-Zero. Reaching them through the kernel would mean GPU MMIO or loading a vendor library in
Ring 0 — both break the framework's hard guardrails ("never physical memory, MSRs, or arbitrary
port I/O; the driver stays narrow"). So GPU sensors are served the only way that fits: a
**user-mode provider inside the broker** that calls the vendor API and publishes the readings as
ordinary named sensors.

This mirrors the existing **USB-HID RGB** decision (`AllowHidRgb`): a capability that cannot pass
the kernel brick-guard is allowed as an **opt-in, reduced-assurance, user-mode** path, with the
broker as the only boundary. Unlike HID RGB, the GPU path is **strictly read-only** — there is no
GPU write op anywhere (it resolves only sensor getters), so the brick risk is nil; the
reduced-assurance label is purely about the vendor-library dependency and running in the broker
process rather than behind the signed driver.

The signed kernel driver is **untouched**: adding GPU sensors required no new IOCTL, no rebuild,
and no re-sign — exactly like adding a board RGB profile.

## How it works

| Layer | File | Role |
|---|---|---|
| Vendor interop | `Gpu/AdlInterop.cs` · `Gpu/NvmlGpuProvider.cs` · `Gpu/LevelZeroGpuProvider.cs` | Minimal per-vendor P/Invoke (no new NuGet dep): load the vendor DLL, init, pick the GPU, read telemetry. AMD pulls the **PMLog** block in one call; NVIDIA/Intel use individual getters. Read-only entry points only — no overclock/fan/power write calls are resolved. |
| Provider + seam | `BrokerSensorBridge/Gpu/GpuSensorProvider.cs` | `IGpuSensorProvider` + `AmdAdlGpuProvider`, `NvidiaNvmlGpuProvider`, `IntelLevelZeroGpuProvider` (the AMD impl caches one PMLog sample per ~250 ms). Process-wide singleton `GpuSensorProvider.Current`, set at startup only when `AllowGpuSensors`; `TryCreate` probes AMD → NVIDIA → Intel and returns the first that answers. |
| Channels | `BrokerSensorBridge/Sensors/ChannelRegistry.cs` | One `ChannelBackendDef` (`IsUserMode`, prefix `gpu.`) whose gate/read close over the provider and ignore the SMBus backend. Per-metric gating: a metric the GPU/driver does not populate is not listed (like `CcdTempPresent`). |
| Provenance | `BrokerSensorBridge/Sensors/SensorDecode.cs` | `DecoderRegistry["gpu."]` cites the ADL PMLog source. Values come back in engineering units, so the "decode" is passthrough. |
| Labels | `calibration.default.json` | `gpu.temp` → "GPU Temperature", etc. Data only — labels/scale/hide, never an address. |
| Config | `Program.cs` (`BridgeConfig.AllowGpuSensors`) | Off by default. `appsettings.json` `"AllowGpuSensors": true` (server-side, sensor service) or `--allow-gpu-sensors`, or install with `-WithGpuSensors`. |

## Served channels (`gpu.*`)

| Id | Unit | ADL PMLog sensor |
|---|---|---|
| `gpu.temp` | °C | `TEMPERATURE_EDGE` |
| `gpu.temp.hotspot` | °C | `TEMPERATURE_HOTSPOT` |
| `gpu.temp.mem` | °C | `TEMPERATURE_MEM` |
| `gpu.fan` | RPM | `FAN_RPM` |
| `gpu.fan.pct` | % | `FAN_PERCENTAGE` |
| `gpu.power` | W | `ASIC_POWER` |
| `gpu.clock.core` | MHz | `CLK_GFXCLK` |
| `gpu.clock.mem` | MHz | `CLK_MEMCLK` |
| `gpu.usage` | % | `INFO_ACTIVITY_GFX` |
| `gpu.voltage` | V | `GFX_VOLTAGE` (sensor-type index 21; reported in mV, converted to V) |

A channel only appears when the GPU/driver actually reports it (PMLog `supported` flag). The
`gpu.temp` channel populates the Reference Console's **GPU Temperature** card automatically
(it matches any `*gpu* + temperature` sensor — no Console change).

## Hardware validation

**Dev box — AMD Radeon RX 7900 XTX (RDNA3), Adrenalin 32.0.31021:** detected by name, 25 PMLog
sensors reported. Idle read via `--once --allow-gpu-sensors`: edge **27 °C** < hotspot **40 °C** <
mem **48 °C** (correct ordering), fan **0 RPM** (zero-fan idle) at **23 %** duty target, core
**~200 MHz**, mem **2686 MHz**, usage **~5 %**, core voltage **~0.727 V** (`gpu.voltage`, `GFX_VOLTAGE`
index 21, reported in mV → V). The PMLog index numbering was anchored to ground
truth on this card by `BUS_LANES = 16` (PCIe x16). `gpu.power` (`ASIC_POWER`) is **not populated**
by the current RDNA3 driver's PMLog set, so it reports not-available rather than a guessed value —
the channel will light up on a driver/ASIC that does report it.

**Still pending:** confirming ADL returns data from the **LocalSystem service in session 0** (the
above was an interactive run). The HID path needed that check; if ADL refuses headless, the
`IGpuSensorProvider` seam lets the AMD provider move to a small sidecar with no other change.

## Provenance / licensing

All three are **published, first-party vendor interfaces**, used as documented facts; the vendor
DLLs ship with their respective drivers and are **loaded at runtime via P/Invoke, never
redistributed**. They are loaded by **absolute `System32` path** (not by bare name), so the
provider cannot be hijacked by a same-named DLL on the search path. No GPL code is used. Recorded
in `THIRD-PARTY-NOTICES.md`.

- **AMD ADL** — entry-point names, struct layouts, and `ADL_PMLOG_*` indices are AMD ADL SDK facts
  (`adl_sdk.h` / `adl_structures.h`), cross-checked against LibreHardwareMonitor's `ADLSensorType`.
- **NVIDIA NVML** — the published NVIDIA Management Library C API (`nvml.h`); the same interface
  `nvidia-smi` uses. Read-only getters only.
- **Intel Level Zero / Sysman** — the oneAPI `ze_api.h` / `zes_api.h` telemetry API. Read-only.
  The `stype` descriptor tags and `*_state` struct layouts are the first thing to re-verify on a
  real Intel GPU (a wrong tag degrades one metric to not-available, it does not crash).

## Notes for the NVIDIA / Intel bring-up (untested paths)

Both are wired exactly like AMD and selected automatically by `TryCreate`; they need a machine with
that GPU to validate. When testing:

- **NVIDIA:** confirm `nvml.dll` loads (absolute `System32` path on modern drivers; the provider also
  falls back to the absolute `…\NVIDIA Corporation\NVSMI\` path — both hijack-safe, never search-order).
  Expect `gpu.temp`, `gpu.fan.pct`, `gpu.power`, `gpu.clock.core/.mem`,
  `gpu.usage`. Hot-spot / memory-temp / fan-RPM aren't in NVML's base getter API → not listed.
- **Intel:** the provider sets `ZES_ENABLE_SYSMAN=1` before `zeInit`. Verify the `zes_structure_type_t`
  tag constants in `LevelZeroGpuProvider.cs` against the installed `zes_api.h` first — they gate the
  frequency/temperature property reads. Expect edge + memory temp, GPU + memory clock, fan RPM;
  power and utilization (counter deltas) are deferred.

Multi-GPU could extend the ids to `gpu0.*` / `gpu1.*` later (today the first present GPU is served).
