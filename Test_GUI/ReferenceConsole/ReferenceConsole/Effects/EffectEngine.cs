using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Broker.Client;

namespace ReferenceConsole.Effects;

/*---------------------------------------------------------------------------*\
| EffectEngine                                                               |
|                                                                            |
|   The console's render loop. Each connected RGB device can be assigned an  |
|   effect; when running, the loop renders every enabled device's per-LED    |
|   buffer each tick and streams it to the broker (rgb.set colors=[…]),      |
|   deduping unchanged frames to respect the control service's rate limit.   |
|   Sensor values (for the temperature effect) are refreshed on a slower     |
|   cadence; audio capture is started/stopped automatically.                 |
\*---------------------------------------------------------------------------*/
public sealed class EffectEngine
{
    private sealed class Slot
    {
        public required string DeviceId;
        public required int LedCount;
        public IEffect? Effect;
        public bool Enabled;
        public RgbColor[] Buffer = Array.Empty<RgbColor>();
        public RgbColor[]? LastPushed;
    }

    private readonly BrokerClient _control;
    private readonly BrokerClient _sensors;
    private readonly Dictionary<string, Slot> _slots = new();
    private readonly Dictionary<string, double> _sensorCache = new();
    private readonly object _gate = new();

    public AudioCapture Audio { get; } = new();
    public int Fps { get; set; } = 20;
    /// <summary>How often the sensor cache (for the Temperature effect) is refreshed — RGB-side, independent of the Sensors tab.</summary>
    public int SensorRefreshMs { get; set; } = 750;
    public bool Running { get; private set; }

    /// <summary>Raised after a device's frame is rendered (deviceId, colors). Loop thread — marshal to UI.</summary>
    public event Action<string, RgbColor[]>? FrameRendered;
    /// <summary>Raised on a push failure (deviceId, message). Loop thread.</summary>
    public event Action<string, string>? PushFailed;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    private readonly Stopwatch _appClock = Stopwatch.StartNew();

    public EffectEngine(BrokerClient control, BrokerClient sensors)
    {
        _control = control; _sensors = sensors;
    }

    public IEffect? GetEffect(string deviceId)
    {
        lock (_gate) { return _slots.TryGetValue(deviceId, out var s) ? s.Effect : null; }
    }

    /// <summary>Render and push a single frame for one device (manual paint / apply-once).</summary>
    public async Task<bool> RenderOnceAsync(string deviceId, CancellationToken ct = default)
    {
        Slot? slot;
        lock (_gate) { _slots.TryGetValue(deviceId, out slot); }
        if (slot?.Effect == null) return false;
        var ctx = new RenderContext(_appClock.Elapsed.TotalSeconds, 0, SensorLookup, Audio.Latest);
        slot.Effect.Render(in ctx, slot.Buffer);
        FrameRendered?.Invoke(deviceId, slot.Buffer);
        bool ok = await _control.RgbSetLedsAsync(deviceId, slot.Buffer, ct);
        if (ok) slot.LastPushed = (RgbColor[])slot.Buffer.Clone();
        return ok;
    }

    public void AssignEffect(string deviceId, int ledCount, IEffect? effect)
    {
        lock (_gate)
        {
            if (!_slots.TryGetValue(deviceId, out var s))
                _slots[deviceId] = s = new Slot { DeviceId = deviceId, LedCount = ledCount };
            s.LedCount = ledCount;
            s.Effect = effect;
            s.Buffer = new RgbColor[ledCount];
            s.LastPushed = null;
        }
    }

    public void SetEnabled(string deviceId, bool enabled)
    {
        lock (_gate) { if (_slots.TryGetValue(deviceId, out var s)) s.Enabled = enabled; }
    }

    public bool IsEnabled(string deviceId)
    {
        lock (_gate) { return _slots.TryGetValue(deviceId, out var s) && s.Enabled; }
    }

    public void Start()
    {
        if (Running) return;
        Running = true;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (!Running) return;
        Running = false;
        _cts?.Cancel();
        try { if (_loop != null) await _loop; } catch { /* cancellation */ }
        Audio.Stop();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        double last = 0;
        double nextSensor = 0;

        while (!ct.IsCancellationRequested)
        {
            double now = clock.Elapsed.TotalSeconds;
            double dt = now - last; last = now;

            // Slow-cadence sensor refresh for the temperature effect.
            if (now >= nextSensor)
            {
                nextSensor = now + Math.Max(0.05, SensorRefreshMs / 1000.0);
                _ = RefreshSensorsAsync();
            }

            // Auto-manage loopback capture based on whether any audio effect is live.
            bool needAudio = AnyAudioEnabled();
            if (needAudio && !Audio.Running) Audio.Start();
            else if (!needAudio && Audio.Running) Audio.Stop();

            var ctx = new RenderContext(now, dt, SensorLookup, Audio.Latest);

            // Snapshot work to do without holding the lock across awaits.
            List<Slot> active;
            lock (_gate)
            {
                active = new List<Slot>();
                foreach (var s in _slots.Values)
                    if (s.Enabled && s.Effect != null) active.Add(s);
            }

            foreach (var s in active)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    s.Effect!.Render(in ctx, s.Buffer);
                    FrameRendered?.Invoke(s.DeviceId, s.Buffer);
                    if (Changed(s.Buffer, s.LastPushed))
                    {
                        bool ok = await _control.RgbSetLedsAsync(s.DeviceId, s.Buffer, ct);
                        if (ok) s.LastPushed = (RgbColor[])s.Buffer.Clone();
                        else PushFailed?.Invoke(s.DeviceId, "rgb.set denied/failed");
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { PushFailed?.Invoke(s.DeviceId, ex.Message); }
            }

            int frameMs = Math.Max(5, 1000 / Math.Max(1, Fps));
            double spent = (clock.Elapsed.TotalSeconds - now) * 1000;
            int wait = Math.Max(1, frameMs - (int)spent);
            try { await Task.Delay(wait, ct); } catch { break; }
        }
    }

    private bool AnyAudioEnabled()
    {
        lock (_gate)
        {
            foreach (var s in _slots.Values)
                if (s.Enabled && s.Effect is AudioSpectrumEffect) return true;
        }
        return false;
    }

    private async Task RefreshSensorsAsync()
    {
        try
        {
            var list = await _sensors.SensorReadAllAsync();
            lock (_sensorCache)
            {
                _sensorCache.Clear();
                foreach (var s in list) if (s.Value is { } v) _sensorCache[s.Id] = v;
            }
        }
        catch { /* transient; keep last cache */ }
    }

    private double? SensorLookup(string id)
    {
        lock (_sensorCache) { return _sensorCache.TryGetValue(id, out var v) ? v : null; }
    }

    private static bool Changed(RgbColor[] a, RgbColor[]? b)
    {
        if (b == null || a.Length != b.Length) return true;
        for (int i = 0; i < a.Length; i++)
            if (a[i].R != b[i].R || a[i].G != b[i].G || a[i].B != b[i].B) return true;
        return false;
    }
}
