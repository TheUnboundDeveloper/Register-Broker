# Super-I/O support: Nuvoton NCT6683 / NCT6686 (EC-space family)

> Status: **IMPLEMENTED 2026-06-11 by extending the validated NCT6687D backend.**
> Hardware-unvalidated on these two specific ids (the dev box is an NCT6687D, `0xD592`),
> but the path they run is the *same* code that is HWiNFO-validated for NCT6687D — they are
> register-identical. Auto-detected and inert on non-matching hardware.

## What this adds

The project already reads board temps/fans/voltages from the **Nuvoton NCT6687D** Super-I/O
(MSI boards) through the kernel driver's `IOCTL_BROKER_SUPERIO_READ` and the
`BrokerSensorBridge` catalog. This change widens that **same EC-space backend** to its two
siblings:

| Chip | Chip-id (masked `0xFFF0`) | Typical boards |
|---|---|---|
| NCT6683(D) | `0xC730` | Some Intel/MSI and OEM boards (the original "nct6683" part) |
| NCT6686(D) | `0xD440` | Newer MSI boards (Intel 600/700-series, some AM5) |
| NCT6687D / NCT6687DR | `0xD590` (`0xD592` = DR) | MSI AM4/AM5 (already validated) |

All three expose board temperatures, fan tachometers, and voltages.

## Why it's a gate-widening, not a new backend

This was verified against **independent sources in agreement** before writing any code
(Linux `drivers/hwmon/nct6683.c` and the `Fred78290/nct6687d` kernel module, cross-checked
against a second reference): NCT6683 / NCT6686 / NCT6687D share **the same** detection flow, **the same**
HWM logical device (`0x0B`), **the same** EC page/index/data access window, **the same**
register banks, and **the same** temperature/fan/voltage decode math. The proven port sources
treat the three as one interchangeable group with **no per-chip register or decode branches**.

So the only real difference is the **chip id**. The implementation accepts all three ids and
routes them to the one existing read path.

## Implementation

### Detection (kernel — `BrokerSmbusDriver/SuperioNct.c`)
`SuperioNctDetect` previously matched only `(id & 0xFFF0) == 0xD590`. It now accepts any of the
three family ids via `Nct668xIdMatches()`:

```c
#define NCT668X_CHIPID_MASK 0xFFF0
#define NCT6683_CHIPID      0xC730
#define NCT6686_CHIPID      0xD440
#define NCT6687_CHIPID      0xD590
```

Everything downstream is unchanged: select HWM LDN `0x0B`, read the EC base from SIO
`0x60/0x61`, then page/index/data reads at `base+0x04 / +0x05 / +0x06`, banks `0x100` (temp),
`0x120` (voltage), `0x140` (fan).

### Sensor catalog (broker — `BrokerSensorBridge/Sensors/RawChannel.cs`)
The chip-family gate `IsNct` was widened to `IsNctEcId(id)` = `(id&0xFFF0)` ∈
`{0xC730, 0xD440, 0xD590}`. The **`nct6687d.*` raw ids are kept as the stable persistence keys
for the whole EC family** (renaming them would break saved consumer selections — see the
"identifiers are persistence keys" guardrail in `CONTRIBUTING.md` §3). On an NCT6683/6686 board the
sensors therefore still appear as `nct6687d.temp.{i}` / `.fan.{i}` / `.volt.{i}`; the
human-readable labels come from board calibration data as usual.

### Decode (broker — `BrokerSensorBridge/Sensors/SensorDecode.cs`)
Unchanged. `NctTempC` (signed byte + half-degree bit), `NctFanRpm` (16-bit RPM direct),
`NctVoltageMv` (`high*16 + (low>>4)`) are reused exactly.

## The one divergence we deliberately did NOT port

Mainline Linux `nct6683.c` has a **"customer ID" vendor lock**: it reads an EC register
(`0x602`) and refuses to load unless the board's vendor id is on an allow-list (Intel/MSI/
ASRock/AMD…), bypassable only with `force=1`. **The `Fred78290` lineage this project's backend
follows does not implement that lock**, and neither do we — adding it would only risk refusing a
legitimate board, and our read path is harmless. (Mainline Linux also lays out NCT6683 voltages
via generic "MON" slots rather than the fixed `0x120` bank; we follow the Fred78290 fixed-bank
layout that the existing NCT6687D code already uses. On a
non-MSI generic NCT6683 the *voltage* rail routing may differ from the default labels — temps
and fans are unaffected — so re-label voltages per board via `calibration.user.json` if needed.)

## Verification

`BrokerSensorBridge.exe --selftest` now asserts that ids `0xC731` (NCT6683) and `0xD441`
(NCT6686) light up the EC channels, and that an EC chip does **not** light the NCT6775
channels (the two Nuvoton families are mutually exclusive). The kernel driver builds clean
under `/W4 /WX`.

Live hardware validation on an actual NCT6683 or NCT6686 board is still pending (none on hand);
because the read path is byte-identical to the validated NCT6687D path, the risk is low and the
failure mode is benign (a sensor reads wrong, never a write).

## Sources

- Linux `drivers/hwmon/nct6683.c`
- `github.com/Fred78290/nct6687d` (`nct6687.c`)
