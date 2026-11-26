// 素材のプローブ（設計プラン 3章・7章）。
// 解像度・fps・長さを調べ、タイムライン配置と書き出し設定の元にする。
// 主系は FFMpegCore の FFProbe、失敗時は ffprobe CLI の JSON にフォールバックする。

using System.Globalization;
using System.IO;
using System.Text.Json;
using Editor.Core;
using FFMpegCore;

namespace Editor.Media;

/// <summary>動画・音声ファイルの情報を調べる。</summary>
public sealed class MediaProbe : IMediaProbe
{
    private readonly FfmpegSettings _settings;

    public MediaProbe(FfmpegSettings settings)
    {
        _settings = settings;
    }

    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("素材ファイルが見つかりません。", path);

        try
        {
            _settings.ConfigureFFMpegCore();
            var analysis = await FFProbe.AnalyseAsync(path).ConfigureAwait(false);
            var video = analysis.PrimaryVideoStream;
            var audio = analysis.PrimaryAudioStream;
            return new MediaInfo(
                Path: path,
                Duration: analysis.Duration,
                Width: video?.Width ?? 0,
                Height: video?.Height ?? 0,
                FrameRate: video?.FrameRate ?? 0,
                HasVideo: video is not null,
                HasAudio: audio is not null,
                VideoCodec: video?.CodecName,
                AudioCodec: audio?.CodecName);
        }
        catch (Exception ex) when (ex is not FileNotFoundException)
        {
            // FFMpegCore 経由で取れなければ ffprobe CLI にフォールバックする。
            return await ProbeViaCliAsync(path, ct).ConfigureAwait(false);
        }
    }

    private async Task<MediaInfo> ProbeViaCliAsync(string path, CancellationToken ct)
    {
        var args = $"-v quiet -print_format json -show_format -show_streams \"{path}\"";
        var result = await FfmpegProcess.RunAsync(_settings.FfprobeExePath, args, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"ffprobe が失敗しました: {result.StandardError}");

        using var doc = JsonDocument.Parse(result.StandardOutput);
        var root = doc.RootElement;

        var duration = TimeSpan.Zero;
        if (root.TryGetProperty("format", out var format)
            && format.TryGetProperty("duration", out var durationElement)
            && ParseDurationSeconds(durationElement.GetString()) is { } seconds)
        {
            duration = TimeSpan.FromSeconds(seconds);
        }

        var width = 0;
        var height = 0;
        var frameRate = 0.0;
        string? videoCodec = null;
        string? audioCodec = null;
        var hasVideo = false;
        var hasAudio = false;

        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var codecType = stream.GetProperty("codec_type").GetString();
                if (codecType == "video" && !hasVideo)
                {
                    hasVideo = true;
                    width = stream.GetProperty("width").GetInt32();
                    height = stream.GetProperty("height").GetInt32();
                    videoCodec = stream.GetProperty("codec_name").GetString();
                    if (stream.TryGetProperty("r_frame_rate", out var fpsElement))
                        frameRate = ParseFrameRate(fpsElement.GetString());
                }
                else if (codecType == "audio" && !hasAudio)
                {
                    hasAudio = true;
                    audioCodec = stream.GetProperty("codec_name").GetString();
                }
            }
        }

        return new MediaInfo(path, duration, width, height, frameRate, hasVideo, hasAudio, videoCodec, audioCodec);
    }

    /// <summary>
    /// ffprobe の duration 文字列（"2.500000" 形式）を秒にする。
    /// ffprobe の出力は常にドット小数なので、CurrentCulture で読むと
    /// カンマ小数のロケールで桁区切りと誤解されて壊れる。InvariantCulture で読む。
    /// 読めなければ null。
    /// </summary>
    internal static double? ParseDurationSeconds(string? value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : null;

    /// <summary>"30000/1001" 形式のフレームレートを double にする。ffprobe の出力なので InvariantCulture で読む。</summary>
    internal static double ParseFrameRate(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return 0;
        var parts = value.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            && denominator != 0)
        {
            return numerator / denominator;
        }
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var single) ? single : 0;
    }
}
