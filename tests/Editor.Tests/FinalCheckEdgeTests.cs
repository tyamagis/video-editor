// 最終確認（2026-11-25）で追加した境界条件テスト。
// 直近6機能（速度変更／フェード・ダッキング／スナップ・リップル・グループ／
// LUT／シャープン・エンボス／逆再生キャッシュ）のコードレビューで見つけた
// カバレッジの薄い箇所と、実バグ2件の回帰テストを集めた。
// - CubeLut: 負・巨大な LUT_1D_SIZE で TryParse が例外を投げていた（契約違反）。
// - ffmpeg 引数の数値書式が CurrentCulture 依存で、小数点カンマのロケールでは
//   フィルタ文字列が壊れていた（InvariantCulture に統一済み）。

using System.Globalization;
using Editor.Audio;
using Editor.Core;
using Editor.Media;
using Editor.Render;
using Xunit;

namespace Editor.Tests;

/// <summary>最終確認で追加した境界条件・回帰テスト。</summary>
public sealed class FinalCheckEdgeTests
{
    // ---------- CubeLut：LUT_1D_SIZE の異常値（回帰） ----------

    [Fact]
    public void CubeLut_NegativeLut1DSize_ReturnsFalseWithoutThrowing()
    {
        // LUT_1D_SIZE -5 → skip=-15。size=2 の expected=24 に対しデータ行3行（9値）で
        // 9-(-15)=24 となり、旧実装では件数チェックを通過して CopyTo が例外を投げた。
        var lines = new List<string> { "LUT_1D_SIZE -5", "LUT_3D_SIZE 2" };
        for (var i = 0; i < 3; i++)
            lines.Add("0 0 0");
        var ok = CubeLut.TryParse(string.Join("\n", lines), out var lut, out var error);
        Assert.False(ok);
        Assert.Null(lut);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void CubeLut_HugeLut1DSize_ReturnsFalseWithoutThrowing()
    {
        // size1D * 3 が int の範囲を超える値でも例外なく false（件数不一致）になること。
        var lines = new List<string> { "LUT_1D_SIZE 2000000000", "LUT_3D_SIZE 2" };
        for (var i = 0; i < 8; i++)
            lines.Add("0 0 0");
        var ok = CubeLut.TryParse(string.Join("\n", lines), out var lut, out var error);
        Assert.False(ok);
        Assert.Null(lut);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void CubeLut_NegativeDataValues_AreAccepted()
    {
        // LUT 値には負・1超があり得る（パーサは丸めない）。
        var lines = new List<string> { "LUT_3D_SIZE 2" };
        for (var i = 0; i < 8; i++)
            lines.Add("-0.5 0.25 1.5");
        Assert.True(CubeLut.TryParse(string.Join("\n", lines), out var lut, out _));
        Assert.NotNull(lut);
        Assert.Equal(-0.5f, lut!.Data[0]);
        Assert.Equal(1.5f, lut.Data[2]);
    }

    // ---------- カルチャ依存の数値書式（回帰） ----------

    private static void WithCulture(string name, Action body)
    {
        var prev = Thread.CurrentThread.CurrentCulture;
        var prevUi = Thread.CurrentThread.CurrentUICulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo(name);
            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(name);
            body();
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = prev;
            Thread.CurrentThread.CurrentUICulture = prevUi;
        }
    }

    [Fact]
    public void AtempoChain_UnderCommaDecimalCulture_UsesDot()
    {
        WithCulture("de-DE", () =>
        {
            Assert.Equal("atempo=0.500,atempo=0.500,atempo=0.500,atempo=0.800,", AudioExportPlanner.AtempoChain(0.1));
            Assert.Equal("atempo=0.500,atempo=0.500,atempo=0.500,atempo=0.800", ExportService.AtempoChain(0.1));
            Assert.Equal("atempo=2.000,atempo=2.000,", AudioExportPlanner.AtempoChain(4.0));
        });
    }

    [Fact]
    public void BuildClipFilter_UnderCommaDecimalCulture_UsesDot()
    {
        WithCulture("de-DE", () =>
        {
            var clip = new Clip { Speed = 2.0, NoiseReductionEnabled = true, NoiseReductionStrength = 0.5 };
            var filter = AudioExportPlanner.BuildClipFilter(0, clip, 1.5, 4.25, 120, "a0");
            Assert.Contains("atrim=start=1.500:end=4.250", filter);
            Assert.Contains("afftdn=nr=15.5", filter);
            Assert.Equal("volume=1.5000,", AudioExportPlanner.VolumeFilter(1.5));
            Assert.DoesNotContain("1,500", filter);
            Assert.DoesNotContain("15,5", filter);
        });
    }

    [Fact]
    public void SingleClipFilterArguments_UnderCommaDecimalCulture_UsesDot()
    {
        WithCulture("de-DE", () =>
        {
            var args = ExportService.BuildFilterArguments(reverse: false, speed: 1.5);
            Assert.Contains("setpts=PTS/1.500", args);
            Assert.Contains("atempo=1.500", args);
        });
    }

    // ---------- AudioGainEnvelope：境界条件 ----------

    [Fact]
    public void FadeGainAt_NaNFadesAndZeroDuration_AreOne()
    {
        Assert.Equal(1.0, AudioGainEnvelope.FadeGainAt(double.NaN, double.NaN, 4, 2));
        Assert.Equal(1.0, AudioGainEnvelope.FadeGainAt(1, 1, 0, 0));
        Assert.Equal(1.0, AudioGainEnvelope.FadeGainAt(1, 1, -3, 0));
    }

    [Fact]
    public void DuckGainAt_TrapezoidExactBoundaries()
    {
        // 区間 [2, 4]、アタック 0.1、リリース 0.3、既定 -12dB。
        var intervals = new List<AudioSegment> { new(2, 4) };
        var duck = AudioGainEnvelope.DuckGainLinear(AudioGainEnvelope.DefaultDuckAmountDb);
        Assert.Equal(1.0, AudioGainEnvelope.DuckGainAt(2.0, intervals, 12, 0.1, 0.3), precision: 9);
        Assert.Equal(duck, AudioGainEnvelope.DuckGainAt(2.1, intervals, 12, 0.1, 0.3), precision: 9);
        Assert.Equal(duck, AudioGainEnvelope.DuckGainAt(4.0, intervals, 12, 0.1, 0.3), precision: 9);
        Assert.Equal(1.0, AudioGainEnvelope.DuckGainAt(4.3, intervals, 12, 0.1, 0.3), precision: 9);
        Assert.Equal(1.0, AudioGainEnvelope.DuckGainAt(1.0, intervals, 12, 0.1, 0.3), precision: 9);
        Assert.Equal(1.0, AudioGainEnvelope.DuckGainAt(5.0, intervals, 12, 0.1, 0.3), precision: 9);
    }

    [Fact]
    public void DuckGainAt_NaNSettings_FallBackToDefaults()
    {
        var intervals = new List<AudioSegment> { new(0, 1) };
        var gain = AudioGainEnvelope.DuckGainAt(0.5, intervals, double.NaN, double.NaN, double.NaN);
        Assert.Equal(AudioGainEnvelope.DuckGainLinear(AudioGainEnvelope.DefaultDuckAmountDb), gain, precision: 9);
    }

    [Fact]
    public void EvaluateBreakpoints_EmptyAndSinglePoint()
    {
        Assert.Equal(1.0, AudioGainEnvelope.EvaluateBreakpoints(Array.Empty<GainBreakpoint>(), 3));
        var single = new List<GainBreakpoint> { new(2, 0.4) };
        Assert.Equal(0.4, AudioGainEnvelope.EvaluateBreakpoints(single, 0));
        Assert.Equal(0.4, AudioGainEnvelope.EvaluateBreakpoints(single, 2));
        Assert.Equal(0.4, AudioGainEnvelope.EvaluateBreakpoints(single, 99));
    }

    [Fact]
    public void MergeIntervals_EmptyUnsortedAndDegenerate()
    {
        Assert.Empty(AudioGainEnvelope.MergeIntervals(Array.Empty<AudioSegment>()));
        var merged = AudioGainEnvelope.MergeIntervals(new[]
        {
            new AudioSegment(5, 6),
            new AudioSegment(1, 2),
            new AudioSegment(3, 3),   // 長さ0は捨てる。
            new AudioSegment(4, 3),   // 逆向きは捨てる。
            new AudioSegment(1.5, 7), // [1,2] と重なり、[5,6] も飲む。
        });
        Assert.Single(merged);
        Assert.Equal(new AudioSegment(1, 7), merged[0]);
    }

    [Fact]
    public void CombinedGainAt_OutsideClipRange_IsZero()
    {
        var clip = new Clip { StartSeconds = 10, DurationSeconds = 4 };
        Assert.Equal(0, AudioGainEnvelope.CombinedGainAt(clip, 9.999, Array.Empty<AudioSegment>()));
        Assert.Equal(0, AudioGainEnvelope.CombinedGainAt(clip, 14.001, Array.Empty<AudioSegment>()));
        Assert.Equal(1.0, AudioGainEnvelope.CombinedGainAt(clip, 12, Array.Empty<AudioSegment>()));
    }

    // ---------- AudioActivityAnalyzer.FindActiveSegments：境界条件 ----------

    [Fact]
    public void FindActiveSegments_ThresholdEqualityCountsAsActive()
    {
        // しきい値ちょうどの窓は「鳴っている」扱い（>= 判定）。
        var rms = new[] { 0.0, 0.5, 0.5, 0.5, 0.0 };
        var segments = AudioActivityAnalyzer.FindActiveSegments(rms, durationSeconds: 0.05, threshold: 0.5);
        Assert.Single(segments);
        Assert.Equal(0.01, segments[0].StartSeconds, precision: 9);
        Assert.Equal(0.04, segments[0].EndSeconds, precision: 9);
    }

    [Fact]
    public void FindActiveSegments_AllSilentOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(AudioActivityAnalyzer.FindActiveSegments(new[] { 0.1, 0.2 }, 0.02, 0.5));
        Assert.Empty(AudioActivityAnalyzer.FindActiveSegments(Array.Empty<double>(), 0, 0.5));
    }

    [Fact]
    public void FindActiveSegments_EndClampsToDuration()
    {
        // 窓の合計が durationSeconds を超えていても、区間の終端は範囲内に丸める。
        var rms = new[] { 1.0, 1.0, 1.0, 1.0, 1.0 };
        var segments = AudioActivityAnalyzer.FindActiveSegments(rms, durationSeconds: 0.035, threshold: 0.5);
        Assert.Single(segments);
        Assert.Equal(0.035, segments[0].EndSeconds, precision: 9);
    }

    // ---------- SnapCalculator：境界条件 ----------

    [Fact]
    public void SnapToNearest_NegativeTolerance_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SnapCalculator.SnapToNearest(1.0, new[] { 1.0 }, -0.1));
    }

    [Fact]
    public void SnapMoveStart_EndSnapBelowZero_ClampsToZero()
    {
        // 終了端だけが候補（0.5）に近く、開始を前に引くと負になるケース。
        // 候補 0.5、長さ 2、要求開始 0.6 → 終了端 2.6 は候補から遠く、
        // 開始端 0.6 が候補 0.5 に吸着して開始 0.5。
        var byStart = SnapCalculator.SnapMoveStart(0.6, 2.0, new[] { 0.5 }, 0.2);
        Assert.Equal(0.5, byStart);
        // 終了端吸着で開始が負になるケース：候補 1.0、長さ 3、要求開始 0.2。
        // 終了端 3.2 は候補から遠い。開始端 0.2 は候補まで 0.8 で許容外 → そのまま 0.2。
        var untouched = SnapCalculator.SnapMoveStart(0.2, 3.0, new[] { 1.0 }, 0.5);
        Assert.Equal(0.2, untouched);
        // 要求開始が負なら 0 に丸める。
        var negative = SnapCalculator.SnapMoveStart(-1.0, 2.0, Array.Empty<double>(), 0.1);
        Assert.Equal(0, negative);
    }

    [Fact]
    public void CollectSnapTimes_NegativePlayhead_IsExcluded()
    {
        var project = new EditorProject();
        var times = SnapCalculator.CollectSnapTimes(project, playheadSeconds: -3);
        Assert.DoesNotContain(-3.0, times);
        Assert.Contains(0.0, times);
    }

    // ---------- 速度：極端値と往復 ----------

    [Fact]
    public void ChangeSpeed_NaN_BecomesNormalSpeed()
    {
        var clip = new Clip { StartSeconds = 0, DurationSeconds = 4, Speed = 2.0 };
        TimelineEditor.ChangeSpeed(clip, double.NaN);
        Assert.Equal(1.0, clip.Speed);
        // 使用範囲（4秒×2倍=8素材秒）を保ち、等速では長さ 8 になる。
        Assert.Equal(8, clip.DurationSeconds);
    }

    [Fact]
    public void SourceTimelineRoundTrip_ExtremeSpeeds()
    {
        foreach (var reversed in new[] { false, true })
        foreach (var speed in new[] { 0.1, 4.0 })
        {
            var clip = new Clip
            {
                StartSeconds = 3,
                DurationSeconds = 5,
                InPointSeconds = 2,
                IsReversed = reversed,
                Speed = speed,
            };
            for (var t = clip.StartSeconds; t <= clip.EndSeconds; t += 0.25)
            {
                var source = clip.GetSourceTimeSeconds(t);
                Assert.Equal(t, clip.GetTimelineTimeSeconds(source), precision: 9);
            }
            // 範囲外の時刻は端に丸める。
            Assert.Equal(clip.GetSourceTimeSeconds(clip.StartSeconds), clip.GetSourceTimeSeconds(-100));
            Assert.Equal(clip.GetSourceTimeSeconds(clip.EndSeconds), clip.GetSourceTimeSeconds(1e9));
        }
    }

    [Fact]
    public void ClampSpeed_Infinity_ClampsToRange()
    {
        Assert.Equal(Clip.MaxSpeed, Clip.ClampSpeed(double.PositiveInfinity));
        Assert.Equal(Clip.MinSpeed, Clip.ClampSpeed(double.NegativeInfinity));
        Assert.Equal(1.0, Clip.ClampSpeed(double.NaN));
    }
}
