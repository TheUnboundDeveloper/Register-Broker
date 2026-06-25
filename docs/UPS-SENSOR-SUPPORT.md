# UPS sensor support (USB HID Power Device)

Read-only UPS/battery telemetry, served over the normal sensor ops as `ups.*` channels.

**Posture:** a UPS is a standard USB **HID Power Device** (top-level collection usage page `0x84`),
so this is a **user-mode** source with the same reduced assurance as the GPU and Aquacomputer
providers — no kernel driver, no brick-guard, **no write path**, read-only. It is **opt-in**
(`AllowUpsSensors` / `--allow-ups-sensors`) and **removable** (hot-plug aware; the `ups.*` channels
appear only while the UPS is present and never flap on a single missed sample).

**Validation:** HW-validated on a CyberPower UPS (VID `0x0764` / PID `0x0501`).

## Served channels

| Id | Unit | HID usage (USB-IF HID Power Device tables) |
|---|---|---|
| `ups.charge` | % | Battery System `0x85/0x66` RemainingCapacity |
| `ups.runtime` | min | Battery System `0x85/0x68` RunTimeToEmpty (seconds ÷ 60) |
| `ups.load` | % | Power Device `0x84/0x35` PercentLoad |
| `ups.voltage.in` | V | Power Device `0x84/0x30` Voltage (input flow collection) |
| `ups.voltage.out` | V | Power Device `0x84/0x30` Voltage (output flow collection) |

A channel only appears when the device exposes that usage. A UPS must report at least
RemainingCapacity to be recognized (so a non-UPS device on page `0x84` is rejected).

## How it works

- **Discovery:** `HidDevice.OpenByUsagePage(0x84)` enumerates HID Power Devices by class, not by a
  vendor id, so any compliant UPS works. The first device whose feature value caps include
  RemainingCapacity is selected.
- **Usage map:** resolved at runtime from the device's feature value caps
  (`HidDevice.GetValueCaps`) — report ids and link collections are discovered, never hard-coded. The
  two voltages are the `0x84/0x30` caps under flow sub-collections (link collection ≥ 2), in
  ascending order → input, output.
- **Reads:** each distinct feature report is fetched once per poll (`HidD_GetFeature`); each usage is
  extracted with `HidP_GetUsageValue`. A `0xFFFF` read is treated as not-available.
- **Scaling:** the **raw logical value** is used directly (runtime additionally ÷ 60 for minutes). The
  HID `UnitExponent` on consumer UPS descriptors is routinely garbage (it would scale 120 V to
  `1.2e9`), so — like [Network UPS Tools](https://networkupstools.org/) — it is ignored.

## Not exposed (yet)

- **AC-present / Charging / Discharging** flags are HID **button** items (1-bit) in the
  `PresentStatus` collection, not value caps, so reading them needs button-cap parsing
  (`HidP_GetButtonCaps` / `HidP_GetUsages`) — deferred.
- Apparent/active power and frequency are device-dependent and were not part of the validated set.

## Dev probes (DevProbes build only)

- `--ups-dump` — list HID Power Device interfaces and dump every feature value cap
  (page/usage/report-id/bits/exponent) with the raw and (exponent-scaled, for comparison) value.
  Used to anchor the usage map against a labeled reference.
- `--ups-read` — create the real `UpsSensorProvider` and print every `ups.*` metric, to validate the
  provider end-to-end without deploying.
