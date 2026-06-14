# Architecture & Security

Register Broker exists to give non-admin applications real low-level hardware access **without** the unbounded, BYOVD-prone driver that the WinRing0 model requires. This page explains the trust boundaries that make that possible.

---

## The pieces

```
┌─────────────────────┐      named pipe (JSON)        ┌──────────────────────────┐
│  non-admin client   │ ──────────────────────────▶  │  BrokerSensorBridge       │
│  (your app / CLI)   │   sensors:read / rgb:write    │  (LocalSystem service)   │
└─────────────────────┘                               │  auth · scopes · policy  │
                                                      │  rate-limit · audit log  │
                                                      │  logical-id → hardware   │
                                                      └────────────┬─────────────┘
                                                                   │ bounded IOCTLs
                                                                   ▼
                                                      ┌──────────────────────────┐
                                                      │  BrokerSmbus (KMDF)      │
                                                      │  named-register IOCTLs   │
                                                      │  in-kernel allow-list +  │
                                                      │  brick-guards            │
                                                      └──────────────────────────┘
```

- **The client** runs as a normal user. It only ever sends **logical ids** (`smu.cpu.temp`, `ram0`) — never an address, register, bus, or index.
- **The broker** is the single elevated process. It holds the only driver handle, authenticates clients, enforces scopes and rate limits, maps logical ids to hardware, and writes an audit log. Elevation never crosses back to the client.
- **The driver** exposes only **bounded, validated, named-register IOCTLs**. There is no general-purpose port I/O, physical-memory, or MSR primitive — by design.

---

## Why this is safer than WinRing0

| | WinRing0 model | Register Broker |
|---|---|---|
| Who's privileged | every app that loads the driver | one audited broker service |
| Driver capability | arbitrary port I/O / memory / MSR | bounded, named-register IOCTLs only |
| What a client can address | anything | only baked, kernel-enforced logical ids |
| Scanning the bus | possible | impossible (no enumeration primitive) |
| Auditing | none | every connect / auth / op logged with identity |
| Blast radius if abused | full ring-0 | the exact, pre-mapped registers the broker exposes |

A signed WinRing0 is a signed skeleton key. A signed `BrokerSmbus` can only ever touch the specific registers baked into its allow-list — which is why exposing it broadly is acceptable in a way a generic driver never is.

---

## Defense in depth

1. **Register maps live in signed code, never in data.** Calibration JSON can relabel, rescale, or hide a channel; it can **never** point hardware at a new address.
2. **In-kernel allow-list + brick-guards.** RGB writes are confined to a brick-guarded address window (`0x70–0x77` / `0x39–0x3A` for ENE SMBus, an EC RGB-register window for Super-I/O). SPD ranges (`0x50–0x57`) and the JC42 thermal window are refused **in the kernel**, so the broker cannot be tricked into corrupting RAM SPD.
3. **Identity-based auth, no secrets.** Clients are authenticated by pipe DACL + peer-process identity + optional Authenticode signer-thumbprint pin. There is no token to leak.
4. **Least privilege by split.** The sensor broker is read-only; RGB writes live in a separate, write-only control service with its own scope and pipe.
5. **Always-on policy.** Token-bucket rate limiting, bounded session counts (32 total / 8 per identity), and a persistent `audit.log` of every connect, auth decision, and operation.
6. **Narrow by contract.** Adding a capability means adding a *specific bounded IOCTL*, validated in-kernel and gated by a broker scope — never a generic mechanism.

---

## Hardware safety posture

Hardware sequences are ported from proven references (Linux hwmon / i2c drivers) as register **facts**, cross-checked against a second source, and brought up read-only first. Two Nuvoton Super-I/O families are mutually exclusive by chip-id gate, and every backend auto-detects and stays inert when its hardware is absent. Fragile, high-risk hardware (e.g. the Gigabyte/ITE controller family) was deliberately **removed rather than half-supported** — a no-brick project values not bricking over breadth.

---

## <a name="help-wanted-production-signing"></a>⚠️ Help wanted: production driver signing

**The kernel driver is currently test-signed.** It loads only on machines with **test-signing on** and **Memory Integrity (HVCI) off** — fine for development, not acceptable for ordinary users.

To ship Register Broker as a real, installable framework, `BrokerSmbus` needs **production code signing** — an EV certificate plus Microsoft attestation signing for the kernel-mode driver, through the Windows Hardware Developer Program.

**This is the single biggest thing standing between Register Broker and broad public usability.** If you're a hardware vendor, a security-tooling company, or an individual with access to EV code-signing and the hardware dev program, **sponsorship or signing support would be hugely appreciated** — please open an issue or reach out. The whole point of this project is to make low-level hardware access *safer* for everyone; a signed driver is the last mile.

---

## Further reading (in-repo docs)

- `docs/ARCHITECTURE.md` — full design & threat model
- `docs/SECURITY.md` — security posture
- `docs/SIGNING-AND-DEPLOYMENT.md` — the road to a production-signed install
- `docs/CLIENT-PROTOCOL.md` — authoritative wire contract
- `docs/SENSOR-MAP.md` / `docs/SENSOR-CHIPSET-INVENTORY.md` — served ids & chipset coverage
