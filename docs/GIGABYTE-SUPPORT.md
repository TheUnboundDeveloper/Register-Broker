# Gigabyte motherboard support (sensors + RGB)

> Status: **REMOVED FROM THE BUILD (2026-06-11).** A contact who works on these
> controllers directly reviewed this design and found its sources (OpenRGB's
> IT7236 controller, liquidctl issue threads) unreliable:
>
> - These controllers are **intermodal** (support several interfaces); which one a
>   board actually wires up varies per board — only board/firmware-level reverse
>   engineering settles it. Gigabyte's own tooling uses specific methods per board.
> - SMBus **0x68 is valid only for the IT8295** on Gigabyte boards.
> - **0x28 is a programming/debug port**, not a consumer control path. OpenRGB's
>   IT7236 controller drives it anyway — workable on single-controller boards,
>   likely broken on multi-controller ones. True device addresses are at **0x50+**.
> - The **liquidctl issue threads are out of date / incorrect** — untrustworthy.
>
> The sources were **archived, not deleted** — see `_archive_gigabyte\README.md`
> (corrections recorded verbatim) for what was removed and how it can return.
> `BROKER_SUPERIO_KIND_ITE` stays reserved in the protocol so the wire value is
> never reused. The rest of this document is kept as the **design record** of the
> original (hardware-unvalidated) approach.

> Original status banner (2026-06-10): NEW, gated, hardware-UNVALIDATED. Authored to be
> tested on a real Gigabyte board by a third party, then "baked in" once confirmed.
> Nothing here activates on the existing MSI/AMD dev box — every Gigabyte path is behind
> auto-detect and is inert when the matching chip/device is absent.

This adds two independent capabilities for Gigabyte boards, each mapped onto the existing
broker so **clients still name logical sensors/devices and never see a register or
address** (the same no-scan / register-hiding rule the rest of the project follows):

1. **Sensor data (read-only):** board temperatures, fan tachometers, and voltages from the
   **ITE IT87xx Super-I/O** family (IT8688E / IT8689E / IT8686E / IT8628E / …) that Gigabyte
   uses instead of MSI's Nuvoton NCT6687D. CPU die temperature still comes from the existing
   AMD SMU path (it's per-CPU, not per-board) — unchanged.
2. **RGB (read state + write):** motherboard RGB zones via the **ITE IT8297** "RGB Fusion 2"
   controller over **USB-HID** — a **user-mode, no-driver, no-admin** transport.

Both are **auto-detected**: the sensor path lights up only when an ITE Super-I/O chip id is
found on the LPC bus; the RGB path lights up only when an IT8297 USB-HID device enumerates.
On hardware that has neither (e.g. the MSI dev box) the code is dormant and the existing
NCT6687D + ENE-DRAM paths are untouched.

---

## Why these chips, and where the encodings come from

Gigabyte's hardware differs from the MSI dev box in exactly two places that matter here:

| Function | MSI dev box (done) | Gigabyte (this work) | Port source |
|---|---|---|---|
| Board temps/fans/volts | Nuvoton **NCT6687D** (LPC) | ITE **IT87xx** (LPC) | Linux `drivers/hwmon/it87.c` |
| CPU die temp | AMD SMU (SMN) | AMD SMU (SMN) — **same** | unchanged |
| DRAM RGB | ENE/Aura SMBus | (board-dependent; not in v1) | — |
| Board RGB | (n/a) | ITE **IT8297** USB-HID | OpenRGB `GigabyteRGBFusion2USB*` |

Per the project guardrail, **no register encoding is invented**. The ITE sensor map is ported
from the long-established Linux `it87` hwmon driver; the IT8297 RGB protocol is ported from
OpenRGB's own `GigabyteRGBFusion2USBController` (vendored in this tree under
`…/OpenRGB/Controllers/GigabyteRGBFusion2USBController/`).

---

## 1. Sensors — ITE IT87xx Super-I/O (read-only)

### Transport (unchanged contract)
Reuses the **existing** `IOCTL_BROKER_SUPERIO_READ` exactly as the NCT6687D path does — the
IOCTL already speaks a generic, named `{Kind, Index}` request (Temp / Fan / Voltage), so **no
wire-protocol or C# mirror change is needed for the read path**. The kernel decides which
Super-I/O backend (NCT vs ITE) answers, based on what it detected at load. The client still
names a logical sensor; the EC register stays baked in the `.sys`.

### Detection (kernel, `SuperioIte.c`, ported from `it87.c superio_enter/_inw`)
At driver load, after the NCT probe fails, the driver tries the ITE probe on LPC `0x2E`/`0x4E`:

- **Enter config:** `0x87, 0x01, 0x55, <0x55 if port 0x2E else 0xAA>` (ITE's key — different
  from Nuvoton's `0x87,0x87`).
- **Chip id:** 16-bit at SIO regs `0x20/0x21`. ITE ids are `0x86xx`/`0x87xx`
  (e.g. `0x8688`); Nuvoton is `0xD5xx`. The driver records the id and a chip-kind tag.
- **EC base:** select Environment Controller logical device **LDN 0x04**, read base from
  `0x60/0x61`.
- **Exit:** write `0x02` to SIO reg `0x02`.

If found, the driver sets `SuperioAvailable = TRUE`, stores `SuperioBase`, `SuperioChipId`,
and `SuperioChipKind = ITE`. The existing NCT detect remains first; a board can only match one.

### HWM register access (ported from `it87.c it87_read_value`)
ITE is simpler than Nuvoton — no EC paging. Two ports at `base`:
`outb(reg, base+5); val = inb(base+6)`.

Baked register map (from `it87.c`):

| Kind | Index | Register | Raw returned | Broker decode |
|---|---|---|---|---|
| Temp | 0..5 | `0x29 + i` | signed byte | `(sbyte)raw` °C (1 °C/LSB) |
| Fan | 0..5 | low `{0x0d,0x0e,0x0f,0x80,0x82,0x4c}` + high `{0x18,0x19,0x1a,0x81,0x83,0x4d}` | `high<<8 | low` | `val ? 1350000/val : 0` RPM |
| Voltage | 0..8 | `0x20 + i` (i≤7), `0x2f` (i=8 / VBAT) | byte | `raw × 16 mV × divider` |

The kernel bounds `Index` per kind (ITE has 6 temps / 6 fans / up to 9 volts) and returns
`BadRequest` past that. The 16-bit fan and the EC read are serialized by the same per-backend
mutex the NCT path uses; the driver's sequential IOCTL dispatch already serializes across
backends.

### Catalog (broker, `SensorCatalog.cs`)
The broker learns the Super-I/O chip from a new `SuperioChipId` field appended to the
`IOCTL_BROKER_SMBUS_INFO` response (additive — existing field offsets unchanged). Then:

- **NCT entries** are now gated on `SuperioAvailable && chip is Nuvoton` (so they no longer
  appear on a Gigabyte/ITE board).
- **ITE entries** are gated on `SuperioAvailable && chip is ITE`. v1 exposes them **by index**
  with `unconfirmed` labels (`board.ite.tempN`, `board.ite.fanN`, `board.ite.voltN`) — exactly the
  proven NCT bring-up workflow: the tester maps index→label against HWiNFO, then we promote to
  named ids and bake. Temps and fans are high-confidence; **voltage scaling/dividers are
  board-specific and explicitly marked unconfirmed** until HWiNFO-validated.

### What's deliberately NOT here
Per-core/PECI temps beyond the 6 EC temps; SMU PM-table metrics (same as the MSI box). DRAM
DIMM temps reuse the existing JC42 SMBus path (works on any board with TS DIMMs + the driver).

---

## 2. RGB — ITE IT8297 over USB-HID (read state + write)

### Why USB-HID, not SMBus
Gigabyte "RGB Fusion 2" boards expose the IT8297 in two ways. OpenRGB's `…SMBus` path drives
it at SMBus `0x68` using **32-byte block writes** — which this project's kernel driver does
**not** implement (its write IOCTL is byte/word only), and blind block-writes to a board
controller carry brick risk. OpenRGB's `…USB` path drives the same chip as a **USB-HID
feature-report device**, which is:

- **user-mode** — no kernel driver, **cannot touch the SMBus / SPD / brick the bus**;
- **no admin** — HID feature reports need no elevation;
- the path OpenRGB uses for most modern Gigabyte boards (Z390+, X570, B550, Z690, …).

So the broker drives Gigabyte board RGB over USB-HID and **needs neither the kernel driver nor
elevation for RGB**. (The SMBus-`0x68` variant — for the specific older boards in OpenRGB's
allowlist — is documented under "Deferred" below; it would require a brick-guarded block-write
IOCTL and is intentionally out of v1.)

### Transport / detection (`GigabyteUsbHid.cs`, `GigabyteUsbRgbController.cs`)
Self-contained HID P/Invoke (`hid.dll` + `setupapi.dll`) — **no new NuGet dependency**.
Detection enumerates HID devices for:

- **VID `0x048D`**, **PID `0x8297` or `0x5702`** (the IT8297 / IT8297-style controllers),
  matching OpenRGB's detector.

Each detected controller is initialized exactly as OpenRGB does: `report_id = 0xCC`, send
`0x60 0x00`, read the 64-byte `IT8297Report` feature report (product string, fw, chip id,
LED-strip byte order), then `EnableBeat(false)` (`0x31 0x00`). This is the **read** side — the
device descriptor/state we can read back (per-LED color readback is not supported by the
hardware, same limitation OpenRGB documents).

### Write protocol (ported from `GigabyteRGBFusion2USBController`)
All packets are 64-byte HID feature reports (`HidD_SetFeature`). For a solid zone color the
broker uses the `PktEffect` packet:

- header `0x20 + zone` (zones `0x20..0x27`), `zone0 = 1 << zone`,
- `effect_type = 1` (static), `max_brightness = 100`, `color0 = (r<<16)|(g<<8)|b`,
- then **Apply** = `0x28 0xFF`.

Addressable per-LED strips (the `D_LED` `0x58/0x59` `PktRGB` path) are scaffolded but **v1
exposes solid-color zones** (whole-device set), which is what the broker's `rgb.set {color}` /
per-LED-averaged `{colors}` map onto cleanly.

### Broker integration (mates with the existing `rgb.list` / `rgb.set`)
RGB control gains a small abstraction so the new transport coexists with the validated ENE
SMBus DRAM path **without changing it**:

- `IRgbController` — `{ Id, Label, LedCount, SetAll(rgb), SetLeds(colors) }`.
- `EneRgbController` — wraps the existing `EneController` (the ENE/Aura SMBus DRAM path,
  behavior identical to before).
- `GigabyteUsbRgbController` — one per IT8297 zone, drives it over USB-HID.
- `RgbRegistry` — built at control-service startup: ENE SMBus devices **when the driver write
  path is present** (as today) **plus** any detected Gigabyte USB zones. `BrokerControlServer`
  serves `rgb.list` / `rgb.set` from the registry.

Consequence: **Gigabyte USB RGB works even with no kernel driver installed** (the control
service offers `rgb:write` whenever the registry has any device, SMBus *or* USB). The MSI ENE
path is unaffected — same `EneController` calls, just behind the `EneRgbController` wrapper.

---

## Auto-detect summary (nothing fires on the wrong hardware)

| Capability | Gate | Effect when absent |
|---|---|---|
| ITE board sensors | kernel finds an ITE chip id on LPC | broker lists no `board.ite.*`; NCT path (if any) unaffected |
| NCT board sensors | kernel finds a Nuvoton chip id | unchanged from today |
| Gigabyte USB RGB | IT8297 USB-HID (VID 0x048D) enumerates | registry has no USB zones; ENE DRAM path unaffected |
| ENE DRAM RGB | driver write path present (as today) | unchanged from today |

The MSI dev box: ITE detect fails (it's Nuvoton) → no ITE sensors; no IT8297 USB device → no
Gigabyte RGB. **Zero behavior change on the dev box.**

---

## Build & hand-off (for the tester)

1. **Driver** (adds `SuperioIte.c`): `BrokerSmbusDriver\scripts\Build-Driver-DirectLink.ps1`,
   then test-sign + install (`Sign-Driver-TestCert.ps1`, `bcdedit /set testsigning on`, reboot,
   `Install-Driver.ps1`). Needed for **sensors** only.
2. **Bridge** (adds the ITE catalog + USB-HID RGB): `scripts\Build-BrokerSensorBridge.ps1`
   (or `Build-All.ps1`). Needed for sensors **and** RGB.
3. **RGB-only quick test (no driver/admin):** with just the control service running,
   `BrokerSensorBridge.exe --client --control --op=rgb.list` should list Gigabyte zones, and
   `--op=rgb.set --device=mbzone0 --color=00FF00` should turn a zone green.
4. **Sensor test:** `--client --op=sensor.list` should show `board.ite.*`; compare each
   `board.ite.tempN` / `fanN` / `voltN` to **HWiNFO** and report the index→label mapping back.

### Validation checklist to "bake in"
- [ ] ITE chip id detected + logged (driver INFO `SuperioChipId`).
- [ ] `board.ite.tempN` match HWiNFO board temps (to the degree) → name + bake labels.
- [ ] `fanN` RPM match HWiNFO → name headers.
- [ ] `voltN` — derive each rail's divider vs HWiNFO (this is the unconfirmed part) → bake.
- [ ] IT8297 USB device enumerated (VID 0x048D, PID logged).
- [ ] Each `mbzone{0..7}` maps to a physical header → name zones.
- [ ] Confirm solid-color set + apply works; note which zones are addressable (future per-LED).

Once the tester confirms, promote the by-index `board.ite.*` ids and `mbzone*` to named ids and
remove the `unconfirmed` markers — the same path the NCT6687D map followed.

---

## Deferred (documented, not in v1)
- **IT8297 over SMBus `0x68`** (older allowlisted boards): needs a brick-guarded **block-write**
  IOCTL (the driver currently writes byte/word only). Higher risk; do only if a tester's board
  is USB-less.
- **Addressable `D_LED` strips** (per-LED `PktRGB`): scaffolded; v1 is solid-color zones.
- **Intel-Gigabyte SMBus writes**: the kernel write path is wired/validated for AMD FCH only.
- **Native product names / effects in OpenRGB**: same OpenRGB-core-fork question as the rest of
  the project (shape A in `OPENRGB-INTEGRATION.md`).
