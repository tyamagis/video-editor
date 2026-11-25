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
    /// 素材の inPoint から duration 秒分（素材側の長さ）を切り出して MP4 化する。
    /// タイムライン編集の結果を1本の動画として確認する、最初の経路。
    /// reverse が true のとき、切り出した区間を映像・音声とも逆向きにする
    /// （-vf reverse / -af areverse。音声の無い素材でも -af areverse は無視され、失敗しない）。
    /// speed が 1.0 以外のとき、映像は setpts、音声は atempo（ピッチ保持）で速度を変え、
    /// 出力の長さは duration ÷ speed になる（タイムライン書き出しと同じ結果）。
    /// </summary>
    public async Task ExportSingleClipAsync(
        string sourcePath,
        double inPointSeconds,
        double durationSeconds,
        string outputPath,
        ExportSettings settings,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        bool reverse = false,
        double speed = 1.0)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("素材ファイルが見つかりません。", sourcePath);
        settings.Validate();

        var clampedSpeed = Clip.ClampSpeed(speed);
        var outputDuration = durationSeconds / clampedSpeed;
        var filters = BuildFilterArguments(reverse, clampedSpeed);
        var args = $"-y -ss {inPointSeconds:F3} -i \"{sourcePath}\" -t {outputDuration:F3}" +
                   $"{filters} " +
                   $"-c:v libx264 -preset {settings.Preset} -crf {settings.Crf} " +
                   $"-pix_fmt yuv420p -r {settings.FrameRate} -s {settings.Width}x{settings.Height} " +
                   $"-c:a aac -b:a {settings.AudioBitrateKbps}k -movflags +faststart \"{outputPath}\"";

        var exitCode = await FfmpegProcess.RunWithProgressAsync(
            _settings.FfmpegExePath, args, outputDuration, progress, ct).ConfigureAwait(false);

        if (exitCode != 0)
            throw new InvalidOperationException($"書き出しに失敗しました（ffmpeg 終了コード {exitCode}）。");
    }

    /// <summary>
    /// 逆再生・速度変更の ffmpeg フィルタ引数（" -vf ... -af ..."、先頭に空白）。
    /// どちらも無ければ空文字。映像の速度変更は setpts=PTS/速度（フレームの時刻を
    /// 割り、出力フレームレートで再サンプルされる）。音声はタイムライン書き出しと
    /// 同じ atempo 連鎖（ピッチ保持）を使うため、AudioExportPlanner と同じ規則で組む。
    /// 逆再生と速度変更の順序は、映像は reverse → setpts、音声は areverse → atempo
    /// （逆向きにした後で長さを変える。タイムライン書き出しのフィルタ順序と一致）。
    /// </summary>
    internal static string BuildFilterArguments(bool reverse, double speed)
    {
        var clamped = Clip.ClampSpeed(speed);
        var hasSpeed = Math.Abs(clamped - 1.0) > 1e-9;
        if (!reverse && !hasSpeed)
            return string.Empty;

        var video = new List<string>();
        if (reverse)
            video.Add("reverse");
        if (hasSpeed)
            video.Add($"setpts=PTS/{clamped:F3}");

        var audio = new List<string>();
        if (reverse)
            audio.Add("areverse");
        if (hasSpeed)
            audio.Add(AtempoChain(clamped));

        return $" -vf {string.Join(",", video)} -af {string.Join(",", audio)}";
    }

    /// <summary>
    /// 速度（倍率）を ffmpeg の atempo の連鎖にする（カンマ区切り、末尾カンマなし）。
    /// atempo 1つは 0.5〜2.0 の範囲しか受け付けないため、範囲外は 2.0 / 0.5 を
    /// 先に掛けて残りを最後に掛ける。規則は AudioExportPlanner.AtempoChain と同じ。
    /// </summary>
    internal static string AtempoChain(double speed)
    {
        var clamped = Clip.ClampSpeed(speed);
        var parts = new List<string>();
        var remaining = clamped;
        while (remaining > 2.0)
        {
            parts.Add("atempo=2.000");
            remaining /= 2.0;
        }
        while (remaining < 0.5)
        {
            parts.Add("atempo=0.500");
            remaining /= 0.5;
        }
        parts.Add($"atempo={remaining:F3}");
        return string.Join(",", parts);
    }
}
