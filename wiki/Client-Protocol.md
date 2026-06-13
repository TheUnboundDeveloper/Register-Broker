# Client Protocol (v2)

This is the contract a **non-admin client** uses to consume sensor data — and drive RGB — through the broker. A consumer makes **named, authorized, least-privilege queries** over a local named pipe. Any language can implement it: a named pipe plus a JSON parser; no crypto, no secret to manage. There is **no HTTP surface** — the pipes are the only serving interface.

There are **two pipes**, same framing and auth model, different scope:

| Pipe | Service | Scope | Ops |
|---|---|---|---|
| `\\.\pipe\SensorBroker` | sensor broker (default) | `sensors:read` | `sensor.list`, `sensor.read`, `sensor.readall`, `ping` |
| `\\.\pipe\BrokerControl` | RGB control service | `rgb:write` | `rgb.list`, `rgb.set`, `ping` |

The sensor broker **never** offers writes; the control service is a separate, write-only process. Most consumers only need the sensor broker.

---

## 1. Transport & framing

- **Pipe:** fixed, well-known name. Open for read/write, byte mode.
- **Frame:** every message is a **4-byte big-endian length prefix** followed by that many bytes of **UTF-8 JSON**. Max frame 64 KB. One JSON object per frame.
- The broker runs elevated and holds the only driver handle; the client runs as a normal user. Elevation never crosses to the client.

## 2. Authentication — identity, not a secret

There is **no shared secret, no token to provision, no handshake crypto.** On connect, the broker authenticates the client by **pipe DACL + peer-process identity + (optional) Authenticode signer-thumbprint pin**:

- If the client is **not** authorized, the broker **closes the pipe with no reply.** Treat "connection closed instead of an `ok`" as "denied."
- Default is `RequireAuthorizedClient = false` — **audit-only** (clients the pipe DACL admits are allowed, and logged with their identity). A hardened deployment pins the consumer's signer thumbprint.

The client does nothing special for auth — it just connects.

## 3. Handshake

| # | Direction | Frame |
|---|-----------|-------|
| 1 | client → broker | `{"type":"hello","protocol":2,"scopes":["sensors:read"]}` |
| 2 | broker → client | `{"type":"ok","token":"<b64>","scopes":["sensors:read"]}` — or the pipe closes |

- `scopes` in the hello is what the client *requests*; the `ok` echoes what was *granted*.
- `token` is an opaque session token; include it on every subsequent request. Sessions expire after **10 minutes** — on a later `deny` with no other cause, re-`hello`.

## 4. Sensor operations

Every request is `{"token":"<b64>","op":"<name>", ...}`.

### `sensor.list` — discover the catalog
```
→ {"token":"…","op":"sensor.list"}
← {"type":"data","op":"sensor.list","sensors":[
     {"id":"smu.cpu.temp","label":"CPU Temperature","unit":"°C"},
     {"id":"nct6687d.fan.3","label":"Fan 3","unit":"RPM"}, … ]}
```
This is the **only** discovery mechanism. It returns the broker's curated catalog — there is no way to enumerate hardware, addresses, or registers.

### `sensor.read` — read one named sensor
```
→ {"token":"…","op":"sensor.read","id":"smu.cpu.temp"}
← {"type":"data","op":"sensor.read","id":"smu.cpu.temp","value":56.6,"unit":"°C"}
   | {"type":"deny"}      (unknown id, unavailable, or not permitted — uniform, no oracle)
```
The client never sends an address, register, bus, or index — only a logical id.

### `sensor.readall` — read every available sensor in one op
```
→ {"token":"…","op":"sensor.readall"}
← {"type":"data","op":"sensor.readall","sensors":[
     {"id":"smu.cpu.temp","value":56.6,"unit":"°C"},
     {"id":"nct6687d.fan.3","value":3833,"unit":"RPM"}, … ]}
```
**Use this for periodic polling** — it costs ONE op against the rate limiter, versus one `sensor.read` per sensor.

### `ping` — liveness
```
→ {"token":"…","op":"ping"}        ← {"type":"pong"}
```

## 5. Deny / error semantics

- **`{"type":"deny"}`** is uniform and intentionally uninformative: bad/expired token, ungranted scope, unknown/unavailable id, or rate-limited. By design, the client cannot distinguish these.
- **Rate limiting:** sensor broker default **30 ops/s, burst 60**; control service **120 ops/s, burst 240**. The bucket is per *identity*, so reconnecting doesn't reset it. Poll at a human cadence (≤ 1 Hz is plenty) and use `sensor.readall`.
- **Sessions:** at most **32 total**, **8 per identity**.
- **Audit:** every connect, auth decision, and op→result is recorded broker-side.

## 6. RGB control plane (`\\.\pipe\BrokerControl`)

Same transport, framing, auth, deny/rate-limit, and audit rules — only the pipe name, requested scope (`rgb:write`), and ops differ. The scope is offered only when the deployment enables it (`AllowRgbWrite`/`AllowHidRgb`) *and* the RGB registry has a drivable device.

### `rgb.list` — discover controllable devices
```
→ {"token":"…","op":"rgb.list"}
← {"type":"data","op":"rgb.list","devices":[
     {"id":"ram0","label":"GSkill RGB (DIMM 0)","leds":5,"kind":"dram","transport":"smbusene"},
     {"id":"mb.argb0","label":"Front ARGB Fans","leds":60,"kind":"mbargb","transport":"usbhid"}, … ]}
```
Each device carries a `kind` (`dram` / `mb12v` / `mbargb`) and a `transport` (`smbusene` / `superioec` / `usbhid`). The device set is the active board's profile crossed with the transports actually present.

### `rgb.set` — set a named device's color(s)
```
# whole device, one color:
→ {"token":"…","op":"rgb.set","device":"ram0","color":"00FF00"}

# per-LED frame update — one RRGGBB per LED, in LED order:
→ {"token":"…","op":"rgb.set","device":"ram0","colors":["FF0000","00FF00","0000FF","FFFFFF","FF00FF"]}

← {"type":"data","op":"rgb.set","device":"ram0"}
   | {"type":"deny"}     (no rgb:write, unknown device, bad color, rate-limited)
```
The client names a logical device — never a bus, address, or register. SPD and arbitrary SMBus writes are **not** reachable this way.

### Transports & assurance

| `transport` | Hardware | Safety boundary | Status |
|---|---|---|---|
| `smbusene` | ENE/Aura DRAM modules | **Kernel** SMBus brick-guard | validated |
| `superioec` | NCT6687 12V header (JRGB) | **Kernel** EC RGB-register brick-guard | inert until validated (`CAP_SUPERIO_RGB` off) |
| `usbhid` | MSI Mystic Light ARGB headers | **Broker only** — no kernel guard | opt-in (`AllowHidRgb`, default off) |

> **`rgb.set` is colors only — there are no effect ops.** Animation (breathing, rainbow, music sync) is deliberately the consumer's job: render frames client-side and send per-LED `rgb.set` updates within the rate limit. The broker hosts no effects engine.

## 7. Minimal client flow (pseudocode)

```
pipe = open("\\.\pipe\SensorBroker")           // closed immediately => not authorized
send(pipe, {type:"hello", protocol:2, scopes:["sensors:read"]})
ok = recv(pipe)                                  // closed => denied
token = ok.token
catalog = request(pipe, {token, op:"sensor.list"}).sensors
loop (≤1 Hz):
    r = request(pipe, {token, op:"sensor.readall"})   // one op for the whole catalog
    if r.type == "data": for s in r.sensors: use(s.id, s.value, s.unit)
    else if denied repeatedly: re-hello (session may have expired)
```

Framing helper: write 4-byte BE length, then UTF-8 JSON; read 4 bytes length, then that many bytes, parse JSON.

## 8. Stability

- **Protocol version is `2`.** Additive ops/fields may appear; the framing, auth model, and `hello → ok → token` flow are stable.
- New sensors appear as new catalog ids — clients discover them via `sensor.list`, no client change needed. This is the intended extension path (and why there's no addressing).
