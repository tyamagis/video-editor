// BPM自動検出と波形ビート検出（設計プラン 4章・フェーズ2）。
// スペクトルフラックスでオンセット（音の立ち上がり）を検出し、
// 自己相関で一定BPMのテンポを推定する。拍時刻は検出したオンセットに
// 寄せて並べるため、わずかなテンポの揺れにも追従する。
// 検出はユーザーがボタンを押したときだけ走り、結果はファイルごとに
// キャッシュする（同じファイルの再検出は計算しない）。

using System.IO;
using NAudio.Wave;

namespace Editor.Audio;

/// <summary>BPM・ビート検出の結果。時刻はすべて素材内（ファイル先頭からの秒）。</summary>
public sealed class BeatDetectionResult
{
    public required string SourcePath { get; init; }

    /// <summary>検出できたか。できなければ FailureReason に理由が入る。</summary>
    public bool Detected { get; init; }

    public string? FailureReason { get; init; }

    public double? Bpm { get; init; }

    /// <summary>検出の確からしさ（0〜1）。1に近いほどテンポがはっきりしている。</summary>
    public double Confidence { get; init; }

    /// <summary>素材内での最初の拍の時刻（秒）。</summary>
    public double OffsetSeconds { get; init; }

    /// <summary>素材内の拍時刻列（オンセットに寄せたもの）。</summary>
    public IReadOnlyList<double> BeatTimes { get; init; } = Array.Empty<double>();

    public double DurationSeconds { get; init; }
}

/// <summary>音声からBPMと拍時刻を検出する。一定BPMの曲が対象。</summary>
public sealed class AudioBeatAnalyzer
{
    /// <summary>検出するBPMの範囲（60〜200）。</summary>
    private const double MinBpm = 60.0;
    private const double MaxBpm = 200.0;

    /// <summary>解析用のサンプルレート。元の音声をこの付近まで間引く。</summary>
    private const int TargetSampleRate = 22050;

    private const int FftSize = 1024;
    private const int HopSize = 256;

    /// <summary>検出に必要な最小の長さ（秒）。これより短いと拍が読めない。</summary>
    private const double MinDurationSeconds = 4.0;

    /// <summary>検出に必要な最小のオンセット数。</summary>
    private const int MinOnsets = 8;

    /// <summary>テンポがはっきりしているとみなす自己相関ピークの下限。初期値（実曲で調整する余地あり）。</summary>
    private const double MinAutocorrPeak = 0.08;

    private const int MaxCacheEntries = 32;

    private readonly FftAnalyzer _fft = new();
    private readonly Dictionary<string, CacheEntry> _cache = new();
    private readonly object _cacheLock = new();

    private sealed record CacheEntry(long Length, DateTime LastWriteTimeUtc, BeatDetectionResult Result);

    /// <summary>
    /// 音声ファイルを解析し、BPMと拍時刻を返す。同じファイル（サイズ・更新時刻が同じ）は
    /// キャッシュから返す。重い計算はバックグラウンドで行う。
    /// </summary>
    public async Task<BeatDetectionResult> AnalyzeAsync(string audioPath, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(audioPath))
            return Fail(audioPath, "音声ファイルが指定されていません。");

        string key;
        try
        {
            key = Path.GetFullPath(audioPath);
        }
        catch (Exception)
        {
            return Fail(audioPath, "音声ファイルのパスが正しくありません。");
        }

        FileInfo info;
        try
        {
            info = new FileInfo(key);
            if (!info.Exists)
                return Fail(audioPath, "音声ファイルが見つかりません。");
        }
        catch (Exception ex)
        {
            return Fail(audioPath, $"音声ファイルを確認できませんでした：{ex.Message}");
        }

        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var entry)
                && entry.Length == info.Length
                && entry.LastWriteTimeUtc == info.LastWriteTimeUtc)
                return entry.Result;
        }

        var result = await Task.Run(() => AnalyzeCore(key, ct), ct).ConfigureAwait(false);

        lock (_cacheLock)
        {
            if (_cache.Count >= MaxCacheEntries)
                _cache.Clear();
            _cache[key] = new CacheEntry(info.Length, info.LastWriteTimeUtc, result);
        }
        return result;
    }

    private static BeatDetectionResult Fail(string path, string reason) => new()
    {
        SourcePath = path,
        Detected = false,
        FailureReason = reason
    };

    private BeatDetectionResult AnalyzeCore(string path, CancellationToken ct)
    {
        float[] samples;
        int sampleRate;
        double duration;
        try
        {
            (samples, sampleRate) = ReadMonoDownsampled(path, ct);
            duration = (double)samples.Length / sampleRate;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail(path, $"音声を読み込めませんでした：{ex.Message}");
        }

        if (duration < MinDurationSeconds)
            return Fail(path, $"曲が短すぎます（{MinDurationSeconds:F0}秒以上必要です）。");

        // 無音チェック：最大振幅が小さすぎれば検出しない。
        var peak = 0f;
        foreach (var s in samples)
        {
            var a = Math.Abs(s);
            if (a > peak)
                peak = a;
        }
        if (peak < 0.01f)
            return Fail(path, "音が小さすぎます（無音に近いです）。");

        var frameRate = sampleRate / (double)HopSize; // フレーム/秒
        var flux = ComputeFlux(samples, sampleRate, ct);
        if (flux.Length == 0)
            return Fail(path, "音声が短すぎて解析できませんでした。");

        var onsets = PickOnsets(flux, frameRate);
        if (onsets.Count < MinOnsets)
            return Fail(path, "拍が検出できませんでした（音の立ち上がりが少なすぎます）。");

        var (period, peakValue) = EstimatePeriod(flux, frameRate);
        if (period <= 0 || peakValue < MinAutocorrPeak)
            return Fail(path, "拍がはっきりしませんでした（テンポが一定でない可能性があります）。");

        var bpm = 60.0 / period;
        var offset = FindBestPhase(flux, frameRate, period, duration);
        var beats = BuildBeatTimes(onsets, offset, period, duration);

        return new BeatDetectionResult
        {
            SourcePath = path,
            Detected = true,
            Bpm = bpm,
            // 自己相関ピーク 0.4 付近で信頼度 1 になるよう換算する（初期値）。
            Confidence = Math.Clamp(peakValue * 2.5, 0, 1),
            OffsetSeconds = offset,
            BeatTimes = beats,
            DurationSeconds = duration
        };
    }

    /// <summary>
    /// 音声をモノラル化し、解析用サンプルレート付近まで間引いて読む。
    /// 間引きは連続フレームの平均（簡易ローパス）で行う。
    /// </summary>
    private static (float[] Samples, int SampleRate) ReadMonoDownsampled(string path, CancellationToken ct)
    {
        using var reader = new AudioFileReader(path);
        var channels = reader.WaveFormat.Channels;
        var sampleRate = reader.WaveFormat.SampleRate;
        var factor = Math.Max(1, (int)Math.Round((double)sampleRate / TargetSampleRate));
        var outRate = sampleRate / factor;

        var samples = new List<float>();
        var buffer = new float[8192 * channels];
        double acc = 0;
        var accCount = 0;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            var frames = read / channels;
            for (var f = 0; f < frames; f++)
            {
                double mono = 0;
                for (var c = 0; c < channels; c++)
                    mono += buffer[f * channels + c];
                mono /= channels;
                acc += mono;
                accCount++;
                if (accCount == factor)
                {
                    samples.Add((float)(acc / factor));
                    acc = 0;
                    accCount = 0;
                }
            }
        }
        // 末尾の余り（1グループに満たない分）は捨てる。
        return (samples.ToArray(), outRate);
    }

    /// <summary>スペクトルフラックス（スペクトルの増加分だけを足したオンセット包絡）を計算する。</summary>
    private double[] ComputeFlux(float[] samples, int sampleRate, CancellationToken ct)
    {
        var frameCount = (samples.Length - FftSize) / HopSize + 1;
        if (frameCount <= 0)
            return Array.Empty<double>();

        var flux = new double[frameCount];
        var prevMag = new double[FftSize / 2];
        var window = new float[FftSize];
        for (var f = 0; f < frameCount; f++)
        {
            if ((f & 0x1FF) == 0)
                ct.ThrowIfCancellationRequested();

            Array.Copy(samples, f * HopSize, window, 0, FftSize);
            var frame = _fft.Analyze(window, sampleRate, f * HopSize / (double)sampleRate, FftSize);
            double sum = 0;
            for (var b = 0; b < frame.Magnitudes.Length; b++)
            {
                var diff = frame.Magnitudes[b] - prevMag[b];
                if (diff > 0)
                    sum += diff;
                prevMag[b] = frame.Magnitudes[b];
            }
            flux[f] = sum;
        }
        return flux;
    }

    /// <summary>
    /// フラックスの局所最大値からオンセット（音の立ち上がり）を拾う。
    /// しきい値は全体の中央値と最大値から決め、50ms以内の重複は強い方だけ残す。
    /// </summary>
    private static List<(double Time, double Strength)> PickOnsets(double[] flux, double frameRate)
    {
        var onsets = new List<(double Time, double Strength)>();
        if (flux.Length < 3)
            return onsets;

        var sorted = flux.OrderBy(x => x).ToArray();
        var median = sorted[sorted.Length / 2];
        var max = sorted[^1];
        var threshold = median + 0.25 * (max - median);

        const double minGap = 0.05; // 50ms
        for (var f = 1; f < flux.Length - 1; f++)
        {
            if (flux[f] <= threshold)
                continue;
            if (flux[f] < flux[f - 1] || flux[f] <= flux[f + 1])
                continue; // 局所最大でない（同値の連続は先頭だけ残す）。

            var candidate = (Time: f / frameRate, Strength: flux[f]);
            if (onsets.Count > 0 && candidate.Time - onsets[^1].Time < minGap)
            {
                // 近すぎる場合は強い方だけ残す。
                if (candidate.Strength > onsets[^1].Strength)
                    onsets[^1] = candidate;
                continue;
            }
            onsets.Add(candidate);
        }
        return onsets;
    }

    /// <summary>
    /// フラックスの自己相関から最も強い周期（拍の間隔）を求める。
    /// 60〜200 BPM の範囲で探す。半分のテンポへの誤り（2拍分を1拍とみなす）を防ぐため、
    /// 半分のラグがほぼ同じだけ強い場合はそちらを採用する。
    /// 戻り値は（周期・秒、自己相関ピーク（0〜1））。
    /// </summary>
    private static (double PeriodSeconds, double Peak) EstimatePeriod(double[] flux, double frameRate)
    {
        var n = flux.Length;
        var mean = flux.Average();
        var centered = new double[n];
        for (var i = 0; i < n; i++)
            centered[i] = flux[i] - mean;

        var norm0 = 0.0;
        foreach (var v in centered)
            norm0 += v * v;
        if (norm0 <= 1e-12)
            return (0, 0);

        var minLag = (int)Math.Ceiling(60.0 / MaxBpm * frameRate);
        var maxLag = (int)Math.Floor(60.0 / MinBpm * frameRate);
        maxLag = Math.Min(maxLag, n - 1);
        if (minLag < 1 || maxLag <= minLag)
            return (0, 0);

        var values = new double[maxLag + 1];
        var bestLag = minLag;
        var bestValue = double.NegativeInfinity;
        for (var lag = minLag; lag <= maxLag; lag++)
        {
            double sum = 0;
            for (var i = 0; i + lag < n; i++)
                sum += centered[i] * centered[i + lag];
            var value = sum / norm0;
            values[lag] = value;
            if (value > bestValue)
            {
                bestValue = value;
                bestLag = lag;
            }
        }

        var halfLag = bestLag / 2;
        if (halfLag >= minLag && values[halfLag] >= bestValue * 0.8)
        {
            bestLag = halfLag;
            bestValue = values[halfLag];
        }

        return (bestLag / frameRate, bestValue);
    }

    /// <summary>
    /// 拍の位相（最初の拍の時刻）を決める。0〜1周期を 5ms 刻みで試し、
    /// 予想される拍位置のフラックスの合計が最大の位相を選ぶ。
    /// </summary>
    private static double FindBestPhase(double[] flux, double frameRate, double period, double duration)
    {
        const double step = 0.005; // 5ms刻み
        var bestPhase = 0.0;
        var bestScore = double.NegativeInfinity;
        for (var phase = 0.0; phase < period; phase += step)
        {
            double score = 0;
            for (var t = phase; t <= duration; t += period)
            {
                var frame = (int)Math.Round(t * frameRate);
                // 前後1フレームの最大値を取る（フレーム刻みのずれを吸収する）。
                var local = 0.0;
                for (var f = frame - 1; f <= frame + 1; f++)
                {
                    if (f >= 0 && f < flux.Length && flux[f] > local)
                        local = flux[f];
                }
                score += local;
            }
            if (score > bestScore)
            {
                bestScore = score;
                bestPhase = phase;
            }
        }
        return bestPhase;
    }

    /// <summary>
    /// 拍時刻列を作る。等間隔の予想位置ごとに、±25%周期以内で最も近いオンセットが
    /// あればその時刻を使い、無ければ予想位置のままにする。
    /// </summary>
    private static List<double> BuildBeatTimes(
        List<(double Time, double Strength)> onsets, double offset, double period, double duration)
    {
        var beats = new List<double>();
        var window = period * 0.25;
        var onsetIndex = 0;
        for (var t = offset; t <= duration; t += period)
        {
            while (onsetIndex < onsets.Count && onsets[onsetIndex].Time < t - window)
                onsetIndex++;

            double? nearest = null;
            var bestDist = double.MaxValue;
            for (var i = onsetIndex; i < onsets.Count && onsets[i].Time <= t + window; i++)
            {
                var dist = Math.Abs(onsets[i].Time - t);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    nearest = onsets[i].Time;
                }
            }
            beats.Add(nearest ?? t);
        }
        return beats;
    }
}
