// サムネイルとプレビュー用フレーム取得。
// ffmpeg で1フレームだけ切り出し、PNGバイトとして返す。
// プレビュー描画（Editor.Render）はこのバイト列を SkiaSharp で開く。
// 連続再生時の再抽出を避けるため、直近1フレームだけキャッシュする。

using System.IO;
using Editor.Core;

namespace Editor.Media;

/// <summary>ffmpeg の単フレーム切り出しによるフレーム取得。</summary>
public sealed class ThumbnailService : IVideoFrameSource
{
    private readonly FfmpegSettings _settings;

    // 直近1件のキャッシュ（キー：素材パス＋時刻＋幅）。
    // 素材が差し替えられていないか、身元（パス＋サイズ＋更新日時）でも照合する。
    // 以前はキーだけで引いていたため、差し替え後の古いフレームを返すことがあった。
    private (string Path, double TimeSeconds, int MaxWidth)? _lastKey;
    private SourceIdentity? _lastIdentity;
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
        var args = FormattableString.Invariant($"-y -ss {timeSeconds:F3} -i \"{sourcePath}\" -frames:v 1 -vf scale={width}:-1 -q:v 3 \"{outputPath}\"");
        var result = await FfmpegProcess.RunAsync(_settings.FfmpegExePath, args, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"サムネイル抽出に失敗しました: {result.StandardError}");
    }

    public async Task<byte[]?> GetFramePngAsync(
        string sourcePath, double timeSeconds, int maxWidth, CancellationToken ct = default)
    {
        // ファイルの現在の身元を取る。取れなければ（削除済みなど）キャッシュは使わず、
        // そのまま抽出を試みる（従来通り、失敗すれば null が返る）。
        var key = (sourcePath, Math.Round(timeSeconds, 3), maxWidth);
        if (_lastKey == key && _lastPng is not null
            && _lastIdentity is { } lastIdentity
            && SourceIdentity.TryCapture(sourcePath, out var current)
            && lastIdentity.Matches(current))
            return _lastPng;

        var tempPath = Path.Combine(Path.GetTempPath(), $"ve-frame-{Guid.NewGuid():N}.png");
        try
        {
            var args = FormattableString.Invariant($"-y -ss {timeSeconds:F3} -i \"{sourcePath}\" -frames:v 1 -vf scale={maxWidth}:-1 \"{tempPath}\"");
            var result = await FfmpegProcess.RunAsync(_settings.FfmpegExePath, args, ct).ConfigureAwait(false);
            if (result.ExitCode != 0 || !File.Exists(tempPath))
                return null;

            var bytes = await File.ReadAllBytesAsync(tempPath, ct).ConfigureAwait(false);
            _lastKey = key;
            // 抽出した時点の身元を保存する。照合時の身元と抽出時の身元が
            // 違えば（抽出中に差し替えられた場合）、次回は再抽出になる。
            _lastIdentity = SourceIdentity.TryCapture(sourcePath, out var stored) ? stored : null;
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
