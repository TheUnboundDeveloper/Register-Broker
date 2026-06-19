using System.Diagnostics;
using RgbAudioReactive;

/*---------------------------------------------------------------------------*\
| RgbAudioReactive — audio-reactive RGB over the Register Broker             |
|                                                                            |
|   A standalone, NON-ADMIN consumer. It captures audio (the microphone, or   |
|   the system output via loopback), turns it into a light show, and streams  |
|   per-LED frames to whatever zones the broker currently exposes — all over  |
|   the public control pipe. Nothing here touches the signed driver or the    |
|   broker's privileged surface; it is exactly what any third-party app does.  |
|                                                                            |
|   Usage:                                                                     |
|     RgbAudioReactive --source=output  --mode=spectrum                       |
|     RgbAudioReactive --source=mic     --mode=level                          |
|     RgbAudioReactive --devices=ram0,mb.argb0   (restrict to some zones)     |
|     RgbAudioReactive --fps=30 --bands=16                                    |
|   Ctrl+C to stop (zones are blacked out on exit).                           |
\*---------------------------------------------------------------------------*/

var opts = Options.Parse(args);
if (opts is null) { Options.PrintUsage(); return 1; }

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

using var broker = new BrokerClient();
try
{
    Console.WriteLine(@"Connecting to \\.\pipe\BrokerControl as a non-admin client...");
    await broker.ConnectAsync(cts.Token);
}
catch (Exception ex)
{
    Console.Error.WriteLine("Connect failed: " + ex.Message);
    Console.Error.WriteLine("  Is the BrokerControl (RGB) service running? Start it with Start-BrokerServices.ps1.");
    return 1;
}

// --- discover zones, then apply the optional --devices filter ---
IReadOnlyList<BrokerClient.Zone> all = await broker.ListZonesAsync(cts.Token);
var zones = (opts.DeviceFilter is { Count: > 0 } f
        ? all.Where(z => f.Contains(z.Id, StringComparer.OrdinalIgnoreCase))
        : all)
    .ToList();

if (zones.Count == 0)
{
    Console.Error.WriteLine(all.Count == 0
        ? "The broker exposed no RGB zones. Enable a transport (e.g. AllowHidRgb) and retry."
        : "No zones matched --devices. Available: " + string.Join(", ", all.Select(z => z.Id)));
    return 1;
}

Console.WriteLine($"Driving {zones.Count} zone(s):");
foreach (var z in zones)
    Console.WriteLine($"  {z.Id,-14} {z.Label} [{z.Leds} LEDs] ({z.kindAndTransport()})");

// --- frame rate: the control service token-buckets at 120 ops/s (burst 240) PER identity,
//     and each zone is one op per frame. Cap the FPS so all zones together stay under the
//     sustained limit, with margin. --fps can lower it but never push past the cap. ---
int safeCap = Math.Max(5, 110 / zones.Count);
int fps = Math.Clamp(opts.Fps ?? safeCap, 1, safeCap);
if (opts.Fps is int req && req > safeCap)
    Console.WriteLine($"Note: requested {req} fps would exceed the broker rate limit for {zones.Count} zone(s); capped to {fps} fps.");
Console.WriteLine($"Source: {opts.Source}   Mode: {opts.Mode}   Frame rate: {fps} fps   Bands: {opts.Bands}");
Console.WriteLine("Running. Press Ctrl+C to stop.");

// --- start audio capture and the render loop ---
using var analyzer = new AudioAnalyzer(opts.Bands);
try { analyzer.Start(opts.Source); }
catch (Exception ex)
{
    Console.Error.WriteLine("Audio capture failed: " + ex.Message);
    Console.Error.WriteLine(opts.Source == AudioSource.Microphone
        ? "  Check a microphone is set as the default recording device."
        : "  Check a playback device is active (loopback follows the default output).");
    return 1;
}

var viz = new Visualizer(opts.Mode);
var buffers = zones.ToDictionary(z => z.Id, z => new string[Math.Max(1, z.Leds)]);
var lastSent = new Dictionary<string, string>();   // dedupe: skip a zone if its frame is unchanged
var frameDelay = TimeSpan.FromSeconds(1.0 / fps);
int denyStreak = 0;

try
{
    while (!cts.IsCancellationRequested)
    {
        long t0 = Stopwatch.GetTimestamp();
        AnalysisState state = analyzer.Snapshot();

        foreach (var z in zones)
        {
            string[] buf = buffers[z.Id];
            viz.Render(state, buf);
            string frame = string.Concat(buf);                 // cheap change key
            if (lastSent.TryGetValue(z.Id, out var prev) && prev == frame)
                continue;                                       // identical (e.g. silence) -> save an op

            bool ok = await broker.SetLedsAsync(z.Id, buf, cts.Token);
            if (ok) { lastSent[z.Id] = frame; denyStreak = 0; }
            else if (++denyStreak >= 30)
            {
                // sustained denies usually mean the 10-minute session expired — reconnect once.
                Console.WriteLine("Re-authenticating with the broker...");
                try { await broker.ConnectAsync(cts.Token); lastSent.Clear(); denyStreak = 0; }
                catch (Exception ex) { Console.Error.WriteLine("Reconnect failed: " + ex.Message); cts.Cancel(); }
            }
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(t0);
        TimeSpan wait = frameDelay - elapsed;
        if (wait > TimeSpan.Zero) await Task.Delay(wait, cts.Token);
    }
}
catch (OperationCanceledException) { /* Ctrl+C — fall through to blackout */ }

// --- black out every zone so the lights don't freeze on the last frame ---
Console.WriteLine("\nStopping; clearing zones...");
foreach (var z in zones)
{
    var off = new string[Math.Max(1, z.Leds)];
    Array.Fill(off, "000000");
    try { await broker.SetLedsAsync(z.Id, off, CancellationToken.None); } catch { /* shutting down */ }
}
return 0;


/*---------------------------------------------------------------------------*\
| Options — tiny --flag=value parser                                         |
\*---------------------------------------------------------------------------*/
internal sealed class Options
{
    public AudioSource Source { get; private init; } = AudioSource.Output;
    public VisualMode Mode { get; private init; } = VisualMode.Spectrum;
    public int Bands { get; private init; } = 12;
    public int? Fps { get; private init; }
    public IReadOnlyList<string>? DeviceFilter { get; private init; }

    public static Options? Parse(string[] args)
    {
        if (args.Any(a => a is "-h" or "--help" or "/?")) return null;
        var src = AudioSource.Output;
        var mode = VisualMode.Spectrum;
        int bands = 12;
        int? fps = null;
        List<string>? devices = null;

        foreach (string a in args)
        {
            string val = Val(a);
            switch (Key(a))
            {
                case "--source":
                    if (val is "mic" or "microphone" or "input") src = AudioSource.Microphone;
                    else if (val is "output" or "speaker" or "speakers" or "loopback") src = AudioSource.Output;
                    else { Console.Error.WriteLine($"Unknown --source '{val}' (use mic|output)"); return null; }
                    break;
                case "--mode":
                    if (val is "level" or "vu") mode = VisualMode.Level;
                    else if (val is "spectrum" or "fft") mode = VisualMode.Spectrum;
                    else { Console.Error.WriteLine($"Unknown --mode '{val}' (use level|spectrum)"); return null; }
                    break;
                case "--bands": bands = Math.Clamp(ParseInt(val, 12), 1, 64); break;
                case "--fps": fps = Math.Clamp(ParseInt(val, 30), 1, 120); break;
                case "--devices":
                    devices = val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    break;
                default:
                    Console.Error.WriteLine($"Unknown argument '{a}'");
                    return null;
            }
        }
        return new Options { Source = src, Mode = mode, Bands = bands, Fps = fps, DeviceFilter = devices };
    }

    private static string Key(string arg) { int i = arg.IndexOf('='); return i < 0 ? arg : arg[..i]; }
    private static string Val(string arg) { int i = arg.IndexOf('='); return i < 0 ? "" : arg[(i + 1)..]; }
    private static int ParseInt(string s, int dflt) => int.TryParse(s, out int v) ? v : dflt;

    public static void PrintUsage()
    {
        Console.WriteLine(@"RgbAudioReactive — audio-reactive RGB through the Register Broker

  --source=mic|output   react to the microphone, or the system output (loopback). default: output
  --mode=level|spectrum VU meter, or rainbow FFT visualiser.                      default: spectrum
  --bands=N             spectrum frequency bands (1-64).                          default: 12
  --fps=N               target frame rate; auto-capped to the broker rate limit.  default: auto
  --devices=a,b,c       restrict to these zone ids (default: every zone rgb.list reports)
  -h, --help            this help

Examples:
  RgbAudioReactive --source=output --mode=spectrum
  RgbAudioReactive --source=mic --mode=level --devices=ram0,ram1");
    }
}

internal static class ZoneExtensions
{
    public static string kindAndTransport(this BrokerClient.Zone z)
        => string.Join("/", new[] { z.Kind, z.Transport }.Where(s => !string.IsNullOrEmpty(s)));
}
