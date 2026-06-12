# Plan: data-driven board calibration + backend/detector registry

> Goal: scale from "a few chips I hand-validated" to "any chipset the community has mapped,"
> **without** widening the security surface and **without** breaking the validated stack
> (NCT6687D sensors, AMD SMU temp, ENE DRAM RGB).
> Status: **Phases 1–2 DONE & regression-gated** — the broker assembles its
> sensor catalog from raw channels + a calibration data file; the validated MSI NCT6687D map lives
> in `calibration.default.json`; the `--selftest` calibration cases prove the data path reproduces
> the validated labels/scales, that legacy ids resolve via aliases, and gate the chipset families
> (NCT6683/6686 light the EC channels, NCT6798 lights `nct6775.*`, the two Nuvoton families are
> mutually exclusive, decode math checks). **Phase 3 (detector/backend registry) is the next open
> phase**; phases 4–6 not yet built. Each phase is independently shippable and non-breaking.
>
> Implemented files: `Sensors/SensorDecode.cs`, `Sensors/RawChannel.cs` (raw channels),
> `Sensors/Calibration.cs` (board DMI + loader + alias map), rewritten `SensorCatalog.cs`,
> `calibration.default.json`. Wired in `Program.RunBrokerAsync`; regression test
> `Program.SelfTestCalibration`.
>
> Phase-2 polish DONE (2026-06-10): (a) **dev-box DMI confirmed & matched exactly** — board is
> `Micro-Star International Co., Ltd. / MPG B550I GAMING EDGE MAX WIFI (MS-7C92)`, now an exact
> `match` in the default file; (b) **user-override file wired** —
> `C:\ProgramData\SensorBroker\calibration.user.json` is layered over the baked default (last file
> wins). New `--calibration` inspector prints the detected DMI, which files matched, and the full
> resolved catalog (id/label/unit/availability); `--calibration --user=<path>` tests an override
> from anywhere. Example template shipped as `calibration.user.example.json` (an MSI/NCT6687D
> example). Verified live: a user override relabeled and hid channels over the default, and
> `--selftest` stays PASS.

## The one invariant everything hangs on

There are exactly two layers, and the boundary between them is the security boundary:

| Layer | Lives in | Trust | Can it reach hardware? |
|---|---|---|---|
| **Chip backend** — register maps, decode, detection | signed driver / signed broker code | trusted, audited, rarely changes | yes — it *is* the bounded ring-0 / SMBus surface |
| **Board calibration** — labels, scales, offsets, zone names | a **data file** keyed by board DMI | untrusted, community-sourced, large | **no** — only renames/scales channels the backend already exposes |

**Safety property (must hold for every phase):** calibration data can only (a) rename a channel,
(b) apply a bounded numeric scale/offset to its value, (c) hide a channel. It can **never**
introduce an address, register, index, or scan that the chip backend didn't already expose. The
data schema has **no address field** — injecting one is structurally impossible, not just
disallowed. This is what makes community data PRs safe to accept.

---

## Locked decisions (2026-06-10)

1. **Public ids = stable raw ids + alias map.** The id a client persists is a **stable raw id**
   `{chip}.{kind}.{index}` (e.g. `nct6687d.volt.0`, `nct6775.temp.3`) — never changes, independent
   of calibration. Calibration sets only the **human label** (`"+12V"`) and the **scale**, never
   the id. Existing semantic ids (`board.12v.volt`) are preserved via an **alias map**
   (`board.12v.volt` → `nct6687d.volt.0`) so saved consumer configs keep resolving. (Chosen over
   keeping semantic ids, which would churn whenever a channel is re-labeled.) The ids also serve
   as **family-stable persistence keys**: `nct6687d.*` covers the whole NCT668x EC family
   (NCT6683/6686/6687D) and `nct6775.*` the bank-select family (NCT6779…6798).

2. **RGB breadth = adapt existing GPL controller code in a separate sidecar process.** Reuse
   third-party GPL-2.0 RGB controller/detector tables for fast multi-vendor breadth, but keep
   that GPL code in a **separate sidecar process** the broker talks to over IPC. The **broker +
   driver stay license-clean** (preserving flexibility for manufacturer integration); the GPL
   boundary is the IPC seam (the commonly-accepted "arm's-length" position — still worth a real
   license review before shipping). Sensors are unaffected (no GPL there). (Chosen over
   in-process adapt, which would make the broker binary GPL, and over full re-porting, which
   doesn't scale past a few chips.)

---

## Phase 1 — Surface raw channels + dogfood the data path (no UX change) — ✅ DONE

**Intent:** create the seam calibration plugs into, and *prove* the data layer is non-regressing
by reproducing the hand-tuned output from data.

- Generalize the sensor catalog so each chip backend **enumerates raw channels**
  (`{rawId, kind, index, defaultUnit, defaultDecode}`) with **no labels** — a uniform list for
  the Nuvoton families and future chips (done in
  [Sensors/RawChannel.cs](../BrokerSensorBridge/Sensors/RawChannel.cs)). Default served label =
  the raw id when no calibration matches.
- Move the **existing validated MSI B550I NCT6687D map** (the hand-corrected labels + ×12/×5/…
  scales formerly in [SensorCatalog.cs](../BrokerSensorBridge/SensorCatalog.cs)) out of code and
  into the **first calibration entry** (done — it lives in `calibration.default.json`).
- **Regression gate:** with that entry loaded, `sensor.list` / `sensor.readall` on the dev box
  must produce **the same ids/labels/values as the hand-wired catalog**. Proven via the
  `--selftest` calibration cases before any new chip depended on it.

Shipped as: identical behavior on the dev box, but now driven through the calibration loader.

## Phase 2 — Calibration data format + loader — ✅ DONE

- **Schema (JSON), versioned** (as implemented in `calibration.default.json`):
  ```
  { "schema": 1,
    "defaults": [
      { "rawId": "smu.cpu.temp", "label": "CPU Temperature" },
      { "rawId": "nct6775.volt.0", "label": "Voltage 0 (unconfirmed)" }, ... ],
    "boards": [
      { "match": { "manufacturer": "Micro-Star International Co., Ltd.",
                   "product": "MPG B550I GAMING EDGE MAX WIFI (MS-7C92)" },
        "channels": [
          { "rawId": "nct6687d.volt.0", "label": "+12V", "scale": 12.0 },
          { "rawId": "nct6687d.temp.3", "label": "Chipset Temperature" },
          { "rawId": "nct6687d.volt.10", "hidden": true }   // hidden supported (not used by the shipped entry)
        ] } ] }
  ```
  (Comments and trailing commas are allowed by the loader.)
- **Board identity:** read DMI from the BIOS registry key
  (`HKLM\HARDWARE\DESCRIPTION\System\BIOS`, `BaseBoardManufacturer`/`BaseBoardProduct`) — no
  extra dependency (`BoardIdentity.Detect` in `Sensors/Calibration.cs`).
- **Loader (broker):** generic `defaults` first → board-matched `channels` on top → produce the
  served catalog. No board match → generic defaults → raw ids.
- **Loader validation (the safety gate):** every `rawId` in data must resolve to a backend-exposed
  channel; unknown ids are ignored (never create access); a channel with no `rawId` is dropped.
  Schema has no address/register field at all.
- **Precedence + location:** a baked, curated `calibration.default.json` (ships with the broker)
  overlaid by an optional user file (`%PROGRAMDATA%\SensorBroker\calibration.user.json`, template
  `calibration.user.example.json`) — files are layered, last wins per channel, so a user can fix
  their board with no rebuild.

## Phase 3 — Detector / backend registry (chips become add-one-file) — ⬜ OPEN (next)

- Replace the hand-wired detection (`SuperioNctDetect`-then-`SuperioNct6775Detect` in
  [Driver.c](../BrokerSmbusDriver/Driver.c), the `SuperioReadDispatch` switch in
  [SuperioNct.c](../BrokerSmbusDriver/SuperioNct.c), the AMD/Intel switch in
  [SmbusDetect.c](../BrokerSmbusDriver/SmbusDetect.c), and the chip gates in
  [Sensors/RawChannel.cs](../BrokerSensorBridge/Sensors/RawChannel.cs)) with a **table of
  backends**, each registering `{ probe(), build()/enumerateChannels() }`. Detection iterates
  the table; first match per category wins (one Super-I/O, one SMBus vendor, one SMU).
- Kernel side: a small Super-I/O probe table (NCT668x EC, NCT6775 family, …). Broker side:
  chipKind → raw-channel generator + decode. The IOCTL contract and the broker pipe protocol
  **do not change**.
- **Net effect:** adding a chip = one backend file + one registry line. Zero edits to dispatch,
  protocol, or the security boundary.

## Phase 4 — RGB breadth via a GPL sidecar (per decision #2)

- Stand up a **separate RGB sidecar process** that hosts adapted third-party GPL-2.0 RGB
  controller/detector code (isolated here). It enumerates supported RGB controllers and exposes
  a tiny IPC surface (named pipe) mirroring the broker's RGB ops (`rgb.list` / `rgb.set`, named
  devices, no addresses).
- In the broker, add a `SidecarRgbController : IRgbController` that forwards to the sidecar, so
  detected controllers appear in the existing
  [RgbRegistry](../BrokerSensorBridge/Rgb/RgbRegistry.cs) and are served over the control pipe
  unchanged. The broker + driver link **no** GPL code; the IPC pipe is the license boundary.
- RGB board calibration (zone names, LED counts, byte order) follows the same data pattern where
  the source tables don't already supply it.
- The IT8297 USB-HID path that was built earlier is **retired** (removed with the Gigabyte
  backend 2026-06-11; design record `GIGABYTE-SUPPORT.md`); if revived it can stay native in the broker
  (license-clean re-port) or move into the sidecar — decide per-chip; native stays for the ones
  we ported ourselves.
- **Before shipping:** a real license review of the IPC-boundary position.

## Phase 5 — Community contribution pipeline

- **`--calibration-capture`:** dump every raw channel with its live value (and a draft data entry
  skeleton) so a contributor fills in labels by comparing to HWiNFO/BIOS. This automates the
  exact friend's-checklist bring-up workflow.
- **`--validate-calibration <file>`:** check a submitted board file against the schema and the
  known raw-channel ids — so a contributor *and CI* can verify a mapping with no hardware and no
  way to introduce access.
- Docs: a one-page "how to map your board" guide. Data-only PRs; the trusted code stays frozen.

## Phase 6 — Manufacturer-credible hardening

- Production code-signing of driver + broker; flip `RequireAuthorizedClient` **on** by default
  with a pinned production signer.
- Curated/signed default calibration bundle (shipped defaults are trusted; user overrides are
  clearly user-scope and can't escalate).
- The security write-up aimed at a vendor reviewer: the code/data boundary, the no-address
  schema, the bounded IOCTLs, the identity pin — i.e. *why this is the safe replacement for a
  per-vendor kernel driver.*

---

## Sequencing & risk

- **Phases 1–2 were the spine** and were pure-additive on the broker (no driver change, no
  protocol change). Phase 1's regression gate de-risked the entire migration before anything new
  depended on it — both are now done; the channel-enumeration shape carried the NCT6775 family
  addition without protocol changes.
- **Phase 3** touches detection in both driver and broker but keeps the contract fixed; 1–2
  having proven the channel-enumeration shape, it is the next open phase.
- **Phases 4–6** are breadth and productization; order them to taste once the spine is in.
- **Do not** at any point: put addresses in calibration data, move register maps out of the signed
  binary, or change a persisted public id without an alias. Those three are the lines that keep
  the security model and user configs intact.

## What stays exactly as-is
The bounded IOCTL contract, the broker pipe protocol (`sensor.*` / `rgb.*`), the no-scan / named-
catalog rule, the identity/signer auth, rate-limit, and audit. This migration changes **how the
catalog is assembled** (code → code+data, hand-wired → registry), not the surface clients or
attackers see.
