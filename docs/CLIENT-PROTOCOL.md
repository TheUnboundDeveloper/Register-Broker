# Register Broker — Client Protocol (v2)

The contract for a **non-admin client** to consume sensor data — and drive RGB hardware —
through the broker. A consumer makes **named, authorized, least-privilege queries** over a
local named pipe. Any language can implement it (named pipe + a JSON parser; no crypto, no
secret to manage). There is **no HTTP surface** — the pipes are the only serving interface.

There are **two pipes**, same framing and auth model, different scope:

| Pipe | Service | Scope | Ops |
|---|---|---|---|
| `\\.\pipe\SensorBroker` | sensor broker (default) | `sensors:read` (+ `smbus:read` if a driver backs it — note there is **no** raw `smbus.read` op; the scope gates nothing address-shaped) | `sensor.list`, `sensor.read`, `sensor.readall`, `ping` |
| `\\.\pipe\BrokerControl` | RGB control service (`--control`) | `rgb:write` | `rgb.list`, `rgb.set`, `ping` |

The sensor broker **never** offers writes; the control service is a separate, write-only
process. Most consumers only need the sensor broker — §6 covers the control plane.

Authoritative companions: `ARCHITECTURE.md` (trust boundaries), `SENSOR-MAP.md` (catalog),
the C# reference client `BrokerSensorBridge/BrokerControlClient.cs`. For a practical
walkthrough with copy-paste C#/Python clients, see `INTEGRATING.md`.

---

## 1. Transport & framing

- **Pipe:** `\\.\pipe\SensorBroker` (fixed, well-known). Open for read/write, byte mode.
- **Frame:** every message is a **4-byte big-endian length prefix** followed by that many
  bytes of **UTF-8 JSON**. Max frame 64 KB (control JSON is tiny). One JSON object per frame.
- The broker runs elevated and holds the only driver handle; the client runs as a normal
  user. Elevation never crosses to the client.

## 2. Authentication — identity, not a secret

There is **no shared secret, no token to provision, no handshake crypto.** On connect the
broker authenticates the client by **pipe DACL + peer-process identity + (optional)
Authenticode signer-thumbprint pin**:

- If the client is **not** authorized, the broker **closes the pipe with no reply.** A
  client must treat "connection closed instead of an `ok`" as "denied."
- Enforcement is broker policy (`RequireAuthorizedClient` + `AllowedClientPaths` /
  `AllowedClientSigners`). Default is `RequireAuthorizedClient = false` — **audit-only**
  (clients the pipe DACL admits are allowed, and logged with their identity). A hardened
  deployment pins the consumer's signer thumbprint.

The client does nothing special for auth — it just connects. Either it gets an `ok`, or the
pipe closes.

## 3. Handshake

| # | Direction | Frame |
|---|-----------|-------|
| 1 | client → broker | `{"type":"hello","protocol":2,"scopes":["sensors:read"]}` |
| 2 | broker → client | `{"type":"ok","token":"<b64>","scopes":["sensors:read"]}` — or the pipe closes |

- `scopes` in the hello is what the client *requests*; the `ok` echoes what was *granted*
  (requested ∩ what the broker can back). For sensor consumers this is `sensors:read`.
- `token` is an opaque session token; include it on every subsequent request. Sessions
  expire after **10 minutes** — on a later `deny` with no other cause, re-`hello`.

## 4. Operations

Every request is `{"token":"<b64>","op":"<name>", ...}`. Responses are single frames.

### `sensor.list` — discover the catalog
```
→ {"token":"…","op":"sensor.list"}
← {"type":"data","op":"sensor.list","sensors":[
     {"id":"smu.cpu.temp","label":"CPU Temperature","unit":"°C"},
     {"id":"nct6687d.temp.2","label":"VRM MOS Temperature","unit":"°C"},
     {"id":"nct6687d.fan.3","label":"Fan 3","unit":"RPM"}, …
  ]}
```
This is the **only** discovery mechanism. It returns the broker's curated catalog — there is
no way to enumerate hardware, addresses, or registers. Ids are **stable raw ids**
(`{chip}.{kind}.{index}`, see `SENSOR-MAP.md`); labels come from board calibration data.

### `sensor.read` — read one named sensor
```
→ {"token":"…","op":"sensor.read","id":"smu.cpu.temp"}
← {"type":"data","op":"sensor.read","id":"smu.cpu.temp","value":56.6,"unit":"°C"}
   | {"type":"error","op":"sensor.read","id":"…","status":"BusError"}
   | {"type":"deny"}                       (unknown id, unavailable, or not permitted)
```
`id` must come from `sensor.list`. **The client never sends an address, register, bus, or
index** — only a logical id. An unknown/unavailable id returns a uniform `deny` (no oracle).
Legacy semantic ids saved by older consumers (`cpu.temp`, `board.vrm.temp`, `fan3`, …) still
resolve via a built-in alias map, but `sensor.list` publishes only the raw ids.

### `sensor.readall` — read every available sensor in one op
```
→ {"token":"…","op":"sensor.readall"}
← {"type":"data","op":"sensor.readall","sensors":[
     {"id":"smu.cpu.temp","value":56.6,"unit":"°C"},
     {"id":"nct6687d.temp.2","value":39.5,"unit":"°C"},
     {"id":"nct6687d.fan.3","value":3833,"unit":"RPM"}, … ]}
```
Returns the current value of every catalog sensor that reads successfully this cycle (failed
ones are omitted). **Use this for periodic polling of many sensors** — it costs ONE op against
the rate limiter, versus one `sensor.read` per sensor. Still catalog-only (no addressing); the
id set matches `sensor.list`. This is what a consumer polling the whole catalog should call
each cycle.

### `ping` — liveness
```
→ {"token":"…","op":"ping"}        ← {"type":"pong"}
```

> The legacy bulk `sensors` op (the old LibreHardwareMonitor-shaped tree payload) was
> **removed 2026-06-12** along with the LHM dependency. Consumers poll `sensor.readall`.

## 5. Deny / error semantics

- **`{"type":"deny"}`** is uniform and intentionally uninformative: bad/expired token,
  ungranted scope, unknown/unavailable sensor id, or **rate-limited**. The client cannot
  distinguish these — by design.
- **Rate limiting:** the broker token-buckets each session — sensor broker default
  **30 ops/s, burst 60**; control service **120 ops/s, burst 240** (so per-LED frame updates
  fit). The bucket is per *identity*, so reconnecting doesn't reset it. A client that exceeds
  it gets `deny`; back off and retry. Poll at a human cadence (≤ 1 Hz is plenty); use
  `sensor.readall` for a whole read cycle, don't hammer.
- **Sessions:** bounded — at most **32 total**, **8 per identity**; a session ends when its
  connection closes (or after the 10-minute expiry).
- **Audit:** every connect, auth decision, and op→result is recorded broker-side. Assume your
  queries are logged with your process identity.

## 6. RGB control plane (`\\.\pipe\BrokerControl`)

The write side lives in a **separate, write-only service** (`BrokerSensorBridge.exe
--control`). Same transport, framing, auth, deny/rate-limit, and audit rules — only the pipe
name, requested scope, and ops differ. The `rgb:write` scope is offered **only** when the
deployment enables it (`allowRgbWrite`) *and* the RGB registry actually has a drivable
device; the sensor broker never serves these ops.

Handshake requests `rgb:write`:

```
→ {"type":"hello","protocol":2,"scopes":["rgb:write"]}
← {"type":"ok","token":"<b64>","scopes":["rgb:write"]}     — or the pipe closes
```

### `rgb.list` — discover controllable devices
```
→ {"token":"…","op":"rgb.list"}
← {"type":"data","op":"rgb.list","devices":[
     {"id":"ram0",        "label":"GSkill RGB (DIMM 0)",        "leds":5,  "kind":"dram",     "transport":"smbusene"},
     {"id":"ram1",        "label":"GSkill RGB (DIMM 1)",        "leds":5,  "kind":"dram",     "transport":"smbusene"},
     {"id":"mb.argb0",    "label":"Front ARGB Fans (JRAINBOW)", "leds":60, "kind":"mbargb",   "transport":"usbhid"},
     {"id":"razer.naga",  "label":"Razer Naga Trinity",         "leds":3,  "kind":"mouse",    "transport":"usbhidrazer"},
     {"id":"razer.cynosa","label":"Razer Cynosa Chroma",        "leds":132,"kind":"keyboard", "transport":"usbhidrazer"}, … ]}
```
Each device carries a `kind` (`dram` / `mb12v` / `mbargb` / `keyboard` / `mouse`) for grouping, and
a `transport` (`smbusene` / `superioec` / `usbhid` / `usbhidrazer`) for diagnostics. The device set
is the active board's profile crossed with the transports actually present (so motherboard-header
zones appear only on a board that has them and a host where that transport is enabled — see §6.1),
**plus board-independent USB-HID peripherals** (Razer Chroma keyboards/mice), matched by USB
vendor/product/interface rather than the board profile. `kind` and `transport` are additive fields
— older clients that read only `id`/`label`/`leds` keep working, and new values may appear over
time, so treat them as opaque strings.

### `rgb.set` — set a named device's color(s)
Two forms (both name a device, never an address):
```
# whole device, one color:
→ {"token":"…","op":"rgb.set","device":"ram0","color":"00FF00"}

# per-LED (a consumer frame update) — one RRGGBB per LED, in LED order:
→ {"token":"…","op":"rgb.set","device":"ram0","colors":["FF0000","00FF00","0000FF","FFFFFF","FF00FF"]}

← {"type":"data","op":"rgb.set","device":"ram0"}        (note: no color field is echoed back)
   | {"type":"error","op":"rgb.set","device":"ram0"}
   | {"type":"deny"}                         (no rgb:write, unknown device, bad color, rate-limited)
```
`color`/`colors` are `RRGGBB` hex. The `colors` list is clamped to the device's LED count. As
with sensors, **the client names a logical device — never a bus, address, or register.** The
device→hardware map is baked into the broker (`RgbCatalog`, a DMI-matched board profile); the
client contract is identical for every transport. SPD and arbitrary SMBus writes are **not**
reachable this way. The control service allows a higher op rate than the sensor broker
(120 ops/s, burst 240) so per-LED frame updates aren't rate-limited.

### 6.1 Transports & assurance (motherboard headers)
`rgb.set` is transport-agnostic, but the **safety boundary differs by transport** — surfaced as
the `transport` field so an operator knows what bounds a write:

| `transport` | Hardware | Safety boundary | Status |
|---|---|---|---|
| `smbusene` | ENE/Aura DRAM modules | **Kernel** SMBus brick-guard (`0x70–0x77` / `0x39–0x3A`) | validated |
| `superioec` | NCT6687 12V header (JRGB) | **Kernel** EC RGB-register brick-guard | inert until the EC RGB window is hardware-validated (`CAP_SUPERIO_RGB` off) |
| `usbhid` | MSI Mystic Light headers + many USB-HID peripherals (Logitech, SteelSeries, Corsair iCUE V2, HyperX, Cooler Master, NZXT, Roccat, Redragon, ASUS, Lian Li, AMD Wraith Prism) | **Broker only** — baked report builder, *no kernel guard* | `AllowHidRgb` **on by default**; MSI Mystic Light validated, most peripherals HW-unvalidated |
| `usbhidrazer` | Razer Chroma peripherals (keyboards / mice) | **Broker only** — baked report builder, *no kernel guard* | `AllowHidRgb` **on by default**; validated (Naga Trinity, Cynosa Chroma) |

The `usbhid` / `usbhidrazer` paths are user-mode HID transports (reduced assurance). As of
2026-06-22 they are **enabled by default** (`AllowHidRgb` defaults to `true`); set `AllowHidRgb:
false` in `appsettings.json` to opt out (`--allow-hid-rgb` still forces it on). Adding a new board's
zones is a broker-only change — the kernel exposes only stable, class-wide windows, so no driver
recompile is needed. Peripherals (Razer, Logitech, SteelSeries, …) are **board-independent**:
matched by USB vendor/product/interface+usage (not the DMI board profile), so they appear on any
host with the device present and `AllowHidRgb` on.

> **`rgb.set` is colors only — there are no effect ops.** Animation (breathing, rainbow,
> music sync) is deliberately the consumer's job: render frames client-side and send
> per-LED `rgb.set` updates at your own cadence within the rate limit. The broker hosts
> no effects engine; if effect ops ever exist they will be additive ops dispatched to a
> separate sidecar process (a deferred post-1.0 design), never an engine
> inside the broker.

## 7. Minimal client flow (pseudocode)

```
pipe = open("\\.\pipe\SensorBroker")           // closed immediately => not authorized
send(pipe, {type:"hello", protocol:2, scopes:["sensors:read"]})
ok = recv(pipe)                                  // closed => denied
token = ok.token
catalog = request(pipe, {token, op:"sensor.list"}).sensors
loop (≤1 Hz):
    r = request(pipe, {token, op:"sensor.readall"})      // one op for the whole catalog
    if r.type == "data": for s in r.sensors: use(s.id, s.value, s.unit)
    else if denied repeatedly: re-hello (session may have expired)
```

Framing helper: write 4-byte BE length, then UTF-8 JSON; read 4 bytes length, then that many
bytes, parse JSON. (See `BrokerControlServer.WriteFrameAsync`/`ReadFrameAsync` and
`BrokerControlClient` for the reference implementation.)

## 8. Stability

- **Protocol version is `2`.** Send it in the hello. Additive ops/fields may appear; the
  framing, auth model, and the `hello → ok → token` flow are stable.
- New sensors appear as new catalog ids — clients discover them via `sensor.list`, no client
  change needed. This is the intended extension path (and why there's no addressing).
