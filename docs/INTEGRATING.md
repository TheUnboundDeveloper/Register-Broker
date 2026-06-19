# Integrating Register Broker into your application

How to add Register Broker support to an app — a hardware-monitoring dashboard, a game
overlay, an RGB frontend, anything that wants sensor values or RGB control **without
running as admin and without shipping a kernel driver**.

This is the practical companion to [CLIENT-PROTOCOL.md](CLIENT-PROTOCOL.md) (the
authoritative wire contract). Read that for the full semantics; copy from here to get
running in minutes.

> **Want a complete, working reference?** The first-party
> **[Reference Console](REFERENCE-CONSOLE.md)** ([`Test_GUI/ReferenceConsole/`](../Test_GUI/ReferenceConsole/))
> is a non-admin GUI that does everything below — sensor polling and per-LED RGB — over these
> exact pipes. Its `Broker.Client/` project is a portable, dependency-free port of the wire
> format you can read alongside the snippets here.

## What integration buys you

- **No elevation.** Your app runs as a normal user; the broker's services hold the only
  privileged handle.
- **No hardware code.** You never touch SMBus, Super-I/O, or SMU — you read a named
  catalog (`smu.cpu.temp`, `nct6687d.fan.3`) and the broker handles every chip it knows.
- **No dependencies.** The protocol is a named pipe + length-prefixed JSON. Any language
  that can open `\\.\pipe\...` and parse JSON can integrate — no client library required
  (though the reference C# client `BrokerSensorBridge/BrokerControlClient.cs` is there
  to copy).

## The five-minute version

1. Connect to `\\.\pipe\SensorBroker` (byte mode, read/write).
2. Send `{"type":"hello","protocol":2,"scopes":["sensors:read"]}`.
3. Read the `ok` reply, keep its `token`.
4. Call `{"token":…,"op":"sensor.list"}` once to discover ids; let the user pick.
5. Poll `{"token":…,"op":"sensor.readall"}` at ≤ 1 Hz; display values.

Every frame on the wire — both directions — is a **4-byte big-endian length prefix
followed by that many bytes of UTF-8 JSON** (one object per frame, max 64 KB).

If the pipe **closes instead of answering**, you were denied by policy — that's the
protocol's uniform "no", not a transport bug. If the broker isn't installed, the pipe
won't exist (`ERROR_FILE_NOT_FOUND`): treat both as "sensors unavailable" and degrade
gracefully.

## C# client (complete, no dependencies beyond the BCL)

```csharp
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

sealed class RegisterBrokerClient : IDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private string? _token;

    public RegisterBrokerClient(string pipeName = "SensorBroker")
        => _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);

    public void Connect(string scope = "sensors:read", int timeoutMs = 2000)
    {
        _pipe.Connect(timeoutMs);                       // IOException if broker absent
        Send(new { type = "hello", protocol = 2, scopes = new[] { scope } });
        var ok = Receive();                             // pipe closed here => denied
        _token = ok.GetProperty("token").GetString();
    }

    public JsonElement Request(string op, object? extra = null)
    {
        var req = new Dictionary<string, object?> { ["token"] = _token, ["op"] = op };
        if (extra != null)
            foreach (var p in JsonSerializer.SerializeToElement(extra).EnumerateObject())
                req[p.Name] = p.Value;
        Send(req);
        return Receive();
    }

    private void Send(object msg)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(msg);
        byte[] frame = new byte[4 + body.Length];
        frame[0] = (byte)(body.Length >> 24); frame[1] = (byte)(body.Length >> 16);
        frame[2] = (byte)(body.Length >> 8);  frame[3] = (byte)body.Length;
        body.CopyTo(frame, 4);
        _pipe.Write(frame, 0, frame.Length);
    }

    private JsonElement Receive()
    {
        byte[] len = ReadExactly(4);
        int n = (len[0] << 24) | (len[1] << 16) | (len[2] << 8) | len[3];
        return JsonDocument.Parse(ReadExactly(n)).RootElement;
    }

    private byte[] ReadExactly(int n)
    {
        var buf = new byte[n];
        for (int off = 0; off < n;)
        {
            int r = _pipe.Read(buf, off, n - off);
            if (r <= 0) throw new IOException("pipe closed (denied or broker stopped)");
            off += r;
        }
        return buf;
    }

    public void Dispose() => _pipe.Dispose();
}
```

Using it:

```csharp
using var broker = new RegisterBrokerClient();
broker.Connect();                                        // sensors:read

var list = broker.Request("sensor.list");                // once, for discovery/UI
foreach (var s in list.GetProperty("sensors").EnumerateArray())
    Console.WriteLine($"{s.GetProperty("id")} — {s.GetProperty("label")}");

while (true)                                             // poll loop, one op per cycle
{
    var r = broker.Request("sensor.readall");
    if (r.GetProperty("type").GetString() == "data")
        foreach (var s in r.GetProperty("sensors").EnumerateArray())
            Update(s.GetProperty("id").GetString()!,
                   s.GetProperty("value").GetDouble(),
                   s.GetProperty("unit").GetString()!);
    // repeated deny => session expired (10 min): reconnect + re-hello
    Thread.Sleep(1000);
}
```

## Python client (stdlib only)

A byte-mode named pipe opens like a file on Windows — no `pywin32` needed:

```python
import json, struct

class RegisterBroker:
    def __init__(self, pipe=r"\\.\pipe\SensorBroker", scope="sensors:read"):
        self.f = open(pipe, "r+b", buffering=0)     # FileNotFoundError => not installed
        self._send({"type": "hello", "protocol": 2, "scopes": [scope]})
        self.token = self._recv()["token"]          # EOF here => denied by policy

    def request(self, op, **kw):
        self._send({"token": self.token, "op": op, **kw})
        return self._recv()

    def _send(self, msg):
        body = json.dumps(msg).encode("utf-8")
        self.f.write(struct.pack(">I", len(body)) + body)

    def _recv(self):
        hdr = self._read(4)
        return json.loads(self._read(struct.unpack(">I", hdr)[0]))

    def _read(self, n):
        buf = b""
        while len(buf) < n:
            chunk = self.f.read(n - len(buf))
            if not chunk:
                raise ConnectionError("pipe closed (denied or broker stopped)")
            buf += chunk
        return buf

broker = RegisterBroker()
for s in broker.request("sensor.readall")["sensors"]:
    print(f'{s["id"]:24} {s["value"]:>8} {s["unit"]}')
```

## RGB control

Same client code, different pipe and scope: connect to `\\.\pipe\BrokerControl`
requesting `rgb:write` (the deployment must have installed the control service —
`-WithRgbControl`). Then:

```python
rgb = RegisterBroker(pipe=r"\\.\pipe\BrokerControl", scope="rgb:write")
devices = rgb.request("rgb.list")["devices"]            # [{id,label,leds,kind,transport}, ...]
rgb.request("rgb.set", device="ram0", color="00FF00")   # whole device
rgb.request("rgb.set", device="ram0",                   # or per-LED, one frame
            colors=["FF0000", "00FF00", "0000FF", "FFFFFF", "FF00FF"])
```

Rules of the road for RGB consumers:

- **Group by `kind`/`transport`.** Each device carries `kind` (`dram`/`mb12v`/`mbargb`/`keyboard`/
  `mouse`) and `transport` (`smbusene`/`superioec`/`usbhid`/`usbhidrazer`). Most are board-specific
  (motherboard-header zones appear only on a supported board with `AllowHidRgb`), but USB-HID
  **peripherals** (Razer Chroma keyboards/mice) are board-independent — present on any host with the
  device and `AllowHidRgb`. Treat `kind`/`transport` as opaque strings (new values may appear);
  don't hard-code `ram0` — enumerate `rgb.list`.
- **Per-LED support varies by transport.** DRAM and the Razer extended-matrix peripherals honor the
  full `colors` array; the MSI USB-HID motherboard zone currently applies the lead color (per-LED
  streaming there is a future item).
- **Effects are your job.** The broker writes colors; it hosts no animation engine.
  Render frames in your app and send per-LED `rgb.set` calls at your own cadence.
- The control pipe's rate limit (120 ops/s, burst 240) comfortably fits per-device
  frame updates at tens of FPS — one `rgb.set` with a `colors` array updates a whole
  device in **one** op. Don't send one op per LED.
- You can't scan. `rgb.list` is the entire controllable universe; there is no way to
  reach an address, and SPD/arbitrary SMBus writes are blocked in the kernel.

## Integration rules that keep you working across versions

- **Persist sensor *ids*, never labels or list positions.** Ids (`{chip}.{kind}.{index}`)
  are the stability contract; labels come from board calibration data and can change.
- **Poll with `sensor.readall`**, not N × `sensor.read` — it costs one op against the
  rate limit (sensor pipe: 30 ops/s, burst 60). ≤ 1 Hz is plenty for monitoring.
- **Handle `deny` as routine, not as an error dialog.** It uniformly means bad/expired
  token, ungranted scope, unknown id, or rate-limited. On repeated deny: back off,
  reconnect, re-hello (sessions expire after 10 minutes).
- **New sensors appear as new catalog ids.** Re-run `sensor.list` to pick them up; no
  client update is needed when the broker learns a new chip.
- **Expect to be audited.** Every connect and op is logged broker-side with your
  process identity. Hardened deployments can also *enforce* an allow-list by
  Authenticode signer or path (`RequireAuthorizedClient`) — if your users run such a
  deployment, they add your app's signer thumbprint via the installer flags
  (see [REFERENCE.md](REFERENCE.md)); your code does nothing special either way.

## Shipping it

- **Detect, don't require.** Probe for the pipe at startup; if it's missing, hide or
  gray out the sensor/RGB features and point users at the Register Broker install
  guide ([USER-GUIDE.md](USER-GUIDE.md)). Don't make the broker a hard dependency.
- **Don't bundle the broker silently.** It installs Windows services and (today) a
  test-signed kernel driver — that's an explicit, informed user decision, not something
  to hide inside your installer.
- **License boundary:** the broker is AGPL-3.0 with a Commercial Exception. Your app
  talks to it at arm's length over a documented pipe protocol — it links nothing from
  this project. Copying the sample code on this page into your client is intended use,
  whatever your app's license. (Embedding/redistributing the broker itself is where
  AGPL or the commercial license applies — see [LICENSE](../LICENSE).)

## Troubleshooting

| Symptom | Meaning |
|---|---|
| Pipe doesn't exist (`FileNotFoundError` / `ERROR_FILE_NOT_FOUND`) | Broker not installed or services stopped |
| Pipe closes right after connect (before `ok`) | Denied by policy — identity/signature not on the allow-list of a hardened deployment |
| `{"type":"deny"}` on every op | Expired token (re-hello), ungranted scope, or rate-limited |
| `rgb:write` not granted in the `ok` | Control service not installed (`-WithRgbControl`) or no drivable RGB device present |
| `{"type":"error", "status":"..."}` | The hardware op itself failed (e.g. `BusError`) — transient; retry next cycle |
