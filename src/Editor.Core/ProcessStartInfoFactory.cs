// 外部プロセス起動時の ProcessStartInfo 生成ヘルパー。
// ProcessStartInfo を作るだけの BCL のみの処理で ffmpeg 依存はないため、
// Editor.Media.FfmpegProcess.CreateStartInfo から Core へ移動した。
// Media 側の FfmpegProcess.CreateStartInfo は互換のためここへ委譲するラッパーとして残る。

using System.Diagnostics;

namespace Editor.Core;

/// <summary>外部プロセス起動時の共通設定を作る（ウィンドウなし・入出力リダイレクト）。</summary>
public static class ProcessStartInfoFactory
{
    /// <summary>
    /// ffmpeg / ffprobe 起動時の共通設定（ウィンドウなし・入出力リダイレクト）。
    /// 長期プロセス（常駐ストリーム）と短期プロセスで設定を揃えるための1か所。
    /// 標準入力へ送るプロセス（タイムライン書き出し）は redirectStandardInput: true を指定する。
    /// </summary>
    public static ProcessStartInfo CreateStartInfo(
        string exePath, string arguments, bool redirectStandardInput = false)
        => new()
        {
            FileName = exePath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectStandardInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
}
