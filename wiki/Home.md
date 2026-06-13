# Register Broker

**Universal low-level hardware access for non-admin applications — the secure, auditable replacement for the WinRing0 / per-app-elevation model.**

Register Broker is a framework, not an app. There is **no GUI**. It exposes PC sensor data and RGB hardware control to ordinary, non-administrator client programs through a tightly structured, kernel-enforced contract — preserving the kind of deep data and control that tools like WinRing0 gave you, but without handing every application raw ring-0 access to your machine.

> **Quick links:** [Getting Started](Getting-Started) · [Client Protocol](Client-Protocol) · [Architecture & Security](Architecture-and-Security)

---

## The problem it solves

For years, the only way for a Windows app to read motherboard sensors or drive RGB lighting was to ship a driver like **WinRing0** — a generic kernel driver that grants *arbitrary* port I/O, physical memory, and MSR access to whatever process loads it. That model has two fatal flaws:

1. **Every app needs admin / elevation**, or bundles its own kernel driver.
2. **Those drivers are unbounded.** A signed WinRing0 is a signed skeleton key — it can read or write *any* hardware register, which is exactly why it keeps showing up in malware and BYOVD (bring-your-own-vulnerable-driver) attacks.

Register Broker keeps the *capability* — genuine low-level sensor and RGB access — while removing the *blast radius*.

---

## How it works

The framework is two narrow components plus a hardened client protocol:

| Component | Role |
|---|---|
| **`BrokerSmbus`** (KMDF kernel driver) | One small, signed driver. Exposes only **bounded, validated, named-register IOCTLs** — never physical memory, MSRs, or arbitrary port I/O. Every read/write is checked in-kernel against a baked address allow-list. |
| **`BrokerSensorBridge`** (LocalSystem broker) | Runs as the `SensorBroker` + `BrokerControl` services. Owns the control plane: authentication, scopes, policy, rate-limiting, and the audit log. Maps **logical ids** to hardware. |
| **Client protocol (v2)** | Non-admin apps connect over fixed well-known named pipes, authenticated by pipe DACL + peer-process identity + Authenticode signer-thumbprint pin (no shared secrets). |

### The core safety guarantee

Clients name **logical ids** — `smu.cpu.temp`, `ram0`, `nct6687d.*` — and **never** addresses. They cannot scan the bus and cannot read or write outside the baked, kernel-enforced register map. The map lives in **signed code, never in data**: configuration JSON can relabel, rescale, or hide a channel, but it can **never** point the hardware at a new address. SPD ranges and the JC42 thermal window are refused in-kernel; RGB writes are confined to a brick-guarded address allow-list.

The result: a non-admin client gets exactly the sensor catalog and RGB zones the broker chooses to expose, every operation is rate-limited and written to an audit log, and there is no general-purpose hardware primitive for anything — including malware — to abuse.

---

## What it can do today

- **Non-admin sensor catalog** — a 33-sensor catalog served to unprivileged clients via a single rate-limited `sensor.readall` poll. Backends auto-detect and stay inert when their hardware is absent:
  - AMD SMU (Tctl + per-CCD temps)
  - Nuvoton NCT668x EC family (NCT6683 / 6686 / 6687D)
  - Nuvoton NCT6775 bank-select family (6779–6798)
  - JC42 DIMM temperatures
  - AMD FCH SMBus (Intel i801 path written, pending validation)
- **Non-admin RGB control** — board-aware, multi-transport, DMI-matched board profiles:
  - **ENE DRAM RGB** over SMBus (per-LED, hardware-validated)
  - **MSI Mystic Light motherboard RGB** over USB-HID (hardware-validated)
  - NCT6687 EC 12V header path (wired, kept deliberately inert pending register bring-up)
- **Diagnostics & integrity** — backend enumeration, a full self-test suite (signature checks, calibration regression, chipset gates, scope enforcement, rate-limit checks), and signed-code register maps throughout.

---

## Design philosophy

- **The driver stays narrow.** New capability means a new *specific, bounded, in-kernel-validated* IOCTL gated by broker scopes — never a generic primitive.
- **No bricking.** Hardware sequences are ported from proven references (Linux hwmon/i2c drivers) as register *facts*, cross-checked against a second source, and brought up read-only first. Fragile, high-risk hardware families are deliberately left out rather than half-supported.
- **Ids are forever.** Sensor ids are persistence keys that consumer apps save against, so they stay stable; renames route through an alias map.
- **Auditable by construction.** Identity-pinned auth, always-on rate limiting and session caps, and a persistent audit log mean every privileged action has a name attached to it.

---

## Project status

Register Broker is **functional and live-validated** on the developer's hardware (an MSI MPG B550I GAMING EDGE MAX WIFI) — sensors, DRAM RGB, and MSI Mystic Light motherboard RGB are all confirmed working from a non-admin client. Several additional chipset backends are built and code-complete but **awaiting community hardware validation** (Intel i801 SMBus, the NCT6775 family, NCT6683/6686).

---

## ⚠️ Help wanted: a production code-signing sponsor

**The kernel driver is currently test-signed.** That's perfect for development, but it means running Register Broker today requires enabling Windows test-signing mode — which is not something most users should or will do.

To ship this as a real, installable framework that anyone can use without disabling Windows security features, the driver needs to be **production code-signed** — ideally with an EV certificate and Microsoft attestation signing for the kernel-mode driver.

**I'm looking for a sponsor, company, or developer who can help get the `BrokerSmbus` driver properly signed.** If you're a hardware vendor, a security-tooling company, or an individual with access to EV code-signing and the Windows Hardware Developer Program, this is the single biggest thing standing between Register Broker and broad public usability. The whole point of this project is to make low-level hardware access *safer* for everyone — a signed driver is the last mile.

If you can help, please open an issue or reach out. **Sponsorship or signing support would be hugely appreciated.**

---

## License

**AGPL-3.0 with a Commercial Exception.** Provenance and third-party reference attributions are documented in `THIRD-PARTY-NOTICES.md` (Linux-kernel reference drivers are used as register *facts*; the ENE/Aura RGB protocol is treated as a publicly documented protocol).
