using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Broker.Client;

/*---------------------------------------------------------------------------*\
| BrokerClient                                                               |
|                                                                            |
|   A portable, dependency-free client for the Register Broker control       |
|   pipes. It reimplements EXACTLY the wire format the broker speaks so the   |
|   reference console is a transparent instrument: 4-byte big-endian length  |
|   prefix + UTF-8 JSON frames, a hello/ok identity handshake (no shared     |
|   secret -- the broker authorizes by who the binary is), then scoped       |
|   {token, op, ...} requests.                                               |
|                                                                            |
|   Two well-known pipes:                                                     |
|     SensorBroker   -- read-only sensor catalog/values (scope sensors:read) |
|     BrokerControl  -- RGB control                      (scope rgb:write)   |
|                                                                            |
|   Nothing here is Windows-specific beyond the named pipe itself; the UI    |
|   layered on top is cross-platform Avalonia. The connection target (the    |
|   broker service) only runs on Windows today.                              |
\*---------------------------------------------------------------------------*/
public sealed class BrokerClient : IAsyncDisposable
{
    public const int Protocol = 2;
    public const string SensorPipe = "SensorBroker";
    public const string ControlPipe = "BrokerControl";
    private const int MaxFrameBytes = 1 << 20;

    private readonly string _pipeName;
    private readonly SemaphoreSlim _io = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private string? _token;

    /// <summary>Scopes the broker actually granted after the handshake.</summary>
    public IReadOnlyList<string> GrantedScopes { get; private set; } = Array.Empty<string>();

    /// <summary>True once <see cref="ConnectAsync"/> succeeds and a session token is held.</summary>
    public bool IsConnected => _pipe is { IsConnected: true } && _token is not null;

    private BrokerClient(string pipeName) => _pipeName = pipeName;

    /// <summary>A client for the read-only sensor pipe.</summary>
    public static BrokerClient ForSensors() => new(SensorPipe);

    /// <summary>A client for the RGB control pipe.</summary>
    public static BrokerClient ForControl() => new(ControlPipe);

    /*-- Connect + handshake. Throws BrokerException on connect failure or denial. --*/
    public async Task ConnectAsync(IReadOnlyList<string> scopes, int connectTimeoutMs = 5000, CancellationToken ct = default)
    {
        await DisposeAsync();
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(connectTimeoutMs, ct);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            await pipe.DisposeAsync();
            throw new BrokerException($"Could not connect to \\\\.\\pipe\\{_pipeName}. Is the broker service running?", ex);
        }

        JsonElement ok;
        try
        {
            await WriteFrameAsync(pipe, new { type = "hello", protocol = Protocol, scopes }, ct);
            ok = await ReadFrameAsync(pipe, ct);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException)
        {
            await pipe.DisposeAsync();
            throw new BrokerException("Broker closed the connection without authorizing (peer-identity/signature gate).", ex);
        }

        if (TypeOf(ok) != "ok")
        {
            await pipe.DisposeAsync();
            throw new BrokerException("Authorization denied by the broker.");
        }

        _pipe = pipe;
        _token = ok.GetProperty("token").GetString();
        GrantedScopes = ok.TryGetProperty("scopes", out var s) && s.ValueKind == JsonValueKind.Array
            ? s.EnumerateArray().Select(e => e.GetString() ?? "").Where(x => x.Length > 0).ToArray()
            : Array.Empty<string>();
    }

    /*-- One scoped request/response round-trip on the live session. Serialized:
          the effect engine streams frames on the same pipe as manual sends, and
          two interleaved request/response pairs would corrupt the stream. --*/
    public async Task<JsonElement> RequestAsync(object opFields, CancellationToken ct = default)
    {
        if (_pipe is null || _token is null)
            throw new BrokerException("Not connected. Call ConnectAsync first.");

        // Merge {token} with the caller's op fields into one object via a dictionary.
        var dict = new Dictionary<string, object?> { ["token"] = _token };
        foreach (var p in opFields.GetType().GetProperties())
            dict[p.Name] = p.GetValue(opFields);

        await _io.WaitAsync(ct);
        try
        {
            await WriteFrameAsync(_pipe, dict, ct);
            return await ReadFrameAsync(_pipe, ct);
        }
        finally { _io.Release(); }
    }

    // ---- Typed convenience ops ---------------------------------------------

    public async Task<bool> PingAsync(CancellationToken ct = default)
        => TypeOf(await RequestAsync(new { op = "ping" }, ct)) == "pong";

    public async Task<IReadOnlyList<SensorInfo>> SensorListAsync(CancellationToken ct = default)
    {
        var r = await RequestAsync(new { op = "sensor.list" }, ct);
        return ParseSensors(r, valued: false);
    }

    public async Task<IReadOnlyList<SensorInfo>> SensorReadAllAsync(CancellationToken ct = default)
    {
        var r = await RequestAsync(new { op = "sensor.readall" }, ct);
        return ParseSensors(r, valued: true);
    }

    public async Task<IReadOnlyList<RgbDevice>> RgbListAsync(CancellationToken ct = default)
    {
        var r = await RequestAsync(new { op = "rgb.list" }, ct);
        var list = new List<RgbDevice>();
        if (TypeOf(r) == "data" && r.TryGetProperty("devices", out var devs) && devs.ValueKind == JsonValueKind.Array)
            foreach (var d in devs.EnumerateArray())
                list.Add(new RgbDevice(
                    Str(d, "id"), Str(d, "label"),
                    d.TryGetProperty("leds", out var l) && l.TryGetInt32(out var n) ? n : 0,
                    d.TryGetProperty("kind", out var k) ? k.GetString() : null,
                    d.TryGetProperty("transport", out var t) ? t.GetString() : null));
        return list;
    }

    /// <summary>rgb.set device=&lt;id&gt; color=RRGGBB. Returns true on a data response.</summary>
    public async Task<bool> RgbSetAsync(string device, string colorHex, CancellationToken ct = default)
        => TypeOf(await RequestAsync(new { op = "rgb.set", device, color = colorHex.TrimStart('#') }, ct)) == "data";

    /// <summary>
    /// rgb.set device=&lt;id&gt; colors=[RRGGBB,…] — the per-LED frame form the broker
    /// routes to IRgbController.SetLeds. This is how the console streams effect frames.
    /// </summary>
    public async Task<bool> RgbSetLedsAsync(string device, IReadOnlyList<RgbColor> colors, CancellationToken ct = default)
    {
        var hex = new string[colors.Count];
        for (int i = 0; i < colors.Count; i++) hex[i] = colors[i].ToHex();
        return TypeOf(await RequestAsync(new { op = "rgb.set", device, colors = hex }, ct)) == "data";
    }

    private static IReadOnlyList<SensorInfo> ParseSensors(JsonElement r, bool valued)
    {
        var list = new List<SensorInfo>();
        if (TypeOf(r) == "data" && r.TryGetProperty("sensors", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var s in arr.EnumerateArray())
                list.Add(new SensorInfo(
                    Str(s, "id"),
                    s.TryGetProperty("label", out var lb) ? lb.GetString() ?? "" : "",
                    Str(s, "unit"),
                    valued && s.TryGetProperty("value", out var v) && v.TryGetDouble(out var d) ? d : null));
        return list;
    }

    // ---- Wire framing: 4-byte big-endian length + UTF-8 JSON ----------------

    private static async Task WriteFrameAsync(PipeStream pipe, object obj, CancellationToken ct)
    {
        byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj));
        byte[] frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        await pipe.WriteAsync(frame, ct);
        await pipe.FlushAsync(ct);
    }

    private static async Task<JsonElement> ReadFrameAsync(PipeStream pipe, CancellationToken ct)
    {
        byte[] lenBuf = await ReadExactAsync(pipe, 4, ct);
        int len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
        if (len < 0 || len > MaxFrameBytes) throw new InvalidDataException("control frame too large");
        byte[] body = await ReadExactAsync(pipe, len, ct);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    private static async Task<byte[]> ReadExactAsync(PipeStream pipe, int count, CancellationToken ct)
    {
        byte[] buf = new byte[count];
        int off = 0;
        while (off < count)
        {
            int n = await pipe.ReadAsync(buf.AsMemory(off, count - off), ct);
            if (n == 0) throw new EndOfStreamException();
            off += n;
        }
        return buf;
    }

    private static string TypeOf(JsonElement el)
        => el.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";

    private static string Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";

    public async ValueTask DisposeAsync()
    {
        if (_pipe is not null)
        {
            try { await _pipe.DisposeAsync(); } catch { /* closing */ }
            _pipe = null;
        }
        _token = null;
    }
}

/// <summary>One sensor catalog entry. <see cref="Value"/> is null for sensor.list (no values).</summary>
public sealed record SensorInfo(string Id, string Label, string Unit, double? Value);

/// <summary>One RGB device/zone from rgb.list. Kind/Transport are surfaced when the broker sends them.</summary>
public sealed record RgbDevice(string Id, string Label, int Leds, string? Kind, string? Transport);

/// <summary>Any broker-level failure (connect, denial, protocol).</summary>
public sealed class BrokerException : Exception
{
    public BrokerException(string message, Exception? inner = null) : base(message, inner) { }
}
