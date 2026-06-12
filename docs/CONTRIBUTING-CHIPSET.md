# Contributing a chipset

The step-by-step guide to adding hardware support — a new Super-I/O sensor chip, SMBus
host controller, or CPU-vendor sensor path. Read [CONTRIBUTING.md](CONTRIBUTING.md)
first for the build environments, conventions, and the guardrails; this document is the
deep walkthrough of the one task it summarizes in §4b.

A chipset contribution is **four small pieces**, each with a proven in-tree example:

| # | Piece | Lives in | Reference |
|---|---|---|---|
| 1 | Register-fact table ported from a proven source | your head → the file banner | §2 |
| 2 | Kernel backend (`probe` + bounded `read`) | `BrokerSmbusDriver/Superio<Chip>.c` | `SuperioNct.c` |
| 3 | Decoder (raw bytes → engineering units) | `BrokerSensorBridge/Sensors/SensorDecode.cs` | `NctTempC` et al. |
| 4 | Channel registration (stable ids, chip-id gate) | `BrokerSensorBridge/Sensors/RawChannel.cs` | `nct6687d.*` block |

No protocol change, no new IOCTL, no client change — a sensor chip rides the existing
named `{kind, index}` Super-I/O read op, and clients discover the new ids via
`sensor.list` automatically.

---

## 1. Ground rules (the non-negotiables)

These come from the project's security model
([BROKER-DESIGN.md](BROKER-DESIGN.md)); a PR that violates one will be rejected:

- **Register maps live in signed code, never in data.** Your chip's registers are baked
  into the kernel backend; the broker-side channel table is C# code. Nothing
  client-supplied or file-supplied ever becomes an address.
- **Port register facts from proven sources — never invent.** Primary source is the
  Linux hwmon/i2c driver for your chip; cross-check against a **second independent
  reference** (a datasheet, a second open-source implementation). Reproduce *facts*
  (ports, ids, offsets, encodings), not code expression — see
  [`THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md).
- **Read-only.** A sensor backend performs no hardware writes beyond what the access
  mechanism itself requires (e.g. bank/page select, the documented one-bit
  IO-space-lock clear on NCT6791+). Anything more needs explicit security review.
- **Detection is exclusive.** One board matches exactly one Super-I/O backend. Your
  probe must no-op if an earlier backend already claimed a chip, and your chip-id gate
  must not overlap an existing family's ids.
- **Raw ids are persistence keys.** Clients save selections by id; once your ids ship,
  they never change. Choose the family-keyed prefix carefully (§5).

---

## 2. Port the register map from Linux hwmon

Find the Linux driver for your chip (`drivers/hwmon/` — e.g. `nct6683.c`,
`nct6775-core.c`, `it87.c`) and extract a **fact table**. For a Super-I/O chip you
need:

| Fact | Example (NCT668x, from `SuperioNct.c`) |
|---|---|
| SIO config ports | `0x2E/0x2F`, fallback `0x4E/0x4F` |
| Enter / exit key | `0x87` ×2 / `0xAA` |
| Chip-id registers + expected ids + mask | regs `0x20/0x21`; `0xC730/0xD440/0xD590` masked `0xFFF0` |
| HWM logical device number | `0x0B` |
| Base-address registers | `0x60/0x61` |
| Access mechanism | EC page/index/data window at base `+0x04/+0x05/+0x06` |
| Sensor register layout per kind | temps `0x100+i·2`, volts `0x120+i·2`, fans `0x140+i·2` |
| Channel counts per kind | 7 temps / 16 volts / 8 fans |
| Raw encodings | temp = signed byte + half-degree bit; fan = 16-bit BE RPM |

Record **both sources** (file + repo/commit or document) in your backend's banner
comment — `SuperioNct.c`'s banner is the template. If the two sources disagree on a
fact, stop and resolve it before writing code.

> Pick the right family boundary. If several chips share the detection flow, window
> mechanism, and register layout (as NCT6683/6686/6687D do), they are **one backend
> with a chip-id gate**, not three backends. If they differ in mechanism (as the
> bank-select NCT6775 family does from the EC-space NCT668x), they are separate
> backends even though they're all "Nuvoton".

---

## 3. Write the kernel backend

Create `BrokerSmbusDriver/Superio<Chip>.c` modeled on
[`SuperioNct.c`](../BrokerSmbusDriver/SuperioNct.c) (EC-window mechanism) or
[`SuperioNct6775.c`](../BrokerSmbusDriver/SuperioNct6775.c) (bank-select mechanism).
The shape is two functions plus the descriptor row that registers them:

```c
/* Probe: read the SIO chip id; claim the controller ONLY on a gate match.
   MUST no-op if a previous backend already claimed a chip. */
VOID SuperioXxxDetect(_Inout_ SMBUS_CONTROLLER* Controller);

/* Bounded read: a named {Kind, Index}, never an address. Validate Index against
   the per-kind count and return BrokerSmbusBadRequest past it. Compute the
   register from the BAKED base + index, serialize hardware access with the
   backend mutex, return raw bytes (no interpretation in Ring 0). */
UINT32 SuperioXxxRead(_In_ const SMBUS_CONTROLLER* Controller,
                      _In_ UINT32 Kind, _In_ UINT32 Index, _Out_ UINT32* Raw);
```

Then wire it up (three one-line changes):

1. Declare both functions in `BrokerSmbusDriver/SmbusController.h`.
2. Add a `BROKER_SUPERIO_KIND_XXX` value in `inc/SmbusBrokerProtocol.h` —
   **append a new value; never renumber or reuse an existing one** (`KIND_ITE = 2`
   is reserved by an archived backend, for example).
3. Add one descriptor row to the Super-I/O detection table (in `SuperioNct.c`) and
   one `case` to `SuperioReadDispatch`. Order in the table is detection order.

Driver conventions: boxed `/*---*\` banner with the fact table + both sources; builds
clean under `/W4 /WX`; every IOCTL-reachable field validated before use. Build with
`.\BrokerSmbusDriver\scripts\Build-Driver-DirectLink.ps1` (stop the services first —
a loaded driver locks the `.sys`).

**SMBus host controllers** follow the same pattern one level up: a
`SmbusXxxDiscoverBuses` + `SmbusXxxRead/Write` pair in `SmbusXxx.c` (reference:
`SmbusAmd.c`, validated; `SmbusIntel.c`, written), registered in `SmbusDetect.c`'s
vendor dispatch. Bring read-up on **SPD (`0x50`, read-only, non-destructive)** before
anything else.

---

## 4. Write the broker decoder

Add pure functions to
[`Sensors/SensorDecode.cs`](../BrokerSensorBridge/Sensors/SensorDecode.cs) converting
the raw register packing your backend returns into the chip's native unit (°C / RPM /
volts-at-pin). Cite the source in the XML doc comment, like the existing decoders:

```csharp
/// <summary>
/// XXX voltage ADC reading (Linux xxx in_from_reg): one byte at 8 mV/LSB.
/// This is the PIN reading; board dividers are calibration scale, applied later.
/// </summary>
public static int XxxVoltageMv(uint raw) => (int)(raw & 0xFF) * 8;
```

Decoders are board-independent and carry no labels — per-rail divider multipliers and
human names are **calibration data** (`calibration.default.json`), layered on top by
`SensorCatalog`. Don't fold a board's divider into the decode.

---

## 5. Register the channels

Add a channel block to
[`Sensors/RawChannel.cs`](../BrokerSensorBridge/Sensors/RawChannel.cs):

- **Pick the stable id prefix** — `{family-key}.{kind}.{index}`, e.g.
  `nct6687d.volt.0`. The prefix is the key for the whole register-identical family
  (`nct6687d.*` deliberately covers NCT6683/6686/6687D), because clients persist these
  ids. Renames later happen only via the alias map, never in place.
- **Gate on the detected chip id** — an availability predicate over
  `ISmbusBackend.SuperioChipId` under your family's mask, mutually exclusive with the
  other families' gates (see `IsNct` / `IsNct6775`).
- Each channel names a backend op (`TryReadSuperioRaw(kind, index, …)`) and applies
  your decoder. **No addresses appear at this layer.**

Then document the ids: one row per channel group in
[`SENSOR-MAP.md`](SENSOR-MAP.md), a section in
[`SENSOR-CHIPSET-INVENTORY.md`](SENSOR-CHIPSET-INVENTORY.md) (marked 🟡 until
hardware-validated), and a family detail doc if the mechanism is new
(pattern: [`SUPERIO-NCT6775-FAMILY.md`](SUPERIO-NCT6775-FAMILY.md)).

Add a selftest gate case: `--selftest` runs chipset detection gates against mock
chip ids with no hardware — assert your chip id lights your channels, a sibling
family's id does not, and your decode produces known values from known raw bytes
(see the existing NCT6775 cases).

---

## 6. Validate on hardware

`--selftest` proves the gates and decodes; only real silicon proves the register map.
On a board with your chip:

1. Build everything; install (`.\scripts\Install-SensorBrokerService.ps1`).
2. `BrokerSensorBridge.exe --calibration` — confirm the printed board DMI, detected
   chip id, and resolved catalog (your channels present, others absent).
3. From a **non-admin** shell: `--client --op=sensor.readall` — every channel returns
   `Ok` or an honest status.
4. Compare against a known-good monitor (HWiNFO) — temps to the degree, fans to a few
   RPM, voltages to the millivolt *after* calibration scaling.
5. For register-level debugging, dev-probe builds
   (`dotnet publish -p:DevProbes=true`, see [DEV-GUIDE.md](DEV-GUIDE.md)) expose
   `--superio-read` / `--smbus-read`. Never ship one.

No hardware in hand? Submit anyway, marked 🟡 **implemented, unvalidated** — that's an
honest, accepted state (`SuperioNct6775.c` shipped that way), and the validation
campaign ([TESTING.md](TESTING.md)) exists to close the gap.

## 7. PR checklist

- [ ] Fact table in the backend banner, with **two independent sources** cited
- [ ] Probe is gate-exact, no-ops when a chip is already claimed, no id overlap
- [ ] Read path bounded (`Index` validated per kind) and read-only
- [ ] Driver builds warning-clean (`/W4 /WX`); broker builds; **`--selftest` green**
- [ ] Selftest gate case added for the new chip id
- [ ] No addresses outside signed code (grep your diff for hex in `.cs`/`.json` data)
- [ ] Ids follow `{family}.{kind}.{index}` and are documented in `SENSOR-MAP.md`
- [ ] `SENSOR-CHIPSET-INVENTORY.md` updated with the honest status (✅ or 🟡)
- [ ] Validation evidence in the PR: board model + DMI string, chip id, side-by-side
      readings vs HWiNFO — or an explicit "no hardware, 🟡" statement
