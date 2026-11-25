// ダッキング用：音声が「鳴っている区間」の解析（IAudioActivityProvider の実装）。
// 無音検出（SilenceDetector）と同じ 10ms RMS 包絡を使い、しきい値以上の区間を返す。
// 無音検出との違いは目的だけ（あちらは切る無音区間、こちらは鳴っている区間）で、
// 包絡の取り方・窓・モノラル化はそろえてある。
// ナレーションの子音・息継ぎで包絡が細かく上下するとダッキングがポンピングする
// ため、0.15 秒以下の途切れはつなぎ、0.03 秒未満の単発は落とす。
// 結果はメモリにキャッシュする（素材の身元＋使用範囲＋しきい値が鍵。
// PeakAnalyzer と同じ流儀）。

using System.IO;
using Editor.Core;
using NAudio.Wave;

namespace Editor.Audio;

/// <summary>クリップ使用範囲の「鳴っている区間」を測る。結果はメモリにキャッシュする。</summary>
public sealed class AudioActivityAnalyzer : IAudioActivityProvider
{
    /// <summary>振幅包絡の窓の長さ（秒）。無音検出と同じ 10ms。</summary>
    private const double WindowSeconds = 0.01;

    /// <summary>この長さ（秒）以下の途切れは区間をつなぐ（息継ぎ・子音でのポンピング防止）。</summary>
    internal const double MaxGapSeconds = 0.15;

    /// <summary>この長さ（秒）未満の単発の区間は落とす（クリックノイズ等）。</summary>
    internal const double MinActiveSeconds = 0.03;

    private readonly object _gate = new();
    private readonly Dictionary<string, IReadOnlyList<AudioSegment>> _results = new();
    private readonly Dictionary<string, Task<IReadOnlyList<AudioSegment>>> _inFlight = new();

    public Task<IReadOnlyList<AudioSegment>> GetActiveSegmentsAsync(
        string sourcePath, double inPointSeconds, double durationSeconds, double thresholdDb,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(sourcePath) || durationSeconds <= 0)
            return Task.FromResult<IReadOnlyList<AudioSegment>>(Array.Empty<AudioSegment>());

        string key;
        try
        {
            var full = Path.GetFullPath(sourcePath);
            var info = new FileInfo(full);
            if (!info.Exists)
                return Task.FromResult<IReadOnlyList<AudioSegment>>(Array.Empty<AudioSegment>());
            key = $"{full}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{inPointSeconds:R}|{durationSeconds:R}|{thresholdDb:R}";
        }
        catch
        {
            return Task.FromResult<IReadOnlyList<AudioSegment>>(Array.Empty<AudioSegment>());
        }

        lock (_gate)
        {
            if (_results.TryGetValue(key, out var cached))
                return Task.FromResult(cached);
            if (_inFlight.TryGetValue(key, out var running))
                return running;

            var task = Task.Run(() =>
            {
                try
                {
                    var result = AnalyzeCore(sourcePath, inPointSeconds, durationSeconds, thresholdDb, ct);
                    lock (_gate) { _results[key] = result; }
                    return result;
                }
                finally
                {
                    lock (_gate) { _inFlight.Remove(key); }
                }
            }, ct);
            _inFlight[key] = task;
            return task;
        }
    }

    private static IReadOnlyList<AudioSegment> AnalyzeCore(
        string path, double inPointSeconds, double durationSeconds, double thresholdDb, CancellationToken ct)
    {
        try
        {
            var rms = ReadEnvelope(path, inPointSeconds, durationSeconds, ct);
            var threshold = Math.Pow(10, AudioGainEnvelope.ClampThresholdDb(thresholdDb) / 20.0);
            return FindActiveSegments(rms, durationSeconds, threshold);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 解析に失敗しても再生・書き出しは止めない（ダッキング無しで進む）。
            return Array.Empty<AudioSegment>();
        }
    }

    /// <summary>
    /// 指定範囲の音声をモノラル化し、10ms ごとの RMS を返す
    /// （SilenceDetector.ReadEnvelope と同じ方法）。
    /// </summary>
    private static double[] ReadEnvelope(
        string path, double inPointSeconds, double durationSeconds, CancellationToken ct)
    {
        using var reader = new AudioFileReader(path);
        var channels = reader.WaveFormat.Channels;
        var sampleRate = reader.WaveFormat.SampleRate;
        if (sampleRate <= 0 || channels <= 0)
            throw new InvalidOperationException("音声の形式が読めませんでした。");

        var samplesPerWindow = Math.Max(1, (int)Math.Round(sampleRate * WindowSeconds));
        var targetFrames = (long)Math.Ceiling(durationSeconds * sampleRate);
        var targetFrame = Math.Max(0, (long)(inPointSeconds * sampleRate));
        reader.Position = Math.Min(targetFrame * channels * 4, reader.Length);

        var envelope = new List<double>();
        var buffer = new float[8192 * channels];
        double sumSq = 0;
        var windowCount = 0;
        long processedFrames = 0;
        int read;
        while (processedFrames < targetFrames
               && (read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            var frames = read / channels;
            for (var f = 0; f < frames && processedFrames < targetFrames; f++)
            {
                double mono = 0;
                for (var c = 0; c < channels; c++)
                    mono += buffer[f * channels + c];
                mono /= channels;
                sumSq += mono * mono;
                windowCount++;
                processedFrames++;

                if (windowCount == samplesPerWindow)
                {
                    envelope.Add(Math.Sqrt(sumSq / windowCount));
                    sumSq = 0;
                    windowCount = 0;
                }
            }
        }
        if (windowCount >= samplesPerWindow / 2)
            envelope.Add(Math.Sqrt(sumSq / windowCount));

        return envelope.ToArray();
    }

    /// <summary>
    /// RMS 列から「鳴っている区間」を拾う（純粋関数・テスト対象）。
    /// しきい値以上の窓の連続を区間とし、短い途切れをつなぎ、短い単発を落とす。
    /// 時刻は範囲先頭からの相対秒。
    /// </summary>
    internal static List<AudioSegment> FindActiveSegments(double[] rms, double durationSeconds, double threshold)
    {
        var raw = new List<AudioSegment>();
        var i = 0;
        while (i < rms.Length)
        {
            if (rms[i] < threshold)
            {
                i++;
                continue;
            }
            var runStart = i;
            while (i < rms.Length && rms[i] >= threshold)
                i++;
            var start = runStart * WindowSeconds;
            var end = Math.Min(i * WindowSeconds, durationSeconds);
            if (end > start)
                raw.Add(new AudioSegment(start, end));
        }
        var merged = AudioGainEnvelope.MergeIntervals(raw, MaxGapSeconds);
        return merged.Where(s => s.DurationSeconds >= MinActiveSeconds).ToList();
    }
}
