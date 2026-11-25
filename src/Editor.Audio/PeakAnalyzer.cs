// クリップの使用範囲のサンプルピーク解析（ピーク正規化用）。
// SilenceDetector と同じく NAudio の AudioFileReader で使用範囲だけを読み、
// チャンネルごとの最大絶対値を取る（モノラル平均ではない）。
// 波形キャッシュ（WaveformCache）は使わない。あれは表示用のバケット列で、
// チャンネルをモノラル平均してからピークを取るため、片チャンネルだけに
// ピークがある素材では最大で約 6dB 過小評価になる。正規化のゲイン計算には
// 正確なピークが要るため、ここでは直接読み直す。
// 結果はメモリにキャッシュする（素材の身元＋使用範囲が鍵）。プレビューと
// 書き出しが同じインスタンスを共有すれば、2回目以降は読み直しが無い。

using System.IO;
using Editor.Core;
using NAudio.Wave;

namespace Editor.Audio;

/// <summary>クリップ使用範囲のサンプルピークを測る。結果はメモリにキャッシュする。</summary>
public sealed class PeakAnalyzer : IAudioPeakProvider
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AudioPeakResult> _results = new();
    private readonly Dictionary<string, Task<AudioPeakResult>> _inFlight = new();

    public Task<AudioPeakResult> GetPeakAsync(
        string sourcePath, double inPointSeconds, double durationSeconds,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(sourcePath))
            return Task.FromResult(new AudioPeakResult(0, false, "音声ファイルが指定されていません。"));
        if (durationSeconds <= 0)
            return Task.FromResult(new AudioPeakResult(0, false, "クリップの長さがありません。"));

        string key;
        try
        {
            var full = Path.GetFullPath(sourcePath);
            var info = new FileInfo(full);
            if (!info.Exists)
                return Task.FromResult(new AudioPeakResult(0, false, "音声ファイルが見つかりません。"));
            // 素材の身元（パス＋サイズ＋更新日時）と使用範囲が鍵。素材が差し替え・
            // 上書きされたり、クリップをトリミングしたりすれば別キーになる。
            key = $"{full}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{inPointSeconds:R}|{durationSeconds:R}";
        }
        catch (Exception ex)
        {
            return Task.FromResult(new AudioPeakResult(0, false, $"音声ファイルを確認できませんでした：{ex.Message}"));
        }

        lock (_gate)
        {
            if (_results.TryGetValue(key, out var cached))
                return Task.FromResult(cached);
            if (_inFlight.TryGetValue(key, out var running))
                return running; // 同じ範囲の解析中は1回にまとめる。

            var task = Task.Run(() =>
            {
                try
                {
                    var result = AnalyzeCore(sourcePath, inPointSeconds, durationSeconds, ct);
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

    /// <summary>
    /// 指定範囲の音声を読み、チャンネルごとの最大絶対値（サンプルピーク）を返す。
    /// イン点までシークしてから読む（SilenceDetector と同じ方法。
    /// MP3 などではフレーム単位の誤差（最大で約 26ms）が出るが、ピークは
    /// 範囲全体の最大値のため、実用上の影響は無視できる）。
    /// </summary>
    private static AudioPeakResult AnalyzeCore(
        string path, double inPointSeconds, double durationSeconds, CancellationToken ct)
    {
        try
        {
            using var reader = new AudioFileReader(path);
            var channels = reader.WaveFormat.Channels;
            var sampleRate = reader.WaveFormat.SampleRate;
            if (sampleRate <= 0 || channels <= 0)
                return new AudioPeakResult(0, false, "音声の形式が読めませんでした。");

            var targetFrames = (long)Math.Ceiling(durationSeconds * sampleRate);
            var targetFrame = Math.Max(0, (long)(inPointSeconds * sampleRate));
            reader.Position = Math.Min(targetFrame * channels * 4, reader.Length);

            double peak = 0;
            var buffer = new float[8192 * channels];
            long processedFrames = 0;
            int read;
            while (processedFrames < targetFrames
                   && (read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                var frames = read / channels;
                for (var f = 0; f < frames && processedFrames < targetFrames; f++)
                {
                    for (var c = 0; c < channels; c++)
                    {
                        var abs = Math.Abs(buffer[f * channels + c]);
                        if (abs > peak)
                            peak = abs;
                    }
                    processedFrames++;
                }
            }

            return new AudioPeakResult(peak, true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AudioPeakResult(0, false, $"音声を読み込めませんでした：{ex.Message}");
        }
    }
}
