// 歌詞の一括流し込みのテスト（LyricsFileReader / LyricsImport）。
// ファイル読み込み（UTF-8・BOM付きUTF-8・Shift_JIS・UTF-16・空ファイル・
// 文字コード不明・存在しないファイル）、空行の扱い、配置計算、
// プロジェクトへの追加（テキストトラックの作成・再利用、既定値）を対象とする。

using System.Text;
using Editor.Core;
using Xunit;

namespace Editor.Tests;

/// <summary>歌詞の一括流し込み（LyricsImport）のテスト。</summary>
public sealed class LyricsImportTests
{
    static LyricsImportTests()
    {
        // Shift_JIS のテストファイルを作るためにコードページのプロバイダを登録する。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static Encoding ShiftJis => Encoding.GetEncoding(932);

    private static LyricsImportOptions DefaultOptions(double start = 10) => new()
    {
        StartSeconds = start,
        IntervalSeconds = 3,
        ClipDurationSeconds = 2.5
    };

    private static string WriteTextFile(TempDirectory temp, string name, string text, Encoding encoding)
    {
        var path = temp.File(name);
        File.WriteAllText(path, text, encoding);
        return path;
    }

    private static string WriteBytesFile(TempDirectory temp, string name, byte[] bytes)
    {
        var path = temp.File(name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // ---- ファイル読み込み：エンコーディング ----

    /// <summary>UTF-8（BOMなし）の歌詞ファイルが読めること。</summary>
    [Fact]
    public void ReadPhrases_Utf8_ReadsLines()
    {
        using var temp = new TempDirectory();
        var path = WriteTextFile(temp, "lyrics.txt", "あいうえお\nかきくけこ\nさしすせそ", new UTF8Encoding(false));

        var phrases = LyricsFileReader.ReadPhrases(path, DefaultOptions());

        Assert.Equal(new[] { "あいうえお", "かきくけこ", "さしすせそ" }, phrases);
    }

    /// <summary>BOM付きUTF-8の歌詞ファイルが読めること（BOMが1行目に残らないこと）。</summary>
    [Fact]
    public void ReadPhrases_Utf8WithBom_StripsBom()
    {
        using var temp = new TempDirectory();
        var path = WriteTextFile(temp, "lyrics.txt", "あいうえお\nかきくけこ", new UTF8Encoding(true));

        var phrases = LyricsFileReader.ReadPhrases(path, DefaultOptions());

        Assert.Equal(new[] { "あいうえお", "かきくけこ" }, phrases);
        // xUnit 2.9.2 の Assert.DoesNotContain(string, string) は、含んでいない
        // "\uFEFF" に対しても DoesNotContainException を投げるため使わない
        // （実測済み）。代わりに素の string.Contains で確認する。
        Assert.False(phrases[0].Contains('\uFEFF'));
    }

    /// <summary>Shift_JISの歌詞ファイルが読めること（UTF-8として読めないバイト列はShift_JISで読み直す）。</summary>
    [Fact]
    public void ReadPhrases_ShiftJis_FallsBackToShiftJis()
    {
        using var temp = new TempDirectory();
        var path = WriteTextFile(temp, "lyrics.txt", "あいうえお\nかきくけこ", ShiftJis);
        // 念のため Shift_JIS のバイト列であることを確認する（UTF-8 として読めないこと）。
        Assert.Throws<DecoderFallbackException>(() =>
            new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path)));

        var phrases = LyricsFileReader.ReadPhrases(path, DefaultOptions());

        Assert.Equal(new[] { "あいうえお", "かきくけこ" }, phrases);
    }

    /// <summary>UTF-16 LE（BOM付き）の歌詞ファイルが読めること（おまけ対応）。</summary>
    [Fact]
    public void ReadPhrases_Utf16LeWithBom_ReadsLines()
    {
        using var temp = new TempDirectory();
        var path = WriteTextFile(temp, "lyrics.txt", "あいうえお\nかきくけこ", Encoding.Unicode);

        var phrases = LyricsFileReader.ReadPhrases(path, DefaultOptions());

        Assert.Equal(new[] { "あいうえお", "かきくけこ" }, phrases);
    }

    /// <summary>UTF-8 としても Shift_JIS としても読めないバイト列はエラーになること。</summary>
    [Fact]
    public void ReadPhrases_UndecodableBytes_ThrowsLyricsImportException()
    {
        using var temp = new TempDirectory();
        // 0x81 は Shift_JIS の2バイト文字の先行バイトだが、後続の 0x20 は
        // 後続バイトとして無効（UTF-8 としても無効）。
        // なお単独の 0xFF は Shift_JIS では U+F8F3 にデコードされるため
        // （実測済み）、「読めないバイト列」の例にはならない。
        var path = WriteBytesFile(temp, "lyrics.txt", new byte[] { 0x81, 0x20 });

        var ex = Assert.Throws<LyricsImportException>(() =>
            LyricsFileReader.ReadPhrases(path, DefaultOptions()));

        Assert.Contains("文字コード", ex.Message);
    }

    /// <summary>存在しないファイルはエラーになること。</summary>
    [Fact]
    public void ReadPhrases_MissingFile_ThrowsLyricsImportException()
    {
        using var temp = new TempDirectory();

        var ex = Assert.Throws<LyricsImportException>(() =>
            LyricsFileReader.ReadPhrases(temp.File("no-such-file.txt"), DefaultOptions()));

        Assert.Contains("開けませんでした", ex.Message);
    }

    // ---- ファイル読み込み：行の扱い ----

    /// <summary>CRLF・LF・CR が混ざったファイルでも1行ずつ読めること。</summary>
    [Fact]
    public void ReadPhrases_MixedLineEndings_ReadsLines()
    {
        using var temp = new TempDirectory();
        var path = WriteTextFile(temp, "lyrics.txt", "one\r\ntwo\rthree\nfour", new UTF8Encoding(false));

        var phrases = LyricsFileReader.ReadPhrases(path, DefaultOptions());

        Assert.Equal(new[] { "one", "two", "three", "four" }, phrases);
    }

    /// <summary>末尾の改行で余計な空行が増えないこと。</summary>
    [Fact]
    public void ReadPhrases_TrailingNewline_DoesNotAddExtraPhrase()
    {
        using var temp = new TempDirectory();
        var path = WriteTextFile(temp, "lyrics.txt", "one\ntwo\n", new UTF8Encoding(false));

        var phrases = LyricsFileReader.ReadPhrases(path, DefaultOptions());

        Assert.Equal(new[] { "one", "two" }, phrases);
    }

    /// <summary>行の前後の空白（全角スペース含む）は除かれること。</summary>
    [Fact]
    public void ReadPhrases_LinesAreTrimmed()
    {
        using var temp = new TempDirectory();
        var path = WriteTextFile(temp, "lyrics.txt", "  one  \n\u3000two\u3000", new UTF8Encoding(false));

        var phrases = LyricsFileReader.ReadPhrases(path, DefaultOptions());

        Assert.Equal(new[] { "one", "two" }, phrases);
    }

    /// <summary>空行は既定でスキップされること（空白だけの行も含む）。</summary>
    [Fact]
    public void ReadPhrases_EmptyLines_SkippedByDefault()
    {
        using var temp = new TempDirectory();
        var path = WriteTextFile(temp, "lyrics.txt", "one\n\n   \n\u3000\ntwo\n", new UTF8Encoding(false));

        var phrases = LyricsFileReader.ReadPhrases(path, DefaultOptions());

        Assert.Equal(new[] { "one", "two" }, phrases);
    }

    /// <summary>空行をスキップしない設定では、空行も空のフレーズとして残り、タイミングがずれないこと。</summary>
    [Fact]
    public void ReadPhrases_SkipEmptyLinesFalse_KeepsEmptyLines()
    {
        using var temp = new TempDirectory();
        var path = WriteTextFile(temp, "lyrics.txt", "one\n\ntwo", new UTF8Encoding(false));
        var options = DefaultOptions();
        options.SkipEmptyLines = false;

        var phrases = LyricsFileReader.ReadPhrases(path, options);

        Assert.Equal(new[] { "one", string.Empty, "two" }, phrases);
    }

    /// <summary>空のファイルはエラーになること（何も配置しない）。</summary>
    [Fact]
    public void ReadPhrases_EmptyFile_ThrowsLyricsImportException()
    {
        using var temp = new TempDirectory();
        var path = WriteBytesFile(temp, "lyrics.txt", Array.Empty<byte>());

        var ex = Assert.Throws<LyricsImportException>(() =>
            LyricsFileReader.ReadPhrases(path, DefaultOptions()));

        Assert.Contains("行がありません", ex.Message);
    }

    /// <summary>空行だけのファイルもエラーになること（スキップ後に0行になるため）。</summary>
    [Fact]
    public void ReadPhrases_OnlyEmptyLines_ThrowsLyricsImportException()
    {
        using var temp = new TempDirectory();
        var path = WriteTextFile(temp, "lyrics.txt", "\n\n   \n", new UTF8Encoding(false));

        Assert.Throws<LyricsImportException>(() =>
            LyricsFileReader.ReadPhrases(path, DefaultOptions()));
    }

    // ---- 配置計算 ----

    /// <summary>開始時刻は開始位置から間隔ずつ進み、長さは全行同じになること。</summary>
    [Fact]
    public void PlanClips_ComputesStartTimesFromStartAndInterval()
    {
        var plan = LyricsImport.PlanClips(
            new[] { "one", "two", "three" }, DefaultOptions(start: 10));

        Assert.Equal(3, plan.Count);
        Assert.Equal(new[] { 10.0, 13.0, 16.0 }, plan.Select(p => p.StartSeconds));
        Assert.All(plan, p => Assert.Equal(2.5, p.DurationSeconds));
        Assert.Equal(new[] { "one", "two", "three" }, plan.Select(p => p.Text));
    }

    /// <summary>開始位置の負は 0 に丸められること。</summary>
    [Fact]
    public void PlanClips_NegativeStart_ClampedToZero()
    {
        var options = DefaultOptions(start: -5);

        var plan = LyricsImport.PlanClips(new[] { "one", "two" }, options);

        Assert.Equal(new[] { 0.0, 3.0 }, plan.Select(p => p.StartSeconds));
    }

    /// <summary>間隔が 0 以下のときはエラーになること。</summary>
    [Fact]
    public void PlanClips_NonPositiveInterval_ThrowsArgumentException()
    {
        var options = DefaultOptions();
        options.IntervalSeconds = 0;

        Assert.Throws<ArgumentException>(() =>
            LyricsImport.PlanClips(new[] { "one" }, options));
    }

    /// <summary>長さが 0 以下のときはエラーになること。</summary>
    [Fact]
    public void PlanClips_NonPositiveDuration_ThrowsArgumentException()
    {
        var options = DefaultOptions();
        options.ClipDurationSeconds = -1;

        Assert.Throws<ArgumentException>(() =>
            LyricsImport.PlanClips(new[] { "one" }, options));
    }

    /// <summary>間隔が長さより短い場合も、指定どおりに重ねて配置すること（エラーにしない）。</summary>
    [Fact]
    public void PlanClips_IntervalShorterThanDuration_AllowsOverlap()
    {
        var options = DefaultOptions(start: 0);
        options.IntervalSeconds = 1;
        options.ClipDurationSeconds = 3;

        var plan = LyricsImport.PlanClips(new[] { "one", "two" }, options);

        Assert.Equal(new[] { 0.0, 1.0 }, plan.Select(p => p.StartSeconds));
        Assert.All(plan, p => Assert.Equal(3.0, p.DurationSeconds));
    }

    // ---- プロジェクトへの追加 ----

    private static EditorProject SampleProject()
    {
        var project = new EditorProject { Name = "テスト", Version = 1 };
        var track = project.AddTrack(TrackKind.Video, "ビデオ 1");
        track.Clips.Add(new Clip
        {
            Name = "クリップ1",
            Kind = ClipKind.Video,
            StartSeconds = 0,
            DurationSeconds = 10
        });
        return project;
    }

    /// <summary>テキストトラックが無ければ作り（名前は手動追加と同じ「テキスト 1」）、クリップが計画どおりに置かれること。</summary>
    [Fact]
    public void ImportInto_CreatesTextTrackAndClips()
    {
        var project = SampleProject();
        var plan = LyricsImport.PlanClips(new[] { "one", "two" }, DefaultOptions(start: 5));

        var added = LyricsImport.ImportInto(project, plan);

        var textTrack = project.Tracks.Single(t => t.Kind == TrackKind.Text);
        Assert.Equal("テキスト 1", textTrack.Name);
        Assert.Equal(2, added.Count);
        Assert.Equal(new[] { "one", "two" }, added.Select(c => c.Text));
        Assert.Equal(new[] { "one", "two" }, added.Select(c => c.Name));
        Assert.All(added, c => Assert.Equal(ClipKind.Text, c.Kind));
        Assert.Equal(new[] { 5.0, 8.0 }, added.Select(c => c.StartSeconds));
        Assert.All(added, c => Assert.Equal(2.5, c.DurationSeconds));
        Assert.Same(textTrack.Clips[0], added[0]);
    }

    /// <summary>既存のテキストトラックがあれば再利用し、新しいトラックは作らないこと。</summary>
    [Fact]
    public void ImportInto_ReusesExistingTextTrack()
    {
        var project = SampleProject();
        var existing = project.AddTrack(TrackKind.Text, "テキスト 1");
        existing.Clips.Add(new Clip
        {
            Name = "既存",
            Kind = ClipKind.Text,
            Text = "既存",
            StartSeconds = 0,
            DurationSeconds = 5
        });
        var plan = LyricsImport.PlanClips(new[] { "new" }, DefaultOptions(start: 10));

        var added = LyricsImport.ImportInto(project, plan);

        Assert.Single(project.Tracks, t => t.Kind == TrackKind.Text);
        Assert.Equal(2, existing.Clips.Count);
        Assert.Equal("既存", existing.Clips[0].Text);
        Assert.Same(existing.Clips[1], added[0]);
    }

    /// <summary>追加されるテキストクリップは手動追加と同じ既定値（フォント・サイズ・色）になること。</summary>
    [Fact]
    public void ImportInto_UsesSameDefaultsAsManualTextClip()
    {
        var project = SampleProject();
        var manual = new Clip
        {
            Name = "歌詞テロップ",
            Kind = ClipKind.Text,
            Text = "歌詞テロップ",
            StartSeconds = 0,
            DurationSeconds = 5
        };
        var plan = LyricsImport.PlanClips(new[] { "one" }, DefaultOptions(start: 0));

        var added = LyricsImport.ImportInto(project, plan);

        Assert.Equal(manual.FontFamily, added[0].FontFamily);
        Assert.Equal(manual.FontSize, added[0].FontSize);
        Assert.Equal(manual.FontColorHex, added[0].FontColorHex);
        Assert.Equal(string.Empty, added[0].SourcePath);
    }

    /// <summary>流し込んだ歌詞クリップが保存・再読込で残ること。</summary>
    [Fact]
    public void Serialize_RoundTrip_PreservesImportedLyricClips()
    {
        var project = SampleProject();
        var plan = LyricsImport.PlanClips(new[] { "あいうえお", "かきくけこ" }, DefaultOptions(start: 2));
        LyricsImport.ImportInto(project, plan);

        var restored = ProjectStore.Deserialize(ProjectStore.Serialize(project));

        var textTrack = restored.Tracks.Single(t => t.Kind == TrackKind.Text);
        Assert.Equal(new[] { "あいうえお", "かきくけこ" }, textTrack.Clips.Select(c => c.Text));
        Assert.Equal(new[] { 2.0, 5.0 }, textTrack.Clips.Select(c => c.StartSeconds));
    }
}
