// キャッシュの身元照合・命名の共通ヘルパー（SourceIdentity / CacheFileNaming）のテスト。
// 命名規則と照合結果が従来と同一であること（既存キャッシュが無効にならないこと）と、
// 各キャッシュが共通ヘルパー経由で正しく動くことを対象とする。
// ディスクキャッシュの往復・無効化条件そのものは AnalysisCacheTests / WaveformCacheTests が担う。

using System.Security.Cryptography;
using System.Text;
using Editor.Audio;
using Editor.Core;
using Editor.Media;
using Xunit;

namespace Editor.Tests;

/// <summary>キャッシュの身元照合・命名の共通ヘルパーのテスト。</summary>
public sealed class SourceCacheTests
{
    private static string CreateSource(TempDirectory temp, string name, int size)
    {
        var path = temp.File(name);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    /// <summary>テスト用の小さな WAV（PCM16・モノラル・8000Hz）を作る。</summary>
    private static void WriteWav(string path, double frequencyHz, double amplitude, int seconds = 1)
    {
        const int sampleRate = 8000;
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
            var s = (short)(amplitude * short.MaxValue * Math.Sin(2 * Math.PI * frequencyHz * i / sampleRate));
            writer.Write(s);
        }
    }

    // ---- SourceIdentity ----

    [Fact]
    public void Capture_MatchesLiveFile()
    {
        using var temp = new TempDirectory();
        var sourcePath = CreateSource(temp, "song.mp3", 1234);
        var info = new FileInfo(sourcePath);

        Assert.True(SourceIdentity.TryCapture(sourcePath, out var identity));
        Assert.Equal(Path.GetFullPath(sourcePath), identity.FullPath);
        Assert.Equal(info.Length, identity.Length);
        Assert.Equal(info.LastWriteTimeUtc.Ticks, identity.LastWriteTimeUtcTicks);
        Assert.Equal(info.LastWriteTimeUtc, identity.LastWriteTimeUtc);
        Assert.Equal(DateTimeKind.Utc, identity.LastWriteTimeUtc.Kind);
        Assert.True(identity.Matches(info));
    }

    [Fact]
    public void TryCapture_MissingFile_ReturnsFalse()
    {
        using var temp = new TempDirectory();

        Assert.False(SourceIdentity.TryCapture(temp.File("no-such-file.mp3"), out _));
    }

    [Fact]
    public void TryCapture_InvalidPath_ReturnsFalse()
    {
        // NUL を含むパスは FileInfo の構築で例外になるが、TryCapture は false を返す。
        Assert.False(SourceIdentity.TryCapture("a\0b.mp3", out _));
    }

    [Fact]
    public void Matches_ChangedSize_ReturnsFalse()
    {
        using var temp = new TempDirectory();
        var sourcePath = CreateSource(temp, "song.mp3", 1234);
        Assert.True(SourceIdentity.TryCapture(sourcePath, out var identity));

        File.WriteAllBytes(sourcePath, new byte[5678]);

        Assert.False(identity.Matches(new FileInfo(sourcePath)));
    }

    [Fact]
    public void Matches_ChangedWriteTime_ReturnsFalse()
    {
        using var temp = new TempDirectory();
        var sourcePath = CreateSource(temp, "song.mp3", 1234);
        var info = new FileInfo(sourcePath);
        Assert.True(SourceIdentity.TryCapture(sourcePath, out var identity));

        // 同じサイズでも更新日時が変われば別物（上書き保存された素材など）。
        File.SetLastWriteTimeUtc(sourcePath, info.LastWriteTimeUtc.AddSeconds(1));

        Assert.False(identity.Matches(new FileInfo(sourcePath)));
    }

    [Fact]
    public void Matches_DifferentPath_ReturnsFalse()
    {
        using var temp = new TempDirectory();
        var sourcePath = CreateSource(temp, "song.mp3", 1234);
        var info = new FileInfo(sourcePath);
        var otherPath = CreateSource(temp, "other.mp3", 1234);
        File.SetLastWriteTimeUtc(otherPath, info.LastWriteTimeUtc);
        Assert.True(SourceIdentity.TryCapture(sourcePath, out var identity));

        // 同じ内容・同じサイズ・同じ更新日時でも、パスが違えば別物。
        Assert.False(identity.Matches(new FileInfo(otherPath)));
    }

    [Fact]
    public void Matches_DifferentSpellingOfSameFile_ReturnsTrue()
    {
        using var temp = new TempDirectory();
        var sourcePath = CreateSource(temp, "song.mp3", 1234);
        Assert.True(SourceIdentity.TryCapture(sourcePath, out var identity));

        // 「./」や「sub/..」を含む書き方でも、同じファイルなら一致する
        // （従来の照合が両辺を GetFullPath で正規化していたのと同じ）。
        var dotted = Path.Combine(temp.Dir, ".", "song.mp3");
        var withParent = Path.Combine(temp.Dir, "sub", "..", "song.mp3");
        Assert.True(identity.Matches(new FileInfo(dotted)));
        Assert.True(identity.Matches(new FileInfo(withParent)));
    }

    [Fact]
    public void Matches_CaseDifference_Matches()
    {
        using var temp = new TempDirectory();
        var sourcePath = CreateSource(temp, "song.mp3", 1234);
        var info = new FileInfo(sourcePath);

        // パスの大文字小文字は区別しない（従来のディスクキャッシュの
        // OrdinalIgnoreCase 照合と同じ。Linux では実ファイルは別物だが、
        // 照合の意味を変えないため従来通りとする）。
        var upper = new SourceIdentity(
            Path.GetFullPath(sourcePath).ToUpperInvariant(),
            info.Length, info.LastWriteTimeUtc.Ticks);
        Assert.True(upper.Matches(info));
    }

    [Fact]
    public void Matches_OtherIdentity_ComparesAllFields()
    {
        using var temp = new TempDirectory();
        var sourcePath = CreateSource(temp, "song.mp3", 1234);
        var info = new FileInfo(sourcePath);
        var identity = SourceIdentity.Capture(info);

        Assert.True(identity.Matches(SourceIdentity.Capture(new FileInfo(sourcePath))));
        Assert.False(identity.Matches(
            identity with { Length = identity.Length + 1 }));
        Assert.False(identity.Matches(
            identity with { LastWriteTimeUtcTicks = identity.LastWriteTimeUtcTicks + 1 }));
        Assert.False(identity.Matches(
            identity with { FullPath = Path.Combine(temp.Dir, "other.mp3") }));
    }

    // ---- CacheFileNaming ----

    [Fact]
    public void GetCacheFileBase_EqualsSha256OfLowercasedFullPath()
    {
        using var temp = new TempDirectory();
        var sourcePath = CreateSource(temp, "Song.MP3", 1234);

        // 規則を独立に計算して突き合わせる（従来の AudioAnalysisCache と同一の規則）。
        var fullPath = Path.GetFullPath(sourcePath).ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(fullPath));
        var expected = Path.Combine(
            temp.File("MyProject.cache"), Convert.ToHexString(hash).ToLowerInvariant());

        Assert.Equal(expected, CacheFileNaming.GetCacheFileBase(temp.File("MyProject.cache"), sourcePath));
    }

    [Fact]
    public void GetCacheFileBase_SameFileDifferentSpelling_SameBase()
    {
        using var temp = new TempDirectory();
        var sourcePath = CreateSource(temp, "song.mp3", 1234);
        var cacheDir = temp.File("MyProject.cache");

        var dotted = Path.Combine(temp.Dir, ".", "song.mp3");
        var withParent = Path.Combine(temp.Dir, "sub", "..", "song.mp3");
        var upper = Path.GetFullPath(sourcePath).ToUpperInvariant();

        Assert.Equal(
            CacheFileNaming.GetCacheFileBase(cacheDir, sourcePath),
            CacheFileNaming.GetCacheFileBase(cacheDir, dotted));
        Assert.Equal(
            CacheFileNaming.GetCacheFileBase(cacheDir, sourcePath),
            CacheFileNaming.GetCacheFileBase(cacheDir, withParent));
        // 大文字小文字の違いも同じ基名になる（小文字化しているため）。
        Assert.Equal(
            CacheFileNaming.GetCacheFileBase(cacheDir, sourcePath),
            CacheFileNaming.GetCacheFileBase(cacheDir, upper));
    }

    [Fact]
    public void GetCacheFileBase_DifferentPaths_DifferentBase()
    {
        using var temp = new TempDirectory();
        var cacheDir = temp.File("MyProject.cache");

        Assert.NotEqual(
            CacheFileNaming.GetCacheFileBase(cacheDir, temp.File("a.mp3")),
            CacheFileNaming.GetCacheFileBase(cacheDir, temp.File("b.mp3")));
    }

    [Fact]
    public void GetCacheFilePath_AppendsSuffix()
    {
        using var temp = new TempDirectory();
        var sourcePath = CreateSource(temp, "song.mp3", 1234);
        var cacheDir = temp.File("MyProject.cache");

        Assert.Equal(
            CacheFileNaming.GetCacheFileBase(cacheDir, sourcePath) + ".waveform.json",
            CacheFileNaming.GetCacheFilePath(cacheDir, sourcePath, ".waveform.json"));
    }

    [Fact]
    public void GetCacheDirectory_IsNextToProject()
    {
        using var temp = new TempDirectory();

        Assert.Equal(
            temp.File("MyProject.cache"),
            CacheFileNaming.GetCacheDirectory(temp.File("MyProject.veproj")));
    }

    [Fact]
    public void Delegation_AudioAnalysisCacheMatchesHelper()
    {
        using var temp = new TempDirectory();
        var sourcePath = CreateSource(temp, "song.mp3", 1234);
        var cacheDir = temp.File("MyProject.cache");
        var projectPath = temp.File("MyProject.veproj");

        // 従来の公開 API は委譲になったが、返す値はヘルパーと同一。
        Assert.Equal(
            CacheFileNaming.GetCacheFileBase(cacheDir, sourcePath),
            AudioAnalysisCache.GetCacheFileBase(cacheDir, sourcePath));
        Assert.Equal(
            CacheFileNaming.GetCacheDirectory(projectPath),
            AudioAnalysisCache.GetCacheDirectory(projectPath));
    }

    // ---- 従来との同一性（ディスクキャッシュ） ----

    private static CachedAudioAnalysis SampleAnalysis(
        string sourcePath, long length, DateTime lastWriteTimeUtc)
        => new()
        {
            SourcePath = sourcePath,
            Length = length,
            LastWriteTimeUtc = lastWriteTimeUtc,
            SampleRate = 44100,
            FrameRate = 30,
            DurationSeconds = 0.1,
            FftSize = 2048,
            StereoRate = 4000,
            Magnitudes = new[] { new float[1024] },
            StereoLeft = Array.Empty<float>(),
            StereoRight = Array.Empty<float>()
        };

    [Fact]
    public void AnalysisCache_RelativeSpelling_RoundTrips()
    {
        using var temp = new TempDirectory();
        var sourcePath = CreateSource(temp, "song.mp3", 1234);
        var info = new FileInfo(sourcePath);
        var cacheDir = temp.File("MyProject.cache");
        AudioAnalysisCache.Save(SampleAnalysis(sourcePath, info.Length, info.LastWriteTimeUtc), cacheDir);

        // 保存時と違う書き方（「./」を含む）で読んでも当たる（従来と同じ正規化）。
        var dotted = Path.Combine(temp.Dir, ".", "song.mp3");
        Assert.NotNull(AudioAnalysisCache.TryLoad(dotted, 30, 2048, cacheDir));
    }

    [Fact]
    public void AnalysisCache_CaseChangedHeaderPath_StillLoads()
    {
        using var temp = new TempDirectory();
        var sourcePath = CreateSource(temp, "song.mp3", 1234);
        var info = new FileInfo(sourcePath);
        var cacheDir = temp.File("MyProject.cache");
        AudioAnalysisCache.Save(SampleAnalysis(sourcePath, info.Length, info.LastWriteTimeUtc), cacheDir);

        // ヘッダーの SourcePath の大文字小文字を変えても読める（従来の OrdinalIgnoreCase 照合と同じ）。
        var jsonPath = CacheFileNaming.GetCacheFilePath(cacheDir, sourcePath, ".audio.json");
        var json = File.ReadAllText(jsonPath);
        File.WriteAllText(jsonPath, json.Replace(sourcePath, sourcePath.ToUpperInvariant()));

        Assert.NotNull(AudioAnalysisCache.TryLoad(sourcePath, 30, 2048, cacheDir));
    }

    [Fact]
    public void AnalysisCache_SavePath_EqualsHelperPath()
    {
        using var temp = new TempDirectory();
        var sourcePath = CreateSource(temp, "song.mp3", 1234);
        var info = new FileInfo(sourcePath);
        var cacheDir = temp.File("MyProject.cache");
        AudioAnalysisCache.Save(SampleAnalysis(sourcePath, info.Length, info.LastWriteTimeUtc), cacheDir);

        Assert.True(File.Exists(CacheFileNaming.GetCacheFilePath(cacheDir, sourcePath, ".audio.json")));
        Assert.True(File.Exists(CacheFileNaming.GetCacheFilePath(cacheDir, sourcePath, ".audio.bin")));
    }

    [Fact]
    public void WaveformCache_SavePath_EqualsHelperPath()
    {
        using var temp = new TempDirectory();
        var sourcePath = CreateSource(temp, "tone.wav", 100);
        var cacheDir = temp.File("MyProject.cache");
        var data = new WaveformData
        {
            SourcePath = sourcePath,
            SampleRate = 8000,
            DurationSeconds = 1.0,
            MinPeaks = new float[10],
            MaxPeaks = new float[10]
        };

        WaveformCache.Save(sourcePath, data, cacheDir);

        Assert.True(File.Exists(CacheFileNaming.GetCacheFilePath(cacheDir, sourcePath, ".waveform.json")));
        var loaded = WaveformCache.TryLoad(sourcePath, 10, cacheDir);
        Assert.NotNull(loaded);
        Assert.Equal(8000, loaded!.SampleRate);
    }

    // ---- メモリキャッシュの身元照合 ----

    [Fact]
    public void AudioBandEnergyProvider_SourceReplaced_ReAnalyzes()
    {
        using var temp = new TempDirectory();
        var sourcePath = temp.File("tone.wav");
        WriteWav(sourcePath, frequencyHz: 440, amplitude: 0.5);
        var provider = new AudioBandEnergyProvider();

        provider.Precompute("clip1", sourcePath, 30);
        // 400〜660Hz 帯（440Hz を含む）が最強で、660〜920Hz 帯はほぼ 0。
        var before = provider.GetSpectrumBands("clip1", 0.5, 2, 400, 920, logarithmic: false);
        Assert.Equal(1.0, before[0], 6);
        Assert.True(before[1] < 0.1);

        // 同じパスに 880Hz の音を差し替える（同じサイズ・違う更新日時）。
        WriteWav(sourcePath, frequencyHz: 880, amplitude: 0.5);
        provider.Precompute("clip2", sourcePath, 30);
        // 再解析されていれば 660〜920Hz 帯が最強になる。古い解析結果が
        // 残っていれば 400〜660Hz 帯が最強のままになる。
        var after = provider.GetSpectrumBands("clip2", 0.5, 2, 400, 920, logarithmic: false);
        Assert.True(after[0] < 0.1);
        Assert.Equal(1.0, after[1], 6);
    }

    [Fact]
    public async Task AudioBeatAnalyzer_SameFile_ReturnsCachedResult()
    {
        using var temp = new TempDirectory();
        var sourcePath = temp.File("clicks.wav");
        WriteClickTrack(sourcePath);
        var analyzer = new AudioBeatAnalyzer();

        var first = await analyzer.AnalyzeAsync(sourcePath);
        var second = await analyzer.AnalyzeAsync(sourcePath);

        // 同じファイル（サイズ・更新日時が同じ）なら同じ結果オブジェクトが返る。
        Assert.Same(first, second);
    }

    [Fact]
    public async Task AudioBeatAnalyzer_SourceReplaced_ReAnalyzes()
    {
        using var temp = new TempDirectory();
        var sourcePath = temp.File("clicks.wav");
        WriteClickTrack(sourcePath);
        var analyzer = new AudioBeatAnalyzer();

        var first = await analyzer.AnalyzeAsync(sourcePath);

        // 無音に差し替える（更新日時が変わる）。
        WriteWav(sourcePath, frequencyHz: 440, amplitude: 0.0, seconds: 5);
        var second = await analyzer.AnalyzeAsync(sourcePath);

        // 再解析されており、無音なので検出できない。古い結果が残っていれば
        // 同じオブジェクト（＝検出済みの結果）が返ってしまう。
        Assert.NotSame(first, second);
        Assert.False(second.Detected);
    }

    /// <summary>5秒のクリック音（0.5秒間隔＝120BPM相当）を作る。</summary>
    private static void WriteClickTrack(string path)
    {
        const int sampleRate = 22050;
        const int seconds = 5;
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
            double s = 0;
            var time = (double)i / sampleRate;
            var sinceClick = time % 0.5;
            if (sinceClick < 0.03)
            {
                // 30ms のバースト（ハニング窓付き 1500Hz）。
                var w = 0.5 * (1 - Math.Cos(2 * Math.PI * sinceClick / 0.03));
                s = 0.9 * w * Math.Sin(2 * Math.PI * 1500 * sinceClick);
            }
            writer.Write((short)(s * short.MaxValue));
        }
    }

    // ---- サムネイルキャッシュの身元照合 ----

    [Fact]
    public async Task ThumbnailService_SourceReplaced_ReExtracts()
    {
        // ffmpeg が無ければこのテストは対象外（環境に ffmpeg が無いだけ）。
        if (!await IsFfmpegAvailableAsync())
            return;

        using var temp = new TempDirectory();
        var sourcePath = temp.File("clip.mp4");
        var service = new ThumbnailService(new FfmpegSettings());

        await MakeColorVideoAsync(sourcePath, "red");
        var first = await service.GetFramePngAsync(sourcePath, 0.5, 64);
        Assert.NotNull(first);

        // 同じキー（パス＋時刻＋幅）で2回目はキャッシュが返る。
        var cached = await service.GetFramePngAsync(sourcePath, 0.5, 64);
        Assert.Equal(first, cached);

        // 同じパスに青い動画を差し替える。身元照合が無ければ古い赤フレームが返る。
        await MakeColorVideoAsync(sourcePath, "blue");
        var second = await service.GetFramePngAsync(sourcePath, 0.5, 64);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
    }

    private static async Task<bool> IsFfmpegAvailableAsync()
    {
        try
        {
            var result = await FfmpegProcess.RunAsync("ffmpeg", "-version");
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task MakeColorVideoAsync(string path, string color)
    {
        var args = $"-y -f lavfi -i color=c={color}:s=64x64:r=10:d=1 -c:v libx264 -pix_fmt yuv420p \"{path}\"";
        var result = await FfmpegProcess.RunAsync("ffmpeg", args);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"テスト動画の生成に失敗しました: {result.StandardError}");
    }
}
