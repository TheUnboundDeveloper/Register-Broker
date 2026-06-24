# Aquacomputer sensors (`aqua.*`) — read-only, user-mode, removable

Register Broker can serve telemetry from **Aquacomputer** USB-HID controllers as
`aqua.*` sensors over the normal `sensor.list` / `sensor.read` / `sensor.readall`
ops, non-admin, alongside the CPU/board/GPU sensors. The first supported controller
is the **Aquacomputer Quadro** (USB `0x0C70:0xF00D`).

This is a **broker-only** feature — **no kernel driver change and no re-sign.** An
Aquacomputer controller is an off-board USB device, not a motherboard SMBus/Super-I/O
chip, so (like the GPU sensors and USB-HID RGB) it is a **user-mode** source: reduced
assurance, opt-in, and **strictly read-only** (no write op is resolved for it).

## What is served (Quadro, core set)

| Id | Meaning | Unit |
|---|---|---|
| `aqua.temp.0` … `aqua.temp.3` | 4 physical temperature inputs | °C |
| `aqua.flow.0` | coolant flow | L/h |
| `aqua.fan.0` … `aqua.fan.3` | 4 fan tachometers | RPM |

A temperature input with no probe connected reports the device's `0x7FFF` sentinel and
is **gated out** (not listed, not a bogus reading). Per-fan voltage/current/power exist
in the report and may be added later; they are intentionally not in the core id set.

Default labels (calibration `aqua.temp.0` → "Aqua Temp 1", etc.) follow the Quadro's
physical port numbering and can be relabeled/rescaled/hidden via calibration JSON like
any other channel (calibration can never address hardware).

## Removable / hot-pluggable — no flapping

The controller can be **physically unplugged at runtime**, so `aqua.*` channels are
flagged **`removable`** in `sensor.list` (an additive field; the protocol stays v2). A
consumer should render hot-plug absence as "not connected" rather than treat a vanished
sensor as an error.

The provider is **hot-plug aware** so absence is clean, not flapping:

- A background poller thread (re)opens the device and does a **blocking `ReadFile`** on
  the interrupt IN endpoint — the Quadro **streams** its status report and does **not**
  answer `HidD_GetInputReport` (`GET_REPORT` returns `ERROR_GEN_FAILURE`).
- A **staleness window** (5 s) gives hysteresis: one missed report never drops the
  sensors; a sustained loss marks the whole group absent.
- While absent the poller **retries on a backoff** (3 s) so the sensors come back on
  re-plug. (A controller that is absent at broker start is picked up on the next service
  restart; removal and re-plug of a controller present at start are handled live.)

## Protocol provenance (facts, cross-checked)

The Quadro status-report layout is ported as register **facts** from the Linux hwmon
driver **`drivers/hwmon/aquacomputer_d5next.c`**, cross-checked against **liquidctl**
(`liquidctl/driver/aquacomputer.py`). The two sources agree on every offset, scale,
endianness, and sentinel. Offsets live in `QuadroProtocol` (`Aqua/AquaSensorProvider.cs`):

| Field | Offset (incl. report-id byte) | Encoding | Scale |
|---|---|---|---|
| status report | id `0x01`, 220 bytes | — | read via `ReadFile` (interrupt IN) |
| temps ×4 | `0x34`, stride 2 | big-endian s16, sentinel `0x7FFF` | centi-°C ÷100 |
| flow | `0x6E` | big-endian u16 | dL/h ÷10 → L/h |
| fan RPM ×4 | `0x70/0x7D/0x8A/0x97` + `0x08` | big-endian u16 | raw = RPM |

See `THIRD-PARTY-NOTICES.md` for licensing of the referenced sources.

## Enabling it (opt-in, off by default)

Reduced-assurance posture (user-mode, no kernel brick-guard), so it is opt-in like the
GPU sensors. Set on the **sensor service** (server-side):

- `appsettings.json`: `"AllowAquaSensors": true`, **or**
- CLI: `--allow-aqua-sensors`, **or**
- install: `Install-SensorBrokerService.ps1 -WithAquaSensors`

Then a non-admin client sees the `aqua.*` ids appear in `sensor.list` whenever a
supported controller is present.

## Scope / assurance

- **Read-only.** No Aquacomputer write op exists; the controller's fan-control/RGB
  write reports are deliberately **not** implemented here.
- **User-mode, reduced assurance** — no kernel driver, no brick-guard, no address
  allow-list (there is no kernel surface involved). Same posture as the GPU and USB-HID
  RGB paths. See `SECURITY.md`.
- **HW-validated** on the dev box's Quadro (4 temps, fan RPM, flow read live and
  physically consistent; values cross-checked against the ported offsets).
