# Sensor & Data Map — authoritative location catalog

Every datum the `BrokerSmbus` driver can surface, with its **baked-in physical
location** and the user-mode decode. This is the catalog the broker exposes by
**logical name** — clients request by name, never by address. No scanning, no
client-supplied addresses on the broker path (see memory `sensor-catalog-no-scan`).

Conventions: the **driver returns raw register bytes**; the **broker applies the decode**
(`Sensors/SensorDecode.cs` — all Linux-hwmon register facts). Locations are hardcoded in the
kernel (the client can never name a raw address). Public ids are **stable raw ids**
`{chip}.{kind}.{index}` (`Sensors/RawChannel.cs`); labels and per-rail scales come from
**board calibration data** (`calibration.default.json` + optional user override — see
`CALIBRATION-AND-REGISTRY-PLAN.md`). Legacy semantic ids (`cpu.temp`, `board.vrm.temp`,
`fan3`, `dimm0.temp`, …) still resolve via the built-in alias map in
`Sensors/Calibration.cs`.

---

## Performance metrics (read-only)

| Logical name | Source | Baked-in location | Raw → value | Status |
|---|---|---|---|---|
| `smu.cpu.temp` (alias `cpu.temp`) | AMD SMU / SMN | root complex `00:00.0` PCI cfg `0x60`(index)/`0x64`(data); SMN reg **`0x00059800`** | `((raw>>21)&0x7FF)·0.125`; if `raw & (1<<19)` then `−49`; Tdie = Tctl − offset (0 on Vermeer) °C | ✅ validated (5800X3D: `0x69BB0000` → 56.6 °C, matched HWiNFO) |
| `smu.ccd.0`–`smu.ccd.7` (alias `cpu.ccd{n}.temp`) | AMD SMU / SMN | SMN `0x00059800 + ccd_offset + ccd*4` (`ccd_offset` per-model from `k10temp`; Vermeer=`0x154`) | valid if `raw & 0x800`; °C = `(raw & 0x7FF)·0.125 − 49` | ✅ validated (ported from `k10temp`); each CCD appears only when its valid bit is set (HWiNFO "CPU CCD1" = our `smu.ccd.0`). |
| `smu.cpu.vcore` | AMD SMU / SMN | SVI2 telemetry plane, SMN `0x0005A010` (core; Matisse 17h/0x71 + Vermeer 19h/0x21) | V = `1.550 − 0.00625·((raw>>16)&0xFF)`, clamped ≥ 0 | ✅ validated (5800X3D); ported from `zenpower`. Offered only on models with known planes; else absent. |
| `smu.soc.voltage` | AMD SMU / SMN | SVI2 telemetry plane, SMN `0x0005A00C` (SoC; same models) | V = `1.550 − 0.00625·((raw>>16)&0xFF)`, clamped ≥ 0 | ✅ validated (5800X3D); ported from `zenpower`. Currents/power deliberately **not** exposed (board-dependent telemetry factor); PM-table power/clocks deferred — see README. |
| `nct6687d.temp.0`–`.5` (aliases `board.{cpu,system,vrm,chipset,socket,pcie}.temp`) | Super-I/O NCT668x EC (NCT6683/6686/6687D) | EC `0x100 + idx*2` | signed byte °C + half-bit at `(reg+1)>>7` (`NctTempC`) | ✅ NCT6687D validated; MSI B550I calibration labels matched HWiNFO ("System"/"MOS"/"Chipset"/"CPU Socket"/"PCIE_1" to the degree). NCT6683/6686 light the same channels (chip-id gated), HW-unvalidated. |
| `nct6687d.fan.0`–`.7` (alias `fan{i}`) | Super-I/O NCT668x EC | EC `0x140 + i*2` (16-bit BE) | RPM directly (`NctFanRpm`) | ✅ (fan3 = 3785 RPM; header labels board-specific) |
| `nct6687d.pwm.0`–`.7` (labels `Fan {i} PWM`) | Super-I/O NCT668x EC | EC `0x160 + i` (single duty byte) | `raw/255·100` → % (`NctPwmPercent`) | **Read-only telemetry** — the duty the chip is currently driving each fan header at. NO write path exists (reading cannot change a fan). ✅ NCT6687D validated (pwm.3 = 27 % with fan.3 = 4137 RPM; unpopulated headers show their default duty). |
| `nct6687d.volt.0`–`.14` (aliases `board.{12v,5v,soc,dram,vcore,3v3,cpu1p8,3vsb,avsb,vtt,vbat}.volt` + `board.volt{5,6,7,10}`) | Super-I/O NCT668x EC | EC `0x120 + idx*2` (16-bit) | pin mV = `high·16 + (low>>4)` (`NctVoltageMv`); per-rail labels + scales are **calibration DATA** (MSI B550I entry: ×12 +12V, ×5 +5V, ×2 DRAM, else ×1) | ✅ validated against HWiNFO via the MSI B550I calibration entry (generic Linux labels were wrong: idx8 = +3.3V not "AVCC3", idx13 = VTT not "VBat", idx14 = VBat added). idx11↔12 (3VSB/AVSB, both 3.340 V) may be swapped; idx5/6/7/10 kept generic. |
| `nct6775.temp.0`–`.5` | Super-I/O NCT6775 bank-select family (NCT6779/6791/6792/6793/6795/6796/6797/6798) | bank/index HWM registers, baked in the kernel backend (`SuperioNct6775.c`; see `SUPERIO-NCT6775-FAMILY.md`) | same NCT temp packing (`NctTempC`) | 🟡 built, hardware-unvalidated. Mutually exclusive with the NCT668x EC family (chip-id gated). |
| `nct6775.fan.0`–`.6` | Super-I/O NCT6775 family | baked in kernel backend | 16-bit RPM (`NctFanRpm`) | 🟡 built, hardware-unvalidated |
| `nct6775.volt.0`–`.15` | Super-I/O NCT6775 family | baked in kernel backend | single ADC byte × 8 mV (`Nct6775VoltageMv`); rail scales are calibration data | 🟡 built, hardware-unvalidated (default labels "unconfirmed") |
| `dimm.0`–`dimm.7` (alias `dimm{n}.temp`) | SMBus TSE2004 (JC-42.4) | bus 0, addr `0x18+slot`, reg `0x05` (word, MSB-first) | `sign_extend(reg[12:0]) · 0.0625` °C (`Jc42TempC`, ported from Linux `jc42`) | ✅ validated — dev box DIMMs DO have onboard TS; `dimm.0/1` = 42.5/41.8 °C, matched HWiNFO. Each id appears only when a sensor ACKs at that slot (non-destructive probe). |

Super-I/O access: SIO config port `0x2E`/`0x4E`, enter `0x87`×2 / exit `0xAA`; chip id at SIO
`0x20/0x21`. NCT668x EC family ids (masked `0xFFF0`): `0xC730` NCT6683, `0xD440` NCT6686,
`0xD590` NCT6687D — EC path: HWM logical device `0x0B`, EC base from SIO `0x60/0x61`, EC read =
page(`+0x04`)/index(`+0x05`)/data(`+0x06`). The NCT6775 bank-select family is gated on its own
chip-id set; the two Nuvoton families are disjoint, so no chip ever lights both channel sets.
Register facts ported from the Linux hwmon drivers (`nct6687d`, `nct6775`).

The ITE IT87xx backend (`it87.*` channels) was **retired 2026-06-11** (removed from the
tree; design record `GIGABYTE-SUPPORT.md`), along with its `board.ite.*` aliases.

## Config / identification (not a live metric)

| Logical name | Source | Location | Notes |
|---|---|---|---|
| DIMM SPD | SMBus EEPROM | bus 0, addr `0x50–0x57`, reg = byte offset | Static config (DDR4=`0x0C` etc.). Dev box: 2 DDR4 UDIMMs at `0x50`/`0x51`. Read via dev probe only. |

## Control surface (writes — separate service, separate guards)

Exposed by **logical device name**, never an address. Lives in the **separate write-only
control service** (`--control`, pipe `\\.\pipe\BrokerControl`, `rgb:write` scope) — the
sensor broker never offers writes. Ops: `rgb.list` /
`rgb.set {device, color:"RRGGBB" | colors:["RRGGBB",…]}` (per-LED).

Zones come from a **DMI-matched board profile** (`RgbCatalog`, this dev box = MSI B550I). Each zone
reports `kind` (`dram`/`mb12v`/`mbargb`) and `transport` (`smbusene`/`superioec`/`usbhid`).

| Logical name | Kind / transport | Baked-in location | Notes | Status |
|---|---|---|---|---|
| `ram0` | dram / smbusene | bus 0, addr `0x39` | "GSkill RGB (DIMM 0)", 5 LEDs; `EneController` write | ✅ live (non-admin, per-LED) |
| `ram1` | dram / smbusene | bus 0, addr `0x3A` | "GSkill RGB (DIMM 1)", 5 LEDs; same | ✅ |
| `mb.argb0` | mbargb / usbhid | MSI Mystic Light, USB PID `0x7C92`, packet offset 31 (JRAINBOW1) | 60 LEDs; `MysticLightHidController`; `AllowHidRgb` (**on by default**), listed only when the device is present | ✅ live (per-LED validated on dev box) |
| `mb.jrgb0` | mb12v / superioec | NCT6687 EC RGB window | 1 LED zone; `MysticLightEcController`; **inert** until the EC window is HW-validated (`CAP_SUPERIO_RGB` off) | 🟡 wired, not listed |

The ENE/Aura and MSI Mystic Light protocols are publicly documented hardware protocols, reproduced
as register facts. DRAM per-LED frames are **one atomic 3-byte SMBus block write per LED** (driver
`WriteBlock`; DIRECT/APPLY latched once per controller). Addresses/PIDs are baked in signed code;
clients never supply/search them (memory `smbus-write-safety`). Each transport's boundary differs:
SMBus and the EC path are kernel brick-guarded (writes only to `0x70–0x77`/`0x39–0x3A`, or the
NCT6687 RGB register window); the USB-HID path is user-mode (broker report builder + USB PID pin,
no kernel guard) and opt-in. Adding a zone in an existing window is broker-only — no driver rebuild.

---

## Access-path rules

- **Broker (non-admin clients):** catalog-only. Ops are `sensor.list` / `sensor.read {id}` /
  `sensor.readall` over `\\.\pipe\SensorBroker`, gated by the `sensors:read` scope. An
  unknown id is rejected (uniform `deny`); there is no way to request an address/register or
  to enumerate hardware.
- **Control client (non-admin writes):** named-device only. Ops are `rgb.list` /
  `rgb.set {device, color|colors}` over `\\.\pipe\BrokerControl`, gated by the `rgb:write`
  scope (separate write-only service). A device→hardware map is baked in; no client-supplied
  address ever reaches the driver.
- **Dev probes (admin, direct-to-driver, COMPILE-TIME GATED):** `--smu-read` /
  `--superio-read` (named sensors) and `--smbus-read --bus --addr --cmd` (raw addressing)
  open `\\.\BrokerSmbus` directly, bypassing the broker. They exist only in a
  `-p:DevProbes=true` build (a normal build excludes them entirely) — raw addressing exists
  **only** here, for bring-up, and is never reachable by a broker client.
- **Driver:** returns raw register values for baked-in locations only. Narrow IOCTLs
  (`SMBUS_XFER`, `SMU_READ`, `SUPERIO_READ`, brick-guarded write incl. `WriteBlock`), no
  physical-memory/MSR/arbitrary-port primitives.

## Adding a sensor

1. (driver) if a new mechanism: add a narrow IOCTL returning raw bytes for the baked-in
   location; gate detection (e.g. CPUID/PCI/chip id).
2. (map) add the row here with location + decode + source.
3. (broker) add a raw channel in `Sensors/RawChannel.cs` (stable `{chip}.{kind}.{index}` id,
   chip-gated) + a decoder in `Sensors/SensorDecode.cs`; labels/scales come from calibration
   DATA (`calibration.default.json` / user override), never from the channel. It becomes
   available via `sensor.read` / `sensor.readall`. No client-facing addressing is ever added.
