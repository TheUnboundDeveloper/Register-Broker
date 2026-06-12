using System.IO.Pipes;
using System.Linq;
using System.Text.Json;

namespace BrokerSensorBridge;

/*---------------------------------------------------------------------------*\
| BrokerControlClient                                                        |
|                                                                            |
|   A real, out-of-process consumer of the broker — the thing a non-admin    |
|   third-party tool would do. It connects to the broker's well-known        |
|   control pipe, gets past the peer-identity/signature gate (no secret to    |
|   know — the broker authorizes by who the binary IS), and issues one        |
|   scoped request (catalog sensors / ping / rgb), printing the result.      |
|                                                                            |
|   This is the proof of the privilege boundary: the broker runs elevated    |
|   once and owns the sensor/SMBus access; this client runs as a normal user  |
|   and gets data only by passing the gate. Run it from a NON-elevated        |
|   shell to demonstrate non-admin access:                                    |
|                                                                            |
|     BrokerSensorBridge.exe --client                     (= sensor.list)    |
|     BrokerSensorBridge.exe --client --op=ping                              |
|     BrokerSensorBridge.exe --client --op=sensor.readall (all values, 1 op) |
|     BrokerSensorBridge.exe --client --op=sensor.read --id=cpu.temp         |
\*---------------------------------------------------------------------------*/
internal static class BrokerControlClient
{
    public static async Task<int> RunAsync(string[] args)
    {
        bool control = args.Any(a => a.Equals("--control", StringComparison.OrdinalIgnoreCase));
        string pipeName = control ? BrokerProtocol.ControlPipeName : BrokerProtocol.PipeName;

        string op        = ArgStr(args, "--op=", control ? "rgb.list" : "sensor.list");
        string scopesArg  = ArgStr(args, "--scopes=", control ? "rgb:write" : "sensors:read");
        string[] scopes   = scopesArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        Console.WriteLine($@"Connecting to \\.\pipe\{pipeName} as a non-admin client (elevated={IsElevated()}).");
        try { await pipe.ConnectAsync(5000, cts.Token); }
        catch (Exception ex)
        {
            Console.WriteLine("Connect failed (is the broker running?): " + ex.Message);
            return 1;
        }

        /*-- 1. hello + 2. ok/deny. The broker has already decided whether to talk
              to us at all, based on our process identity/signature. If we were
              rejected, it closes the pipe with no reply — which can surface on the
              hello write (server closed first) or the ok read (server closed after),
              so both map to the same clean "denied" message instead of a crash. --*/
        JsonElement ok;
        try
        {
            await BrokerControlServer.WriteFrameAsync(pipe,
                new { type = "hello", protocol = BrokerProtocol.Version, scopes }, cts.Token);
            ok = await BrokerControlServer.ReadFrameAsync(pipe, cts.Token);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException)
        {
            Console.WriteLine("Broker closed the connection without authorizing (peer-identity/signature gate?).");
            return 1;
        }
        if (GetType(ok) != "ok")
        {
            Console.WriteLine("Authorization denied by the broker.");
            return 1;
        }
        string token   = ok.GetProperty("token").GetString()!;
        string granted = string.Join(", ", ok.GetProperty("scopes").EnumerateArray().Select(e => e.GetString()));
        Console.WriteLine($"Authorized by the broker. Granted scopes: [{granted}]");

        /*-- 4. one scoped request. Sensor access is catalog-only: name a logical
              sensor (--id), never an address. There is no raw-addressing op here. --*/
        object request = op.ToLowerInvariant() switch
        {
            "ping"           => new { token, op = "ping" },
            "sensor.list"    => new { token, op = "sensor.list" },
            "sensor.readall" => new { token, op = "sensor.readall" },
            "sensor.read"    => new { token, op = "sensor.read", id = ArgStr(args, "--id=", "cpu.temp") },
            "rgb.list"       => new { token, op = "rgb.list" },
            "rgb.set"        => new { token, op = "rgb.set", device = ArgStr(args, "--device=", "ram0"), color = ArgStr(args, "--color=", "00FF00") },
            _                => new { token, op }   // pass through; the server denies unknown ops
        };
        await BrokerControlServer.WriteFrameAsync(pipe, request, cts.Token);

        JsonElement resp = await BrokerControlServer.ReadFrameAsync(pipe, cts.Token);
        return Report(op, resp);
    }

    private static int Report(string op, JsonElement resp)
    {
        string type = GetType(resp);

        if (op.Equals("ping", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(type == "pong" ? "pong (broker alive)" : "unexpected: " + resp);
            return type == "pong" ? 0 : 1;
        }

        if (op.Equals("sensor.read", StringComparison.OrdinalIgnoreCase))
        {
            if (type == "data")
            {
                Console.WriteLine($"{resp.GetProperty("id").GetString()} = {resp.GetProperty("value").GetDouble():F2} {resp.GetProperty("unit").GetString()}");
                return 0;
            }
            Console.WriteLine("sensor.read denied/failed: " + resp);
            return 1;
        }

        if (op.Equals("sensor.list", StringComparison.OrdinalIgnoreCase))
        {
            if (type == "data" && resp.TryGetProperty("sensors", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                Console.WriteLine($"Sensor catalog ({list.GetArrayLength()} available):");
                foreach (var s in list.EnumerateArray())
                    Console.WriteLine(string.Format("  {0,-14} {1} [{2}]",
                        s.GetProperty("id").GetString(), s.GetProperty("label").GetString(), s.GetProperty("unit").GetString()));
                return 0;
            }
            Console.WriteLine("sensor.list denied/failed: " + resp);
            return 1;
        }

        if (op.Equals("sensor.readall", StringComparison.OrdinalIgnoreCase))
        {
            if (type == "data" && resp.TryGetProperty("sensors", out var all) && all.ValueKind == JsonValueKind.Array)
            {
                Console.WriteLine($"Sensor values ({all.GetArrayLength()} read, one op):");
                foreach (var s in all.EnumerateArray())
                    Console.WriteLine(string.Format("  {0,-18} {1,10:F2} {2}",
                        s.GetProperty("id").GetString(), s.GetProperty("value").GetDouble(), s.GetProperty("unit").GetString()));
                return 0;
            }
            Console.WriteLine("sensor.readall denied/failed: " + resp);
            return 1;
        }

        if (op.Equals("rgb.set", StringComparison.OrdinalIgnoreCase))
        {
            if (type == "data")
            {
                // The server stopped echoing `color` when per-LED frames landed; treat
                // every response field as optional so a success can never crash the printer.
                string device = resp.TryGetProperty("device", out var d) ? d.GetString() ?? "?" : "?";
                string color  = resp.TryGetProperty("color", out var c) ? $" -> #{c.GetString()}" : "";
                Console.WriteLine($"rgb.set OK: {device}{color}");
                return 0;
            }
            Console.WriteLine("rgb.set denied/failed: " + resp);
            return 1;
        }

        if (op.Equals("rgb.list", StringComparison.OrdinalIgnoreCase))
        {
            if (type == "data" && resp.TryGetProperty("devices", out var devs) && devs.ValueKind == JsonValueKind.Array)
            {
                Console.WriteLine($"RGB devices ({devs.GetArrayLength()}):");
                foreach (var dv in devs.EnumerateArray())
                    Console.WriteLine(string.Format("  {0,-8} {1} [{2} LEDs]",
                        dv.GetProperty("id").GetString(), dv.GetProperty("label").GetString(), dv.GetProperty("leds").GetInt32()));
                return 0;
            }
            Console.WriteLine("rgb.list denied/failed: " + resp);
            return 1;
        }

        /* Unknown op: print the raw response (the server has already denied it). */
        Console.WriteLine($"{op}: " + resp);
        return type == "data" ? 0 : 1;
    }

    private static string GetType(JsonElement el)
        => el.TryGetProperty("type", out var t) ? (t.GetString() ?? "") : "";

    private static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static string ArgStr(string[] args, string prefix, string fallback)
    {
        string? a = args.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return a is null ? fallback : a[prefix.Length..];
    }

}
