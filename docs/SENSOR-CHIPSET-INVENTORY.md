# Sensor & RGB chipset inventory

> What hardware the broker + driver can read/drive, organized **by chipset** and cross-referenced
> **by motherboard manufacturer**. Updated 2026-06-12 (all listed backends verified wired:
> detection, dispatch, catalog channels, and selftest gates).
>
> Legend — **Status**:
> - ✅ **Validated** — confirmed on real hardware (HWiNFO / live read or write on the dev box).
> - 🟡 **Implemented, unvalidated** — code ported verbatim from proven sources, builds + unit-tested,
>   but not yet run on the specific silicon (auto-detected, inert when absent).
> - ⬜ **Written, not validated** — code exists but no hardware path proven.
> - 🗄️ **Archived** — removed from the build pending better information.
>
> **Golden rule:** clients name a *logical sensor / device*, never an address. Every register and
> hardware sequence here is **ported verbatim** from Linux hwmon driver register facts —
> never invented. See the guardrails in `CONTRIBUTING.md` §3.

---

## 1. By chipset / sensor source

### CPU temperature — AMD SMU (SMN) · ✅ Validated
- **What:** Ryzen Family 17h/19h reported temperature (Tctl) + per-CCD die temps.
- **How:** `IOCTL_BROKER_SMU_READ` → `SmuAmd.c` (PCI root-complex SMN window). Decode k10temp/zenpower (`SensorDecode.AmdCpuTctlC` / `AmdCcdTempC`).
- **Channels:** `smu.cpu.temp`, `smu.ccd.0..7` (CCDs presence-gated on the valid bit).
- **Source:** Linux `k10temp` / `zenpower`. **Replaces WinRing0 for CPU temp.**

### Board temps / fans / voltages — Nuvoton NCT668x (EC-space) · ✅ NCT6687D / 🟡 NCT6683 · NCT6686
- **What:** board temperatures, fan tachometers, voltages.
- **How:** `IOCTL_BROKER_SUPERIO_READ` → `SuperioNct.c`. LPC SIO detect, HWM LDN `0x0B`, EC page/index/data window, banks `0x100`/`0x120`/`0x140`.
- **Chips / ids (masked `0xFFF0`):** NCT6683 `0xC730` 🟡 · NCT6686 `0xD440` 🟡 · NCT6687D/DR `0xD590`/`0xD592` ✅.
- **Channels:** `nct6687d.temp.*` / `.fan.*` / `.volt.*` (the `nct6687d.*` ids are the stable key for the whole EC family).
- **Detail:** `docs/SUPERIO-NCT6683-NCT6686.md`. **Source:** Linux `nct6683.c`, `Fred78290/nct6687d`.

### Board temps / fans / voltages — Nuvoton NCT6775 family (bank-select) · 🟡 Implemented, unvalidated
- **What:** board temperatures, fan tachometers, voltages.
- **How:** `IOCTL_BROKER_SUPERIO_READ` → `SuperioNct6775.c`. LPC SIO detect (mask `0xFFF8`), HWM LDN `0x0B`, **bank-select** window, IO-space-lock clear on NCT6791+.
- **Chips / ids (masked `0xFFF8`):** NCT6779 `0xC560` · NCT6791 `0xC800` · NCT6792 `0xC910` · NCT6793 `0xD120` · NCT6795 `0xD350` · NCT6796 `0xD420` · NCT6797 `0xD450` · NCT6798 `0xD428`.
- **Channels:** `nct6775.temp.0..5`, `nct6775.fan.0..6`, `nct6775.volt.0..15`.
- **Detail:** `docs/SUPERIO-NCT6775-FAMILY.md`. **Source:** Linux `nct6775-core.c` / `nct6775-platform.c`.
- **Not yet covered (documented):** older NCT6775F/6776F; rarer NCT6799D/6701D/5585D.

### DIMM temperature — JEDEC JC42.4 / TSE2004av (SMBus) · ✅ Validated
- **What:** per-DIMM thermal sensor.
- **How:** SMBus read at `0x18 + slot` reg `0x05` via `SmbusAmd.c`; decode `SensorDecode.Jc42TempC`. Presence-probed per slot.
- **Channels:** `dimm.0..7`.
- **Source:** Linux `jc42`.

### SMBus host controller — AMD FCH (PIIX4/SB800) · ✅ Validated
- **What:** the SMBus transport that DIMM temps + DRAM RGB ride on.
- **How:** `SmbusAmd.c`, KERNCZ base discovery + 4-port mux, gated on device-id/revision. Read validated on real SPD/DIMM temps; bounded brick-guarded write validated for DRAM RGB.
- **Source:** Linux `i2c-piix4`.

### SMBus host controller — Intel i801 · ⬜ Written, not validated
- **How:** `SmbusIntel.c` (BAR4). Code complete; **no Intel test hardware** — read unproven, write `NotImplemented`.
- **Source:** Linux `i2c-i801`.

---

## 2. By chipset / RGB control

### DRAM RGB — ENE / Aura (SMBus) · ✅ Validated
- **What:** per-LED DDR4 DRAM lighting (the canonical non-admin RGB demo).
- **How:** brick-guarded SMBus block-write (`EneController` / `RgbCatalog`) over `IOCTL_BROKER_SMBUS_WRITE`, address allowlist enforced **in-kernel**. Direct mode, per-LED frames.
- **Devices:** `ram0`, `ram1` (baked map; client names the device, never an address).

---

## 3. By motherboard manufacturer

This maps the **Super-I/O sensor chip** (the part that varies most by vendor) to manufacturers,
so you can tell at a glance whether a given board's board-level sensors are covered. CPU temp
(AMD SMU) and DIMM temp (JC42) are vendor-independent and covered wherever the AMD FCH SMBus is
present. RGB coverage is DRAM-only today.

| Manufacturer | Common Super-I/O sensor chip(s) | Covered by | Status |
|---|---|---|---|
| **MSI** | NCT6687D / NCT6686 (EC-space) | `SuperioNct.c` | ✅ 6687D / 🟡 6686 |
| **ASUS** | NCT6798D / NCT6796D / NCT6793D / NCT6791D (NCT6775 family) | `SuperioNct6775.c` | 🟡 |
| **ASRock** | NCT6796D / NCT6779D / NCT6791D (NCT6775 family) | `SuperioNct6775.c` | 🟡 |
| **Gigabyte** | NCT6798D / NCT6796D on some boards; **ITE IT87xx** on others | `SuperioNct6775.c` (Nuvoton boards only) | 🟡 Nuvoton / 🗄️ ITE archived |
| **EVGA** | NCT6779D / NCT6796D (NCT6775 family) | `SuperioNct6775.c` | 🟡 |
| **Biostar** | NCT6779D / NCT6796D (NCT6775 family) | `SuperioNct6775.c` | 🟡 |

> Notes & caveats:
> - The Super-I/O part is a **board-design choice**, not fixed per brand — a vendor ships
>   different chips across model years and tiers. Treat the table as "what you'll usually find,"
>   and let **auto-detect** (the chip id read at `SIO 0x20/0x21`) be the source of truth. Run
>   `BrokerSensorBridge.exe --calibration` to print the detected board DMI + chip id + resolved
>   catalog on any specific board.
> - **Gigabyte** boards that use a Nuvoton NCT6775-family Super-I/O for sensors are covered by
>   the new backend; Gigabyte boards that use an **ITE** Super-I/O are **not** (ITE archived).
> - 🟡 entries are register-correct from proven sources but **not yet validated on that exact
>   silicon** — the dev box is an MSI NCT6687D. Validation on a real board is the remaining step;
>   the failure mode for an unvalidated *read* is a wrong number, never a brick.

---

## 4. Quick coverage summary

| Capability | Mechanism | Status |
|---|---|---|
| CPU die temp (Ryzen) | AMD SMU / SMN | ✅ |
| Board temps/fans/volts (MSI) | NCT6687D EC-space | ✅ |
| Board temps/fans/volts (MSI newer) | NCT6683 / NCT6686 EC-space | 🟡 |
| Board temps/fans/volts (ASUS/ASRock/Gigabyte-Nuvoton/EVGA/Biostar) | NCT6775 family bank-select | 🟡 |
| DIMM temps | JC42 over AMD FCH SMBus | ✅ |
| DRAM RGB (per-LED, non-admin) | ENE/Aura SMBus, brick-guarded | ✅ |
| Intel SMBus host | i801 | ⬜ |
| Gigabyte/ITE motherboard RGB + ITE sensors | — | 🗄️ archived |
