// プレビュー用フレームのストリーミング取得。
// 従来は1フレームごとに ffmpeg プロセスを起動していたため、
// 30fps 再生で毎秒30回プロセスを立ち上げて重かった。
// ここでは ffmpeg を1プロセスだけ常駐させ、rawvideo をパイプで
// 連続デコードして必要な時刻のフレームを渡す。シーク時のみ張り直す。
// 取得に失敗した場合は呼び出し側が単発抽出（ThumbnailService）に
// フォールバックするので、プレビューが真っ黒になることはない。

using System.Diagnostics;

namespace Editor.Media;

/// <summary>1フレーム分の生画素（BGRA）とその時刻・サイズ。</summary>
public sealed record VideoFrameData(byte[] Bgra, int Width, int Height, double TimeSeconds);

/// <summary>常駐 ffmpeg からのフレーム取得口。</summary>
public interface IStreamingFrameSource
{
    /// <summary>
    /// 指定時刻のフレームを返す。映像の終端より後の時刻は null。
    /// 取得できない場合（ffmpeg 不在など）は例外を投げ、呼び出し側が
    /// 単発抽出にフォールバックする。
    /// </summary>
    Task<VideoFrameData?> GetFrameAsync(
        string sourcePath, double timeSeconds, int maxWidth,
        CancellationToken ct = default);
}

/// <summary>ffmpeg を常駐させて rawvideo を連続で読むフレーム取得。</summary>
public sealed class StreamingFrameSource : IStreamingFrameSource, IDisposable
{
    private readonly FfmpegSettings _settings;
    private readonly MediaProbe _probe;
    private readonly Dictionary<string, MediaInfo> _probeCache = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Session? _session;
    private bool _disposed;

    public StreamingFrameSource(FfmpegSettings settings, MediaProbe probe)
    {
        _settings = settings;
        _probe = probe;
    }

    public async Task<VideoFrameData?> GetFrameAsync(
        string sourcePath, double timeSeconds, int maxWidth,
        CancellationToken ct = default)
    {
        if (timeSeconds < 0)
            timeSeconds = 0;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var info = await GetInfoAsync(sourcePath, ct).ConfigureAwait(false);
            if (info is null || !info.HasVideo || info.Width <= 0 || info.Height <= 0)
                throw new InvalidOperationException("映像情報が取れませんでした。");

            var fps = info.FrameRate > 0 ? info.FrameRate : 30.0;
            var outHeight = Math.Max(2, (int)Math.Round(info.Height * (double)maxWidth / info.Width));
            var frameBytes = maxWidth * outHeight * 4;

            if (!CanServe(sourcePath, maxWidth, timeSeconds))
                await StartSessionAsync(sourcePath, timeSeconds, maxWidth, outHeight, frameBytes, ct)
                    .ConfigureAwait(false);

            var session = _session!;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var frame = await ReadFrameAsync(session, ct).ConfigureAwait(false);
                if (frame is null)
                {
                    session.Ended = true;
                    return null; // 終端まで読んだ。
                }

                var frameTime = session.StartSeconds + session.NextIndex / fps;
                session.NextIndex++;
                session.LastTime = frameTime;
                if (frameTime >= timeSeconds - 1e-6)
                    return new VideoFrameData(frame, session.Width, session.Height, frameTime);
                // 要求時刻より前なら読み捨てて次へ。
            }
        }
        catch (OperationCanceledException)
        {
            // 途中キャンセルはフレーム境界が崩れるのでセッションを捨てる。
            DisposeSession();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>現在のセッションで timeSeconds を出せるか。</summary>
    private bool CanServe(string sourcePath, int maxWidth, double timeSeconds)
    {
        var s = _session;
        if (s is null || s.Ended)
            return false;
        if (HasExitedQuietly(s.Process))
            return false;
        if (s.SourcePath != sourcePath || s.MaxWidth != maxWidth)
            return false;
        if (timeSeconds < s.StartSeconds - 1e-6)
            return false; // 巻き戻しは張り直す。
        if (s.NextIndex > 0 && timeSeconds > s.LastTime + 2.0)
            return false; // 大きく先へ飛ぶときも張り直す方が速い。
        return true;
    }

    private async Task StartSessionAsync(
        string sourcePath, double startSeconds, int maxWidth, int outHeight, int frameBytes,
        CancellationToken ct)
    {
        DisposeSession();

        var args = $"-nostdin -loglevel error -ss {startSeconds:F3} -i \"{sourcePath}\" " +
                   $"-an -vf scale={maxWidth}:{outHeight} -pix_fmt bgra -f rawvideo -";
        var startInfo = new ProcessStartInfo
        {
            FileName = _settings.FfmpegExePath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            process.Dispose();
            throw new InvalidOperationException($"ffmpeg の起動に失敗しました: {ex.Message}", ex);
        }

        // stderr を読み捨てないとパイプが詰まって ffmpeg が止まる。失敗は無視する。
        _ = DrainStderrAsync(process.StandardError);

        _session = new Session
        {
            SourcePath = sourcePath,
            MaxWidth = maxWidth,
            Width = maxWidth,
            Height = outHeight,
            FrameBytes = frameBytes,
            StartSeconds = startSeconds,
            Process = process,
            Stream = process.StandardOutput.BaseStream,
            LastTime = startSeconds
        };
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>ストリームから1フレーム分だけ読む。終端なら null。</summary>
    private static async Task<byte[]?> ReadFrameAsync(Session session, CancellationToken ct)
    {
        var buffer = new byte[session.FrameBytes];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await session.Stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
                return null;
            read += n;
        }
        return buffer;
    }

    private async Task<MediaInfo?> GetInfoAsync(string path, CancellationToken ct)
    {
        if (_probeCache.TryGetValue(path, out var cached))
            return cached;
        try
        {
            var info = await _probe.ProbeAsync(path, ct).ConfigureAwait(false);
            _probeCache[path] = info;
            return info;
        }
        catch
        {
            return null;
        }
    }

    private void DisposeSession()
    {
        var s = _session;
        _session = null;
        if (s is null)
            return;
        try
        {
            // Kill(entireProcessTree: true) は子プロセスを探すために全プロセスを
            // 列挙し、保護されたプロセスで Win32Exception を吐いて Output を
            // 埋め尽くす。ffmpeg に子プロセスはいないので単体 Kill で十分。
            if (!HasExitedQuietly(s.Process))
                s.Process.Kill();
        }
        catch
        {
            // 終了済み・権限なしなどは無視する。
        }
        try
        {
            s.Process.Dispose();
        }
        catch
        {
            // 破棄時の失敗は無視する。
        }
    }

    /// <summary>HasExited が例外を吐く場合（ハンドル無効など）は終了扱いにする。</summary>
    private static bool HasExitedQuietly(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>stderr を読み捨てる。プロセス終了時の失敗は無視する。</summary>
    private static async Task DrainStderrAsync(StreamReader reader)
    {
        try
        {
            await reader.ReadToEndAsync().ConfigureAwait(false);
        }
        catch
        {
            // 無視する。
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        DisposeSession();
        _gate.Dispose();
    }

    private sealed class Session
    {
        public required string SourcePath { get; init; }
        public required int MaxWidth { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required int FrameBytes { get; init; }
        public required double StartSeconds { get; init; }
        public required Process Process { get; init; }
        public required Stream Stream { get; init; }
        public int NextIndex { get; set; }
        public double LastTime { get; set; }
        public bool Ended { get; set; }
    }
}
