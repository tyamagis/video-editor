// MP4 書き出し（設計プラン 5章）。
// 形式は MP4 / H.264（音声 AAC）、1080p・4K、30fps・60fps。
// フェーズ1の土台として、まず単一クリップの区間書き出しを実装する。
// タイムライン全体の書き出しは Editor.Render の FrameSequenceExporter が
// フレームを順に描いて ffmpeg にパイプする（同じ ExportSettings を使う）。

using System.IO;
using Editor.Core;

namespace Editor.Media;

/// <summary>単一素材の区間を MP4 に書き出す。</summary>
public sealed class ExportService
{
    private readonly FfmpegSettings _settings;

    public ExportService(FfmpegSettings settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// 素材の inPoint から duration 秒分を切り出して MP4 化する。
    /// タイムライン編集の結果を1本の動画として確認する、最初の経路。
    /// reverse が true のとき、切り出した区間を映像・音声とも逆向きにする
    /// （-vf reverse / -af areverse。音声の無い素材でも -af areverse は無視され、失敗しない）。
    /// </summary>
    public async Task ExportSingleClipAsync(
        string sourcePath,
        double inPointSeconds,
        double durationSeconds,
        string outputPath,
        ExportSettings settings,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        bool reverse = false)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("素材ファイルが見つかりません。", sourcePath);
        settings.Validate();

        var reverseFilters = reverse ? " -vf reverse -af areverse" : string.Empty;
        var args = $"-y -ss {inPointSeconds:F3} -i \"{sourcePath}\" -t {durationSeconds:F3}" +
                   $"{reverseFilters} " +
                   $"-c:v libx264 -preset {settings.Preset} -crf {settings.Crf} " +
                   $"-pix_fmt yuv420p -r {settings.FrameRate} -s {settings.Width}x{settings.Height} " +
                   $"-c:a aac -b:a {settings.AudioBitrateKbps}k -movflags +faststart \"{outputPath}\"";

        var exitCode = await FfmpegProcess.RunWithProgressAsync(
            _settings.FfmpegExePath, args, durationSeconds, progress, ct).ConfigureAwait(false);

        if (exitCode != 0)
            throw new InvalidOperationException($"書き出しに失敗しました（ffmpeg 終了コード {exitCode}）。");
    }
}
