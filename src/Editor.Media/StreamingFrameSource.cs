// プレビュー用フレームのストリーミング取得。
// 従来は1フレームごとに ffmpeg プロセスを起動していたため、
// 30fps 再生で毎秒30回プロセスを立ち上げて重かった。
// ここでは ffmpeg を常駐させ、rawvideo をパイプで
// 連続デコードして必要な時刻のフレームを渡す。シーク時のみ張り直す。
// トランジション区間では2つの素材を交互に読むため、セッションは
// 素材ごとに最大2つまで保持する（3つめが要るときは最も古く使ったものを捨てる）。
// 取得に失敗した場合は呼び出し側が単発抽出（ThumbnailService）に
// フォールバックするので、プレビューが真っ黒になることはない。
//
// 逆再生対策：逆再生では素材時刻が毎フレーム少しずつ後退するため、常駐セッションは
// 毎回「巻き戻し」になって張り直し（＝単発抽出並みに遅い）だった。そこで、後退する
// 要求が検出されたら、要求時刻を含む一定区間（チャンク）を1回の ffmpeg 起動で
// まとめてデコードしてメモリにキャッシュし、以後は区間内をキャッシュから返す。
// チャンクはフレーム番号で揃え（開始フレーム番号はチャンク長の倍数）、デコード開始は
// 半フレーム分だけ手前にずらす。これにより、チャンク内フレームの画素は「その時刻へ
// 直接シークして最初に返るフレーム」と一致する（テストで担保する）。
// キャッシュは素材の身元（SourceIdentity）で照合し、上限（バイト数）を超えたら
// 最も古く使ったチャンクから捨てる。

using System.Diagnostics;
using Editor.Core;

namespace Editor.Media;

/// <summary>ffmpeg を常駐させて rawvideo を連続で読むフレーム取得。</summary>
public sealed class StreamingFrameSource : IStreamingFrameSource, IDisposable
{
    private readonly FfmpegSettings _settings;
    private readonly MediaProbe _probe;
    private readonly Dictionary<string, (MediaInfo Info, SourceIdentity Identity)> _probeCache = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>同時に保持する ffmpeg セッションの上限。トランジションで2素材を交互に読むため 2。</summary>
    private const int MaxSessions = 2;

    /// <summary>素材（パス＋横幅）ごとの常駐セッション。</summary>
    private readonly Dictionary<(string SourcePath, int MaxWidth), Session> _sessions = new();

    /// <summary>セッションの使用順（末尾が直近で使ったもの）。3つめが要るとき先頭を捨てる。</summary>
    private readonly List<(string SourcePath, int MaxWidth)> _sessionOrder = new();
    private bool _disposed;

    // ---- 逆再生用チャンクキャッシュ ----

    /// <summary>1チャンクの目標サイズ（バイト）。フレーム数 = この値 ÷ 1フレームのバイト数（下限・上限あり）。</summary>
    private const long ChunkTargetBytes = 48L * 1024 * 1024;

    /// <summary>チャンクキャッシュ全体の上限（バイト）。超えたら古いチャンクから捨てる（最新は残す）。</summary>
    private const long MaxChunkCacheBytes = 128L * 1024 * 1024;

    /// <summary>同時に保持するチャンク数の上限。</summary>
    private const int MaxChunks = 4;

    /// <summary>1チャンクのフレーム数の下限・上限。</summary>
    private const int MinChunkFrames = 4;
    private const int MaxChunkFrames = 120;

    /// <summary>後退とみなす最大のステップ幅（秒）。これより大きく戻るのはシーク扱いで従来経路を使う。</summary>
    private const double MaxReverseStepSeconds = 1.5;

    /// <summary>チャンクキャッシュ（キー：素材パス＋横幅）。</summary>
    private readonly Dictionary<(string SourcePath, int MaxWidth), Chunk> _chunks = new();

    /// <summary>チャンクの使用順（末尾が直近）。上限超過時に先頭から捨てる。</summary>
    private readonly List<(string SourcePath, int MaxWidth)> _chunkOrder = new();

    /// <summary>キーごとの直近の要求時刻。後退ステップの検出に使う。</summary>
    private readonly Dictionary<(string SourcePath, int MaxWidth), double> _lastRequested = new();

    /// <summary>テスト用：現在のチャンクキャッシュの件数と総バイト数。</summary>
    internal (int Count, long Bytes) ChunkCacheStats
        => (_chunks.Count, _chunks.Values.Sum(c => c.Bytes));

    /// <summary>ハードウェアデコードが使えない環境と分かったら true（以後ソフトウェアで動く）。</summary>
    private bool _hwaccelDisabled;

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

        var key = (sourcePath, maxWidth);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var info = await GetInfoAsync(sourcePath, ct).ConfigureAwait(false);
            if (info is null || !info.HasVideo || info.Width <= 0 || info.Height <= 0)
                throw new InvalidOperationException("映像情報が取れませんでした。");

            var fps = info.FrameRate > 0 ? info.FrameRate : 30.0;
            var outHeight = Math.Max(2, (int)Math.Round(info.Height * (double)maxWidth / info.Width));
            var frameBytes = maxWidth * outHeight * 4;

            // 逆再生用チャンクキャッシュ：区間内なら即返す。
            // チャンクから返すときは、その素材の常駐セッションを捨てる。セッションの
            // 読み進み位置は現在の要求時刻と無関係になっているため、残しておくと
            // 次の順方向要求で古い位置のフレームを返しかねない。
            if (TryServeFromChunk(key, timeSeconds, fps, out var chunkFrame))
            {
                DisposeSession(key);
                _lastRequested[key] = timeSeconds;
                return chunkFrame;
            }

            // 後退ステップ（逆再生の連続要求）を検出したら、区間をまとめて読み込む。
            var isReverseStep = _lastRequested.TryGetValue(key, out var prevRequested)
                && timeSeconds < prevRequested - 1e-6
                && timeSeconds >= prevRequested - MaxReverseStepSeconds;
            if (isReverseStep
                && await LoadChunkAsync(sourcePath, key, timeSeconds, maxWidth, outHeight, frameBytes, fps, ct)
                    .ConfigureAwait(false)
                && TryServeFromChunk(key, timeSeconds, fps, out chunkFrame))
            {
                DisposeSession(key); // 上のチャンク返却と同じ理由でセッションを捨てる。
                _lastRequested[key] = timeSeconds;
                return chunkFrame;
            }
            _lastRequested[key] = timeSeconds;

            if (!_sessions.TryGetValue(key, out var session) || !CanServe(session, timeSeconds))
                session = await StartSessionAsync(sourcePath, timeSeconds, maxWidth, outHeight, frameBytes, ct)
                    .ConfigureAwait(false);
            else
                TouchSession(key); // 使えたので直近使用として記録する。

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var frame = await ReadFrameAsync(session, ct).ConfigureAwait(false);
                if (frame is null)
                {
                    // 1フレームも読めずに終わった場合、ハードウェアデコードが
                    // 原因かもしれないので、1回だけソフトウェアで張り直す。
                    if (session.NextIndex == 0 && session.Hwaccel && !_hwaccelDisabled)
                    {
                        _hwaccelDisabled = true;
                        session = await StartSessionAsync(sourcePath, timeSeconds, maxWidth, outHeight, frameBytes, ct)
                            .ConfigureAwait(false);
                        continue;
                    }
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
            // 途中キャンセルはフレーム境界が崩れるのでその素材のセッションを捨てる。
            DisposeSession(key);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>そのセッションで timeSeconds を出せるか。</summary>
    private bool CanServe(Session s, double timeSeconds)
    {
        if (s.Ended)
            return false;
        if (HasExitedQuietly(s.Process))
            return false;
        if (timeSeconds < s.StartSeconds - 1e-6)
            return false; // 巻き戻しは張り直す。
        if (s.NextIndex > 0 && timeSeconds > s.LastTime + 2.0)
            return false; // 大きく先へ飛ぶときも張り直す方が速い。
        return true;
    }

    /// <summary>そのキーを直近で使ったものとして記録する（古い順の先頭から捨てるため）。</summary>
    private void TouchSession((string SourcePath, int MaxWidth) key)
    {
        _sessionOrder.Remove(key);
        _sessionOrder.Add(key);
    }

    private async Task<Session> StartSessionAsync(
        string sourcePath, double startSeconds, int maxWidth, int outHeight, int frameBytes,
        CancellationToken ct)
    {
        var key = (sourcePath, maxWidth);

        // 同じ素材の古いセッションがあれば捨てる。
        DisposeSession(key);

        // 上限を超えるときは最も古く使ったセッションを捨てる。
        while (_sessions.Count >= MaxSessions)
            DisposeSession(_sessionOrder[0]);

        // ハードウェアデコード（GPU）が使えればデコードを GPU に逃がす。
        // 使えない環境では起動直後にフレームが出ないので、呼び出し側で
        // ソフトウェアに切り替えて張り直す（GetFrameAsync 参照）。
        var hwaccel = _hwaccelDisabled ? string.Empty : "-hwaccel auto ";
        var args = $"-nostdin -loglevel error {hwaccel}-ss {startSeconds:F3} -i \"{sourcePath}\" " +
                   $"-an -vf scale={maxWidth}:{outHeight} -pix_fmt bgra -f rawvideo -";
        var startInfo = FfmpegProcess.CreateStartInfo(_settings.FfmpegExePath, args);

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

        var session = new Session
        {
            SourcePath = sourcePath,
            MaxWidth = maxWidth,
            Width = maxWidth,
            Height = outHeight,
            FrameBytes = frameBytes,
            StartSeconds = startSeconds,
            Hwaccel = !_hwaccelDisabled,
            Process = process,
            Stream = process.StandardOutput.BaseStream,
            LastTime = startSeconds
        };
        _sessions[key] = session;
        _sessionOrder.Add(key);
        await Task.CompletedTask.ConfigureAwait(false);
        return session;
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

    /// <summary>
    /// 要求時刻に対して「直接シークした場合に最初に返るフレーム」の番号。
    /// 直接シークは -ss が小数第3位までしか渡らないため、実効シーク時刻は
    /// Round(t, 3) になる。その時刻以降で最初のフレーム（pts が実効時刻以上で最小）を返す。
    /// </summary>
    private static long TargetFrameIndex(double timeSeconds, double fps)
    {
        var effective = Math.Round(timeSeconds, 3);
        return Math.Max(0, (long)Math.Ceiling((effective - 1e-9) * fps));
    }

    /// <summary>チャンクキャッシュから返せれば true（終端より後なら frame は null で true）。</summary>
    private bool TryServeFromChunk(
        (string SourcePath, int MaxWidth) key, double timeSeconds, double fps,
        out VideoFrameData? frame)
    {
        frame = null;
        if (!_chunks.TryGetValue(key, out var chunk))
            return false;

        // 素材が差し替えられていたら古いチャンクは捨てる。
        if (chunk.Identity is not { } identity
            || !SourceIdentity.TryCapture(key.SourcePath, out var current)
            || !identity.Matches(current))
        {
            DropChunk(key);
            return false;
        }

        var target = TargetFrameIndex(timeSeconds, fps);
        var local = target - chunk.StartFrameIndex;
        if (local < 0)
            return false; // チャンクより前は対象外（呼び出し側が必要なら読み直す）。
        if (local >= chunk.Frames.Count)
            return chunk.ReachedEnd; // 終端まで読んだチャンクなら「終端より後」として null を返す。

        TouchChunk(key);
        frame = new VideoFrameData(
            chunk.Frames[(int)local], chunk.Width, chunk.Height, target / fps);
        return true;
    }

    /// <summary>
    /// 要求時刻を含むチャンクを1回の ffmpeg 起動でまとめてデコードしてキャッシュする。
    /// 成功したら true。1フレームも読めなければ false（呼び出し側は従来経路に進む）。
    /// </summary>
    private async Task<bool> LoadChunkAsync(
        string sourcePath, (string SourcePath, int MaxWidth) key, double timeSeconds,
        int maxWidth, int outHeight, int frameBytes, double fps, CancellationToken ct)
    {
        var framesPerChunk = (int)Math.Clamp(ChunkTargetBytes / Math.Max(1, frameBytes), MinChunkFrames, MaxChunkFrames);
        var target = TargetFrameIndex(timeSeconds, fps);
        var startIndex = target / framesPerChunk * framesPerChunk;
        var labelStart = startIndex / fps;
        // デコード開始は半フレーム分だけ手前にずらす。-ss の小数第3位への丸めで
        // 開始フレームが落ちるのを防ぎ、最初に読めるフレームが開始フレームと一致するようにする。
        var seekStart = Math.Max(0, labelStart - 0.5 / fps);

        var frames = await DecodeChunkAsync(sourcePath, seekStart, maxWidth, outHeight, frameBytes, framesPerChunk, ct)
            .ConfigureAwait(false);
        if (frames.Count == 0)
            return false;

        DropChunk(key);
        _chunks[key] = new Chunk
        {
            StartFrameIndex = startIndex,
            Frames = frames,
            ReachedEnd = frames.Count < framesPerChunk,
            Width = maxWidth,
            Height = outHeight,
            Bytes = (long)frames.Count * frameBytes,
            Identity = SourceIdentity.TryCapture(sourcePath, out var identity) ? identity : null
        };
        _chunkOrder.Add(key);

        // 上限を超えたら最も古く使ったチャンクから捨てる（残り1つは上限超えでも残す）。
        while (_chunkOrder.Count > 1
            && (_chunks.Count > MaxChunks || _chunks.Values.Sum(c => c.Bytes) > MaxChunkCacheBytes))
            DropChunk(_chunkOrder[0]);
        return true;
    }

    /// <summary>ffmpeg を1回起動して最大 count フレームを連続デコードする（セッションには登録しない）。</summary>
    private async Task<List<byte[]>> DecodeChunkAsync(
        string sourcePath, double seekStartSeconds, int maxWidth, int outHeight,
        int frameBytes, int count, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var hwaccel = _hwaccelDisabled ? string.Empty : "-hwaccel auto ";
            var args = $"-nostdin -loglevel error {hwaccel}-ss {seekStartSeconds:F3} -i \"{sourcePath}\" " +
                       $"-an -vf scale={maxWidth}:{outHeight} -pix_fmt bgra -f rawvideo -";
            var startInfo = FfmpegProcess.CreateStartInfo(_settings.FfmpegExePath, args);
            var process = new Process { StartInfo = startInfo };
            try
            {
                process.Start();
            }
            catch (Exception)
            {
                process.Dispose();
                return new List<byte[]>(); // 起動できない場合は従来経路に任せる。
            }
            _ = DrainStderrAsync(process.StandardError);

            var session = new Session
            {
                SourcePath = sourcePath,
                MaxWidth = maxWidth,
                Width = maxWidth,
                Height = outHeight,
                FrameBytes = frameBytes,
                StartSeconds = seekStartSeconds,
                Hwaccel = !_hwaccelDisabled,
                Process = process,
                Stream = process.StandardOutput.BaseStream,
                LastTime = seekStartSeconds
            };
            var frames = new List<byte[]>(count);
            try
            {
                while (frames.Count < count)
                {
                    ct.ThrowIfCancellationRequested();
                    var frame = await ReadFrameAsync(session, ct).ConfigureAwait(false);
                    if (frame is null)
                        break;
                    frames.Add(frame);
                }
            }
            finally
            {
                KillSession(session);
            }

            // 1フレームも出なかった場合、ハードウェアデコードが原因かもしれないので
            // 1回だけソフトウェアで読み直す（常駐セッション側と同じ扱い）。
            if (frames.Count == 0 && attempt == 0 && !_hwaccelDisabled)
            {
                _hwaccelDisabled = true;
                continue;
            }
            return frames;
        }
    }

    /// <summary>そのキーを直近で使ったチャンクとして記録する。</summary>
    private void TouchChunk((string SourcePath, int MaxWidth) key)
    {
        _chunkOrder.Remove(key);
        _chunkOrder.Add(key);
    }

    /// <summary>そのキーのチャンクを捨てる（無ければ何もしない）。</summary>
    private void DropChunk((string SourcePath, int MaxWidth) key)
    {
        _chunks.Remove(key);
        _chunkOrder.Remove(key);
    }

    private async Task<MediaInfo?> GetInfoAsync(string path, CancellationToken ct)
    {
        // 素材が差し替えられていないか、身元（パス＋サイズ＋更新日時）で照合する。
        // 以前はパスだけで引いていたため、差し替え後の古い映像情報が残り続けた。
        if (_probeCache.TryGetValue(path, out var cached)
            && SourceIdentity.TryCapture(path, out var current)
            && cached.Identity.Matches(current))
            return cached.Info;
        try
        {
            var info = await _probe.ProbeAsync(path, ct).ConfigureAwait(false);
            if (SourceIdentity.TryCapture(path, out var fresh))
                _probeCache[path] = (info, fresh);
            return info;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>そのキーのセッションを捨てる（無ければ何もしない）。</summary>
    private void DisposeSession((string SourcePath, int MaxWidth) key)
    {
        if (!_sessions.TryGetValue(key, out var s))
            return;
        _sessions.Remove(key);
        _sessionOrder.Remove(key);
        KillSession(s);
    }

    /// <summary>ffmpeg プロセスを終了して破棄する。</summary>
    private static void KillSession(Session s)
    {
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
        foreach (var key in _sessionOrder.ToList())
            DisposeSession(key);
        _chunks.Clear();
        _chunkOrder.Clear();
        _lastRequested.Clear();
        _gate.Dispose();
    }

    /// <summary>逆再生用にまとめてデコードした区間分のフレーム。</summary>
    private sealed class Chunk
    {
        /// <summary>区間の先頭フレームの番号（素材先頭を 0 とする連番）。</summary>
        public required long StartFrameIndex { get; init; }
        public required List<byte[]> Frames { get; init; }
        /// <summary>素材の終端まで読み切ったか（この先の時刻は null が正解）。</summary>
        public required bool ReachedEnd { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required long Bytes { get; init; }
        /// <summary>読み込み時点の素材の身元。差し替え検出用（取れなかった場合は null で、常に無効扱い）。</summary>
        public required SourceIdentity? Identity { get; init; }
    }

    private sealed class Session
    {
        public required string SourcePath { get; init; }
        public required int MaxWidth { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required int FrameBytes { get; init; }
        public required double StartSeconds { get; init; }
        public required bool Hwaccel { get; init; }
        public required Process Process { get; init; }
        public required Stream Stream { get; init; }
        public int NextIndex { get; set; }
        public double LastTime { get; set; }
        public bool Ended { get; set; }
    }
}
