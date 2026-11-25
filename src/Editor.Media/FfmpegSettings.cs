// FFmpeg 設定（設計プラン 3章・8章）。
// 自分だけが使い配布しないため、同梱はしない。
// 当面は OS インストール済みの ffmpeg / ffprobe を PATH から使う。
// 実行ファイルの場所は設定で変えられる。

using System.IO;
using Editor.Core;
using FFMpegCore;

namespace Editor.Media;

/// <summary>ffmpeg / ffprobe の所在設定。既定は PATH 頼み。</summary>
public sealed class FfmpegSettings : IFfmpegConfiguration
{
    public string FfmpegExePath { get; set; } = "ffmpeg";
    public string FfprobeExePath { get; set; } = "ffprobe";

    /// <summary>実行ファイルのあるフォルダ。PATH指定の場合は空文字。</summary>
    public string BinaryFolder
    {
        get
        {
            var dir = Path.GetDirectoryName(FfmpegExePath);
            return string.IsNullOrEmpty(dir) ? string.Empty : dir;
        }
    }

    /// <summary>FFMpegCore にバイナリの場所を教える。PATH指定の場合は何もしない。</summary>
    public void ConfigureFFMpegCore()
    {
        if (string.IsNullOrEmpty(BinaryFolder))
            return;

        GlobalFFOptions.Configure(new FFOptions
        {
            BinaryFolder = BinaryFolder,
            TemporaryFilesFolder = Path.GetTempPath()
        });
    }

    /// <summary>ffmpeg が呼べるか確認する。-version が返れば利用可。</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var (exitCode, _, _) = await FfmpegProcess.RunAsync(
                FfmpegExePath, "-version", ct).ConfigureAwait(false);
            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
