// 周波数帯解析結果のディスクキャッシュ（タスク4・設計プラン 4章）。
// 解析結果はプロジェクトのキャッシュフォルダ（プロジェクト名.cache）に保存し、
// 素材ファイルのパス＋サイズ＋更新日時が変わらなければ再解析しない。
// 形式は JSON ヘッダー＋バイナリ本体の2ファイル。
//   <sha256(素材のフルパス)>.audio.json … 版・素材の身元・解析条件・配列の形
//   <sha256(素材のフルパス)>.audio.bin  … FFT振幅（フレーム×ビン）＋間引きステレオ波形
// FFT振幅は量が多い（5:42・30fps で約40MB）ため、JSON にはせずバイナリにする。
// キャッシュが壊れている・古い・条件が違う場合は無視して再解析する（例外は出さない）。

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Editor.Core;

namespace Editor.Audio;

/// <summary>ディスクキャッシュに保存・復元する解析結果1件分。</summary>
public sealed class CachedAudioAnalysis
{
    public required string SourcePath { get; init; }
    public required long Length { get; init; }
    public required DateTime LastWriteTimeUtc { get; init; }
    public required int SampleRate { get; init; }
    public required double FrameRate { get; init; }
    public required double DurationSeconds { get; init; }
    public required int FftSize { get; init; }
    public required int StereoRate { get; init; }

    /// <summary>フレームごとの FFT 振幅（ビン数 = FftSize/2）。</summary>
    public required float[][] Magnitudes { get; init; }

    /// <summary>間引き後のステレオ波形（左）。</summary>
    public required float[] StereoLeft { get; init; }

    /// <summary>間引き後のステレオ波形（右）。</summary>
    public required float[] StereoRight { get; init; }
}

/// <summary>周波数帯解析結果のディスクキャッシュ（JSONヘッダー＋バイナリ本体）。</summary>
public static class AudioAnalysisCache
{
    /// <summary>キャッシュ形式の版。解析の仕様が変わったら上げると、古いキャッシュは無視される。</summary>
    private const int CacheVersion = 1;

    /// <summary>プロジェクトのキャッシュフォルダ。「MyProject.veproj」→ 同じフォルダの「MyProject.cache」。</summary>
    public static string GetCacheDirectory(string projectPath)
    {
        var directory = Path.GetDirectoryName(projectPath) ?? string.Empty;
        return Path.Combine(directory, Path.GetFileNameWithoutExtension(projectPath) + ".cache");
    }

    /// <summary>
    /// 素材1件分のキャッシュファイルの基名（拡張子なし）。
    /// 素材のフルパス（小文字化）の SHA-256 を16進数にしたもので、パスに使えない文字や
    /// 長すぎるパスを避ける。同じ素材でもプロジェクトが違えばフォルダが違うため衝突しない。
    /// </summary>
    public static string GetCacheFileBase(string cacheDirectory, string sourcePath)
    {
        var fullPath = Path.GetFullPath(sourcePath).ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(fullPath));
        return Path.Combine(cacheDirectory, Convert.ToHexString(hash).ToLowerInvariant());
    }

    /// <summary>
    /// キャッシュを読み込む。無ければ null。壊れている・古い・条件が違う場合も null
    /// （呼び出し側はそのまま再解析すればよい）。例外は出さない。
    /// </summary>
    public static CachedAudioAnalysis? TryLoad(
        string sourcePath, double frameRate, int fftSize, string cacheDirectory)
    {
        try
        {
            if (!File.Exists(sourcePath))
                return null;
            var info = new FileInfo(sourcePath);
            var fileBase = GetCacheFileBase(cacheDirectory, sourcePath);
            var jsonPath = fileBase + ".audio.json";
            var binPath = fileBase + ".audio.bin";
            if (!File.Exists(jsonPath) || !File.Exists(binPath))
                return null;

            var header = JsonSerializer.Deserialize<CacheHeader>(
                File.ReadAllText(jsonPath), JsonDefaults.Indented);
            if (header is null)
                return null;

            // 身元・解析条件の照合。1つでも合わなければ古いキャッシュとして無視する。
            if (header.Version != CacheVersion)
                return null;
            if (!string.Equals(
                    Path.GetFullPath(header.SourcePath),
                    Path.GetFullPath(sourcePath),
                    StringComparison.OrdinalIgnoreCase))
                return null;
            if (header.Length != info.Length
                || header.LastWriteTimeUtcTicks != info.LastWriteTimeUtc.Ticks)
                return null; // 素材が差し替え・上書きされた。
            if (Math.Abs(header.FrameRate - frameRate) > 1e-9)
                return null; // フレームレートが違う解析結果は使い回せない。
            if (header.FftSize != fftSize || header.BinCount != fftSize / 2)
                return null;
            if (header.FrameCount <= 0 || header.StereoSampleCount < 0
                || header.SampleRate <= 0 || header.StereoRate <= 0)
                return null;

            // バイナリ本体の大きさがヘッダー通りか（途中で切れた書き込みを弾く）。
            var expectedBytes = 4L * ((long)header.FrameCount * header.BinCount
                + 2L * header.StereoSampleCount);
            var bytes = File.ReadAllBytes(binPath);
            if (bytes.Length != expectedBytes)
                return null;

            // バイナリ本体：FFT振幅（フレーム×ビン）→ 左波形 → 右波形の順。
            var magnitudes = new float[header.FrameCount][];
            var offset = 0;
            for (var f = 0; f < header.FrameCount; f++)
            {
                var frame = new float[header.BinCount];
                Buffer.BlockCopy(bytes, offset, frame, 0, header.BinCount * 4);
                offset += header.BinCount * 4;
                magnitudes[f] = frame;
            }
            var left = new float[header.StereoSampleCount];
            Buffer.BlockCopy(bytes, offset, left, 0, header.StereoSampleCount * 4);
            offset += header.StereoSampleCount * 4;
            var right = new float[header.StereoSampleCount];
            Buffer.BlockCopy(bytes, offset, right, 0, header.StereoSampleCount * 4);

            return new CachedAudioAnalysis
            {
                SourcePath = header.SourcePath,
                Length = header.Length,
                LastWriteTimeUtc = new DateTime(header.LastWriteTimeUtcTicks, DateTimeKind.Utc),
                SampleRate = header.SampleRate,
                FrameRate = header.FrameRate,
                DurationSeconds = header.DurationSeconds,
                FftSize = header.FftSize,
                StereoRate = header.StereoRate,
                Magnitudes = magnitudes,
                StereoLeft = left,
                StereoRight = right
            };
        }
        catch
        {
            // 壊れたキャッシュで編集が止まるより、再解析する方を選ぶ。
            return null;
        }
    }

    /// <summary>
    /// 解析結果をキャッシュに保存する（失敗したら例外を投げる。呼び出し側で握りつぶしてよい）。
    /// バイナリ本体を先に、ヘッダーを後に原子的に書く。
    /// 途中で止まっても、ヘッダーと本体の大きさが合わなければ読み込み時に無視される。
    /// </summary>
    public static void Save(CachedAudioAnalysis data, string cacheDirectory)
    {
        Directory.CreateDirectory(cacheDirectory);
        var fileBase = GetCacheFileBase(cacheDirectory, data.SourcePath);
        var binCount = data.FftSize / 2;
        var header = new CacheHeader
        {
            Version = CacheVersion,
            SourcePath = Path.GetFullPath(data.SourcePath),
            Length = data.Length,
            LastWriteTimeUtcTicks = data.LastWriteTimeUtc.Ticks,
            SampleRate = data.SampleRate,
            FrameRate = data.FrameRate,
            DurationSeconds = data.DurationSeconds,
            FftSize = data.FftSize,
            BinCount = binCount,
            FrameCount = data.Magnitudes.Length,
            StereoRate = data.StereoRate,
            StereoSampleCount = data.StereoLeft.Length
        };

        // バイナリ本体：FFT振幅（フレーム×ビン）→ 左波形 → 右波形の順。
        var totalBytes = 4L * ((long)header.FrameCount * binCount + 2L * header.StereoSampleCount);
        if (totalBytes > int.MaxValue)
            throw new InvalidDataException("解析結果が大きすぎてキャッシュに保存できません。");
        var bytes = new byte[(int)totalBytes];
        var offset = 0;
        foreach (var frame in data.Magnitudes)
        {
            if (frame.Length != binCount)
                throw new InvalidDataException("FFT振幅のビン数がヘッダーと合いません。");
            Buffer.BlockCopy(frame, 0, bytes, offset, binCount * 4);
            offset += binCount * 4;
        }
        if (data.StereoRight.Length != data.StereoLeft.Length)
            throw new InvalidDataException("ステレオ波形の左右の長さが合いません。");
        Buffer.BlockCopy(data.StereoLeft, 0, bytes, offset, data.StereoLeft.Length * 4);
        offset += data.StereoLeft.Length * 4;
        Buffer.BlockCopy(data.StereoRight, 0, bytes, offset, data.StereoRight.Length * 4);

        AtomicFile.WriteAllBytes(fileBase + ".audio.bin", bytes);
        AtomicFile.WriteAllText(fileBase + ".audio.json",
            JsonSerializer.Serialize(header, JsonDefaults.Indented));
    }

    /// <summary>キャッシュの JSON ヘッダー（.audio.json の中身）。</summary>
    private sealed class CacheHeader
    {
        public int Version { get; set; }
        public string SourcePath { get; set; } = string.Empty;
        public long Length { get; set; }
        public long LastWriteTimeUtcTicks { get; set; }
        public int SampleRate { get; set; }
        public double FrameRate { get; set; }
        public double DurationSeconds { get; set; }
        public int FftSize { get; set; }
        public int BinCount { get; set; }
        public int FrameCount { get; set; }
        public int StereoRate { get; set; }
        public int StereoSampleCount { get; set; }
    }
}
