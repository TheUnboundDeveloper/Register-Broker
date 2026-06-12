<!-- Thanks for contributing! Keep the diff scoped to one concern.
     General guidance: docs/CONTRIBUTING.md · Adding hardware: docs/CONTRIBUTING-CHIPSET.md -->

## What & why

<!-- One paragraph: what this changes and why. Link the issue if there is one. -->

## Checklist (all PRs)

- [ ] `--selftest` passes (`dotnet build … && BrokerSensorBridge.exe --selftest`)
- [ ] Driver builds warning-clean under `/W4 /WX` (if C files were touched)
- [ ] Docs that describe the touched behavior are updated
- [ ] No security guardrail is weakened (clients still name ids, never addresses;
      register maps stay in signed code; the in-kernel write guard is untouched —
      call it out explicitly for security review if it is not)

## Chipset contributions (delete this section otherwise)

<!-- The full walkthrough is docs/CONTRIBUTING-CHIPSET.md §7 -->

- [ ] Fact table in the backend banner with **two independent sources** cited
- [ ] Probe is gate-exact, no-ops when a chip is already claimed, no chip-id overlap
- [ ] Read path bounded (`Index` validated per kind) and read-only
- [ ] One descriptor row added to the kernel registry; one entry in `ChannelRegistry.cs`;
      decoder + `DecoderRegistry` coverage in `SensorDecode.cs`
- [ ] Selftest gate case added for the new chip id
- [ ] Ids follow `{family}.{kind}.{index}` and are documented in `docs/SENSOR-MAP.md`
- [ ] `docs/SENSOR-CHIPSET-INVENTORY.md` updated with the honest status (✅ or 🟡)

**Hardware validation** (see docs/TESTING.md):

```
Board / DMI / chip id:
Side-by-side vs HWiNFO:        <attach or summarize — or state "no hardware, 🟡">
```
