// テスト監査（2026-11-25）で追加：Core の未テスト領域と、非有限値・壊れた入力の回帰。
// - AtomicFile（専用テストが無かった）
// - BeatGrid の NaN・無限大（修正前は bpm=+Inf / duration=+Inf で無限ループ）
// - LyricsImport の非有限値（Validate の穴）と UTF-16 BE 読み込み
// - ProjectStore.LoadFromJson の契約（壊れた JSON は InvalidDataException）
// - PreviewResolution（専用テストが無かった）

using System.Text;
using Editor.Core;
using Editor.Render;
using Xunit;

namespace Editor.Tests;

/// <summary>Core の監査追加テスト（AtomicFile / BeatGrid / LyricsImport / ProjectStore / PreviewResolution）。</summary>
public sealed class TestAuditCoreEdgeTests
{
    // ---- AtomicFile ----

    [Fact]
    public void AtomicFile_WriteAllText_RoundTripsAndLeavesNoTempFiles()
    {
        using var temp = new TempDirectory();
        var path = temp.File("note.txt");
        AtomicFile.WriteAllText(path, "こんにちは、世界");
        Assert.Equal("こんにちは、世界", File.ReadAllText(path));
        // 一時ファイル（*.tmp）が残っていないこと。
        Assert.Empty(Directory.GetFiles(temp.Dir, "*.tmp"));
    }

    [Fact]
    public void AtomicFile_WriteAllText_HasNoBom()
    {
        using var temp = new TempDirectory();
        var path = temp.File("note.txt");
        AtomicFile.WriteAllText(path, "abc");
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { (byte)'a', (byte)'b', (byte)'c' }, bytes);
    }

    [Fact]
    public void AtomicFile_WriteAllText_OverwritesExisting()
    {
        using var temp = new TempDirectory();
        var path = temp.File("note.txt");
        AtomicFile.WriteAllText(path, "古い内容です。長いです。");
        AtomicFile.WriteAllText(path, "新");
        Assert.Equal("新", File.ReadAllText(path));
    }

    [Fact]
    public void AtomicFile_WriteAllBytes_CreatesMissingDirectoryAndRoundTrips()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Dir, "sub", "deep", "data.bin");
        var payload = new byte[] { 0, 1, 2, 250, 255 };
        AtomicFile.WriteAllBytes(path, payload);
        Assert.Equal(payload, File.ReadAllBytes(path));
    }

    [Fact]
    public void AtomicFile_WriteAllText_EmptyContent_WritesEmptyFile()
    {
        using var temp = new TempDirectory();
        var path = temp.File("empty.txt");
        AtomicFile.WriteAllText(path, "");
        Assert.Equal("", File.ReadAllText(path));
    }

    // ---- BeatGrid の非有限値（回帰） ----

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void BeatInterval_NonFiniteBpm_Throws(double bpm)
        => Assert.Throws<ArgumentOutOfRangeException>(() => BeatGridCalculator.BeatIntervalSeconds(bpm));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void BeatMapFromBpm_NonFiniteBpm_Throws(double bpm)
        => Assert.Throws<ArgumentOutOfRangeException>(() => BeatMap.FromBpm(bpm, 0, 10));

    [Fact]
    public void BeatMapFromBpm_InfiniteDuration_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => BeatMap.FromBpm(120, 0, double.PositiveInfinity));

    [Fact]
    public void BeatMapFromBpm_NaNDuration_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => BeatMap.FromBpm(120, 0, double.NaN));

    // ---- LyricsImport の非有限値（回帰）と追加の文字コード ----

    [Fact]
    public void LyricsPlan_NaNInterval_ThrowsArgumentException()
    {
        var options = new LyricsImportOptions { IntervalSeconds = double.NaN };
        Assert.Throws<ArgumentException>(() => LyricsImport.PlanClips(new[] { "a" }, options));
    }

    [Fact]
    public void LyricsPlan_InfiniteDuration_ThrowsArgumentException()
    {
        var options = new LyricsImportOptions { ClipDurationSeconds = double.PositiveInfinity };
        Assert.Throws<ArgumentException>(() => LyricsImport.PlanClips(new[] { "a" }, options));
    }

    [Fact]
    public void LyricsPlan_NaNStart_ThrowsArgumentException()
    {
        var options = new LyricsImportOptions { StartSeconds = double.NaN };
        Assert.Throws<ArgumentException>(() => LyricsImport.PlanClips(new[] { "a" }, options));
    }

    [Fact]
    public void LyricsRead_Utf16BeWithBom_ReadsLines()
    {
        using var temp = new TempDirectory();
        var path = temp.File("lyrics.txt");
        var bytes = new byte[] { 0xFE, 0xFF }
            .Concat(Encoding.BigEndianUnicode.GetBytes("一行目\n二行目"))
            .ToArray();
        File.WriteAllBytes(path, bytes);
        var phrases = LyricsFileReader.ReadPhrases(path, new LyricsImportOptions());
        Assert.Equal(new[] { "一行目", "二行目" }, phrases);
    }

    [Fact]
    public void LyricsRead_BomOnlyFile_ThrowsLyricsImportException()
    {
        using var temp = new TempDirectory();
        var path = temp.File("lyrics.txt");
        File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF });
        Assert.Throws<LyricsImportException>(
            () => LyricsFileReader.ReadPhrases(path, new LyricsImportOptions()));
    }

    // ---- ProjectStore.LoadFromJson の契約（回帰：壊れた入力は InvalidDataException） ----

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"Version\":1,\"Bpm\":\"abc\"}")]
    public void ProjectStore_LoadFromJson_BrokenInput_ThrowsInvalidDataException(string json)
        => Assert.Throws<InvalidDataException>(() => ProjectStore.LoadFromJson(json));

    [Fact]
    public void ProjectStore_LoadFromJson_FutureVersion_ThrowsInvalidDataException()
    {
        var json = $"{{\"Version\":{EditorProject.CurrentVersion + 1}}}";
        Assert.Throws<InvalidDataException>(() => ProjectStore.LoadFromJson(json));
    }

    [Fact]
    public void ProjectStore_LoadFromJson_EmptyObject_LoadsWithMigrationDefaults()
    {
        // Version を持たない v0 形式：移行で既定値が補完される。
        var project = ProjectStore.LoadFromJson("{}");
        Assert.Equal(EditorProject.CurrentVersion, project.Version);
        Assert.Equal(120.0, project.Bpm);
        Assert.Equal(1920, project.Width);
        Assert.Equal(1080, project.Height);
    }

    [Fact]
    public void ProjectStore_Load_MissingFile_ThrowsFileNotFound()
    {
        using var temp = new TempDirectory();
        Assert.Throws<FileNotFoundException>(() => ProjectStore.Load(temp.File("missing.veproj")));
    }

    // ---- PreviewResolution ----

    [Fact]
    public void PreviewResolution_ScalesAndNames()
    {
        Assert.Equal(1.0, PreviewResolution.Full.GetScale());
        Assert.Equal(0.5, PreviewResolution.Half.GetScale());
        Assert.Equal(0.25, PreviewResolution.Quarter.GetScale());
        Assert.Equal("フル", PreviewResolution.Full.GetDisplayName());
        Assert.Equal("1/2", PreviewResolution.Half.GetDisplayName());
        Assert.Equal("1/4", PreviewResolution.Quarter.GetDisplayName());
    }

    [Fact]
    public void PreviewResolution_UnknownValue_FallsBackToFull()
    {
        var unknown = (PreviewResolution)99;
        Assert.Equal(1.0, unknown.GetScale());
        Assert.Equal("フル", unknown.GetDisplayName());
    }

    [Fact]
    public void PreviewResolution_ApplyTo_ScalesAndClampsToMinimum2()
    {
        Assert.Equal((960, 540), PreviewResolution.Half.ApplyTo(1920, 1080));
        Assert.Equal((480, 270), PreviewResolution.Quarter.ApplyTo(1920, 1080));
        Assert.Equal((1920, 1080), PreviewResolution.Full.ApplyTo(1920, 1080));
        // 小さすぎるサイズは最小 2 に丸める。0 や負も同様。
        Assert.Equal((2, 2), PreviewResolution.Quarter.ApplyTo(4, 4));
        Assert.Equal((2, 2), PreviewResolution.Full.ApplyTo(0, -10));
    }
}
