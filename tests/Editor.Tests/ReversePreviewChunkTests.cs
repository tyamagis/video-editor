// 逆再生プレビューのチャンクキャッシュ（StreamingFrameSource）のテスト。
// 逆向きの連続要求（素材時刻が少しずつ後退）でも、各時刻について
// 「その時刻へ直接シークしたフレーム（従来の張り直し挙動）」と画素が
// 完全一致することを担保する。素材は ffmpeg の testsrc（フレームごとに
// 模様が変わる）で決定的に生成する。ffmpeg が無い環境では対象外。

using Editor.Core;
using Editor.Media;
using Xunit;

namespace Editor.Tests;

/// <summary>逆再生用チャンクキャッシュの同一性・無効化テスト。</summary>
public sealed class ReversePreviewChunkTests
{
    private const int Fps = 10;
    private const int Width = 160;

    private static StreamingFrameSource NewSource()
        => new(new FfmpegSettings(), new MediaProbe(new FfmpegSettings()));

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

    /// <summary>フレームごとに模様が変わる動画（testsrc、160x120、10fps、4秒）を作る。</summary>
    private static async Task MakePatternVideoAsync(string path)
    {
        var args = $"-y -f lavfi -i testsrc=duration=4:size=160x120:rate={Fps} -c:v libx264 -pix_fmt yuv420p -g 10 \"{path}\"";
        var result = await FfmpegProcess.RunAsync("ffmpeg", args);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"テスト動画の生成に失敗しました: {result.StandardError}");
    }

    /// <summary>単色の動画（差し替え用、同じサイズ・fps・長さ）を作る。</summary>
    private static async Task MakeColorVideoAsync(string path, string color)
    {
        var args = $"-y -f lavfi -i color=c={color}:s=160x120:r={Fps}:d=4 -c:v libx264 -pix_fmt yuv420p -g 10 \"{path}\"";
        var result = await FfmpegProcess.RunAsync("ffmpeg", args);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"テスト動画の生成に失敗しました: {result.StandardError}");
    }

    /// <summary>フレームごとに模様が変わる長い動画（testsrc、160x120、10fps、12秒）を作る。</summary>
    private static async Task MakeLongPatternVideoAsync(string path)
    {
        var args = $"-y -f lavfi -i testsrc=duration=12:size=160x120:rate={Fps} -c:v libx264 -pix_fmt yuv420p -g 10 \"{path}\"";
        var result = await FfmpegProcess.RunAsync("ffmpeg", args);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"テスト動画の生成に失敗しました: {result.StandardError}");
    }

    /// <summary>
    /// VFR 素材を作る。30fps の testsrc から不規則にフレームを間引き、残ったフレームの
    /// PTS は元のままにする。実フレームは 0, 1/30, 2/30, 1.0, 1.033, 1.067, 3.0,
    /// 3.833〜3.967 秒にだけ現れる（avg_frame_rate=3、r_frame_rate=30）。
    /// </summary>
    private static async Task MakeVariableFrameRateVideoAsync(string path)
    {
        var args = "-y -f lavfi -i testsrc=duration=4:size=160x120:rate=30 " +
                   "-vf \"select='lt(n\\,3)+between(n\\,30\\,32)+eq(n\\,90)+between(n\\,115\\,119)'\" " +
                   $"-vsync vfr -c:v libx264 -pix_fmt yuv420p \"{path}\"";
        var result = await FfmpegProcess.RunAsync("ffmpeg", args);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"テスト動画の生成に失敗しました: {result.StandardError}");
    }

    /// <summary>その時刻へ直接シークしたフレーム（従来挙動の参照）を返す。</summary>
    private static async Task<VideoFrameData?> DirectSeekFrameAsync(string path, double timeSeconds)
    {
        using var source = NewSource();
        return await source.GetFrameAsync(path, timeSeconds, Width);
    }

    [Fact]
    public async Task ReverseSequence_MatchesDirectSeekPerFrame()
    {
        if (!await IsFfmpegAvailableAsync())
            return;

        using var temp = new TempDirectory();
        var sourcePath = temp.File("clip.mp4");
        await MakePatternVideoAsync(sourcePath);

        // 3.0秒から 1/10 秒ずつ後退する逆再生の連続要求。
        var times = new List<double>();
        for (var i = 0; i <= 20; i++)
            times.Add(3.0 - i / (double)Fps);

        using var source = NewSource();
        foreach (var t in times)
        {
            var actual = await source.GetFrameAsync(sourcePath, t, Width);
            var expected = await DirectSeekFrameAsync(sourcePath, t);
            Assert.NotNull(actual);
            Assert.NotNull(expected);
            Assert.Equal(expected!.Bgra, actual!.Bgra);
        }

        // チャンクキャッシュが実際に使われている（1件以上たまっている）こと。
        Assert.True(source.ChunkCacheStats.Count >= 1);
        Assert.True(source.ChunkCacheStats.Bytes > 0);
    }

    [Fact]
    public async Task ReverseSequence_FramesAreNotAllSame()
    {
        // 参照との一致だけでは「全部同じフレームを返している」ケースを検出できないため、
        // 逆再生でフレームが実際に切り替わることも確認する。
        if (!await IsFfmpegAvailableAsync())
            return;

        using var temp = new TempDirectory();
        var sourcePath = temp.File("clip.mp4");
        await MakePatternVideoAsync(sourcePath);

        using var source = NewSource();
        var first = await source.GetFrameAsync(sourcePath, 3.0, Width);
        var later = await source.GetFrameAsync(sourcePath, 2.5, Width);
        Assert.NotNull(first);
        Assert.NotNull(later);
        Assert.NotEqual(first!.Bgra, later!.Bgra);
    }

    [Fact]
    public async Task ChunkCache_SourceReplaced_DoesNotServeStaleFrames()
    {
        if (!await IsFfmpegAvailableAsync())
            return;

        using var temp = new TempDirectory();
        var sourcePath = temp.File("clip.mp4");
        await MakePatternVideoAsync(sourcePath);

        using var source = NewSource();
        // 逆再生の連続要求でチャンクをためる（2.0 → 1.5）。
        for (var i = 0; i <= 5; i++)
            await source.GetFrameAsync(sourcePath, 2.0 - i / (double)Fps, Width);
        Assert.True(source.ChunkCacheStats.Count >= 1);
        var staleReference = await DirectSeekFrameAsync(sourcePath, 1.4);

        // 同じパスに単色の動画を差し替える（サイズ・更新日時が変わる）。
        await MakeColorVideoAsync(sourcePath, "blue");

        // 後退ステップでチャンク範囲内の時刻を要求する。身元照合が無ければ
        // 古い testsrc のフレームが返ってしまう。
        var actual = await source.GetFrameAsync(sourcePath, 1.4, Width);
        var expected = await DirectSeekFrameAsync(sourcePath, 1.4);
        Assert.NotNull(actual);
        Assert.NotNull(expected);
        Assert.Equal(expected!.Bgra, actual!.Bgra);
        Assert.NotEqual(staleReference!.Bgra, actual.Bgra);
    }

    [Fact]
    public async Task LargeBackwardSteps_SustainedScrub_LoadsChunkAndMatchesDirectSeek()
    {
        // 1ステップが 1.5 秒を超える後退スクラブ。単発の大きな後退ではチャンクを
        // 読み込まないが、後退が続いた（3回目）時点でチャンク読み込みに切り替わり、
        // 以後は従来経路（毎回シーク）と画素が一致し続けること。
        if (!await IsFfmpegAvailableAsync())
            return;

        using var temp = new TempDirectory();
        var sourcePath = temp.File("clip.mp4");
        await MakeLongPatternVideoAsync(sourcePath);

        using var source = NewSource();
        var times = new[] { 11.0, 9.0, 7.0, 5.0 };
        for (var i = 0; i < times.Length; i++)
        {
            var actual = await source.GetFrameAsync(sourcePath, times[i], Width);
            var expected = await DirectSeekFrameAsync(sourcePath, times[i]);
            Assert.NotNull(actual);
            Assert.NotNull(expected);
            Assert.Equal(expected!.Bgra, actual!.Bgra);
            if (i < times.Length - 1)
                Assert.Equal(0, source.ChunkCacheStats.Count); // 継続が分かるまでは読み込まない。
        }
        Assert.True(source.ChunkCacheStats.Count >= 1); // 3回目の後退でチャンクが読み込まれた。
    }

    [Fact]
    public async Task Probe_ConstantFrameRate_IsNotFlaggedVariable()
    {
        if (!await IsFfmpegAvailableAsync())
            return;

        using var temp = new TempDirectory();
        var sourcePath = temp.File("clip.mp4");
        await MakePatternVideoAsync(sourcePath);

        var info = await new MediaProbe(new FfmpegSettings()).ProbeAsync(sourcePath);
        Assert.False(info.IsVariableFrameRate);
        Assert.Equal(Fps, info.FrameRate, 3);
    }

    [Fact]
    public async Task VariableFrameRate_ProbeDetectsAndReverseMatchesDirectSeekWithoutChunk()
    {
        // VFR 素材：平均レート（3fps）が代表レートになり、VFR と判定されること。
        // 逆再生の連続要求でもチャンクキャッシュは使われず（フレーム対応が
        // 等間隔前提でずれるため）、毎回シークする従来経路で直接シークと一致すること。
        if (!await IsFfmpegAvailableAsync())
            return;

        using var temp = new TempDirectory();
        var sourcePath = temp.File("vfr.mp4");
        await MakeVariableFrameRateVideoAsync(sourcePath);

        var info = await new MediaProbe(new FfmpegSettings()).ProbeAsync(sourcePath);
        Assert.True(info.IsVariableFrameRate);
        Assert.Equal(3.0, info.FrameRate, 3); // r_frame_rate の 30 ではなく平均レート。

        using var source = NewSource();
        foreach (var t in new[] { 3.9, 3.0, 1.05, 0.05 })
        {
            var actual = await source.GetFrameAsync(sourcePath, t, Width);
            var expected = await DirectSeekFrameAsync(sourcePath, t);
            Assert.NotNull(actual);
            Assert.NotNull(expected);
            Assert.Equal(expected!.Bgra, actual!.Bgra);
        }
        Assert.Equal(0, source.ChunkCacheStats.Count);
    }

    [Fact]
    public async Task ForwardSequence_StillMatchesDirectSeek()
    {
        // 順再生の従来経路がチャンク導入で壊れていないこと。
        if (!await IsFfmpegAvailableAsync())
            return;

        using var temp = new TempDirectory();
        var sourcePath = temp.File("clip.mp4");
        await MakePatternVideoAsync(sourcePath);

        using var source = NewSource();
        for (var i = 0; i <= 10; i++)
        {
            var t = 1.0 + i / (double)Fps;
            var actual = await source.GetFrameAsync(sourcePath, t, Width);
            var expected = await DirectSeekFrameAsync(sourcePath, t);
            Assert.NotNull(actual);
            Assert.NotNull(expected);
            Assert.Equal(expected!.Bgra, actual!.Bgra);
        }
    }
}

/// <summary>チャンク読み込みの開始判定と VFR 検出の純粋ロジックのテスト（ffmpeg 不要）。</summary>
public sealed class ReversePreviewStepDecisionTests
{
    [Theory]
    [InlineData(0.1, 1, true)]        // 通常の逆再生ステップは即読み込む。
    [InlineData(1.5, 1, true)]        // 上限ちょうどでも即読み込む。
    [InlineData(1.500001, 1, false)]  // 大きな後退の単発は通常シーク。
    [InlineData(2.0, 2, false)]       // 続いていても2回目までは読み込まない。
    [InlineData(2.0, 3, true)]        // 3回続いたらスクラブ中とみなして読み込む。
    [InlineData(10.0, 3, true)]
    [InlineData(0.0, 5, false)]       // 静止は対象外。
    [InlineData(-1.0, 5, false)]      // 前進は対象外。
    [InlineData(double.NaN, 5, false)]
    public void ShouldLoadChunk_Cases(double stepBackSeconds, int backwardStreak, bool expected)
        => Assert.Equal(expected, StreamingFrameSource.ShouldLoadChunk(stepBackSeconds, backwardStreak));

    [Theory]
    [InlineData(30.0, 30.0, false)]   // CFR は平均＝最大。
    [InlineData(29.97, 29.97, false)]
    [InlineData(3.0, 30.0, true)]     // 間引き VFR は平均が大きく下がる。
    [InlineData(29.5, 30.0, false)]   // 2% 以内のずれは CFR の誤差として許容。
    [InlineData(29.0, 30.0, true)]    // 2% 超のずれは VFR。
    [InlineData(0.0, 30.0, false)]    // どちらか不明なら CFR 扱い。
    [InlineData(30.0, 0.0, false)]
    [InlineData(double.NaN, 30.0, false)]
    [InlineData(30.0, double.NaN, false)]
    public void DetectVariableFrameRate_Cases(double avgFrameRate, double realFrameRate, bool expected)
        => Assert.Equal(expected, MediaProbe.DetectVariableFrameRate(avgFrameRate, realFrameRate));
}
