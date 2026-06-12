# AMD FCH SMBus read path — bring-up & test checklist (primary)

> ✅ **STATUS: COMPLETED — HARDWARE-VALIDATED (2026-06-08, dev box).** The AMD FCH read
> path read real DDR4 SPD through the driver (`--bus=0 --addr=0x50 --cmd=0x02 -> 0C`),
> and the write path (block write + two-phase completion poll, ENE RGB) was validated
> later. This checklist is kept as the **historical bring-up procedure** — reuse it for
> new AMD boards. Note: the raw probes (`--smbus-read` etc.) are **compile-time gated**
> now — build the bridge with `dotnet publish -p:DevProbes=true` to include them (see
> `../docs/DEV-GUIDE.md`).

Step-by-step for implementing and validating the **AMD FCH** SMBus read path in
[`Smbus.c`](Smbus.c) `BrokerSmbusXfer()` **safely**. This was the **first** bring-up
target because the dev machine is AMD. Intel i801 is the sibling doc
([`BRINGUP-i801.md`](BRINGUP-i801.md)); both are required for a universal tool.

> ⚠️ Live kernel hardware I/O. A wrong sequence can hang the SMBus (hard power cycle)
> or bugcheck. Use a disposable dev box. SPD/RGB reads are non-destructive, but a
> stuck controller is still a reboot. Never enable writes during read bring-up.

## Authoritative references (port, don't invent)

The AMD FCH SMBus is register-compatible with **PIIX4 / ATI SB800**. Do **not**
hand-derive the encodings — port them from a proven, readable implementation:

- **Linux `drivers/i2c/busses/i2c-piix4.c`** — base discovery, SB800 multi-port mux,
  transaction sequence, error handling. The canonical source of truth (reproduced as
  hardware facts; see `../THIRD-PARTY-NOTICES.md`).

This checklist is the *map*; that is the *territory*.

## 0. Lab prerequisites

- [ ] AMD dev box (Ryzen/FCH). Confirm the FCH SMBus controller in PCI
      (VEN_1022, SMBus class; commonly `00:14.0`).
- [ ] **Secure Boot OFF**; **Memory Integrity/HVCI OFF** during bring-up (test-signed,
      non-attested driver won't load otherwise).
- [ ] WDK + VS; driver builds (`scripts\Build-Driver-DirectLink.ps1` — the MSBuild
      route fails `MSB8020` without the WDK VS extension).
- [ ] Recovery ready: service is `start= demand` (no boot-loop risk); know Safe Mode +
      `sc delete BrokerSmbus`; restore point.
- [ ] *(Recommended)* WinDbg kernel debugger on a second machine.

## 1. Find the SMBus base (AMD is NOT a simple PCI BAR)

Unlike Intel's BAR4, the FCH SMBus I/O base comes from the **FCH PM register block**
via index/data ports **0xCD6 (index) / 0xCD7 (data)**:

- [ ] Read the SMBus base from the SB800/FCH PM registers (i2c-piix4 reads index
      `0x00/0x01`); the primary SMBus I/O base is commonly **0x0B00**, secondary
      **0x0B20**. **Read it — don't hard-code.**
- [ ] In the driver, access I/O ports with `READ_PORT_UCHAR`/`WRITE_PORT_UCHAR`; access
      PCI config (to confirm the controller) via `HalGetBusDataByOffset`.

## 2. SB800/FCH multi-port mux (AMD-specific — easy to get wrong)

The FCH multiplexes **multiple SMBus segments** onto one controller. RGB DRAM, the
motherboard, and other devices can live on **different ports**.

- [ ] Implement port selection exactly as i2c-piix4 does for SB800/FCH (a port index
      written to a PM register; protect it with a lock — it's global controller state).
- [ ] Expose each usable port as a separate **bus** in `IOCTL_BROKER_SMBUS_INFO`
      (`BusCount` > 1); the `XFER` request's `BusIndex` selects the port. The broker
      already passes a bus index through — wire it to the FCH port select.
- [ ] Serialize port-switch + transaction as one critical section (never let two
      callers interleave a port change).

## 3. Register map (offset from SMBus base — PIIX4/SB800)

| Off | Name | Use |
|---|---|---|
| 0x00 | SMBHSTSTS | status; poll busy/done; write to clear |
| 0x02 | SMBHSTCNT | protocol bits + START + KILL |
| 0x03 | SMBHSTCMD | command/register byte |
| 0x04 | SMBHSTADD | (address<<1) \| rw  (rw=1 read) |
| 0x05 | SMBHSTDAT0 | data byte 0 |
| 0x06 | SMBHSTDAT1 | data byte 1 |
| 0x07 | SMBBLKDAT | block FIFO |

(Verify exact bit encodings against i2c-piix4: protocol field, START, and the
status/error bits differ subtly from Intel.)

## 4. The read sequence (read-byte first) — port from i2c-piix4

1. [ ] **Select the target FCH port** (§2) under the lock.
2. [ ] **Not-busy check** with bounded timeout; if stuck → `BusError` (don't fight it).
3. [ ] Clear SMBHSTSTS.
4. [ ] `SMBHSTADD = (addr<<1)|1`; `SMBHSTCMD = command`.
5. [ ] Set protocol in SMBHSTCNT (BYTE_DATA) and **START**.
6. [ ] **Poll SMBHSTSTS with a fixed iteration cap** for done/error; on error/timeout →
       KILL, clear, return `BusError`. **Never spin unbounded.**
7. [ ] Read SMBHSTDAT0 → `Resp->Data[0]`, `Resp->Length = 1`. Clear status. Return `Ok`.

Word/block after byte works (DAT0+DAT1; block FIFO, respect `BROKER_SMBUS_MAX_BLOCK`).

## 5. Concurrency with firmware (do NOT skip on AMD)

- [ ] The FCH SMBus is shared with the **SMU/PSP/EC**. Contention can stall or corrupt.
      Mirror i2c-piix4's locking; keep transactions short and serialized.
- [ ] Prefer testing on a desktop FCH first; laptops add EC ownership complications.

## 6. Incremental validation (use the probe)

Test the driver **directly** with the bridge's dev probe — no auth, no consumers.
(The probes are compile-time gated: build the bridge with `-p:DevProbes=true`.)

1. [ ] Make `IOCTL_BROKER_SMBUS_INFO` report real `BusCount` (FCH ports) and
       `Capabilities |= CAP_READ` (set in `Driver.c`).
2. [ ] Build, test-sign, install, reboot (see `README.md`).
3. [ ] **Safest first read — SPD EEPROM** (`0x50..0x57`, non-destructive). Byte 2 =
       memory type (DDR4=0x0C, DDR5=0x12):
       ```
       BrokerSensorBridge.exe --smbus-read --bus=0 --addr=0x50 --cmd=0x02
       ```
       Try each `--bus=` until SPD answers — that identifies which FCH port the DIMMs
       are on. Compare against CPU-Z/Thaiphoon.
4. [ ] Read more SPD bytes; confirm they match a reference tool. Zero brick risk.
5. [ ] Then probe the ENE DRAM RGB controller addresses (`0x39/0x3A`) with `--ene-read`
       — the ENE protocol is a publicly documented hardware protocol, reproduced as
       register facts.
6. [ ] Watch for: timeouts (encoding/port wrong), all-0xFF (wrong port/addr), stalls
       (SMU/EC contention — revisit §5).

## 7. Promote to the broker

- [ ] With the probe reading reliably across the right FCH port, the broker detects
      `CAP_READ` and gates its SMBus-backed catalog entries (DIMM temps, RGB devices)
      on it. Verify the **named** path from a non-admin client:
      `--client --op=sensor.read --id=dimm.0` / `--client --control --op=rgb.list`.
- [ ] Re-test under **HVCI** with an attestation-signed build (the real target).

## 8. Definition of done (AMD live) — ✅ all met 2026-06-08

- [x] SPD reads via `--smbus-read` match a reference tool on the correct FCH port.
- [x] Multi-port (`BusCount`) enumeration correct; `BusIndex` selects the right segment.
- [x] Bounded-timeout errors clean (bad address → `BusError`, no hang).
- [x] `INFO` reports real `BusCount`/`CAP_READ`; the broker's SMBus catalog comes alive.
- [x] Stable across a few hundred reads with no SMU/EC contention issues.
