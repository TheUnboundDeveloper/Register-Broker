# Super-I/O support: Nuvoton NCT6775 family (bank-select)

> Status: **IMPLEMENTED 2026-06-11 as a new kernel backend (`SuperioNct6775.c`).**
> Hardware-UNVALIDATED (the dev box is an NCT6687D, a different family). Every register and
> sequence is **reproduced verbatim from the Linux `nct6775-core.c` / `nct6775-platform.c` /
> `nct6775.h` register facts, cross-checked for byte-for-byte agreement against a second
> independent reference** before any code was written. Auto-detected and inert on non-matching
> hardware. **Read-only.**

## Why this chip family

The Nuvoton **NCT6775 "classic" family** is the single most common Super-I/O hardware-monitor
line on consumer motherboards — used by **ASUS, ASRock, Gigabyte, EVGA, Biostar** and others
across roughly a decade of boards. It is a **different architecture** from the NCT668x EC-space
family already supported (`docs/SUPERIO-NCT6683-NCT6686.md`): it uses a **bank-select** I/O
window rather than a page/index/data EC window.

## Scope: the modern group only

This backend implements the **modern group** — the chips that share one uniform register
layout and dominate boards from ~2015 onward:

| Chip | Chip-id (masked `0xFFF8`) | Era / typical boards |
|---|---|---|
| NCT6779D | `0xC560` | Intel Z170/Z270, X299; some AM4 |
| NCT6791D | `0xC800` | Intel Z170/Z270 (ASUS ROG), X99 |
| NCT6792D | `0xC910` | Intel Z270/X299; some AM4 |
| NCT6793D | `0xD120` | Intel Z370 |
| NCT6795D | `0xD350` | Intel Z390 |
| NCT6796D | `0xD420` | Intel Z490/Z590, AMD X570/B550 (ASUS/ASRock/Gigabyte) |
| NCT6797D | `0xD450` | Intel Z590/HEDT |
| NCT6798D | `0xD428` | Intel Z690/Z790, AMD X670/B650 (ASUS/ASRock/Gigabyte) |

> **The `0xFFF8` mask is load-bearing.** NCT6796 (`0xD420`) and NCT6798 (`0xD428`) differ only
> in the low nibble; a wider mask would conflate them. The kernel keeps `0xFFF8` exactly. (For
> the broker's "is it any NCT6775" channel gate, a `0xFFF0` mask is sufficient and used, because
> 6796 and 6798 expose identical channels — but the two families' id sets are disjoint under
> `0xFFF0`, so no chip is ever misclassified.)

**Deliberately out of scope (documented, not guessed):** the older **NCT6775F / NCT6776F**
(different register banks, and NCT6775F needs a fan-divisor decode that crosses registers), and
the rarer **NCT6799D / NCT6701D / NCT5585D** (exact chip ids not confirmed from a second source
at implementation time). These are recognized as *not* the modern group and simply fall through
(no false claim). They can be added later from the same sources.

## Access architecture (kernel — `BrokerSmbusDriver/SuperioNct6775.c`)

### Detection
1. SIO config port `0x2E` (then `0x4E`); enter key `0x87, 0x87`; exit `0xAA`.
2. Chip id = `(SIO[0x20] << 8) | SIO[0x21]`, matched with mask `0xFFF8` against the table above.
3. Select HWM logical device: write `0x0B` to SIO `0x07`.
4. Base I/O port = `(SIO[0x60] << 8) | SIO[0x61]`, aligned `& ~7` (`IOREGION_ALIGNMENT`).
5. Activate the HWM LDN if needed (SIO `0x30` bit0).
6. **IO-space-lock clear (NCT6791 and newer only):** read SIO config register `0x28`; if bit
   `0x10` is set, write it back with bit `0x10` cleared. This is the **only write** the backend
   performs — a single, bounded, read-modify-write of one bit in Super-I/O config space, ported
   **verbatim and byte-identical** from Linux `nct6791_enable_io_mapping`
   (`NCT6791_REG_HM_IO_SPACE_LOCK_ENABLE = 0x28`, `val & ~0x10`), and confirmed byte-identical
   against a second independent reference. **NCT6779D is excluded** — it has no such lock, exactly
   as the references do. Without this clear, the HWM register window is unreadable on the affected chips.
7. Exit config; require the base to be page-aligned, ≥ `0x100`, and `(base & 0xF007) == 0`.

### HWM register reads (bank-select window)
For a 16-bit register `reg`, with `addrPort = base+5`, `dataPort = base+6`:
```
out(addrPort, 0x4E);          // bank-select
out(dataPort, reg >> 8);      // bank
out(addrPort, reg & 0xFF);    // offset
in (dataPort);                // data byte
```
Serialized under a driver mutex (the bank/offset is global controller state). This matches
the Linux `nct6775` port access path.

## Sensors exposed and decode

The kernel returns **raw bytes**; the broker decodes (`SensorDecode.cs`). Channel ids are
`nct6775.{kind}.{index}`; labels come from board calibration data.

### Temperatures — 6 channels (`nct6775.temp.0..5`)
Registers `{0x073, 0x075, 0x077, 0x079, 0x07B}` (the 5 peripheral *monitor* registers present
across the entire modern group — the NCT6779 floor) plus `0x027` (PECI/CPU peripheral). The
monitor registers are **word-sized**: value byte at `reg`, half-degree (`+0.5 °C`) in **bit 7 of
`reg+1`**; `0x027` is byte-only. Decode = **`NctTempC`** — *identical* to the NCT668x decode
(`signed byte + 0.5 × half-bit`), so it is reused, not re-implemented.

> Which physical sensor each monitor slot reflects (CPUTIN / SYSTIN / AUXTIN…) depends on the
> board's source routing, so — exactly like the existing NCT6687D path — the **labels are board
> calibration data**, not baked into the kernel. Chips with more thermal sources than the
> universal six (e.g. NCT6796/6798 PCH/DIMM/TSENSOR temps) expose only the universal set here;
> the extras are a deliberate, documented omission rather than a guess.

### Fans — 7 channels (`nct6775.fan.0..6`)
Registers `{0x4C0, 0x4C2, 0x4C4, 0x4C6, 0x4C8, 0x4CA, 0x4CE}` (Linux `NCT6779_REG_FAN`), read as
**direct 16-bit big-endian RPM**. Linux uses `fan_from_reg_rpm` for this whole group — these
registers already hold RPM, so no `1350000/count` division is needed. Decode = **`NctFanRpm`**
(raw 16-bit), reused from the NCT668x path.

> This is the one place the two reference approaches genuinely diverge: one reads a *different*
> register bank (`0x4B0…`) as a 13-bit tach **count** and computes `1350000/count`. We follow
> the **Linux direct-RPM `0x4C0` path**, which is simpler and proven word-sized. Mixing the two
> (a `0x4C0` register with a `/count` decode) would give garbage — the implementation does not
> mix them.

### Voltages — 16 channels (`nct6775.volt.0..15`)
Registers `0x480 + index` (`0x480..0x48F`), each a **single ADC byte at 8 mV/LSB**. Decode =
**`Nct6775VoltageMv(raw) = byte × 8`** — a **new** decoder, because this differs from the
NCT668x 16-bit voltage packing. Rails behind an external board divider (`+12V`, `+5V`, …) carry
the divider as the calibration **scale** (e.g. `×12`), exactly as the NCT6687D voltages already
do. Default labels are marked *(unconfirmed)* until a board is calibrated.

## Safety posture

- **Read-only sensors.** The sole write is the verbatim IO-space-lock clear (one config-register
  bit) on the chips that are documented to require it. It is not a sensor/SMBus/RGB write and cannot
  reach the SMBus brick-guard surface.
- **No invented encodings.** Every register, mask, port offset, and the unlock sequence is
  reproduced from the Linux `nct6775` register facts and cross-checked against a second
  independent reference. Where the two approaches diverged (fan bank), the choice is documented above.
- **Narrow IOCTL.** The client names a `{kind, index}`; the register is looked up from a baked-in
  table in the kernel. The client never supplies an address and cannot scan.
- **Auto-detect / inert.** Detection runs after the NCT668x probe and no-ops if a chip was
  already claimed; on non-NCT6775 hardware nothing is exposed.

## Verification

- Kernel driver builds clean under `/W4 /WX`.
- `BrokerSensorBridge.exe --selftest` locks the decode math and gates: NCT6798 (`0xD428`)
  voltage `0x64 → 0.800 V` (byte×8), temp `0x8064 → 100.5 °C` (signed + half), and that NCT6775
  and the EC family are **mutually exclusive** (neither lights the other's channels).
- **Live hardware validation is pending** — no NCT6775-family board on hand. When one is
  available: confirm detection (`--calibration` prints the chip id), sanity-check a couple of
  temps against HWiNFO, and calibrate the voltage rail scales.

## Sources

- Linux `drivers/hwmon/nct6775-core.c`, `nct6775-platform.c`, `nct6775.h`
