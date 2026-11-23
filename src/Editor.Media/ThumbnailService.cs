// サムネイルとプレビュー用フレーム取得。
// ffmpeg で1フレームだけ切り出し、PNGバイトとして返す。
// プレビュー描画（Editor.Render）はこのバイト列を SkiaSharp で開く。
// 連続再生時の再抽出を避けるため、直近1フレームだけキャッシュする。

namespace Editor.Media;

/// <summary>指定時刻の映像フレームを PNG バイト列で取得する口。</summary>
public interface IVideoFrameSource
{
    Task<byte[]?> GetFramePngAsync(string sourcePath, double timeSeconds, int maxWidth, CancellationToken ct = default);
}

/// <summary>ffmpeg の単フレーム切り出しによるフレーム取得。</summary>
public sealed class ThumbnailService : IVideoFrameSource
{
    private readonly FfmpegSettings _settings;

    // 直近1件のキャッシュ（キー：素材パス＋時刻＋幅）。
    private (string Path, double TimeSeconds, int MaxWidth)? _lastKey;
    private byte[]? _lastPng;

    public ThumbnailService(FfmpegSettings settings)
    {
        _settings = settings;
    }

    /// <summary>サムネイル画像をファイルとして書き出す（素材プール表示用）。</summary>
    public async Task ExtractThumbnailAsync(
        string sourcePath, double timeSeconds, string outputPath, int width = 320,
        CancellationToken ct = default)
    {
        var args = $"-y -ss {timeSeconds:F3} -i \"{sourcePath}\" -frames:v 1 -vf scale={width}:-1 -q:v 3 \"{outputPath}\"";
        var result = await FfmpegProcess.RunAsync(_settings.FfmpegExePath, args, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"サムネイル抽出に失敗しました: {result.StandardError}");
    }

    public async Task<byte[]?> GetFramePngAsync(
        string sourcePath, double timeSeconds, int maxWidth, CancellationToken ct = default)
    {
        var key = (sourcePath, Math.Round(timeSeconds, 3), maxWidth);
        if (_lastKey == key && _lastPng is not null)
            return _lastPng;

        var tempPath = Path.Combine(Path.GetTempPath(), $"ve-frame-{Guid.NewGuid():N}.png");
        try
        {
            var args = $"-y -ss {timeSeconds:F3} -i \"{sourcePath}\" -frames:v 1 -vf scale={maxWidth}:-1 \"{tempPath}\"";
            var result = await FfmpegProcess.RunAsync(_settings.FfmpegExePath, args, ct).ConfigureAwait(false);
            if (result.ExitCode != 0 || !File.Exists(tempPath))
                return null;

            var bytes = await File.ReadAllBytesAsync(tempPath, ct).ConfigureAwait(false);
            _lastKey = key;
            _lastPng = bytes;
            return bytes;
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}
