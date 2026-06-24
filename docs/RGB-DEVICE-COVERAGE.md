# RGB Device Coverage — what's in, what's out, and why

This catalogues the RGB device families ported from the public hardware register maps (register
**facts** re-expressed in our own code; never copied — see
`THIRD-PARTY-NOTICES.md`). It exists so the "what's missing?" question has a single honest answer,
and so each exclusion records **the change that would be needed to enable it** — the input for
deciding whether a given device is worth that change.

> Status legend — **Included**: controller + detection + selftest landed (HW-UNVALIDATED unless
> noted; gated/inert per the project's posture until validated on real hardware). **Excluded**: not
> added, with the reason and the enabling change.

All driver `.c` changes below require an elevated WDK rebuild + re-sign to take effect, and nothing
is hardware-validated on the dev box (which has none of this hardware) — the selftest proves protocol
math and the kernel guard windows, not silicon.

## Validation status & how to report a validated device

Every **Included** RGB device below is **HW-UNVALIDATED** unless this doc explicitly marks it
otherwise. "Included" means: the protocol is ported from the public register/HID facts, the broker
detects + drives it, and the packet math passes the `--selftest` gates — but it has **not been
confirmed on physical hardware** (the dev box owns none of it). The protocols are reproduced as facts
and the write paths are bounded (kernel brick-guard for SMBus; `AllowHidRgb` gate — **on by default**
as of 2026-06-22 — plus a per-device USB match for USB-HID), so the risk is "wrong/no color," not
hardware damage. Note that an **unpinned** USB-HID RGB zone (a profile with `HidProductId: 0`) is no
longer driven by default — it's bring-up-only behind `AllowUnpinnedHidRgb` / `--rgb-allow-unpinned-hid`;
shipping profiles **pin** the PID, so a tester binds a new board's controller by setting that flag,
finding the PID with `--hid-scan`, then pinning `HidProductId` — but treat any Included
device as *should work*, not *known-good*, until validated.

**To report a device as validated**, open a GitHub issue (use the **Validation report** form) or note
it here, with: device model + USB VID/PID (or SMBus board), the broker version, and what you observed
(`rgb.list` shows it; `rgb.set <id> <color>` lights it correctly; per-LED works if applicable). We'll
move confirmed devices to a **✅ HW-validated** list. Bug reports (detected-but-wrong-color, wrong LED
order, reverts after N seconds, etc.) are just as useful — they usually map to a single documented
byte-order / keepalive / count caveat.

**Validated so far (dev box, MSI MS-7C92):** ENE/Aura DRAM (SMBus), MSI Mystic Light (USB-HID,
per-LED), Razer Naga Trinity + Cynosa Chroma (USB-HID). Everything else awaits a tester.

---

## Coverage at a glance — counts per interface

Counts are at the **device-family** level (a "family" = one controller class, which often covers many
models/PIDs — e.g. *Corsair iCUE V2* is one family spanning 14 devices, *Redragon* one family spanning
10 PIDs). The representative-models column gives the model/PID breadth the detailed list below
enumerates.

| Transport | Write boundary | Families | Representative models covered | HW-validated (dev box) |
|---|---:|---|---|---|
| **SMBus — DRAM** | kernel brick-guard (per-LED class) | **5** | ENE/Aura, Corsair Vengeance RGB Pro/Dominator, Corsair Vengeance (orig.), Crucial Ballistix, T-Force Xtreem | ENE/Aura ✅ |
| **SMBus — motherboard** | kernel brick-guard (DMI-vendor gated) | **2** | ASRock Polychrome V1/V2/ASR (0x6A), EVGA ACX30 (0x28) | — |
| **USB-HID** (`AllowHidRgb`, on by default) | broker baked report builder (no kernel guard) | **13** | MSI Mystic Light (board headers) · Razer Chroma (Naga Trinity, Cynosa Chroma) · AMD Wraith Prism · Logitech (G203 + per-key G810/G910/G610/G512/G Pro) · SteelSeries (Rival/Aerox mice, Apex 3/OG/350 & Sensei/Rival kb) · Corsair iCUE V2 (**14 devices**: 11 mice, 2 mousepads, K55) · HyperX (Pulsefire/Raid/Haste/Dart/Surge mice, Alloy FPS/Origins/Elite2/Eve1800 kb) · Cooler Master (MP750 pad, MM530/711/720/730 mice) · Roccat (Kone Aimo/Pro) · Redragon (**10 PIDs**) · NZXT Lift · ASUS ROG Ally / Ally X · Lian Li Uni Hub SL V2 / AL V2 | MSI Mystic Light ✅, Razer (Naga Trinity, Cynosa Chroma) ✅ |
| **Total** | — | **20 families** | spanning **80+ distinct models/PIDs** | 3 families / 4 devices HW-validated |

**Excluded at a glance** (detail + the enabling change in §§A–G below): **GPU SMBus RGB** (~20 vendors —
architecture mismatch, breaks no-physical-memory) · **network/serial RGB** (Nanoleaf/Govee/Hue/WLED/… —
out of scope, different product) · **SPD-gated DRAM** (HyperX Predator/Fury, Patriot Viper — would relax
the anti-brick SPD-read refusal) · **Gigabyte RGB Fusion / ITE IT8297** (permanently retired) · **MSI
Super-I/O RGB** (NCT6795/6797 — de-scoped: older-board-only, mutually exclusive with HID, unvalidatable
here) · **the long tail** (~90 further USB-HID controllers not yet extracted — effort, no new
capability). Everything excluded is **effort or policy**, not a hardware-damage risk that the broker
can't bound.

---

## Included (drivable through the broker)

### SMBus — DRAM (kernel brick-guarded, device-aware class per family)
- **ENE / Aura DRAM** (pre-existing, HW-validated)
- **Corsair Vengeance RGB Pro / Dominator** (newer per-LED) — `CorsairDram`
- **Corsair Vengeance** (original single-color) — `CorsairVenDram`
- **Crucial Ballistix** — `CrucialDram`
- **T-Force Xtreem** — `XtreemDram`

### SMBus — motherboard (DMI-vendor gated)
- **ASRock Polychrome V1 / V2 / ASR RGB** (0x6A)
- **EVGA ACX30** (0x28)

### USB-HID (user-mode, `AllowHidRgb` on by default, reduced assurance)
- **MSI Mystic Light** (board headers, pre-existing) · **Razer Chroma** keyboards/mice (pre-existing)
- **AMD Wraith Prism** CPU cooler (per-LED)
- **Logitech G203 Lightsync** mouse (per-LED) · **G810 / G910 / G610 / G512 / G Pro** keyboards (**per-key**)
- **SteelSeries Rival 3** · **Aerox 3 / Aerox 5** mice (zone color)
- **Corsair iCUE V2** — 14 fixed-LED-count devices: 11 mice (Dark Core SE/Pro SE, Harpoon, Ironclaw,
  Katar Pro/V2/XT, M55, M65 Ultra/Wireless, M75), 2 mousepads (MM700/3XL), K55 keyboard (per-LED Direct)
- **SteelSeries** keyboards: Apex 3 (8-zone / T-zone), Apex OG/350 (5-zone RGBA), Sensei / Rival 310 (2-zone)
- **NZXT Lift** mouse (6 LEDs)
- **Roccat** Kone Aimo (11 LEDs) · Kone Pro (2 LEDs)
- **Redragon** mice (10 PIDs, 1 LED) · **Cooler Master** MP750 mousepad (1 LED)
- **HyperX** (keepalive-driven): mice — Pulsefire FPS Pro/Core, Raid, Haste, Dart, Surge; keyboards —
  Alloy FPS RGB, Alloy Origins / 60 / 65 / Elite 2, Eve 1800, Origins 2 65
- **SteelSeries** legacy: Rival 100 (1 zone) · Rival 300 (2 zone) · **Cooler Master** mice MM530 / MM711 / MM720 / MM730
- **ASUS ROG Ally / Ally X** (4 LEDs) · **Lian Li Uni Hub SL V2 / AL V2** (1-fan-per-channel baseline)

> HID transport now covers feature reports, output reports (`HidD_SetOutputReport`), input reports
> (`HidD_GetInputReport`), **and a keepalive refresh loop** (for firmware that reverts unless
> re-sent) — no further broker-capability change is needed for the remaining peripherals.

---

## Excluded — grouped by reason

### A. Architecture mismatch — a bus/transport the narrow driver does not expose
*Enabling these means a fundamentally new capability that conflicts with "the driver stays narrow."*

| Family (count) | Why excluded | Change needed |
|---|---|---|
| **GPU SMBus RGB** (~20: Asus Aura GPU, EVGA/Sapphire/Zotac/PNY/Palit/Gainward/Galax/Manli/Colorful/PowerColor/MSI GPUs) | RGB lives on the **GPU's own I2C bus** reached via the display driver / NVAPI (`REGISTER_I2C_PCI_DETECTOR`, `is_amd_gpu_i2c_bus`), **not** the motherboard SMBus host controller this driver implements. | A whole GPU-I2C / NVAPI subsystem (likely GPU MMIO mapping). **Breaks the no-physical-memory + narrow-driver guardrails.** High effort, high risk — recommend NOT pursuing. |
| **Network / serial RGB** (Nanoleaf, Govee, Philips Hue/Wiz, WLED/DRGB, Yeelight, LIFX, E1.31/DMX, Elgato, etc.) | Not local low-level hardware at all — these are network/serial protocols. Outside the broker's entire reason for existing (secure local register access). | None — this is a different product. Out of scope by design. |

### B. Guardrail conflict — would breach an existing safety boundary
| Family | Why excluded | Change needed |
|---|---|---|
| **HyperX Predator/Fury DRAM**, **Patriot Viper / Viper Steel DRAM** | Device identity lives in the **SPD bytes (0x50–0x57)** the kernel deliberately **refuses to read** (anti-brick). Their RGB classes/windows are reserved (`HyperXDram` 0x27, `ViperDram` 0x77) but detection can't run safely. | An SPD-signature read path that relaxes the SPD-read refusal for specific non-destructive signature bytes. Low–moderate effort; **weakens the anti-brick SPD posture** — weigh carefully. |
| **Gigabyte RGB Fusion (SMBus/USB) / ITE IT8297** | Permanently retired 2026-06-11: unreliable sources + unusually fragile, multi-controller hardware (a bad bet for a no-brick project). `KIND_ITE` reserved forever. | None desired — deliberate, standing exclusion (`docs/GIGABYTE-SUPPORT.md`). |

### C. Removed from scope by decision
| Family | Why excluded | Notes |
|---|---|---|
| **MSI Super-I/O RGB** (NCT6795 / NCT6797) | **Dropped by decision.** It is the *older-board* RGB path (RGB wired to the Nuvoton Super-I/O); it is **mutually exclusive** with the USB-HID Mystic Light controller newer boards use, and **less** capable (4-bit/channel, single whole-board color, no per-LED). The dev box (MS-7C92) has an **NCT6687D, not a 6795/6797**, and drives RGB over HID — so this path can't run or be validated here and benefits only third-party older-MSI boards. | *Feasible but de-scoped* — it was never an architecture/feasibility blocker (the driver already does NCT6795/6797 Super-I/O port I/O for sensors). If older-MSI coverage is ever wanted **and** a tester with that hardware appears: a value-gated Super-I/O config-write IOCTL (restrict the `0x07` selector to {0x09, 0x12}, allow only the 0xE0–0xFF band) + re-sign. |

### D. ~~Missing a small user-mode HID capability~~ — DONE
`HidDevice.GetInputReport` (`HidD_GetInputReport`) was added; **Corsair iCUE V2's 14 fixed-LED-count
devices are now Included** (above). Only the **Corsair matrix keyboards (K60/K70/K95/K100)** remain
out, and for a different reason — their LED count is keyboard-layout-dependent (see E). The Corsair V2
init reads (VID/PID, encoding probe) are best-effort with safe defaults (wired write-cmd, CTRL2
triplet, 65-byte packets); confirm the CTRL1/CTRL2 encoding on real hardware.

### E. Large per-device data tables not yet ported — effort, not risk (user-mode, safe)
| Family | Why excluded | Change needed |
|---|---|---|
| ~~**Logitech per-key keyboards** (G810/G910/G Pro)~~ | **DONE** — per-key Included (key tables generated per layout, report-0x12 frames + commit). | — |
| **Logitech G815 / G915** | Need runtime HID++ feature-index **discovery**, a 4-packet direct-mode engage handshake, and the source flags the G815 hardware-mode bytes as **unverified**. | HID++ root-feature query (`0x0000` getFeature) + the engage sequence + HW validation. Moderate effort. |
| **SteelSeries Apex per-key keyboards** (5/7/Pro = 112 keys; Apex 9 = 111 keys) | The Apex 3 8-zone/T-zone + OldApex are already **Included**. The per-key models use 643/513-byte feature packets — **the `keys[]` scancode tables are now extracted** (ready to port; note the source's 112-vs-111 count quirk + per-generation packet-id). | Add the controllers using the extracted tables (mechanical) + the generation detection. Low–moderate effort. |
| **ASUS Aura USB keyboards** (TUF / Strix / Scope / Flare / Azoth / Claymore) | ROG Ally is **Included**. The keyboards are per-key: Protocol A (3 Scope TKL PIDs) has a known table; **Protocol B** (the large TUF/Strix set) needs a **runtime region/layout query** (`out[4]*100+out[5]`) plus ~30 per-region matrix tables. | Port Protocol A (table in hand) + the Protocol B region query + the specific board's table on demand. Moderate effort. |
| **SteelSeries legacy mice** — remaining: Rival 600 / 650 / 700 | Rival 100/300 and Sensei/Rival 310 are **Included**. Rival 600 has **anomalous report framing** (no 0x00 prefix — `[0]` is the command 0x1C); Rival 700 is a 578-byte feature report; Rival 650 is a 4-write sequence. | Confirm Rival 600's report-id handling on hardware (1-line builder change); port the 650/700 packets. Low–moderate effort. |
| **Corsair iCUE V2 matrix keyboards** (K60 / K70 / K95 / K100 families) | LED count is the matrix **bounding box**; the real lit-key count depends on the keyboard layout (ANSI/ISO/ABNT/JIS) read via a runtime `GET 0x41` query. Hard-coding a count isn't trustworthy. | Add the layout query + per-model key-grid tables (the streaming transport is already done). Moderate effort, low risk. |

### F1. Specced but deferred for a specific reason (not a plain backlog)
| Family | Why deferred | Change needed |
|---|---|---|
| **SteelSeries Rival 600 / 700** | Rival 600 uses an **anomalous report framing** (no 0x00 report-id prefix — `[0]` is the command 0x1C, unlike every sibling); Rival 700 uses a 578-byte feature report. Risky to send blind. | Confirm the report-id handling on hardware (a 1-line builder change once known). Low effort, needs HW. |
| **NZXT Hue 2** channel controllers (Hue 2/Ambient/Smart V2/Kraken/RGB-fan controllers) | LED count is **discovered at runtime** (a device-info read returns per-channel device ids → LED counts); also the detector doesn't pin an interface. Hard-coding a count isn't trustworthy. Color order is G,R,B; needs Direct+Apply per channel. | Implement the device-info enumeration read + frame sizing (transport already supports the reads). Moderate effort. |
| **Roccat Vulcan** keyboards | Per-key, layout-dependent LED count; **brick-sensitive two-interface init** (control + LED interfaces, ready-poll, fw-version-dependent header endianness, "never send mode 0"). | Port the layout tables + the full init/ready-poll sequence + per-key streaming. Moderate–high effort; validate carefully. |

### G. Specced but blocked on a broker capability or firmware behaviour

> **The keepalive blocker is now RESOLVED** — the broker has a keepalive refresh loop, and the whole
> **HyperX family is Included** (above); **Corsair K55** is keepalive-driven too. The rows below remain
> blocked for *other* reasons.

| Family | Why blocked | Change needed |
|---|---|---|
| **Sinowealth / Glorious** (Model O/D wired+wireless, ZET Fury Pro, Genesis Thor/Xenon) | Almost every color update is a **flash read-modify-write** of the saved config (wear); several share **PIDs** (0x1007 = two different devices) and two PIDs (0x0016/0x0049) are **reused by Redragon and bricked them** per the upstream comment. | A stronger positive-ID gate (probe the expected report set, not just VID/PID) + accept the flash-wear trade-off (dedupe + rate-limit). Higher caution; the brick-collision PIDs must stay disabled. **Recommend NOT pursuing the disabled PIDs.** |
| **Thermaltake Riing** (classic/Trio/Quad) | LED count per channel is **user-configured**, not fixed/discoverable; the detector pins **no interface/usage** (needs `--hid-scan`); GRB order. (The Quad's keepalive need is now satisfiable, but the configured-count blocker remains.) | A user-set LED-count config per channel + empirical interface/usage pinning. |
| **Lian Li Uni Hub** — multi-fan + the AL / SL / SL-Infinity variants | SL V2 / AL V2 is **Included** at a **1-fan-per-channel baseline** (LED count = fans × 16 is **user-configured**, not device-read). AL has dual fan+edge sub-rings (146-byte packets); SL has a firmware-version gate + variable-length packets; SL-Infinity has 8 channels + a hardcoded-4-fans quirk. | A **per-channel fan-count config** (unlocks Lian Li multi-fan) + the per-variant packet shapes. |
| **Device-read enumeration hubs** (NZXT Hue 2, Corsair Commander Core / Lighting Node / iCUE Link) | These DO self-report their chain — a device-info read returns per-channel device-ids → LED counts. The broker has the input-report read (`GetInputReport`) but no helper that parses it into frame sizing. | A small enumeration helper (issue the device-info read, map device-ids → LED counts, size frames). Unlocks the iCUE/NZXT fan-hub ecosystem. |
| **Cooler Master** — remaining: MM531 / MM712 mice, GPU (R6000/6900), monitor (GM27-FQS), desk (GD160), keyboards (V1/V2), ARGB strips | MM530/711/720/730 are **Included**. MM531's map is **unconfirmed upstream** (copied from MM530); MM712 uses a separate NORMAL/DIRECT state machine; the GPU/monitor/desk use multi-packet streaming + per-command reads (some offsets agent-flagged); keyboards need per-model key tables; ARGB strips have runtime-set LED counts. | Re-verify offsets against source / port key tables / add the device-info read for the strips. Moderate. |
| **HyperX Alloy Elite** (128 LEDs incl. 22 extended) and **Alloy Origins Core** (TKL) | Alloy Elite uses three separate **extended-LED scatter tables** the extractor flagged as not fully verified; Origins Core derives its LED count from a **runtime keyboard-layout (variant) query**, not a fixed table. | Re-verify Elite's extended tables against source; add the Origins-Core variant query + layout table. Both low-risk (user-mode), moderate effort. |

### F2. Not yet specced — the long tail (user-mode HID, same pipeline, effort only)
~90 further USB-HID controllers were not extracted. None are believed to need new capability beyond
the feature/output/input-report transport now in place; each is an extract-facts → integrate →
selftest job (the method used above). Examples: **Cooler Master**, **Asus Aura USB**
(keyboard/TUF/ROG Ally), **Corsair** Commander Core / Lighting Node / Hydro / iCUE Link, **HyperX**
peripherals, **Ducky**, **Wooting**, **Mountain**, **Redragon**, **Thermaltake Riing**, **Cougar**,
**Creative**, **EVGA USB**, **Dygma**, **Glorious/Sinowealth**, **NZXT** Hue 1, addressable lighting
controllers, etc.

**Change needed:** none structural — just continued per-family extraction + integration. Prioritise by
hardware actually owned (so each lands HW-validated).

---

## Summary — where a change would unlock the most, ranked by value/cost

1. ~~**Corsair iCUE V2**~~ — **DONE** (input-report read added; 14 fixed-count devices Included).
2. ~~**Keepalive refresh loop**~~ — **DONE** (whole **HyperX family** Included; Corsair K55 keepalive-driven).
3. **Table-porting / re-verify follow-ups (no new capability, low risk):** SteelSeries Apex per-key +
   legacy Rival/Sensei mice, Logitech per-key keyboards (G810/G910/GPro per-key; G815/G915), Corsair
   iCUE V2 matrix keyboards (+ layout query), HyperX Alloy Elite + Origins Core, the Cooler Master
   mice/GPU/keyboards (re-verify offsets). Steady safe breadth.
4. **The long tail of ~85 un-specced HID controllers** (Asus Aura USB, Ducky, Wooting, Corsair
   Commander/Lighting Node, Mountain, Dygma, …) — same extract→integrate→selftest pipeline; prioritise
   by hardware owned so each lands HW-validated.
5. **Sinowealth / Glorious** — only with a stronger positive-ID gate + accepting flash-wear; the
   Redragon-collision PIDs (0x0016/0x0049) must stay disabled.
6. **Thermaltake Riing** — needs a user-set per-channel LED count + empirical interface pinning.
7. **HyperX / Patriot DRAM** — only if an SPD-signature read path is judged acceptable against the anti-brick posture.
8. ~~**MSI Super-I/O RGB**~~ — **removed from scope** (older-board-only path, mutually exclusive with HID, can't validate here — see §C).
9. **GPU SMBus RGB / network devices** — **not recommended**; they break the architecture/guardrails or are a different product.
