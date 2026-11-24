// 歌詞ファイルの一括流し込み（Editor.Core）。
// テキストファイル（1行1フレーズ）から歌詞を読み、テキストクリップの
// 配置計画を作り、プロジェクトへ追加する。UI・WPF には依存しないため
// xUnit で直接テストできる。ファイル選択・設定ダイアログは Editor.App 側、
// 配置の Undo は MainViewModel.BeginEdit（スナップショット方式）が担う。

using System.Text;

namespace Editor.Core;

/// <summary>歌詞ファイルの読み込み・配置で起きた、ユーザーへ見せる失敗（ファイルが無い・空・文字コード不明など）。</summary>
public sealed class LyricsImportException : Exception
{
    public LyricsImportException(string message) : base(message) { }

    public LyricsImportException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>歌詞の一括流し込みの設定。</summary>
public sealed class LyricsImportOptions
{
    /// <summary>最初のクリップの開始時刻（秒）。負は 0 に丸める。</summary>
    public double StartSeconds { get; set; }

    /// <summary>次のクリップの開始までの間隔（秒）。0 より大きくなければならない。</summary>
    public double IntervalSeconds { get; set; } = 3.0;

    /// <summary>1クリップの長さ（秒）。0 より大きくなければならない。</summary>
    public double ClipDurationSeconds { get; set; } = 3.0;

    /// <summary>空行（空白だけの行を含む）を飛ばすか。既定は飛ばす。</summary>
    public bool SkipEmptyLines { get; set; } = true;

    /// <summary>設定が有効かを確認する。間隔・長さが 0 以下のときは ArgumentException。</summary>
    public void Validate()
    {
        if (IntervalSeconds <= 0)
            throw new ArgumentException("配置間隔は 0 より大きい秒数にしてください。", nameof(IntervalSeconds));
        if (ClipDurationSeconds <= 0)
            throw new ArgumentException("1クリップの長さは 0 より大きい秒数にしてください。", nameof(ClipDurationSeconds));
    }
}

/// <summary>配置が決まった歌詞1行（テキスト・開始時刻・長さ）。</summary>
public sealed record PlannedLyricClip(string Text, double StartSeconds, double DurationSeconds);

/// <summary>歌詞ファイルの読み込み。</summary>
public static class LyricsFileReader
{
    /// <summary>UTF-8 の厳密なデコーダ（読めないバイトがあれば例外。BOM は Decode 側で先に除く）。</summary>
    private static readonly Encoding StrictUtf8 =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Shift_JIS の厳密なデコーダ（読めないバイトがあれば例外）。
    /// 遅延して作る：static フィールドの初期化順でプロバイダ登録より先に
    /// GetEncoding(932) が走ると、コードページが見つからず失敗するため。
    /// </summary>
    private static readonly Lazy<Encoding> StrictShiftJis = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    });

    /// <summary>
    /// 歌詞ファイルを読み、有効な行の一覧を返す（各行は前後の空白を除いたもの）。
    /// 空行は SkipEmptyLines に従って飛ばす。有効な行が0行のとき、
    /// ファイルが無い・開けないとき、文字コードが判別できないときは
    /// LyricsImportException（何も配置しないのが呼び出し側の約束）。
    /// </summary>
    public static IReadOnlyList<string> ReadPhrases(string path, LyricsImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new LyricsImportException(
                $"歌詞ファイルを開けませんでした：{Path.GetFileName(path)}（{ex.Message}）", ex);
        }

        string text;
        try
        {
            text = Decode(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new LyricsImportException(
                "歌詞ファイルの文字コードが判別できませんでした。UTF-8 または Shift_JIS で保存し直してください。", ex);
        }

        var phrases = new List<string>();
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var phrase = line.Trim();
            if (options.SkipEmptyLines && phrase.Length == 0)
                continue;
            phrases.Add(phrase);
        }

        if (phrases.Count == 0)
            throw new LyricsImportException("歌詞の行がありません。空のファイルか、空行だけのファイルです。");

        return phrases;
    }

    /// <summary>
    /// バイト列を文字列に直す。BOM があればそれに従い（UTF-8 / UTF-16 LE / UTF-16 BE）、
    /// 無ければ UTF-8 として厳密に読み、読めなければ Shift_JIS として厳密に読む。
    /// どちらでも読めなければ DecoderFallbackException。
    /// </summary>
    internal static string Decode(byte[] bytes)
    {
        // UTF-8 の BOM（EF BB BF）。UTF8Encoding は BOM を除かないため、先に除く。
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return StrictUtf8.GetString(bytes, 3, bytes.Length - 3);
        // UTF-16 LE / BE の BOM（Windows のメモ帳などが書く形式。おまけ対応）。
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // UTF-8 として読めないバイト列は Shift_JIS として読む。
            // こちらも厳密に読み、読めなければ例外をそのまま投げる。
            return StrictShiftJis.Value.GetString(bytes);
        }
    }
}

/// <summary>歌詞の行からテキストクリップの配置計画を作り、プロジェクトへ追加する。</summary>
public static class LyricsImport
{
    /// <summary>
    /// 各行の開始時刻を決める。開始時刻は StartSeconds から IntervalSeconds ずつ進む
    /// （間隔は「次のクリップの開始までの秒数」。長さより短い間隔だとクリップが重なるが、
    /// それはユーザーの指定どおりとする）。長さは全行 ClipDurationSeconds。
    /// 開始位置の負は 0 に丸める（マーカーと同じ流儀）。
    /// </summary>
    public static IReadOnlyList<PlannedLyricClip> PlanClips(
        IReadOnlyList<string> phrases, LyricsImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(phrases);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var start = Math.Max(0, options.StartSeconds);
        var plan = new List<PlannedLyricClip>(phrases.Count);
        for (var i = 0; i < phrases.Count; i++)
        {
            plan.Add(new PlannedLyricClip(
                phrases[i],
                start + i * options.IntervalSeconds,
                options.ClipDurationSeconds));
        }
        return plan;
    }

    /// <summary>
    /// 配置計画どおりにテキストクリップをプロジェクトへ追加する。
    /// テキストトラックが無ければ作る（名前は手動追加と同じ「テキスト 1」）。
    /// クリップは手動のテキスト追加と同じ既定値（フォント・サイズ・色は Clip の既定値）になる。
    /// 追加したクリップを返す。
    /// </summary>
    public static IReadOnlyList<Clip> ImportInto(EditorProject project, IReadOnlyList<PlannedLyricClip> plan)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(plan);

        var track = project.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Text)
            ?? project.AddTrack(TrackKind.Text, "テキスト 1");

        var added = new List<Clip>(plan.Count);
        foreach (var item in plan)
        {
            var clip = new Clip
            {
                Name = item.Text,
                Kind = ClipKind.Text,
                Text = item.Text,
                StartSeconds = item.StartSeconds,
                DurationSeconds = item.DurationSeconds
            };
            track.Clips.Add(clip);
            added.Add(clip);
        }
        return added;
    }
}
