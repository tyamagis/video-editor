// タイムライン全体の書き出し（設計プラン 5章）。
// プレビューと同じ TimelineFrameRenderer で全フレームを描き、
// 生フレーム（BGRA）を ffmpeg の標準入力にパイプして H.264 にする。
// 描画とエンコードが分離されているため、プレビュー通りの結果になる。
// 音声は書き出し範囲に入る音声つきクリップを全て集め、ffmpeg の
// フィルタ（atrim / adelay / amix）で1本にミックスして AAC で入れる。

using System.Diagnostics;
using System.Runtime.InteropServices;
using Editor.Core;
using Editor.Media;

namespace Editor.Render;

/// <summary>タイムライン全体を MP4 に書き出す口。</summary>
public interface ITimelineExporter
{
    Task ExportAsync(
        EditorProject project, string outputPath, ExportSettings settings,
        IProgress<double>? progress = null, CancellationToken ct = default);
}

/// <summary>フレームを順に描いて ffmpeg に流し込む書き出し。</summary>
public sealed class FrameSequenceExporter : ITimelineExporter
{
    private readonly IFrameRenderer _renderer;
    private readonly FfmpegSettings _settings;
    private readonly MediaProbe _probe;

    public FrameSequenceExporter(IFrameRenderer renderer, FfmpegSettings settings, MediaProbe probe)
    {
        _renderer = renderer;
        _settings = settings;
        _probe = probe;
    }

    public async Task ExportAsync(
        EditorProject project, string outputPath, ExportSettings settings,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        settings.Validate();
        var startSeconds = project.EffectiveExportInSeconds;
        var endSeconds = project.EffectiveExportOutSeconds;
        var totalFrames = Math.Max(1, (int)Math.Ceiling((endSeconds - startSeconds) * settings.FrameRate));

        var audioPlan = await BuildAudioPlanAsync(project, startSeconds, endSeconds, ct)
            .ConfigureAwait(false);

        string args;
        if (audioPlan.MapLabel is null)
        {
            args = $"-y -f rawvideo -pix_fmt bgra -s {settings.Width}x{settings.Height} " +
                   $"-r {settings.FrameRate} -i - -an " +
                   $"-c:v libx264 -preset {settings.Preset} -crf {settings.Crf} " +
                   $"-pix_fmt yuv420p -movflags +faststart \"{outputPath}\"";
        }
        else
        {
            // 音声入力（0 は映像パイプ、1〜 が音声ファイル）とフィルタを組み立てる。
            var audioInputs = string.Concat(audioPlan.Inputs.Select(i => $" -i \"{i.Path}\""));
            var graph = string.Join(";", audioPlan.Inputs.Select(i => i.Filter));
            if (audioPlan.Inputs.Count > 1)
            {
                var labels = string.Concat(audioPlan.Inputs.Select(i => $"[{i.Label}]"));
                graph += $";{labels}amix=inputs={audioPlan.Inputs.Count}:normalize=0[aout]";
            }
            var duration = endSeconds - startSeconds;
            args = $"-y -f rawvideo -pix_fmt bgra -s {settings.Width}x{settings.Height} " +
                   $"-r {settings.FrameRate} -i -{audioInputs} " +
                   $"-filter_complex \"{graph}\" -map 0:v -map \"[{audioPlan.MapLabel}]\" " +
                   $"-c:v libx264 -preset {settings.Preset} -crf {settings.Crf} " +
                   $"-pix_fmt yuv420p -c:a aac -b:a 192k -r {settings.FrameRate} -t {duration:F3} " +
                   $"-movflags +faststart \"{outputPath}\"";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _settings.FfmpegExePath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);

        try
        {
            var buffer = new byte[settings.Width * settings.Height * 4];
            for (var i = 0; i < totalFrames; i++)
            {
                ct.ThrowIfCancellationRequested();
                var timeSeconds = startSeconds + (double)i / settings.FrameRate;

                using var frame = await _renderer
                    .RenderFrameAsync(project, timeSeconds, settings.Width, settings.Height, ct)
                    .ConfigureAwait(false);

                // SKBitmap（Bgra8888）の画素をそのまま ffmpeg へ送る。
                Marshal.Copy(frame.GetPixels(), buffer, 0, buffer.Length);
                await process.StandardInput.BaseStream
                    .WriteAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);

                progress?.Report((double)(i + 1) / totalFrames);
            }
        }
        finally
        {
            process.StandardInput.Close();
        }

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"タイムライン書き出しに失敗しました: {stderr}");
    }

    /// <summary>書き出し範囲に入る音声つきクリップの入力計画を作る。音声が無ければ MapLabel は null。</summary>
    private async Task<(List<AudioInput> Inputs, string? MapLabel)> BuildAudioPlanAsync(
        EditorProject project, double startSeconds, double endSeconds, CancellationToken ct)
    {
        var inputs = new List<AudioInput>();

        var clips = project.Tracks
            .SelectMany(t => t.Clips)
            .Where(c => !string.IsNullOrEmpty(c.SourcePath)
                        && c.EndSeconds > startSeconds && c.StartSeconds < endSeconds)
            .OrderBy(c => c.StartSeconds)
            .ToList();

        var index = 1; // 0 は映像（標準入力）なので音声は 1 から。
        foreach (var clip in clips)
        {
            ct.ThrowIfCancellationRequested();
            MediaInfo? info;
            try
            {
                info = await _probe.ProbeAsync(clip.SourcePath, ct).ConfigureAwait(false);
            }
            catch
            {
                continue; // 読めない素材は音声なしで進む。
            }
            if (info is null || !info.HasAudio)
                continue;

            var overlapStart = Math.Max(clip.StartSeconds, startSeconds);
            var overlapEnd = Math.Min(clip.EndSeconds, endSeconds);
            if (overlapEnd <= overlapStart)
                continue;

            var sourceStart = clip.InPointSeconds + (overlapStart - clip.StartSeconds);
            var sourceEnd = clip.InPointSeconds + (overlapEnd - clip.StartSeconds);
            var delayMs = (long)Math.Round((overlapStart - startSeconds) * 1000);
            var label = $"a{index}";
            // ノイズ除去が有効なクリップは、書き出し時に afftdn で背景ノイズを減らす
            // （設計プラン 5章で FFmpeg のフィルタが第一候補。プレビューでは反映されない）。
            // atrim の前に掛けることで、ファイル全体からノイズ特性を取る。
            var denoise = clip.NoiseReductionEnabled
                ? $"afftdn=nr={NoiseReductionDb(clip.NoiseReductionStrength):F1}:nf=-50,"
                : string.Empty;
            // 書き出し範囲に入る部分だけ切り出し、範囲内の位置まで遅らせる。
            var filter = $"[{index}:a]{denoise}atrim=start={sourceStart:F3}:end={sourceEnd:F3}," +
                         $"asetpts=PTS-STARTPTS,adelay={delayMs}:all=1[{label}]";
            inputs.Add(new AudioInput(clip.SourcePath, filter, label));
            index++;
        }

        if (inputs.Count == 0)
            return (inputs, null);

        var mapLabel = inputs.Count == 1 ? inputs[0].Label : "aout";
        return (inputs, mapLabel);
    }

    /// <summary>ノイズ除去の強さ（0.0〜1.0）を afftdn の nr（dB、1〜30）に写す。</summary>
    private static double NoiseReductionDb(double strength)
        => 1.0 + Math.Clamp(strength, 0, 1) * 29.0;

    private sealed record AudioInput(string Path, string Filter, string Label);
}
