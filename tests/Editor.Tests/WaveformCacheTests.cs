// 波形キャッシュの単体テスト（WaveformCache）。
// 小さな WAV ファイルを実際に作り、読み込み・キャッシュの再利用・
// 素材の差し替え・キャッシュの破損を対象とする。

using Editor.Audio;
using Xunit;

namespace Editor.Tests;

/// <summary>波形データのディスクキャッシュ（WaveformCache）のテスト。</summary>
public sealed class WaveformCacheTests
{
    /// <summary>テスト用の小さな WAV（PCM16・モノラル・8000Hz・1秒）を作る。</summary>
    private static void WriteWav(string path, double amplitude, int sampleRate = 8000)
    {
        const int seconds = 1;
        var sampleCount = sampleRate * seconds;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF".ToCharArray());
        writer.Write(36 + sampleCount * 2);
        writer.Write("WAVE".ToCharArray());
        writer.Write("fmt ".ToCharArray());
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)1); // モノラル
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data".ToCharArray());
        writer.Write(sampleCount * 2);
        for (var i = 0; i < sampleCount; i++)
        {
            var s = (short)(amplitude * short.MaxValue * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
            writer.Write(s);
        }
    }

    private static string CacheFilePath(string cacheDirectory, string sourcePath)
        => AudioAnalysisCache.GetCacheFileBase(cacheDirectory, sourcePath) + ".waveform.json";

    [Fact]
    public void GetOrRead_FirstRead_WritesCacheFile()
    {
        using var temp = new TempDirectory();
        var sourcePath = temp.File("tone.wav");
        WriteWav(sourcePath, amplitude: 0.5);
        var cacheDir = temp.File("MyProject.cache");
        var cache = new WaveformCache(new WaveformReader()) { CacheDirectory = cacheDir };

        var data = cache.GetOrRead(sourcePath);

        Assert.Equal(8000, data.SampleRate);
        Assert.Equal(1.0, data.DurationSeconds, 3);
        Assert.Equal(2000, data.BucketCount);
        Assert.True(File.Exists(CacheFilePath(cacheDir, sourcePath)));
        // 440Hz・振幅 0.5 の正弦波なので、ピークは 0.5 前後のはず。
        Assert.True(data.MaxPeaks.Max() > 0.4 && data.MaxPeaks.Max() <= 0.5);
        Assert.True(data.MinPeaks.Min() < -0.4 && data.MinPeaks.Min() >= -0.5);
    }

    [Fact]
    public void GetOrRead_SecondRead_UsesCache()
    {
        using var temp = new TempDirectory();
        var sourcePath = temp.File("tone.wav");
        WriteWav(sourcePath, amplitude: 0.5);
        var cacheDir = temp.File("MyProject.cache");
        var cache = new WaveformCache(new WaveformReader()) { CacheDirectory = cacheDir };

        var first = cache.GetOrRead(sourcePath);
        // キャッシュファイルの更新日時を古い固定値にしておく。
        // 2回目に書き直されれば日時が変わるので、再利用したかどうかが分かる。
        var cachePath = CacheFilePath(cacheDir, sourcePath);
        var oldTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(cachePath, oldTime);

        var second = cache.GetOrRead(sourcePath);

        Assert.Equal(oldTime, File.GetLastWriteTimeUtc(cachePath)); // 書き直されていない。
        Assert.Equal(first.MaxPeaks, second.MaxPeaks);
        Assert.Equal(first.MinPeaks, second.MinPeaks);
    }

    [Fact]
    public void GetOrRead_SourceChanged_ReReads()
    {
        using var temp = new TempDirectory();
        var sourcePath = temp.File("tone.wav");
        WriteWav(sourcePath, amplitude: 0.5);
        var cacheDir = temp.File("MyProject.cache");
        var cache = new WaveformCache(new WaveformReader()) { CacheDirectory = cacheDir };

        var first = cache.GetOrRead(sourcePath);
        Assert.True(first.MaxPeaks.Max() > 0.4);

        // 素材を無音に差し替える（同じサイズでも更新日時が変わる）。
        WriteWav(sourcePath, amplitude: 0.0);
        var second = cache.GetOrRead(sourcePath);

        Assert.All(second.MaxPeaks, peak => Assert.Equal(0.0, peak, 5));
        Assert.All(second.MinPeaks, peak => Assert.Equal(0.0, peak, 5));
    }

    [Fact]
    public void GetOrRead_CorruptCache_ReReadsAndRepairs()
    {
        using var temp = new TempDirectory();
        var sourcePath = temp.File("tone.wav");
        WriteWav(sourcePath, amplitude: 0.5);
        var cacheDir = temp.File("MyProject.cache");
        var cache = new WaveformCache(new WaveformReader()) { CacheDirectory = cacheDir };

        _ = cache.GetOrRead(sourcePath);
        var cachePath = CacheFilePath(cacheDir, sourcePath);
        File.WriteAllText(cachePath, "{壊れたキャッシュ");

        // 壊れたキャッシュは無視して読み直し、キャッシュも直る（例外は出ない）。
        var data = cache.GetOrRead(sourcePath);

        Assert.True(data.MaxPeaks.Max() > 0.4);
        Assert.StartsWith("{", File.ReadAllText(cachePath));
        Assert.NotNull(WaveformCache.TryLoad(sourcePath, 2000, cacheDir));
    }

    [Fact]
    public void GetOrRead_NoCacheDirectory_WorksWithoutCache()
    {
        using var temp = new TempDirectory();
        var sourcePath = temp.File("tone.wav");
        WriteWav(sourcePath, amplitude: 0.5);
        var cache = new WaveformCache(new WaveformReader()); // CacheDirectory は null。

        var data = cache.GetOrRead(sourcePath);

        Assert.Equal(2000, data.BucketCount);
        Assert.True(data.MaxPeaks.Max() > 0.4);
        // キャッシュファイルはどこにも作られない。
        Assert.Empty(Directory.GetFiles(temp.Dir, "*.waveform.json"));
    }

    [Fact]
    public void TryLoad_BucketCountMismatch_ReturnsNull()
    {
        using var temp = new TempDirectory();
        var sourcePath = temp.File("tone.wav");
        WriteWav(sourcePath, amplitude: 0.5);
        var cacheDir = temp.File("MyProject.cache");
        var cache = new WaveformCache(new WaveformReader()) { CacheDirectory = cacheDir };

        _ = cache.GetOrRead(sourcePath, bucketCount: 2000);

        // バケット数が違うキャッシュは使えない。
        Assert.Null(WaveformCache.TryLoad(sourcePath, 1000, cacheDir));
        Assert.NotNull(WaveformCache.TryLoad(sourcePath, 2000, cacheDir));
    }
}
