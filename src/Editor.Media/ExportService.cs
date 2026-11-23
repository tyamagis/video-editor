// MP4 書き出し（設計プラン 5章）。
// 形式は MP4 / H.264（音声 AAC）、1080p・4K、30fps・60fps。
// フェーズ1の土台として、まず単一クリップの区間書き出しを実装する。
// タイムライン全体の書き出しは Editor.Render の FrameSequenceExporter が
// フレームを順に描いて ffmpeg にパイプする（同じ ExportSettings を使う）。

using System.IO;

namespace Editor.Media;

/// <summary>書き出し設定。解像度とフレームレート、画質の組み合わせ。</summary>
public sealed record ExportSettings(
    int Width = 1920,
    int Height = 1080,
    int FrameRate = 30,
    int Crf = 20,
    string Preset = "medium")
{
    public static ExportSettings Hd30 { get; } = new(1920, 1080, 30);
    public static ExportSettings Hd60 { get; } = new(1920, 1080, 60);
    public static ExportSettings Uhd30 { get; } = new(3840, 2160, 30);
    public static ExportSettings Uhd60 { get; } = new(3840, 2160, 60);

    public void Validate()
    {
        if (Width <= 0 || Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(Width), "解像度は正の値にしてください。");
        if (FrameRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(FrameRate), "フレームレートは正の値にしてください。");
        if (Crf is < 0 or > 51)
            throw new ArgumentOutOfRangeException(nameof(Crf), "CRFは0〜51の範囲にしてください。");
    }
}

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
    /// </summary>
    public async Task ExportSingleClipAsync(
        string sourcePath,
        double inPointSeconds,
        double durationSeconds,
        string outputPath,
        ExportSettings settings,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("素材ファイルが見つかりません。", sourcePath);
        settings.Validate();

        var args = $"-y -ss {inPointSeconds:F3} -i \"{sourcePath}\" -t {durationSeconds:F3} " +
                   $"-c:v libx264 -preset {settings.Preset} -crf {settings.Crf} " +
                   $"-pix_fmt yuv420p -r {settings.FrameRate} -s {settings.Width}x{settings.Height} " +
                   $"-c:a aac -b:a 192k -movflags +faststart \"{outputPath}\"";

        var exitCode = await FfmpegProcess.RunWithProgressAsync(
            _settings.FfmpegExePath, args, durationSeconds, progress, ct).ConfigureAwait(false);

        if (exitCode != 0)
            throw new InvalidOperationException($"書き出しに失敗しました（ffmpeg 終了コード {exitCode}）。");
    }
}
