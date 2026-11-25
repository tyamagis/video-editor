// 波形データ（タイムラインの波形表示用）のディスクキャッシュ（タスク4・設計プラン 4章）。
// 周波数帯解析と同じくプロジェクトのキャッシュフォルダに保存し、
// 素材ファイルのパス＋サイズ＋更新日時が変わらなければ読み直さない。
// 形式は JSON 1ファイル（<sha256(素材のフルパス)>.waveform.json）。
// ピーク列は 2000 バケット×2（約40KB）と小さいため、JSON のままで十分速い。
// キャッシュが壊れている・古い場合は無視して読み直す（例外は出さない）。

using System.IO;
using System.Text.Json;
using Editor.Core;

namespace Editor.Audio;

/// <summary>波形データのディスクキャッシュ（WaveformReader の前段）。</summary>
public sealed class WaveformCache
{
    /// <summary>キャッシュ形式の版。波形の仕様が変わったら上げると、古いキャッシュは無視される。</summary>
    private const int CacheVersion = 1;

    private readonly WaveformReader _reader;

    /// <summary>波形キャッシュの保存先（プロジェクトのキャッシュフォルダ）。null ならメモリのみで永続化しない。</summary>
    public string? CacheDirectory { get; set; }

    public WaveformCache(WaveformReader reader)
    {
        _reader = reader;
    }

    /// <summary>
    /// 波形を返す。ディスクキャッシュに有効なものがあればそれを使い、
    /// 無ければ読み直してキャッシュに保存する（保存の失敗は無視する）。
    /// </summary>
    public WaveformData GetOrRead(string path, int bucketCount = 2000)
    {
        if (CacheDirectory is not null)
        {
            var cached = TryLoad(path, bucketCount, CacheDirectory);
            if (cached is not null)
                return cached;
        }

        var data = _reader.Read(path, bucketCount);

        if (CacheDirectory is not null)
        {
            try
            {
                Save(path, data, CacheDirectory);
            }
            catch
            {
                // キャッシュの保存失敗で読み込みを止めない（波形自体は読めている）。
            }
        }

        return data;
    }

    /// <summary>
    /// キャッシュを読み込む。無ければ null。壊れている・古い場合は null
    /// （呼び出し側はそのまま読み直せばよい）。例外は出さない。
    /// </summary>
    public static WaveformData? TryLoad(string path, int bucketCount, string cacheDirectory)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var info = new FileInfo(path);
            var cachePath = CacheFileNaming.GetCacheFilePath(cacheDirectory, path, ".waveform.json");
            if (!File.Exists(cachePath))
                return null;

            var entry = JsonSerializer.Deserialize<WaveformCacheEntry>(
                File.ReadAllText(cachePath), JsonDefaults.Indented);
            if (entry is null)
                return null;

            // 身元の照合。1つでも合わなければ古いキャッシュとして無視する。
            // 照合は SourceIdentity に委譲する（従来と同じ：フルパス＋サイズ＋更新日時）。
            if (entry.Version != CacheVersion)
                return null;
            if (!new SourceIdentity(entry.SourcePath, entry.Length, entry.LastWriteTimeUtcTicks)
                    .Matches(info))
                return null; // 素材が差し替え・上書きされた。
            if (entry.BucketCount != bucketCount
                || entry.MinPeaks.Length != bucketCount
                || entry.MaxPeaks.Length != bucketCount)
                return null;

            return new WaveformData
            {
                SourcePath = entry.SourcePath,
                SampleRate = entry.SampleRate,
                DurationSeconds = entry.DurationSeconds,
                MinPeaks = entry.MinPeaks,
                MaxPeaks = entry.MaxPeaks
            };
        }
        catch
        {
            // 壊れたキャッシュで読み込みが止まるより、読み直す方を選ぶ。
            return null;
        }
    }

    /// <summary>波形をキャッシュに保存する（失敗したら例外を投げる。呼び出し側で握りつぶしてよい）。</summary>
    public static void Save(string path, WaveformData data, string cacheDirectory)
    {
        var info = new FileInfo(path);
        var identity = SourceIdentity.Capture(info);
        var entry = new WaveformCacheEntry
        {
            Version = CacheVersion,
            SourcePath = identity.FullPath,
            Length = identity.Length,
            LastWriteTimeUtcTicks = identity.LastWriteTimeUtcTicks,
            SampleRate = data.SampleRate,
            DurationSeconds = data.DurationSeconds,
            BucketCount = data.BucketCount,
            MinPeaks = data.MinPeaks,
            MaxPeaks = data.MaxPeaks
        };
        Directory.CreateDirectory(cacheDirectory);
        var cachePath = CacheFileNaming.GetCacheFilePath(cacheDirectory, path, ".waveform.json");
        AtomicFile.WriteAllText(cachePath, JsonSerializer.Serialize(entry, JsonDefaults.Indented));
    }

    /// <summary>キャッシュの JSON（.waveform.json の中身）。</summary>
    private sealed class WaveformCacheEntry
    {
        public int Version { get; set; }
        public string SourcePath { get; set; } = string.Empty;
        public long Length { get; set; }
        public long LastWriteTimeUtcTicks { get; set; }
        public int SampleRate { get; set; }
        public double DurationSeconds { get; set; }
        public int BucketCount { get; set; }
        public float[] MinPeaks { get; set; } = Array.Empty<float>();
        public float[] MaxPeaks { get; set; } = Array.Empty<float>();
    }
}
