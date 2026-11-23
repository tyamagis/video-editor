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
/// Core の IAudioBandEnergyProvider の実装。フェーズ2のバインドと可視化が使う。
/// 音声ファイル1つにつき FFT フレーム列（帯域エネルギー用）と間引きステレオ波形
/// （リサジュー用）を1回だけ解析し、キャッシュする。同じファイルを使うクリップは
/// 解析結果を共有する（クリップId → ファイルパスの対応だけ持つ）。
/// 解析結果は公開後は不変なので、GetBandEnergy などの読み取りはスレッドセーフ
/// （プレビューと書き出しが別スレッドから読んでも同じ値を返す）。
/// </summary>
public sealed class AudioBandEnergyProvider : Editor.Core.IAudioBandEnergyProvider
{
    /// <summary>帯域エネルギーの解析フレームレートの上限。30Hz で十分な時間分解能がある。</summary>
    private const int MaxAnalysisFrameRate = 30;

    /// <summary>FFTサイズ（設計プラン 4章の初期値 2048）。</summary>
    private const int FftSize = 2048;

    /// <summary>リサジュー用ステレオ波形の間引き後のサンプルレート（Hz/チャンネル）。</summary>
    private const int StereoSampleRate = 4000;

    /// <summary>帯域エネルギーの平滑化に使うフレーム数（因果移動平均。決定的で、プレビューと書き出しが一致する）。</summary>
    private const int SmoothingFrames = 3;

    private readonly FftAnalyzer _analyzer = new();

    /// <summary>ファイル1つ分の解析結果。公開後は不変。</summary>
    private sealed class Analysis
    {
        public required string SourcePath { get; init; }
        public required long Length { get; init; }
        public required DateTime LastWriteTimeUtc { get; init; }
        public required int SampleRate { get; init; }
        public required double FrameRate { get; init; }
        public required double DurationSeconds { get; init; }

        /// <summary>フレームごとの FFT 振幅（ビン数 = FftSize/2）。</summary>
        public required List<float[]> Magnitudes { get; init; }

        /// <summary>間引き後のステレオ波形（StereoRate Hz）。モノラルは左右同じ値。</summary>
        public required float[] StereoLeft { get; init; }
        public required float[] StereoRight { get; init; }

        /// <summary>ステレオ波形の実際のサンプルレート（間引き係数の丸めで 4000 とは少し違う）。</summary>
        public required int StereoRate { get; init; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Analysis> _byPath = new();
    private readonly Dictionary<string, string> _clipToPath = new();

    /// <summary>指定クリップの音声を解析済みか（バインド・可視化が使えるか）。</summary>
    public bool IsAnalyzed(string clipId)
    {
        lock (_gate)
        {
            return _clipToPath.TryGetValue(clipId, out var path) && _byPath.ContainsKey(path);
        }
    }

    public void Precompute(string clipId, string sourcePath, double frameRate)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("音声ファイルが見つかりません。", sourcePath);

        var info = new FileInfo(sourcePath);
        lock (_gate)
        {
            // 同じファイル（サイズ・更新日時が同じ）なら解析し直さない。
            if (_byPath.TryGetValue(sourcePath, out var existing)
                && existing.Length == info.Length
                && existing.LastWriteTimeUtc == info.LastWriteTimeUtc)
            {
                _clipToPath[clipId] = sourcePath;
                return;
            }
        }

        var analysis = AnalyzeFile(sourcePath, info, frameRate);

        lock (_gate)
        {
            _byPath[sourcePath] = analysis;
            _clipToPath[clipId] = sourcePath;
        }
    }

    /// <summary>ファイル全体を解析する（FFT フレーム列＋間引きステレオ波形）。重い処理。</summary>
    private Analysis AnalyzeFile(string sourcePath, FileInfo info, double frameRate)
    {
        var analysisRate = Math.Clamp(frameRate, 1, MaxAnalysisFrameRate);
        var magnitudes = ComputeMagnitudeFrames(sourcePath, analysisRate, out var sampleRate, out var duration);
        var (left, right, stereoRate) = ReadStereoDownsampled(sourcePath);
        return new Analysis
        {
            SourcePath = sourcePath,
            Length = info.Length,
            LastWriteTimeUtc = info.LastWriteTimeUtc,
            SampleRate = sampleRate,
            FrameRate = analysisRate,
            DurationSeconds = duration,
            Magnitudes = magnitudes,
            StereoLeft = left,
            StereoRight = right,
            StereoRate = stereoRate
        };
    }

    /// <summary>映像フレームごとの FFT 振幅を作る（既存の Precompute と同じ窓読み）。</summary>
    private List<float[]> ComputeMagnitudeFrames(
        string sourcePath, double frameRate, out int sampleRate, out double duration)
    {
        using var reader = new AudioFileReader(sourcePath);
        var channels = reader.WaveFormat.Channels;
        sampleRate = reader.WaveFormat.SampleRate;
        duration = reader.TotalTime.TotalSeconds;
        var frameCount = Math.Max(1, (int)Math.Ceiling(duration * frameRate));

        var frames = new List<float[]>(frameCount);
        var window = new float[FftSize * channels];
        for (var f = 0; f < frameCount; f++)
        {
            var centerTime = (f + 0.5) / frameRate;
            var startSample = (long)((centerTime * sampleRate - FftSize / 2) * channels);
            if (startSample < 0)
                startSample = 0;

            // AudioFileReader はバイト単位でシークできる。
            reader.Position = startSample * 4;
            var read = reader.Read(window, 0, window.Length);

            var mono = new float[FftSize];
            var readFrames = read / channels;
            for (var i = 0; i < readFrames && i < FftSize; i++)
            {
                float sum = 0;
                for (var c = 0; c < channels; c++)
                    sum += window[i * channels + c];
                mono[i] = sum / channels;
            }

            var spectrum = _analyzer.Analyze(mono, sampleRate, centerTime, FftSize);
            var magnitudes = new float[spectrum.Magnitudes.Length];
            for (var i = 0; i < magnitudes.Length; i++)
                magnitudes[i] = (float)spectrum.Magnitudes[i];
            frames.Add(magnitudes);
        }
        return frames;
    }

    /// <summary>
    /// 音声を先頭から順に読み、ステレオ波形を間引いて保持する（リサジュー用）。
    /// モノラルは左右に同じ値を入れる。戻り値は（左、右、実際の出力レート）。
    /// </summary>
    private static (float[] Left, float[] Right, int Rate) ReadStereoDownsampled(string path)
    {
        using var reader = new AudioFileReader(path);
        var channels = reader.WaveFormat.Channels;
        var sampleRate = reader.WaveFormat.SampleRate;
        var factor = Math.Max(1, (int)Math.Round((double)sampleRate / StereoSampleRate));
        var outRate = sampleRate / factor;

        var left = new List<float>();
        var right = new List<float>();
        var buffer = new float[8192 * channels];
        double accL = 0, accR = 0;
        var accCount = 0;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            var frames = read / channels;
            for (var f = 0; f < frames; f++)
            {
                float l, r;
                if (channels >= 2)
                {
                    l = buffer[f * channels];
                    r = buffer[f * channels + 1];
                }
                else
                {
                    l = r = buffer[f * channels];
                }
                accL += l;
                accR += r;
                accCount++;
                if (accCount == factor)
                {
                    left.Add((float)(accL / factor));
                    right.Add((float)(accR / factor));
                    accL = 0;
                    accR = 0;
                    accCount = 0;
                }
            }
        }
        return (left.ToArray(), right.ToArray(), outRate);
    }

    private Analysis? FindAnalysis(string clipId)
    {
        lock (_gate)
        {
            return _clipToPath.TryGetValue(clipId, out var path)
                && _byPath.TryGetValue(path, out var analysis)
                ? analysis
                : null;
        }
    }

    public double GetBandEnergy(string clipId, double timeSeconds, double lowHz, double highHz)
    {
        var analysis = FindAnalysis(clipId);
        if (analysis is null || analysis.Magnitudes.Count == 0)
            return 0;

        var index = (int)Math.Floor(timeSeconds * analysis.FrameRate);
        index = Math.Clamp(index, 0, analysis.Magnitudes.Count - 1);

        // 決定的な平滑化：直近 SmoothingFrames フレームの移動平均。
        // 呼び出し順に依存しないため、プレビューと書き出しで同じ値になる。
        double sum = 0;
        var count = 0;
        for (var i = Math.Max(0, index - SmoothingFrames + 1); i <= index; i++)
        {
            sum += RawBandEnergy(analysis.Magnitudes[i], analysis.SampleRate, lowHz, highHz);
            count++;
        }
        return count == 0 ? 0 : sum / count;
    }

    /// <summary>
    /// 指定時刻のスペクトルを対数間隔の帯域に分けて返す（各値 0.0〜1.0）。
    /// そのフレームで最も強い帯域を 1.0 とし、平方根で見た目を広げる。
    /// </summary>
    public double[] GetSpectrumBands(
        string clipId, double timeSeconds, int bandCount, double lowHz, double highHz)
    {
        var result = new double[Math.Max(1, bandCount)];
        var analysis = FindAnalysis(clipId);
        if (analysis is null || analysis.Magnitudes.Count == 0)
            return result;

        var index = (int)Math.Floor(timeSeconds * analysis.FrameRate);
        index = Math.Clamp(index, 0, analysis.Magnitudes.Count - 1);
        var magnitudes = analysis.Magnitudes[index];

        // 対数間隔の帯域境界（Hz）。
        var edges = new double[result.Length + 1];
        var low = Math.Max(1, lowHz);
        var high = Math.Max(low + 1, highHz);
        var ratio = Math.Pow(high / low, 1.0 / result.Length);
        for (var b = 0; b <= result.Length; b++)
            edges[b] = low * Math.Pow(ratio, b);

        var binHz = (double)analysis.SampleRate / FftSize;
        double max = 0;
        for (var b = 0; b < result.Length; b++)
        {
            var firstBin = Math.Max(0, (int)Math.Ceiling(edges[b] / binHz));
            var lastBin = Math.Min(magnitudes.Length - 1, (int)Math.Floor(edges[b + 1] / binHz));
            double bandSum = 0;
            for (var i = firstBin; i <= lastBin; i++)
                bandSum += magnitudes[i];
            result[b] = bandSum;
            if (bandSum > max)
                max = bandSum;
        }

        if (max > 1e-12)
        {
            for (var b = 0; b < result.Length; b++)
                result[b] = Math.Sqrt(result[b] / max);
        }
        return result;
    }

    /// <summary>
    /// 指定時刻の直前 windowSeconds 分のステレオ波形を返す（リサジュー用）。
    /// 解析されていない・窓に標本が無ければ空の配列を返す。
    /// </summary>
    public (float[] Left, float[] Right) GetStereoWindow(
        string clipId, double timeSeconds, double windowSeconds)
    {
        var analysis = FindAnalysis(clipId);
        if (analysis is null || analysis.StereoLeft.Length == 0)
            return (Array.Empty<float>(), Array.Empty<float>());

        var end = (int)Math.Floor(timeSeconds * analysis.StereoRate);
        var start = (int)Math.Floor((timeSeconds - Math.Max(0, windowSeconds)) * analysis.StereoRate);
        end = Math.Clamp(end, 0, analysis.StereoLeft.Length);
        start = Math.Clamp(start, 0, end);
        var count = end - start;
        if (count <= 0)
            return (Array.Empty<float>(), Array.Empty<float>());

        var left = new float[count];
        var right = new float[count];
        Array.Copy(analysis.StereoLeft, start, left, 0, count);
        Array.Copy(analysis.StereoRight, start, right, 0, count);
        return (left, right);
    }

    /// <summary>1フレーム分の帯域エネルギー（平滑化なし）。ビン周波数から直接計算する。</summary>
    private static double RawBandEnergy(float[] magnitudes, int sampleRate, double lowHz, double highHz)
    {
        var binHz = (double)sampleRate / FftSize;
        var firstBin = Math.Max(0, (int)Math.Ceiling(lowHz / binHz));
        var lastBin = Math.Min(magnitudes.Length - 1, (int)Math.Floor(highHz / binHz));
        double total = 0, band = 0;
        for (var i = 0; i < magnitudes.Length; i++)
        {
            var magnitude = magnitudes[i];
            total += magnitude;
            if (i >= firstBin && i <= lastBin)
                band += magnitude;
        }
        return total <= 1e-12 ? 0 : band / total;
    }
}
