using NAudio.Wave;
using NAudio.Dsp;

namespace DynamicIsland.Audio;

/// <summary>
/// 捕获系统音频输出，计算 FFT 频谱数据用于声波条可视化
/// </summary>
public sealed class SpectrumService : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private readonly int _barCount;
    private readonly float[] _spectrum;
    private readonly object _lock = new();
    private bool _isRunning;

    // FFT 参数
    private const int FFT_SIZE = 1024;
    private const int FFT_EXPONENT = 10;
    private readonly Complex[] _fftBuffer = new Complex[FFT_SIZE];
    private readonly float[] _sampleAccumulator = new float[FFT_SIZE];
    private int _samplePos;
    private int _channels = 2;

    public event Action<float[]>? SpectrumUpdated;

    public float[] Spectrum
    {
        get { lock (_lock) return (float[])_spectrum.Clone(); }
    }

    public bool IsRunning => _isRunning;

    public SpectrumService(int barCount = 8)
    {
        _barCount = barCount;
        _spectrum = new float[barCount];
    }

    public void Start()
    {
        if (_isRunning) return;

        try
        {
            _capture = new WasapiLoopbackCapture();
            _channels = _capture.WaveFormat.Channels;

            System.Diagnostics.Debug.WriteLine(
                $"[Spectrum] 格式: {_capture.WaveFormat.SampleRate}Hz, " +
                $"{_channels}ch, {_capture.WaveFormat.BitsPerSample}bit, " +
                $"编码:{_capture.WaveFormat.Encoding}");

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += (_, args) =>
            {
                _isRunning = false;
                if (args.Exception != null)
                    System.Diagnostics.Debug.WriteLine(
                        $"[Spectrum] 录制停止异常: {args.Exception.Message}");
            };

            _capture.StartRecording();
            _isRunning = true;
            System.Diagnostics.Debug.WriteLine("[Spectrum] 已启动");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Spectrum] 启动失败: {ex.Message}");
        }
    }

    public void Stop()
    {
        if (!_isRunning) return;
        try { _capture?.StopRecording(); } catch { }
        _isRunning = false;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;

        try
        {
            int bytesPerSample = _capture?.WaveFormat.BitsPerSample / 8 ?? 4;
            int totalSamples = e.BytesRecorded / bytesPerSample;
            int channels = Math.Max(1, _channels);
            int monoSamples = totalSamples / channels;

            for (int i = 0; i < monoSamples; i++)
            {
                float mono = 0;

                for (int ch = 0; ch < channels; ch++)
                {
                    int sampleIndex = i * channels + ch;
                    int byteOffset = sampleIndex * bytesPerSample;

                    if (byteOffset + bytesPerSample > e.BytesRecorded) break;

                    // IEEE Float 32bit
                    if (bytesPerSample == 4)
                        mono += BitConverter.ToSingle(e.Buffer, byteOffset);
                    // PCM 16bit
                    else if (bytesPerSample == 2)
                        mono += BitConverter.ToInt16(e.Buffer, byteOffset) / 32768f;
                }

                mono /= channels;
                _sampleAccumulator[_samplePos++] = mono;

                if (_samplePos >= FFT_SIZE)
                {
                    _samplePos = 0;
                    PerformFFT();
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Spectrum] 数据处理异常: {ex.Message}");
        }
    }

    private void PerformFFT()
    {
        for (int i = 0; i < FFT_SIZE; i++)
        {
            float window = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (FFT_SIZE - 1))));
            _fftBuffer[i].X = _sampleAccumulator[i] * window;
            _fftBuffer[i].Y = 0;
        }

        FastFourierTransform.FFT(true, FFT_EXPONENT, _fftBuffer);

        int sampleRate = _capture?.WaveFormat.SampleRate ?? 48000;
        double hzPerBin = (double)sampleRate / FFT_SIZE;
        int minBin = Math.Max(1, (int)(80.0 / hzPerBin));
        int maxBin = Math.Min(FFT_SIZE / 2, (int)(14000.0 / hzPerBin));
        int usableBins = maxBin - minBin;

        // ★ 每根 bar 的频率补偿：低频压低，高频提升，让视觉均匀
        float[] barGain = _barCount switch
        {
            8 => [0.6f, 0.8f, 1.0f, 1.2f, 1.6f, 2.2f, 3.0f, 4.0f],
            _ => Enumerable.Range(0, _barCount)
                    .Select(i => 0.6f + 3.4f * i / (_barCount - 1))
                    .ToArray()
        };

        lock (_lock)
        {
            for (int bar = 0; bar < _barCount; bar++)
            {
                // 线性分布，每根 bar 等宽频段
                int startBin = minBin + usableBins * bar / _barCount;
                int endBin = minBin + usableBins * (bar + 1) / _barCount;
                endBin = Math.Max(endBin, startBin + 1);

                float magnitude = 0;
                int count = 0;

                for (int bin = startBin; bin < endBin && bin < FFT_SIZE / 2; bin++)
                {
                    float re = _fftBuffer[bin].X;
                    float im = _fftBuffer[bin].Y;
                    magnitude += MathF.Sqrt(re * re + im * im);
                    count++;
                }

                if (count > 0) magnitude /= count;

                // 应用频率补偿
                magnitude *= barGain[bar];

                // dB 缩放
                float db = magnitude > 0.00001f
                    ? 20f * MathF.Log10(magnitude) + 80f
                    : 0f;

                float scaled = Math.Clamp(db / 40f, 0f, 1f);

                // 平滑：快升慢降
                _spectrum[bar] = scaled > _spectrum[bar]
                    ? scaled * 0.65f + _spectrum[bar] * 0.35f
                    : _spectrum[bar] * 0.82f + scaled * 0.18f;
            }
        }

        SpectrumUpdated?.Invoke(Spectrum);
    }

    public void Dispose()
    {
        Stop();
        _capture?.Dispose();
    }
}
