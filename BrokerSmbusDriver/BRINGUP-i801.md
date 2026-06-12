# i801 SMBus read path — bring-up & test checklist

> ⏳ **STATUS: PENDING HARDWARE (2026-06-12).** `SmbusIntel.c` (`DiscoverBuses()` +
> `Read()`) is **written but NOT hardware-validated** — no Intel box has been available.
> This checklist is the validation procedure to run when one is. The AMD sibling
> ([`BRINGUP-AMD-FCH.md`](BRINGUP-AMD-FCH.md)) completed this process 2026-06-08.
> Note: the raw probes (`--smbus-read` etc.) are **compile-time gated** — build the
> bridge with `dotnet publish -p:DevProbes=true` to include them (see
> `../docs/DEV-GUIDE.md`).

Step-by-step for implementing and validating the **Intel** ICH/PCH (i801) read path in
[`Smbus.c`](Smbus.c) `BrokerSmbusXfer()` **safely**. Both vendors are required for a
universal tool. **On AMD (the dev box), start with**
[`BRINGUP-AMD-FCH.md`](BRINGUP-AMD-FCH.md) — AMD is not just "analogous", its base
discovery and multi-port muxing differ materially.

> ⚠️ This is live kernel hardware I/O. A wrong sequence can hang the SMBus (requiring a
> hard power cycle) or bugcheck. Do it on a **disposable dev box**, not your daily
> driver. Reads of SPD/RGB are non-destructive, but a stuck controller is still a
> reboot. Never enable the write path during read bring-up.

## 0. Lab prerequisites

- [ ] Dedicated/secondary machine with an **Intel** chipset (check `00:1F.3` or `00:1F.4`).
- [ ] **Secure Boot OFF** (BIOS) — required for test-signed drivers.
- [ ] Windows with **Memory Integrity/HVCI OFF** during bring-up (a test-signed,
      non-attested driver won't load under it). Re-evaluate under HVCI later.
- [ ] WDK + VS installed; driver builds (`scripts\Build-Driver-DirectLink.ps1` — the
      MSBuild route fails `MSB8020` without the WDK VS extension).
- [ ] **Recovery ready:** the service is `start= demand` (won't auto-load at boot), so a
      bad build can't boot-loop you. Still: know how to reach Safe Mode and
      `sc delete BrokerSmbus`. Keep a restore point.
- [ ] *(Strongly recommended)* A second machine running **WinDbg** as a kernel
      debugger over network/serial, so a bugcheck gives you a stack instead of a
      mystery reboot. `bcdedit /debug on` + `bcdedit /dbgsettings net ...`.

## 1. Locate the controller and its I/O base (before any transaction)

- [ ] Enumerate PCI for VEN_8086 device class SMBus; the i801 is function `00:1F.3`
      (older) or `00:1F.4` (newer). Confirm the device id against the linux `i2c-i801`
      id list for your PCH.
- [ ] Read **BAR4** (PCI config offset `0x20`). The SMBus I/O base = `BAR4 & 0xFFE0`.
- [ ] Read the **Host Configuration** register (PCI config `0x40`); bit0 (HST_EN) should
      be set. Do **not** modify it.
- [ ] In the driver, do PCI config access via the `HalGetBusDataByOffset` /
      `HalSetBusDataByOffset` (or a `BUS_INTERFACE_STANDARD`) — not hard-coded CF8/CFC.

## 2. Register map (offsets from the SMBus I/O base)

| Off | Name | Use |
|---|---|---|
| 0x00 | HST_STS | status; write 0xFF to clear; poll INTR(0x02)/errors |
| 0x02 | HST_CNT | command: protocol bits + START(0x40) + KILL(0x02) |
| 0x03 | HST_CMD | command/register byte |
| 0x04 | XMIT_SLVA | (address<<1) | rw  (rw=1 for read) |
| 0x05 | HST_D0 | data byte 0 |
| 0x06 | HST_D1 | data byte 1 |
| 0x07 | Block Data Byte | block FIFO |
| 0x0C | AUX_STS | error detail |

Protocols in HST_CNT: Quick `0x00`, Byte `0x04`, Byte-Data `0x08`, Word-Data `0x0C`,
Block `0x14` (verify against your PCH datasheet — encodings are stable but confirm).

## 3. The read sequence (read-byte first)

Implement `BrokerSmbusReadByte` only, to start:

1. [ ] **Bus-idle check:** if `HST_STS & 0x01` (HOST_BUSY), bail with `BusError` (don't
       fight a busy bus).
2. [ ] **Clear status:** write `0xFF` to HST_STS.
3. [ ] Set `XMIT_SLVA = (addr << 1) | 1`.
4. [ ] Set `HST_CMD = command`.
5. [ ] Start: `HST_CNT = BYTE_DATA(0x08) | START(0x40)`.
6. [ ] **Poll with a bounded timeout** (e.g. ≤ 10 ms, fixed iteration cap) for
       `HST_STS & (INTR(0x02) | DEV_ERR | BUS_ERR | FAILED)`. On error/timeout → write
       KILL, clear status, return `BusError`. **Never spin unbounded.**
7. [ ] On INTR: read `HST_D0` into `Resp->Data[0]`, `Resp->Length = 1`.
8. [ ] Clear status (write `0xFF`). Return `Ok`.

Word/block come after byte works: word reads HST_D0+HST_D1; block reads the byte count
then the FIFO (respect `BROKER_SMBUS_MAX_BLOCK`).

## 4. Concurrency with firmware (do not skip)

- [ ] The SMBus is shared with ACPI/EC. Where present, honor the host semaphore /
      `INUSE_STS` (HST_STS bit6 on many PCHs): acquire before, release after. On
      laptops the EC may own the bus — prefer desktops for first bring-up.
- [ ] Serialize all transactions in the driver (the broker serializes too, but the
      driver must not rely on it).

## 5. Incremental validation (use the probe)

Test the driver **directly** with the bridge's dev probe — no auth, no consumers.
(The probes are compile-time gated: build the bridge with `-p:DevProbes=true`.)

1. [ ] Make `IOCTL_BROKER_SMBUS_INFO` report `BusCount=1`, `Capabilities|=CAP_READ`
       once enumeration works (set in `Driver.c`).
2. [ ] Build, test-sign, install, reboot (see `README.md`).
3. [ ] **Safest first read — SPD EEPROM** (non-destructive, well-known): DIMMs answer at
       `0x50..0x57`. Byte 2 is the memory type (DDR4=0x0C, DDR5=0x12):
       ```
       BrokerSensorBridge.exe --smbus-read --bus=0 --addr=0x50 --cmd=0x02
       ```
       Expect a plausible type byte. Compare against what CPU-Z/Thaiphoon reports.
4. [ ] Read a few SPD bytes; confirm they match a known-good tool. This proves the
       sequence end-to-end with zero brick risk.
5. [ ] Only then probe the ENE DRAM RGB controller addresses (`0x39/0x3A`) with
       `--ene-read` — the ENE protocol is a publicly documented hardware protocol,
       reproduced as register facts.
6. [ ] Watch for: timeouts (sequence/encoding wrong), all-0xFF (no ack / wrong addr),
       system stalls (semaphore/contention — revisit §4).

## 6. Promote to the broker

- [ ] With the probe reading reliably, the broker detects `CAP_READ` and gates its
      SMBus-backed catalog entries (DIMM temps, RGB devices) on it. Verify through the
      authenticated channel from a non-admin client:
      `--client --op=sensor.read --id=dimm.0` / `--client --control --op=rgb.list`.
- [ ] Re-test under **HVCI** once you have a properly attestation-signed build — that is
      the real target environment (see `../docs/BROKER-DESIGN.md` §7, §9).

## 7. Definition of done (Intel live)

- [ ] SPD reads via `--smbus-read` match a reference tool.
- [ ] Bounded-timeout error handling verified (probe a non-existent address → clean
      `BusError`, no hang).
- [ ] `INFO` reports real `BusCount`/`CAP_READ`; the broker's SMBus catalog comes alive.
- [ ] No system instability across a few hundred reads.
