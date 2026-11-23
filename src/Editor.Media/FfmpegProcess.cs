// ffmpeg / ffprobe のプロセス実行ヘルパー。
// 書き出し・サムネイル・フレーム抽出は CLI を直接呼ぶことで、
// 引数（フィルタやエンコード設定）がコードから見えるようにする。

using System.Diagnostics;

namespace Editor.Media;

/// <summary>外部プロセスの実行結果。</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>ffmpeg / ffprobe を Process として実行する。</summary>
public static class FfmpegProcess
{
    public static async Task<ProcessResult> RunAsync(
        string exePath, string arguments, CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        return new ProcessResult(
            process.ExitCode,
            await stdoutTask.ConfigureAwait(false),
            await stderrTask.ConfigureAwait(false));
    }

    /// <summary>
    /// 進捗つきで実行する。ffmpeg の stderr に出る "time=HH:MM:SS.xx" を拾い、
    /// 全体に対する進捗率（0.0〜1.0）を報告する。キャンセルでプロセスを止める。
    /// </summary>
    public static async Task<int> RunWithProgressAsync(
        string exePath,
        string arguments,
        double totalSeconds,
        IProgress<double>? progress,
        CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // 標準出力は読み捨て（パイプ詰まり防止）。
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);

        string? line;
        while ((line = await process.StandardError.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (progress is null || totalSeconds <= 0)
                continue;

            var elapsed = TryParseProgressTime(line);
            if (elapsed is not null)
                progress.Report(Math.Clamp(elapsed.Value / totalSeconds, 0, 1));
        }

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        await stdoutTask.ConfigureAwait(false);
        return process.ExitCode;
    }

    /// <summary>ffmpeg の進捗行 "time=00:00:12.34" から経過秒を抜き出す。</summary>
    internal static double? TryParseProgressTime(string line)
    {
        const string marker = "time=";
        var index = line.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
            return null;

        var rest = line[(index + marker.Length)..];
        var end = rest.IndexOf(' ');
        var token = end < 0 ? rest : rest[..end];
        return TimeSpan.TryParse(token, out var time) ? time.TotalSeconds : null;
    }
}
