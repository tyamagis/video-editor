// FFT解析と周波数帯エネルギー（設計プラン 4章）。
// ライブラリ（MathNet.Numerics）を使う。独自実装は、ライブラリで
// 実現できないことが出てきた場合に検討する（決定事項）。
// 解析は事前にフレームごと行い、キャッシュする。再生中に都度FFTはしない。

using System.IO;
using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;
using NAudio.Wave;

namespace Editor.Audio;

/// <summary>1フレーム分のスペクトル。FFTサイズの半分のビンを持つ。</summary>
public sealed class SpectrumFrame
{
    public required double TimeSeconds { get; init; }
    public required double[] Frequencies { get; init; }
    public required double[] Magnitudes { get; init; }
}

/// <summary>MathNet.Numerics によるFFTと帯域エネルギーの計算。</summary>
public sealed class FftAnalyzer
{
    /// <summary>
    /// サンプル列にハニング窓を掛けてFFTし、スペクトルを返す。
    /// fftSize の既定は2048（設計プラン 4章の初期値）。
    /// </summary>
    public SpectrumFrame Analyze(float[] samples, int sampleRate, double timeSeconds, int fftSize = 2048)
    {
        if (fftSize <= 0 || (fftSize & (fftSize - 1)) != 0)
            throw new ArgumentException("FFTサイズは2の累乗にしてください。", nameof(fftSize));

        var data = new Complex32[fftSize];
        var count = Math.Min(samples.Length, fftSize);
        for (var i = 0; i < count; i++)
        {
            // ハニング窓で両端の不連続を抑える。
            var window = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (fftSize - 1)));
            data[i] = new Complex32((float)(samples[i] * window), 0);
        }

        Fourier.Forward(data, FourierOptions.Matlab);

        var half = fftSize / 2;
        var frequencies = new double[half];
        var magnitudes = new double[half];
        for (var i = 0; i < half; i++)
        {
            frequencies[i] = (double)i * sampleRate / fftSize;
            magnitudes[i] = data[i].Magnitude;
        }

        return new SpectrumFrame
        {
            TimeSeconds = timeSeconds,
            Frequencies = frequencies,
            Magnitudes = magnitudes
        };
    }

    /// <summary>指定帯域のエネルギーを 0.0〜1.0 に正規化して返す。</summary>
    public static double BandEnergy(SpectrumFrame frame, double lowHz, double highHz)
    {
        var total = 0.0;
        var band = 0.0;
        for (var i = 0; i < frame.Frequencies.Length; i++)
        {
            var magnitude = frame.Magnitudes[i];
            total += magnitude;
            if (frame.Frequencies[i] >= lowHz && frame.Frequencies[i] <= highHz)
                band += magnitude;
        }
        return total <= 1e-12 ? 0 : band / total;
    }
}

/// <summary>
/// Core の IAudioBandEnergyProvider の実装。フェーズ2のバインドが使う。
/// Precompute で映像フレームごとにスペクトルを作り、GetBandEnergy では
/// 最も近いフレームの値をスムージングして返す。
/// </summary>
public sealed class AudioBandEnergyProvider : Editor.Core.IAudioBandEnergyProvider
{
    private readonly FftAnalyzer _analyzer = new();
    private readonly Dictionary<string, List<SpectrumFrame>> _framesByClip = new();
    private readonly Dictionary<string, double> _frameRateByClip = new();
    private readonly Dictionary<string, double> _smoothed = new();

    public void Precompute(string clipId, string sourcePath, double frameRate)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("音声ファイルが見つかりません。", sourcePath);

        const int fftSize = 2048;
        using var reader = new AudioFileReader(sourcePath);
        var channels = reader.WaveFormat.Channels;
        var sampleRate = reader.WaveFormat.SampleRate;
        var duration = reader.TotalTime.TotalSeconds;
        var frameCount = Math.Max(1, (int)Math.Ceiling(duration * frameRate));

        var frames = new List<SpectrumFrame>(frameCount);
        var window = new float[fftSize * channels];
        for (var f = 0; f < frameCount; f++)
        {
            var centerTime = (f + 0.5) / frameRate;
            var startSample = (long)((centerTime * sampleRate - fftSize / 2) * channels);
            if (startSample < 0)
                startSample = 0;

            // AudioFileReader はバイト単位でシークできる。
            reader.Position = startSample * 4;
            var read = reader.Read(window, 0, window.Length);

            var mono = new float[fftSize];
            var readFrames = read / channels;
            for (var i = 0; i < readFrames && i < fftSize; i++)
            {
                float sum = 0;
                for (var c = 0; c < channels; c++)
                    sum += window[i * channels + c];
                mono[i] = sum / channels;
            }

            frames.Add(_analyzer.Analyze(mono, sampleRate, centerTime, fftSize));
        }

        _framesByClip[clipId] = frames;
        _frameRateByClip[clipId] = frameRate;
    }

    public double GetBandEnergy(string clipId, double timeSeconds, double lowHz, double highHz)
    {
        if (!_framesByClip.TryGetValue(clipId, out var frames) || frames.Count == 0)
            return 0;

        var frameRate = _frameRateByClip[clipId];
        var index = (int)Math.Floor(timeSeconds * frameRate);
        index = Math.Clamp(index, 0, frames.Count - 1);

        var target = FftAnalyzer.BandEnergy(frames[index], lowHz, highHz);

        // 簡易スムージング（ワンポール）。フェーズ2でアタック／リリースに置き換える。
        var key = $"{clipId}:{lowHz}-{highHz}";
        var previous = _smoothed.TryGetValue(key, out var prev) ? prev : target;
        var smoothed = previous + (target - previous) * 0.35;
        _smoothed[key] = smoothed;
        return smoothed;
    }
}
