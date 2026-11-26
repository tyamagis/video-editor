// 無音カット用の無音区間検出（設計プラン 7章・フェーズ3）。
// 振幅包絡（10ms ごとの RMS）でしきい値以下の区間を探し、
// 最短長さ以上のものだけを無音区間として返す。
// 検出はユーザーがボタンを押したときだけ走る。結果はクリップの
// イン点からの相対秒で返す（適用時にタイムライン時刻へ変換する）。

using System.IO;
using NAudio.Wave;

namespace Editor.Audio;

/// <summary>無音区間 1つ。時刻はクリップのイン点からの相対秒（0 = イン点）。</summary>
public sealed record SilenceSegment(double StartSeconds, double EndSeconds)
{
    public double DurationSeconds => EndSeconds - StartSeconds;
}

/// <summary>無音検出の設定。UI から指定する。</summary>
public sealed class SilenceDetectionSettings
{
    /// <summary>無音とみなすしきい値（dBFS）。これ以下の RMS を無音とする。既定 -40dB。</summary>
    public double ThresholdDb { get; init; } = -40;

    /// <summary>無音区間とみなす最短の長さ（秒）。これより短い無音は残す。既定 0.5 秒。</summary>
    public double MinSilenceSeconds { get; init; } = 0.5;

    /// <summary>無音区間の前後に残す余白（秒）。切る範囲を両端からこの分だけ狭める。既定 0.1 秒。</summary>
    public double MarginSeconds { get; init; } = 0.1;
}

/// <summary>無音検出の結果。区間の時刻はクリップのイン点からの相対秒。</summary>
public sealed class SilenceDetectionResult
{
    public required string SourcePath { get; init; }

    /// <summary>検出できたか。できなければ FailureReason に理由が入る。</summary>
    public bool Detected { get; init; }

    public string? FailureReason { get; init; }

    /// <summary>切る対象の無音区間（マージンを引いた後の範囲）。</summary>
    public IReadOnlyList<SilenceSegment> Segments { get; init; } = Array.Empty<SilenceSegment>();

    /// <summary>切る無音区間の合計時間（秒）。</summary>
    public double TotalSilenceSeconds { get; init; }

    /// <summary>解析した区間の長さ（秒）。</summary>
    public double AnalyzedDurationSeconds { get; init; }
}

/// <summary>音声から無音区間を検出する。クリップの使用範囲（イン点〜イン点＋長さ）だけを読む。</summary>
public sealed class SilenceDetector
{
    /// <summary>振幅包絡の窓の長さ（秒）。10ms 単位で無音を判定する。</summary>
    private const double WindowSeconds = 0.01;

    /// <summary>
    /// 音声ファイルの指定範囲の無音区間を検出する。重い読み込みはバックグラウンドで行う。
    /// inPointSeconds / durationSeconds はクリップのイン点と長さ（素材内時刻）。
    /// </summary>
    public async Task<SilenceDetectionResult> AnalyzeAsync(
        string audioPath,
        double inPointSeconds,
        double durationSeconds,
        SilenceDetectionSettings settings,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(audioPath))
            return Fail(audioPath, "音声ファイルが指定されていません。");

        // NaN・無限大は範囲チェック（<= 0 など）をすり抜けるため、先に有限性を弾く。
        // 特にしきい値 NaN は FindSilences の比較が全て false になり無限ループになる。
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0)
            return Fail(audioPath, "クリップの長さがありません。");

        if (!double.IsFinite(settings.ThresholdDb) || settings.ThresholdDb > 0 || settings.ThresholdDb < -90)
            return Fail(audioPath, "しきい値は -90〜0 dB の範囲で指定してください。");

        if (!double.IsFinite(settings.MinSilenceSeconds) || settings.MinSilenceSeconds < 0.05)
            return Fail(audioPath, "最短の無音の長さは 0.05 秒以上にしてください。");

        if (!double.IsFinite(settings.MarginSeconds) || settings.MarginSeconds < 0)
            return Fail(audioPath, "前後の余白は 0 秒以上にしてください。");

        string key;
        try
        {
            key = Path.GetFullPath(audioPath);
        }
        catch (Exception)
        {
            return Fail(audioPath, "音声ファイルのパスが正しくありません。");
        }

        try
        {
            if (!File.Exists(key))
                return Fail(audioPath, "音声ファイルが見つかりません。");
        }
        catch (Exception ex)
        {
            return Fail(audioPath, $"音声ファイルを確認できませんでした：{ex.Message}");
        }

        return await Task.Run(() => AnalyzeCore(key, inPointSeconds, durationSeconds, settings, progress, ct), ct)
            .ConfigureAwait(false);
    }

    private static SilenceDetectionResult Fail(string path, string reason) => new()
    {
        SourcePath = path,
        Detected = false,
        FailureReason = reason
    };

    private SilenceDetectionResult AnalyzeCore(
        string path,
        double inPointSeconds,
        double durationSeconds,
        SilenceDetectionSettings settings,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        double[] rms;
        try
        {
            rms = ReadEnvelope(path, inPointSeconds, durationSeconds, progress, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail(path, $"音声を読み込めませんでした：{ex.Message}");
        }

        var threshold = Math.Pow(10, settings.ThresholdDb / 20.0);
        var segments = FindSilences(rms, durationSeconds, threshold, settings);
        var total = segments.Sum(s => s.DurationSeconds);

        return new SilenceDetectionResult
        {
            SourcePath = path,
            Detected = true,
            Segments = segments,
            TotalSilenceSeconds = total,
            AnalyzedDurationSeconds = durationSeconds
        };
    }

    /// <summary>
    /// 指定範囲の音声をモノラル化し、10ms ごとの RMS（振幅包絡）を返す。
    /// イン点までシークしてから読む（AudioBandEnergyProvider と同じ方法）。
    /// </summary>
    private static double[] ReadEnvelope(
        string path, double inPointSeconds, double durationSeconds,
        IProgress<double>? progress, CancellationToken ct)
    {
        using var reader = new AudioFileReader(path);
        var channels = reader.WaveFormat.Channels;
        var sampleRate = reader.WaveFormat.SampleRate;
        if (sampleRate <= 0 || channels <= 0)
            throw new InvalidOperationException("音声の形式が読めませんでした。");

        var samplesPerWindow = Math.Max(1, (int)Math.Round(sampleRate * WindowSeconds));
        var targetFrames = (long)Math.Ceiling(durationSeconds * sampleRate);

        // イン点までシークする。MP3 などではフレーム単位の誤差（最大で約 26ms）が出る。
        var targetFrame = Math.Max(0, (long)(inPointSeconds * sampleRate));
        reader.Position = Math.Min(targetFrame * channels * 4, reader.Length);

        var envelope = new List<double>();
        var buffer = new float[8192 * channels];
        double sumSq = 0;
        var windowCount = 0;
        long processedFrames = 0;
        var lastReported = -1.0;
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

            // 進捗は 1% 変わるごとにだけ報告する（UI への通知が多すぎるのを防ぐ）。
            if (progress is not null && targetFrames > 0)
            {
                var fraction = (double)processedFrames / targetFrames;
                if (fraction - lastReported >= 0.01)
                {
                    progress.Report(fraction);
                    lastReported = fraction;
                }
            }
        }

        // 末尾の半端な窓（半分以上ある場合だけ採用する）。
        if (windowCount >= samplesPerWindow / 2)
            envelope.Add(Math.Sqrt(sumSq / windowCount));

        return envelope.ToArray();
    }

    /// <summary>
    /// RMS 列から無音区間を拾う。しきい値以下の窓の連続を無音区間とし、
    /// 最短長さ以上のものだけを残し、前後の余白を引いて返す。
    /// </summary>
    private static List<SilenceSegment> FindSilences(
        double[] rms, double durationSeconds, double threshold, SilenceDetectionSettings settings)
    {
        var segments = new List<SilenceSegment>();
        var i = 0;
        while (i < rms.Length)
        {
            if (rms[i] >= threshold)
            {
                i++;
                continue;
            }
            var runStart = i;
            while (i < rms.Length && rms[i] < threshold)
                i++;
            var runEnd = i; // 排他的（runEnd 番目の窓は無音でない）。

            var start = runStart * WindowSeconds;
            var end = Math.Min(runEnd * WindowSeconds, durationSeconds);
            if (end - start < settings.MinSilenceSeconds)
                continue; // 短すぎる無音は残す。

            // 前後の余白を残す（切る範囲を狭める）。余白で消える区間は切らない。
            var cutStart = start + settings.MarginSeconds;
            var cutEnd = end - settings.MarginSeconds;
            if (cutEnd > cutStart)
                segments.Add(new SilenceSegment(cutStart, cutEnd));
        }
        return segments;
    }
}
