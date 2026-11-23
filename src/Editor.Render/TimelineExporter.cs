// タイムライン全体の書き出し（設計プラン 5章）。
// プレビューと同じ TimelineFrameRenderer で全フレームを描き、
// 生フレーム（BGRA）を ffmpeg の標準入力にパイプして H.264 にする。
// 描画とエンコードが分離されているため、プレビュー通りの結果になる。
// 注意：フェーズ1の土台では映像のみ。音声のミックスは次のステップ（README参照）。

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

    public FrameSequenceExporter(IFrameRenderer renderer, FfmpegSettings settings)
    {
        _renderer = renderer;
        _settings = settings;
    }

    public async Task ExportAsync(
        EditorProject project, string outputPath, ExportSettings settings,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        settings.Validate();
        var startSeconds = project.EffectiveExportInSeconds;
        var endSeconds = project.EffectiveExportOutSeconds;
        var totalFrames = Math.Max(1, (int)Math.Ceiling((endSeconds - startSeconds) * settings.FrameRate));

        var args = $"-y -f rawvideo -pix_fmt bgra -s {settings.Width}x{settings.Height} " +
                   $"-r {settings.FrameRate} -i - -an " +
                   $"-c:v libx264 -preset {settings.Preset} -crf {settings.Crf} " +
                   $"-pix_fmt yuv420p -movflags +faststart \"{outputPath}\"";

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
}
