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
    private readonly AudioExportPlanner _audioPlanner;

    public FrameSequenceExporter(IFrameRenderer renderer, FfmpegSettings settings, MediaProbe probe)
    {
        _renderer = renderer;
        _settings = settings;
        _audioPlanner = new AudioExportPlanner(probe);
    }

    public async Task ExportAsync(
        EditorProject project, string outputPath, ExportSettings settings,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        settings.Validate();
        var startSeconds = project.EffectiveExportInSeconds;
        var endSeconds = project.EffectiveExportOutSeconds;
        var totalFrames = Math.Max(1, (int)Math.Ceiling((endSeconds - startSeconds) * settings.FrameRate));

        var audioPlan = await _audioPlanner.BuildAudioPlanAsync(project, startSeconds, endSeconds, ct)
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
                   $"-pix_fmt yuv420p -c:a aac -b:a {settings.AudioBitrateKbps}k -r {settings.FrameRate} -t {duration:F3} " +
                   $"-movflags +faststart \"{outputPath}\"";
        }

        // 標準入力へ生フレームを送るため RedirectStandardInput を有効にする。
        var startInfo = FfmpegProcess.CreateStartInfo(_settings.FfmpegExePath, args, redirectStandardInput: true);

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

}
