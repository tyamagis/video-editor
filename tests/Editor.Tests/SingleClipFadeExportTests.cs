// 単一クリップ書き出し（ExportService）の音声フェード対応テスト。
// 単一クリップ書き出しがタイムライン書き出しと同じ Core の包絡
// （AudioGainEnvelope.BuildBreakpoints → BuildVolumeFilter）を読むことを担保する。
// ダッキングは単一クリップ書き出しの対象外（トリガークリップの解析が要るため。
// ExportService のドキュメント参照）。

using Editor.Core;
using Editor.Media;
using Editor.Render;
using Xunit;

namespace Editor.Tests;

/// <summary>単一クリップ書き出しのフェード適用テスト。</summary>
public sealed class SingleClipFadeExportTests
{
    private static Clip MakeClip(double duration = 4, double fadeIn = 0, double fadeOut = 0)
    {
        var clip = new Clip
        {
            Kind = ClipKind.Audio,
            SourcePath = "dummy.wav",
            StartSeconds = 10,
            DurationSeconds = duration,
        };
        clip.AudioFadeInSeconds = fadeIn;
        clip.AudioFadeOutSeconds = fadeOut;
        return clip;
    }

    // ---- 包絡の一致（単一クリップ経路 vs タイムライン経路） ----

    [Fact]
    public void BuildFadeEnvelope_MatchesTimelineBreakpoints_ForSameClipSettings()
    {
        var clip = MakeClip(duration: 4, fadeIn: 1.0, fadeOut: 1.5);
        var timeline = AudioGainEnvelope.BuildBreakpoints(clip, Array.Empty<AudioSegment>());

        var single = ExportService.BuildFadeEnvelope(1.0, 1.5, clip.DurationSeconds);

        Assert.NotNull(single);
        Assert.Equal(timeline, single);
    }

    [Fact]
    public void SingleClipFilter_FadeOnly_EqualsTimelineVolumeFilter()
    {
        var clip = MakeClip(duration: 4, fadeIn: 1.0, fadeOut: 1.0);
        var breakpoints = AudioGainEnvelope.BuildBreakpoints(clip, Array.Empty<AudioSegment>());
        // タイムライン経路のフィルタ（末尾カンマ付き）から作った期待値。
        var timelineFilter = AudioExportPlanner.EnvelopeVolumeFilter(breakpoints);
        Assert.EndsWith(",", timelineFilter);

        var args = ExportService.BuildFilterArguments(reverse: false, speed: 1.0, breakpoints);

        Assert.Equal($" -af {timelineFilter.TrimEnd(',')}", args);
    }

    [Fact]
    public void SingleClipFilter_FadeIn_BuildsLinearExpression()
    {
        var envelope = ExportService.BuildFadeEnvelope(1.0, 0, timelineDurationSeconds: 4);

        var args = ExportService.BuildFilterArguments(reverse: false, speed: 1.0, envelope);

        Assert.StartsWith(" -af volume='", args);
        Assert.Contains(":eval=frame", args);
        Assert.Contains("if(lt(t,1.0000)", args); // 1秒で上がりきる分岐
        Assert.Contains("0.0000+(1.0000-0.0000)*(t-0.0000)/1.0000", args); // 線形補間
        Assert.DoesNotContain("-vf", args); // 映像フィルタが無ければ -vf は付けない
    }

    // ---- フェード無しは従来通り（volume フィルタを付けない） ----

    [Fact]
    public void BuildFadeEnvelope_NoFade_ReturnsNull()
    {
        Assert.Null(ExportService.BuildFadeEnvelope(0, 0, 4));
        Assert.Null(ExportService.BuildFadeEnvelope(-1, -2, 4)); // 負は 0 に丸める
    }

    [Fact]
    public void SingleClipFilter_NoFade_UnchangedBehavior()
    {
        // 包絡なし（null）→ 従来通り空文字。
        Assert.Equal(string.Empty, ExportService.BuildFilterArguments(false, 1.0, null));
        // フェード無しクリップのブレークポイント列（全て等倍）を渡しても付けない。
        var clip = MakeClip(duration: 4);
        var ones = AudioGainEnvelope.BuildBreakpoints(clip, Array.Empty<AudioSegment>());
        Assert.Equal(string.Empty, ExportService.BuildFilterArguments(false, 1.0, ones));
        // 逆再生・速度変更だけの従来の組み合わせも不変。
        Assert.Equal(" -vf reverse,setpts=PTS/2.000 -af areverse,atempo=2.000",
            ExportService.BuildFilterArguments(true, 2.0, null));
    }

    // ---- 逆再生・速度変更との組み合わせ（包絡は音声チェーンの最後） ----

    [Fact]
    public void SingleClipFilter_FadeWithReverseAndSpeed_VolumeIsLastInAudioChain()
    {
        var envelope = ExportService.BuildFadeEnvelope(1.0, 0, timelineDurationSeconds: 2);

        var args = ExportService.BuildFilterArguments(reverse: true, speed: 2.0, envelope);

        Assert.StartsWith(" -vf reverse,setpts=PTS/2.000 -af areverse,atempo=2.000,volume='", args);
        Assert.EndsWith(":eval=frame", args);
    }

    // ---- 端ケース ----

    [Fact]
    public void BuildFadeEnvelope_FadeLongerThanClip_ClampsToDuration()
    {
        // フェードイン10秒 > クリップ2秒：全体が1本のランプになり、末尾で 1.0 に届く
        // （Core の FadeGainAt と同じ解釈）。
        var envelope = ExportService.BuildFadeEnvelope(10.0, 0, timelineDurationSeconds: 2);

        Assert.NotNull(envelope);
        Assert.Equal(0.0, envelope![0].Gain);
        Assert.Equal(2.0, envelope[^1].TimeSeconds);
        Assert.Equal(1.0, envelope[^1].Gain);
        Assert.Equal(AudioGainEnvelope.FadeGainAt(10.0, 0, 2.0, 1.0),
            AudioGainEnvelope.EvaluateBreakpoints(envelope, 1.0), 6);
        // フィルタも壊れず付く。
        var args = ExportService.BuildFilterArguments(false, 1.0, envelope);
        Assert.Contains("volume='", args);
    }

    [Fact]
    public void BuildFadeEnvelope_OverlappingFades_SaneAndMatchesTimeline()
    {
        // 長さ2秒にイン2秒・アウト2秒（完全な重なり）。Core の解釈では両端 0、
        // 中央 0.25 の山型。ブレークポイント列はタイムライン経路と同一で、
        // ゲインは [0, 1] の範囲を出ない。
        var clip = MakeClip(duration: 2, fadeIn: 2.0, fadeOut: 2.0);
        var timeline = AudioGainEnvelope.BuildBreakpoints(clip, Array.Empty<AudioSegment>());

        var single = ExportService.BuildFadeEnvelope(2.0, 2.0, timelineDurationSeconds: 2);

        Assert.Equal(timeline, single);
        Assert.All(single!, b => Assert.InRange(b.Gain, 0.0, 1.0));
        Assert.Equal(0.0, single![0].Gain);
        Assert.Equal(0.0, single[^1].Gain);
        // 単一クリップ経路のフィルタ文字列もタイムライン経路と一致する。
        var timelineFilter = AudioExportPlanner.EnvelopeVolumeFilter(timeline);
        Assert.Equal($" -af {timelineFilter.TrimEnd(',')}",
            ExportService.BuildFilterArguments(false, 1.0, single));
    }

    [Fact]
    public void BuildFadeEnvelope_NonFiniteInputs_AreGuarded()
    {
        // NaN のフェードは「無し」として丸める（ClampFadeSeconds の契約）。
        Assert.Null(ExportService.BuildFadeEnvelope(double.NaN, double.NaN, 4));
        // +∞ のフェードは上限60秒に丸まり、クリップ長でさらに丸まる（= 全体ランプ）。
        var infinite = ExportService.BuildFadeEnvelope(double.PositiveInfinity, 0, 4);
        Assert.Equal(ExportService.BuildFadeEnvelope(60.0, 0, 4), infinite);
        // 長さが正でない・非有限なら包絡なし（壊れた引数を ffmpeg に渡さない）。
        Assert.Null(ExportService.BuildFadeEnvelope(1.0, 0, 0));
        Assert.Null(ExportService.BuildFadeEnvelope(1.0, 0, -2));
        Assert.Null(ExportService.BuildFadeEnvelope(1.0, 0, double.NaN));
        Assert.Null(ExportService.BuildFadeEnvelope(1.0, 0, double.PositiveInfinity));
    }

    [Fact]
    public void BuildVolumeFilter_NonFinitePoints_AreDropped()
    {
        // Core の共有変換：非有限の点は落とし、残りで式を組む。
        var points = new List<GainBreakpoint>
        {
            new(0, 0.0), new(1.0, double.NaN), new(2.0, 1.0),
        };
        var filter = AudioGainEnvelope.BuildVolumeFilter(points);
        Assert.Contains("volume='", filter);
        Assert.DoesNotContain("NaN", filter);
        // 非有限のオフセットは 0 扱い。
        Assert.Equal(filter, AudioGainEnvelope.BuildVolumeFilter(points, double.NaN));
    }

    // ---- 実経路（実 ffmpeg、既存テストと同じガード方式） ----

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

    private static async Task MakeVideoWithAudioAsync(string path, double seconds)
    {
        var args = $"-y -f lavfi -i color=c=blue:s=64x64:r=10:d={seconds} " +
                   $"-f lavfi -i sine=frequency=440:duration={seconds} " +
                   $"-c:v libx264 -pix_fmt yuv420p -c:a aac -shortest \"{path}\"";
        var result = await FfmpegProcess.RunAsync("ffmpeg", args);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"テスト動画の生成に失敗しました: {result.StandardError}");
    }

    /// <summary>mp4 の音声を f32le モノラル PCM に展開し、先頭と末尾それぞれ seconds 秒の RMS を返す。</summary>
    private static async Task<(double HeadRms, double TailRms)> MeasureHeadTailRmsAsync(
        string mp4Path, string pcmPath, double seconds)
    {
        var result = await FfmpegProcess.RunAsync("ffmpeg",
            $"-y -v error -i \"{mp4Path}\" -vn -ac 1 -ar 8000 -f f32le \"{pcmPath}\"");
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"PCM 展開に失敗しました: {result.StandardError}");
        var bytes = await File.ReadAllBytesAsync(pcmPath);
        var samples = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 4);
        static double Rms(ReadOnlySpan<float> span)
        {
            double sum = 0;
            foreach (var s in span)
                sum += (double)s * s;
            return span.Length == 0 ? 0 : Math.Sqrt(sum / span.Length);
        }
        var window = Math.Min((int)(8000 * seconds), samples.Length / 2);
        return (Rms(samples.AsSpan(0, window)), Rms(samples.AsSpan(samples.Length - window, window)));
    }

    [Fact]
    public async Task ExportSingleClip_WithFadeIn_HeadIsQuieterThanTail()
    {
        if (!await IsFfmpegAvailableAsync())
            return;

        using var temp = new TempDirectory();
        var source = temp.File("src.mp4");
        await MakeVideoWithAudioAsync(source, 2.0);
        var service = new ExportService(new FfmpegSettings());

        // フェードなし（対照）：先頭と末尾で音量はほぼ同じはず。
        var plain = temp.File("plain.mp4");
        await service.ExportSingleClipAsync(source, 0, 2.0, plain, new ExportSettings());
        var (plainHead, plainTail) = await MeasureHeadTailRmsAsync(plain, temp.File("plain.pcm"), 0.2);
        Assert.True(plainHead > 0.5 * plainTail,
            $"フェードなしで先頭が小さすぎます: head={plainHead}, tail={plainTail}");

        // フェードイン2秒（クリップ全体）：先頭は末尾より十分に小さいはず。
        var faded = temp.File("faded.mp4");
        await service.ExportSingleClipAsync(source, 0, 2.0, faded, new ExportSettings(),
            audioFadeInSeconds: 2.0);
        var (fadedHead, fadedTail) = await MeasureHeadTailRmsAsync(faded, temp.File("faded.pcm"), 0.2);
        Assert.True(fadedTail > 0.5 * plainTail,
            $"末尾の音量が対照より小さすぎます: tail={fadedTail}, plainTail={plainTail}");
        Assert.True(fadedHead < 0.25 * fadedTail,
            $"フェードインが効いていません: head={fadedHead}, tail={fadedTail}");
    }
}
