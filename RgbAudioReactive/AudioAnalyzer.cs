using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace RgbAudioReactive;

/*---------------------------------------------------------------------------*\
| AudioAnalyzer                                                              |
|                                                                            |
|   Captures audio via WASAPI and continuously distills it into a small       |
|   AnalysisState (an overall loudness level + a handful of frequency bands).  |
|                                                                            |
|   The ONLY difference between "react to the microphone" and "react to what  |
|   is playing on the speakers" is the endpoint we open:                       |
|     • AudioSource.Microphone -> WasapiCapture on the default CAPTURE device  |
|     • AudioSource.Output     -> WasapiLoopbackCapture on the default RENDER  |
|                                 device (loopback = a copy of the mix going    |
|                                 out the speakers/headphones)                  |
|   Everything downstream (FFT, banding, smoothing) is identical.              |
\*---------------------------------------------------------------------------*/
internal enum AudioSource { Microphone, Output }

/// Immutable snapshot the render loop consumes. Level and Bands are 0..1 (post-fill-fix,
/// post-smoothing); Bands[0] is the lowest frequency band (bass), last is the highest.
internal sealed record AnalysisState(float Level, float[] Bands);

internal sealed class AudioAnalyzer : IDisposable
{
    private const int FftSize = 2048;            // power of two; ~43 ms @ 48 kHz
    private readonly int _bandCount;
    private readonly object _gate = new();

    private IWaveIn? _capture;
    private readonly float[] _window;            // sample ring filling toward an FFT frame
    private int _windowFill;
    private readonly int _fftExp;

    // smoothed, published state (read by the render loop)
    private float _level;
    private readonly float[] _bands;

    // The light-fill fix. NOT an auto-gain that divides by its own recent peak — that
    // collapses dynamic range, so soft music reads as "full" and the lights stay maxed.
    // Instead: a fixed gain, then a ceiling that floors at 1.0 (so it only ever attenuates
    // genuinely-loud signals and never amplifies quiet ones up to full) and decays slowly,
    // plus a noise gate. Quiet stays quiet.
    private readonly float _levelGain;
    private readonly float _noiseGate;
    private float _ceilLevel = 1f;
    private float _ceilGlobal = 1e-5f;     // one shared ceiling for the spectrum (spectral shape)

    public AudioAnalyzer(int bandCount, float gain = 1f, float gate = 0.04f)
    {
        _bandCount = Math.Max(1, bandCount);
        _window = new float[FftSize];
        _bands = new float[_bandCount];
        _levelGain = 5f * gain;
        _noiseGate = Math.Clamp(gate, 0f, 0.95f);
        _fftExp = (int)Math.Log2(FftSize);
    }

    /// LEVEL normaliser (the light-fill fix): fixed gain + a slow ceiling floored at 1.0
    /// (never amplifies quiet up to full) + noise gate. Keeps overall loudness honest.
    private float Normalize(float mag, float gain, ref float ceil)
    {
        float scaled = mag * gain;
        ceil = Math.Max(ceil * 0.9985f, Math.Max(scaled, 1f));   // ~20 s release, floored at 1
        float norm = scaled / ceil;                              // <= 1
        float gated = Math.Clamp((norm - _noiseGate) / (1f - _noiseGate), 0f, 1f);
        return MathF.Pow(gated, 0.6f);                           // gentle gamma
    }

    public void Start(AudioSource source)
    {
        var capture = source == AudioSource.Output
            ? new WasapiLoopbackCapture()                                    // system OUTPUT
            : (IWaveIn)new WasapiCapture(DefaultCaptureDevice());            // MICROPHONE
        capture.DataAvailable += OnData;
        _capture = capture;
        capture.StartRecording();
    }

    /// The current best snapshot — cheap, lock-guarded copy for the render loop.
    public AnalysisState Snapshot()
    {
        lock (_gate)
            return new AnalysisState(_level, (float[])_bands.Clone());
    }

    private static MMDevice DefaultCaptureDevice()
        => new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);

    private void OnData(object? sender, WaveInEventArgs e)
    {
        WaveFormat fmt = _capture!.WaveFormat;
        int channels = fmt.Channels;
        if (channels <= 0) return;

        // The WASAPI shared mix format is reported as Encoding=Extensible on some devices,
        // not IeeeFloat — so key off bit depth: 32-bit => IEEE float (the normal case),
        // 16-bit => PCM.
        if (fmt.BitsPerSample == 32)
        {
            int frames = e.BytesRecorded / (4 * channels);
            for (int i = 0; i < frames; i++)
            {
                float mono = 0f;
                for (int c = 0; c < channels; c++)
                    mono += BitConverter.ToSingle(e.Buffer, (i * channels + c) * 4);
                Push(mono / channels);
            }
        }
        else if (fmt.BitsPerSample == 16)
        {
            int frames = e.BytesRecorded / (2 * channels);
            for (int i = 0; i < frames; i++)
            {
                float mono = 0f;
                for (int c = 0; c < channels; c++)
                    mono += BitConverter.ToInt16(e.Buffer, (i * channels + c) * 2) / 32768f;
                Push(mono / channels);
            }
        }
    }

    private void Push(float sample)
    {
        _window[_windowFill++] = sample;
        if (_windowFill < FftSize) return;
        Analyze();
        _windowFill = 0;
    }

    private void Analyze()
    {
        // --- overall loudness: RMS of the frame, normalised to 0..1 ---
        double sumSq = 0;
        for (int i = 0; i < FftSize; i++) sumSq += _window[i] * (double)_window[i];
        float rms = (float)Math.Sqrt(sumSq / FftSize);

        // --- spectrum: Hann-windowed FFT, magnitudes grouped into log-spaced bands ---
        var fft = new Complex[FftSize];
        for (int i = 0; i < FftSize; i++)
        {
            float w = (float)FastFourierTransform.HannWindow(i, FftSize);
            fft[i].X = _window[i] * w;
            fft[i].Y = 0;
        }
        FastFourierTransform.FFT(true, _fftExp, fft);

        int bins = FftSize / 2;
        var raw = new float[_bandCount];
        // log-spaced edges so bass gets its own bands instead of being swamped by treble
        for (int b = 0; b < _bandCount; b++)
        {
            int lo = (int)(bins * Math.Pow((double)b / _bandCount, 2.0));
            int hi = (int)(bins * Math.Pow((double)(b + 1) / _bandCount, 2.0));
            hi = Math.Min(Math.Max(hi, lo + 1), bins);
            float peak = 0f;
            for (int k = lo; k < hi; k++)
            {
                float mag = (float)Math.Sqrt(fft[k].X * fft[k].X + fft[k].Y * fft[k].Y);
                if (mag > peak) peak = mag;
            }
            raw[b] = peak;
        }

        float globalPeak = 0f;
        for (int b = 0; b < _bandCount; b++) if (raw[b] > globalPeak) globalPeak = raw[b];

        lock (_gate)
        {
            // LEVEL: fixed-gain fill fix (the part that already works).
            float lvl = Normalize(rms, _levelGain, ref _ceilLevel);
            _level = lvl > _level ? lvl : _level * 0.85f + lvl * 0.15f;

            // SPECTRUM: ONE shared ceiling across all bands gives the spectral SHAPE (loud
            // bands tall, quiet bands short), and brightness is gated by overall loudness
            // (_level — the exact signal level-mode uses, which we know drives the lights),
            // so silence is dark and the lights track real audio. Scale-invariant: a ratio.
            _ceilGlobal = Math.Max(_ceilGlobal * 0.999f, Math.Max(globalPeak, 1e-5f));
            // gate by loudness with headroom (0.70), so even the loud peaks sit below max
            // instead of pinning — calmer overall.
            float loudGate = Math.Clamp(_level * 0.70f, 0f, 1f);
            for (int b = 0; b < _bandCount; b++)
            {
                float t = Math.Clamp(raw[b] / _ceilGlobal, 0f, 1f) * loudGate;
                _bands[b] = t > _bands[b] ? t : _bands[b] * 0.80f + t * 0.20f;
            }
        }
    }

    public void Dispose()
    {
        if (_capture is not null)
        {
            try { _capture.StopRecording(); } catch { /* device may already be gone */ }
            _capture.Dispose();
            _capture = null;
        }
    }
}
