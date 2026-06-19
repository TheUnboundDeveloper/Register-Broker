using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ReferenceConsole.Effects;

/*---------------------------------------------------------------------------*\
| AudioCapture                                                               |
|                                                                            |
|   WASAPI loopback capture (whatever the system is playing) → a small FFT → |
|   16 normalized frequency bands + an overall level. The audio effect reads |
|   the latest AudioFrame each render. Raw (unsmoothed) bands are exposed so |
|   the effect owns the smoothing/gain knobs.                               |
|                                                                            |
|   Loopback needs no admin rights -- it's a normal user-session capture,    |
|   which keeps the whole console non-admin like the rest of the framework.  |
\*---------------------------------------------------------------------------*/
public sealed class AudioCapture : IDisposable
{
    private const int FftSize = 2048;     // power of two
    private const int Bands = 16;

    private WasapiLoopbackCapture? _capture;
    private readonly double[] _mono = new double[FftSize];   // ring of newest samples
    private int _writePos;
    private readonly object _lock = new();

    // Boxed so the reference swap is atomic; a struct field can't be volatile.
    private object _latestBox = AudioFrame.Silent;
    public AudioFrame Latest => (AudioFrame)System.Threading.Volatile.Read(ref _latestBox);
    public bool Running { get; private set; }
    public string? LastError { get; private set; }

    public void Start()
    {
        if (Running) return;
        try
        {
            _capture = new WasapiLoopbackCapture();   // default render device
            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += (_, e) => { LastError = e.Exception?.Message; Running = false; };
            _capture.StartRecording();
            Running = true;
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Running = false;
        }
    }

    public void Stop()
    {
        try { _capture?.StopRecording(); } catch { /* shutting down */ }
        _capture?.Dispose();
        _capture = null;
        Running = false;
        System.Threading.Volatile.Write(ref _latestBox, AudioFrame.Silent);
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var fmt = _capture!.WaveFormat;
        int ch = fmt.Channels;
        // Loopback is IEEE float 32-bit; mix channels to mono into the ring buffer.
        if (fmt.Encoding != WaveFormatEncoding.IeeeFloat || fmt.BitsPerSample != 32) return;

        int frames = e.BytesRecorded / (4 * ch);
        lock (_lock)
        {
            for (int f = 0; f < frames; f++)
            {
                double sum = 0;
                int baseByte = f * 4 * ch;
                for (int c = 0; c < ch; c++)
                    sum += BitConverter.ToSingle(e.Buffer, baseByte + c * 4);
                _mono[_writePos] = sum / ch;
                _writePos = (_writePos + 1) % FftSize;
            }
            Analyze();
        }
    }

    // --- FFT + banding (caller holds _lock) ---------------------------------
    private readonly double[] _re = new double[FftSize];
    private readonly double[] _im = new double[FftSize];

    private void Analyze()
    {
        // Copy ring (oldest→newest) and apply a Hann window.
        for (int i = 0; i < FftSize; i++)
        {
            int idx = (_writePos + i) % FftSize;
            double w = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftSize - 1)));
            _re[i] = _mono[idx] * w;
            _im[i] = 0;
        }
        Fft(_re, _im);

        var bands = new float[Bands];
        int half = FftSize / 2;
        // Log-spaced bins from ~bin 2 to half, so bass doesn't dominate one band.
        double minBin = 2, maxBin = half;
        for (int bnd = 0; bnd < Bands; bnd++)
        {
            double lo = minBin * Math.Pow(maxBin / minBin, bnd / (double)Bands);
            double hi = minBin * Math.Pow(maxBin / minBin, (bnd + 1) / (double)Bands);
            int a = (int)Math.Floor(lo), b = Math.Max(a + 1, (int)Math.Floor(hi));
            double sum = 0; int cnt = 0;
            for (int k = a; k < b && k < half; k++)
            {
                double mag = Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]) / half;
                sum += mag; cnt++;
            }
            double avg = cnt > 0 ? sum / cnt : 0;
            // Perceptual compression so quiet detail is visible; clamp to 0..1.
            bands[bnd] = (float)Math.Clamp(Math.Sqrt(avg) * 4.0, 0, 1);
        }

        double rms = 0;
        for (int i = 0; i < FftSize; i++) rms += _mono[i] * _mono[i];
        rms = Math.Sqrt(rms / FftSize);
        float level = (float)Math.Clamp(Math.Sqrt(rms) * 2.0, 0, 1);

        System.Threading.Volatile.Write(ref _latestBox, new AudioFrame(bands, level));
    }

    /// <summary>In-place iterative radix-2 Cooley–Tukey FFT. Length must be a power of two.</summary>
    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            double wlenRe = Math.Cos(ang), wlenIm = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double wRe = 1, wIm = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int u = i + k, v = i + k + len / 2;
                    double vRe = re[v] * wRe - im[v] * wIm;
                    double vIm = re[v] * wIm + im[v] * wRe;
                    re[v] = re[u] - vRe; im[v] = im[u] - vIm;
                    re[u] += vRe; im[u] += vIm;
                    double nwRe = wRe * wlenRe - wIm * wlenIm;
                    wIm = wRe * wlenIm + wIm * wlenRe; wRe = nwRe;
                }
            }
        }
    }

    public void Dispose() => Stop();
}
