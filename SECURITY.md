# Security Policy

Register Broker is a security-posture project: its whole reason to exist is replacing
the WinRing0 / run-everything-as-admin model with one narrow kernel driver behind an
authenticated, audited broker. Security reports are taken seriously and are very welcome.

## Reporting a vulnerability

**Please do not open a public issue for a security problem.**

- Preferred: **GitHub private vulnerability reporting** — *Security → Report a
  vulnerability* on this repository (creates a private advisory only maintainers see).
- Or email: **TheUnboundDeveloper@outlook.com** with `[SECURITY] Register Broker` in the subject.

Include what you'd want yourself: affected component (driver / sensor service / RGB
control / scripts), a reproduction or proof-of-concept, and your assessment of impact.

This is a solo-maintained project. You'll get an acknowledgement within **7 days** and
an assessment within **30**; fixes for confirmed issues in the trust boundary (below)
take priority over all feature work. Coordinated disclosure is appreciated — credit is
given in the changelog and advisory unless you prefer otherwise.

## What counts (the trust boundary)

The interesting surface, in rough priority order:

1. **Kernel driver (`BrokerSmbus`) IOCTL surface** — anything that escapes the bounded,
   validated, named-register model: out-of-bounds access, SMBus write reachability outside the
   in-kernel RGB allow-list (`0x70–0x77`, `0x39–0x3A`), **EC RGB write reachability outside the
   NCT6687 RGB register window** (`IOCTL_BROKER_SUPERIO_RGB_WRITE` — must never reach the EC
   sensor/fan/voltage banks), SPD/JC42 write reachability, pool corruption, type-confusion in the
   request structs.
2. **Broker authentication / authorization** — bypassing the pipe DACL + peer-identity
   + signer-pin gate, scope escalation (e.g. reaching `rgb:write` ops over the sensor
   pipe), session-token forgery or cross-session reuse.
3. **Addressing escapes** — any way a client influences *which* hardware register is
   touched. Clients name catalog ids only; calibration data can relabel/rescale/hide
   but must never address. A bypass of either invariant is a vulnerability.
4. **Local privilege escalation via the services** — the LocalSystem services'
   binaries, config, install scripts, or pipe handling enabling EoP.
5. **Policy-control bypass** — defeating rate limiting, the bounded session table, or
   producing ops that don't appear in the audit log.

## Known and accepted (not new findings)

Documented, deliberate trade-offs — reports on these are welcome only if you can show
impact beyond what's described below and in `docs/ARCHITECTURE.md`:

- **`RequireAuthorizedClient` defaults to off (audit-only).** Until a production signer
  exists to pin, identity is logged but not enforced by default; hardened deployments
  enable enforcement via installer flags. Flipping the default is tracked for the
  production-signing milestone.
- **The driver is test-signed pre-1.x-production.** Running it requires test-signing
  mode (a real posture reduction, documented loudly in `docs/TESTING.md`). Production
  signing (EV + attestation) is a tracked open item.
- **PID-reuse TOCTOU** on peer-process identity is a documented, accepted residual.
- **USB-HID RGB is a reduced-assurance transport, opt-in and off by default.** The MSI Mystic
  Light path (motherboard ARGB headers) is user-mode: the broker talks to the controller
  directly, so it does **not** pass the kernel brick-guard. Its boundaries are the broker's baked
  report builder and a USB product-id pin (only the intended controller is driven). It is enabled
  only by an explicit operator opt-in (`AllowHidRgb`). Its blast radius is the RGB controller
  itself (no SPD/sensor bus); reports showing impact beyond confused LEDs are welcome.

## Supported versions

| Version | Supported |
|---|---|
| latest release (1.0.x) | ✅ |
| anything older | ❌ — update first |
