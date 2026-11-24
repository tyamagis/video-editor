// 周波数帯解析キャッシュの単体テスト（AudioAnalysisCache）。
// 保存・読み込みの往復と、無効化条件（素材の差し替え・条件の違い・破損）を対象とする。
// 実際の音声解析（NAudio）は扱わない。

using Editor.Audio;
using Xunit;

namespace Editor.Tests;

/// <summary>周波数帯解析のディスクキャッシュ（AudioAnalysisCache）のテスト。</summary>
public sealed class AnalysisCacheTests
{
    private static CachedAudioAnalysis Sample(
        string sourcePath, long length, DateTime lastWriteTimeUtc,
        double frameRate = 30, int fftSize = 2048)
    {
        var magnitudes = new float[3][];
        for (var f = 0; f < 3; f++)
        {
            magnitudes[f] = new float[fftSize / 2];
            for (var b = 0; b < magnitudes[f].Length; b++)
                magnitudes[f][b] = f * 100 + b * 0.25f;
        }
        var left = new float[400];
        var right = new float[400];
        for (var i = 0; i < 400; i++)
        {
            left[i] = i * 0.01f;
            right[i] = -i * 0.01f;
        }
        return new CachedAudioAnalysis
        {
            SourcePath = sourcePath,
            Length = length,
            LastWriteTimeUtc = lastWriteTimeUtc,
            SampleRate = 44100,
            FrameRate = frameRate,
            DurationSeconds = 0.1,
            FftSize = fftSize,
            StereoRate = 4000,
            Magnitudes = magnitudes,
            StereoLeft = left,
            StereoRight = right
        };
    }

    private static string CreateSource(TempDirectory temp, string name, int size)
    {
        var path = temp.File(name);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    // ---- 保存・読み込み ----

    [Fact]
    public void SaveLoad_RoundTrip()
    {
        using var temp = new TempDirectory();
        var sourcePath = CreateSource(temp, "song.mp3", 1234);
        var info = new FileInfo(sourcePath);
        var cacheDir = temp.File("MyProject.cache");
        var data = Sample(sourcePath, info.Length, info.LastWriteTimeUtc);

        AudioAnalysisCache.Save(data, cacheDir);
        var loaded = AudioAnalysisCache.TryLoad(sourcePath, 30, 2048, cacheDir);

        Assert.NotNull(loaded);
        Assert.Equal(44100, loaded!.SampleRate);
        Assert.Equal(30, loaded.FrameRate, 9);
        Assert.Equal(0.1, loaded.DurationSeconds, 9);
        Assert.Equal(2048, loaded.FftSize);
        Assert.Equal(4000, loaded.StereoRate);
        Assert.Equal(3, loaded.Magnitudes.Length);
        Assert.Equal(1024, loaded.Magnitudes[0].Length);
        Assert.Equal(1 * 100 + 5 * 0.25f, loaded.Magnitudes[1][5], 5);
        Assert.Equal(399 * 0.01f, loaded.StereoLeft[399], 5);
        Assert.Equal(-399 * 0.01f, loaded.StereoRight[399], 5);
    }

    [Fact]
    public void Save_CreatesJsonAndBin()
    {
        using var temp = new TempDirectory();
        var sourcePath = CreateSource(temp, "song.mp3", 1234);
        var info = new FileInfo(sourcePath);
        var cacheDir = temp.File("MyProject.cache");

        AudioAnalysisCache.Save(Sample(sourcePath, info.Length, info.LastWriteTimeUtc), cacheDir);

        var fileBase = AudioAnalysisCache.GetCacheFileBase(cacheDir, sourcePath);
        Assert.True(File.Exists(fileBase + ".audio.json"));
        Assert.True(File.Exists(fileBase + ".audio.bin"));
    }

    [Fact]
    public void TryLoad_NoCache_ReturnsNull()
    {
        using var temp = new TempDirectory();
        var sourcePath = CreateSource(temp, "song.mp3", 1234);

        Assert.Null(AudioAnalysisCache.TryLoad(sourcePath, 30, 2048, temp.File("MyProject.cache")));
    }

    [Fact]
    public void TryLoad_MissingSource_ReturnsNull()
    {
        using var temp = new TempDirectory();

        Assert.Null(AudioAnalysisCache.TryLoad(
            temp.File("no-such-file.mp3"), 30, 2048, temp.File("MyProject.cache")));
    }

    // ---- 無効化条件 ----

    [Fact]
    public void TryLoad_ChangedSize_ReturnsNull()
    {
        using var temp = new TempDirectory();
        var sourcePath = CreateSource(temp, "song.mp3", 1234);
        var info = new FileInfo(sourcePath);
        var cacheDir = temp.File("MyProject.cache");
        AudioAnalysisCache.Save(Sample(sourcePath, info.Length, info.LastWriteTimeUtc), cacheDir);

        // 素材を差し替えた（サイズが変わった）ら再解析する。
        File.WriteAllBytes(sourcePath, new byte[5678]);

        Assert.Null(AudioAnalysisCache.TryLoad(sourcePath, 30, 2048, cacheDir));
    }

    [Fact]
    public void TryLoad_ChangedWriteTime_ReturnsNull()
    {
        using var temp = new TempDirectory();
        var sourcePath = CreateSource(temp, "song.mp3", 1234);
        var info = new FileInfo(sourcePath);
        var cacheDir = temp.File("MyProject.cache");
        AudioAnalysisCache.Save(Sample(sourcePath, info.Length, info.LastWriteTimeUtc), cacheDir);

        // 同じサイズでも更新日時が変われば再解析する（上書き保存された素材など）。
        File.SetLastWriteTimeUtc(sourcePath, info.LastWriteTimeUtc.AddSeconds(1));

        Assert.Null(AudioAnalysisCache.TryLoad(sourcePath, 30, 2048, cacheDir));
    }

    [Fact]
    public void TryLoad_DifferentFrameRate_ReturnsNull()
    {
        using var temp = new TempDirectory();
        var sourcePath = CreateSource(temp, "song.mp3", 1234);
        var info = new FileInfo(sourcePath);
        var cacheDir = temp.File("MyProject.cache");
        AudioAnalysisCache.Save(Sample(sourcePath, info.Length, info.LastWriteTimeUtc), cacheDir);

        // フレームレートが違う解析結果は使い回せない（60fps のプロジェクトなど）。
        Assert.Null(AudioAnalysisCache.TryLoad(sourcePath, 60, 2048, cacheDir));
    }

    [Fact]
    public void TryLoad_DifferentFftSize_ReturnsNull()
    {
        using var temp = new TempDirectory();
        var sourcePath = CreateSource(temp, "song.mp3", 1234);
        var info = new FileInfo(sourcePath);
        var cacheDir = temp.File("MyProject.cache");
        AudioAnalysisCache.Save(Sample(sourcePath, info.Length, info.LastWriteTimeUtc), cacheDir);

        Assert.Null(AudioAnalysisCache.TryLoad(sourcePath, 30, 4096, cacheDir));
    }

    [Fact]
    public void TryLoad_DifferentPath_ReturnsNull()
    {
        using var temp = new TempDirectory();
        var sourcePath = CreateSource(temp, "song.mp3", 1234);
        var info = new FileInfo(sourcePath);
        var cacheDir = temp.File("MyProject.cache");
        AudioAnalysisCache.Save(Sample(sourcePath, info.Length, info.LastWriteTimeUtc), cacheDir);

        // 同じ内容でもパスが違えば別物（キャッシュファイル名はパスから決まる）。
        var otherPath = CreateSource(temp, "other.mp3", 1234);
        File.SetLastWriteTimeUtc(otherPath, info.LastWriteTimeUtc);

        Assert.Null(AudioAnalysisCache.TryLoad(otherPath, 30, 2048, cacheDir));
    }

    [Fact]
    public void TryLoad_CorruptJson_ReturnsNull()
    {
        using var temp = new TempDirectory();
        var sourcePath = CreateSource(temp, "song.mp3", 1234);
        var info = new FileInfo(sourcePath);
        var cacheDir = temp.File("MyProject.cache");
        AudioAnalysisCache.Save(Sample(sourcePath, info.Length, info.LastWriteTimeUtc), cacheDir);

        var fileBase = AudioAnalysisCache.GetCacheFileBase(cacheDir, sourcePath);
        File.WriteAllText(fileBase + ".audio.json", "{壊れたヘッダー");

        Assert.Null(AudioAnalysisCache.TryLoad(sourcePath, 30, 2048, cacheDir));
    }

    [Fact]
    public void TryLoad_TruncatedBin_ReturnsNull()
    {
        using var temp = new TempDirectory();
        var sourcePath = CreateSource(temp, "song.mp3", 1234);
        var info = new FileInfo(sourcePath);
        var cacheDir = temp.File("MyProject.cache");
        AudioAnalysisCache.Save(Sample(sourcePath, info.Length, info.LastWriteTimeUtc), cacheDir);

        var fileBase = AudioAnalysisCache.GetCacheFileBase(cacheDir, sourcePath);
        var binPath = fileBase + ".audio.bin";
        var bytes = File.ReadAllBytes(binPath);
        File.WriteAllBytes(binPath, bytes[..(bytes.Length / 2)]); // 半分に切る。

        Assert.Null(AudioAnalysisCache.TryLoad(sourcePath, 30, 2048, cacheDir));
    }

    [Fact]
    public void TryLoad_MissingBin_ReturnsNull()
    {
        using var temp = new TempDirectory();
        var sourcePath = CreateSource(temp, "song.mp3", 1234);
        var info = new FileInfo(sourcePath);
        var cacheDir = temp.File("MyProject.cache");
        AudioAnalysisCache.Save(Sample(sourcePath, info.Length, info.LastWriteTimeUtc), cacheDir);

        var fileBase = AudioAnalysisCache.GetCacheFileBase(cacheDir, sourcePath);
        File.Delete(fileBase + ".audio.bin");

        Assert.Null(AudioAnalysisCache.TryLoad(sourcePath, 30, 2048, cacheDir));
    }

    // ---- キャッシュフォルダの場所 ----

    [Fact]
    public void GetCacheDirectory_IsNextToProject()
    {
        using var temp = new TempDirectory();
        var projectPath = temp.File("MyProject.veproj");

        Assert.Equal(temp.File("MyProject.cache"), AudioAnalysisCache.GetCacheDirectory(projectPath));
    }
}
