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
