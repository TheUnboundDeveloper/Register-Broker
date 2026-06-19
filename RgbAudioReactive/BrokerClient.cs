using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace RgbAudioReactive;

/*---------------------------------------------------------------------------*\
| BrokerClient                                                               |
|                                                                            |
|   A persistent, non-admin consumer of the Register Broker RGB control      |
|   plane (\\.\pipe\BrokerControl). Wire format is the project's protocol v2  |
|   (docs/CLIENT-PROTOCOL.md): a 4-byte big-endian length prefix + UTF-8 JSON |
|   per frame, hello -> ok(token), then rgb.list / rgb.set frames on the same |
|   connection. We hold ONE connection open for the whole session so frame    |
|   updates reuse the granted token and stay inside the per-identity rate     |
|   limit (control service: 120 ops/s, burst 240).                            |
|                                                                            |
|   The broker authorizes by process identity/signature, never a secret. If   |
|   we are not authorized it closes the pipe with no reply; we surface that    |
|   as a clean error rather than a crash.                                      |
\*---------------------------------------------------------------------------*/
internal sealed class BrokerClient : IDisposable
{
    private const string ControlPipeName = "BrokerControl";
    private const int Protocol = 2;

    private NamedPipeClientStream? _pipe;
    private string? _token;

    public sealed record Zone(string Id, string Label, int Leds, string Kind, string Transport);

    /// Connect, handshake for rgb:write, and stash the session token. Throws on failure.
    public async Task ConnectAsync(CancellationToken ct)
    {
        Dispose();   // drop any prior connection
        var pipe = new NamedPipeClientStream(".", ControlPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, ct);

        try
        {
            await WriteFrameAsync(pipe, new { type = "hello", protocol = Protocol, scopes = new[] { "rgb:write" } }, ct);
            JsonElement ok = await ReadFrameAsync(pipe, ct);
            if (TypeOf(ok) != "ok")
                throw new InvalidOperationException("broker denied authorization (is rgb:write enabled on the control service?)");
            _token = ok.GetProperty("token").GetString()
                     ?? throw new InvalidOperationException("broker ok frame had no token");
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException)
        {
            pipe.Dispose();
            throw new InvalidOperationException(
                "broker closed the connection without authorizing (peer-identity/signature gate, or control service not running)");
        }

        _pipe = pipe;
    }

    /// Discover the drivable zones. Auto-discovery is the whole point — we drive whatever
    /// the broker currently exposes, with no hardcoded device ids.
    public async Task<IReadOnlyList<Zone>> ListZonesAsync(CancellationToken ct)
    {
        JsonElement resp = await RequestAsync(new { token = _token, op = "rgb.list" }, ct);
        if (TypeOf(resp) != "data" || !resp.TryGetProperty("devices", out var devs) || devs.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("rgb.list failed: " + resp);

        var zones = new List<Zone>(devs.GetArrayLength());
        foreach (var d in devs.EnumerateArray())
        {
            zones.Add(new Zone(
                d.GetProperty("id").GetString() ?? "?",
                d.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "",
                d.TryGetProperty("leds", out var n) ? n.GetInt32() : 1,
                d.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "",
                d.TryGetProperty("transport", out var t) ? t.GetString() ?? "" : ""));
        }
        return zones;
    }

    /// Push one per-LED frame to a zone. Colors are 6-hex RRGGBB, in LED order; the broker
    /// clamps the list to the device's LED count. Returns false on deny/error (e.g. rate
    /// limited) so the caller can back off without tearing the connection down.
    public async Task<bool> SetLedsAsync(string deviceId, string[] colors, CancellationToken ct)
    {
        JsonElement resp = await RequestAsync(new { token = _token, op = "rgb.set", device = deviceId, colors }, ct);
        return TypeOf(resp) == "data";
    }

    private async Task<JsonElement> RequestAsync(object request, CancellationToken ct)
    {
        if (_pipe is null) throw new InvalidOperationException("not connected");
        await WriteFrameAsync(_pipe, request, ct);
        return await ReadFrameAsync(_pipe, ct);
    }

    public void Dispose()
    {
        _pipe?.Dispose();
        _pipe = null;
        _token = null;
    }

    /*-- framing: 4-byte big-endian length prefix + UTF-8 JSON (protocol v2) -------------*/

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
        if (len < 0 || len > 64 * 1024) throw new InvalidDataException("control frame too large");
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
}
