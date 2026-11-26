// テスト監査（2026-11-25）で追加：可視化（スペクトル／波形／リサジュー）は
// 評価・描画とも専用テストが無かった。設定のパース／クランプ、評価器の
// キーフレーム適用、描画の基本経路と「描かない」経路を対象とする。

using Editor.Core;
using Editor.Render;
using SkiaSharp;
using Xunit;

namespace Editor.Tests;

/// <summary>可視化設定・評価・描画（VisualizationSettings / TimelineEvaluator / VisualizationRenderer）のテスト。</summary>
public sealed class TestAuditVisualizationTests
{
    // ---- 設定のパースとクランプ ----

    [Theory]
    [InlineData("Visualization:Gain", "Gain")]
    [InlineData("Visualization:LineWidth", "LineWidth")]
    [InlineData("Visualization:WindowSeconds", "WindowSeconds")]
    [InlineData("Visualization:TrailSeconds", "TrailSeconds")]
    public void TryParseKeyframeParameter_ValidFields(string name, string expectedField)
    {
        Assert.True(VisualizationSettings.TryParseKeyframeParameter(name, out var field));
        Assert.Equal(expectedField, field);
    }

    [Theory]
    [InlineData("Visualization:BandCount")]
    [InlineData("Visualization:")]
    [InlineData("Gain")]
    [InlineData("Effect:Blur.Amount")]
    [InlineData("")]
    public void TryParseKeyframeParameter_InvalidNames(string name)
        => Assert.False(VisualizationSettings.TryParseKeyframeParameter(name, out _));

    [Fact]
    public void Clamps_BoundariesAndPassthrough()
    {
        Assert.Equal(0.1, VisualizationSettings.ClampGain(0.0));
        Assert.Equal(4.0, VisualizationSettings.ClampGain(100));
        Assert.Equal(2.0, VisualizationSettings.ClampGain(2.0));
        Assert.Equal(0.5, VisualizationSettings.ClampLineWidth(0.0));
        Assert.Equal(10.0, VisualizationSettings.ClampLineWidth(99));
        Assert.Equal(0.02, VisualizationSettings.ClampWindowSeconds(0.0));
        Assert.Equal(1.0, VisualizationSettings.ClampWindowSeconds(5));
        Assert.Equal(0.0, VisualizationSettings.ClampTrailSeconds(-1));
        Assert.Equal(1.0, VisualizationSettings.ClampTrailSeconds(2));
        // 未知の項目はそのまま通す。
        Assert.Equal(123.0, VisualizationSettings.ClampKeyframeValue("Unknown", 123.0));
        Assert.Equal(4.0, VisualizationSettings.ClampKeyframeValue("Gain", 100));
    }

    // ---- 評価器 ----

    private static Clip MakeVizClip()
        => new()
        {
            Kind = ClipKind.Visualization,
            StartSeconds = 0,
            DurationSeconds = 10,
            Visualization = new VisualizationSettings { Gain = 2.0, SourceClipId = "src1" }
        };

    [Fact]
    public void Evaluate_VisualizationClip_WithoutKeyframes_KeepsFixedValues()
    {
        var clip = MakeVizClip();
        var state = TimelineEvaluator.EvaluateClip(clip, 5.0);
        Assert.NotNull(state.Visualization);
        Assert.Equal(2.0, state.Visualization!.Gain);
    }

    [Fact]
    public void Evaluate_VisualizationClip_KeyframeGain_IsAppliedAndClamped()
    {
        var clip = MakeVizClip();
        clip.KeyframeTracks.Add(new KeyframeTrack
        {
            ParameterName = "Visualization:Gain",
            Keyframes = new List<Keyframe> { new(0, 10), new(10, 10) }
        });

        var state = TimelineEvaluator.EvaluateClip(clip, 5.0);

        // キーフレーム値 10 は Gain の上限 4 に丸められ、元の設定は変わらない。
        Assert.Equal(4.0, state.Visualization!.Gain);
        Assert.Equal(2.0, clip.Visualization!.Gain);
    }

    [Fact]
    public void Evaluate_NonVisualizationClip_HasNullVisualization()
    {
        var clip = new Clip { Kind = ClipKind.Video, StartSeconds = 0, DurationSeconds = 10 };
        Assert.Null(TimelineEvaluator.EvaluateClip(clip, 5.0).Visualization);
    }

    [Fact]
    public void Serialize_RoundTrip_PreservesVisualizationSettings()
    {
        var project = new EditorProject();
        var track = project.AddTrack(TrackKind.Video, "V1");
        var clip = MakeVizClip();
        clip.Visualization!.Kind = VisualizationKind.Lissajous;
        clip.Visualization.BandCount = 64;
        clip.Visualization.TrailSeconds = 0.5;
        track.Clips.Add(clip);

        var restored = ProjectStore.Deserialize(ProjectStore.Serialize(project));
        var restoredClip = Assert.Single(Assert.Single(restored.Tracks).Clips);
        Assert.NotNull(restoredClip.Visualization);
        Assert.Equal(VisualizationKind.Lissajous, restoredClip.Visualization!.Kind);
        Assert.Equal(64, restoredClip.Visualization.BandCount);
        Assert.Equal("src1", restoredClip.Visualization.SourceClipId);
        Assert.Equal(0.5, restoredClip.Visualization.TrailSeconds);
    }

    // ---- 描画 ----

    private sealed class FakeVisualizationProvider : IAudioVisualizationProvider
    {
        public bool Analyzed { get; set; } = true;
        public double[] Bands { get; set; } = { 0.8, 0.6, 0.4, 0.2 };
        public (float[] Left, float[] Right) Window { get; set; } =
            (Enumerable.Range(0, 200).Select(i => (float)Math.Sin(i * 0.2)).ToArray(),
             Enumerable.Range(0, 200).Select(i => (float)Math.Cos(i * 0.2)).ToArray());

        public bool IsAnalyzed(string clipId) => Analyzed;
        public double[] GetSpectrumBands(string clipId, double timeSeconds, int bandCount,
            double lowHz, double highHz, bool logarithmic) => Bands;
        public (float[] Left, float[] Right) GetStereoWindow(string clipId, double timeSeconds, double windowSeconds)
            => Window;
    }

    private static int CountDrawnPixels(SKBitmap bitmap)
    {
        var count = 0;
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y).Alpha > 0)
                    count++;
        return count;
    }

    private static (EditorProject Project, Clip VizClip) MakeDrawSetup(VisualizationKind kind)
    {
        var project = new EditorProject();
        var track = project.AddTrack(TrackKind.Video, "V1");
        var source = new Clip
        {
            Id = "src1",
            Kind = ClipKind.Audio,
            SourcePath = "music.wav",
            StartSeconds = 0,
            DurationSeconds = 10
        };
        var viz = new Clip
        {
            Kind = ClipKind.Visualization,
            StartSeconds = 0,
            DurationSeconds = 10,
            Visualization = new VisualizationSettings
            {
                Kind = kind,
                SourceClipId = "src1",
                ColorHex = "#FF0000"
            }
        };
        track.Clips.Add(source);
        track.Clips.Add(viz);
        return (project, viz);
    }

    private static EvaluatedClipState StateFor(Clip viz)
        => new() { Clip = viz, TimeSeconds = 5.0, Visualization = viz.Visualization };

    [Theory]
    [InlineData(VisualizationKind.Spectrum)]
    [InlineData(VisualizationKind.Waveform)]
    [InlineData(VisualizationKind.Lissajous)]
    public void DrawClip_WithAnalyzedSource_DrawsPixels(VisualizationKind kind)
    {
        var (project, viz) = MakeDrawSetup(kind);
        using var bitmap = new SKBitmap(200, 200);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        VisualizationRenderer.DrawClip(canvas, viz, StateFor(viz), project, 5.0, 200, 200,
            new FakeVisualizationProvider());
        canvas.Flush();

        Assert.True(CountDrawnPixels(bitmap) > 0);
    }

    [Fact]
    public void DrawClip_NullProvider_DrawsNothing()
    {
        var (project, viz) = MakeDrawSetup(VisualizationKind.Spectrum);
        using var bitmap = new SKBitmap(200, 200);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        VisualizationRenderer.DrawClip(canvas, viz, StateFor(viz), project, 5.0, 200, 200, null);
        canvas.Flush();

        Assert.Equal(0, CountDrawnPixels(bitmap));
    }

    [Fact]
    public void DrawClip_NotAnalyzed_DrawsNothing()
    {
        var (project, viz) = MakeDrawSetup(VisualizationKind.Spectrum);
        using var bitmap = new SKBitmap(200, 200);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        VisualizationRenderer.DrawClip(canvas, viz, StateFor(viz), project, 5.0, 200, 200,
            new FakeVisualizationProvider { Analyzed = false });
        canvas.Flush();

        Assert.Equal(0, CountDrawnPixels(bitmap));
    }

    [Fact]
    public void DrawClip_OutsideSourceRange_DrawsNothing()
    {
        var (project, viz) = MakeDrawSetup(VisualizationKind.Spectrum);
        using var bitmap = new SKBitmap(200, 200);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        // 音源クリップは 0〜10 秒。20 秒時点では鳴っていないので描かない。
        VisualizationRenderer.DrawClip(canvas, viz, StateFor(viz), project, 20.0, 200, 200,
            new FakeVisualizationProvider());
        canvas.Flush();

        Assert.Equal(0, CountDrawnPixels(bitmap));
    }

    [Fact]
    public void DrawSpectrumBars_EmptyOrZeroBands_DrawsNothing()
    {
        using var bitmap = new SKBitmap(100, 100);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        var area = new SKRect(0, 0, 100, 100);

        VisualizationRenderer.DrawSpectrumBars(canvas, area, Array.Empty<double>(), SKColors.Red);
        VisualizationRenderer.DrawSpectrumBars(canvas, area, new[] { 0.0, 0.0 }, SKColors.Red);
        canvas.Flush();

        Assert.Equal(0, CountDrawnPixels(bitmap));
    }

    [Fact]
    public void DrawSpectrumBars_FullBand_DrawsBar()
    {
        using var bitmap = new SKBitmap(100, 100);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        VisualizationRenderer.DrawSpectrumBars(canvas, new SKRect(0, 0, 100, 100),
            new[] { 1.0 }, SKColors.Red);
        canvas.Flush();

        Assert.True(CountDrawnPixels(bitmap) > 1000);
    }

    [Fact]
    public void BuildLissajousPath_PointsAndClamping()
    {
        var left = new float[10];
        var right = new float[10];
        for (var i = 0; i < 10; i++)
        {
            left[i] = i % 2 == 0 ? 10f : -10f;   // 範囲外の値は ±1 に丸められる
            right[i] = i % 2 == 0 ? -10f : 10f;
        }

        using var path = VisualizationRenderer.BuildLissajousPath(
            left, right, 0, 10, cx: 100, cy: 100, half: 50, gain: 1);

        Assert.Equal(10, path.Points.Length);
        Assert.All(path.Points, p =>
        {
            Assert.InRange(p.X, 50, 150);
            Assert.InRange(p.Y, 50, 150);
        });
    }

    [Fact]
    public void BuildLissajousPath_EmptyRange_HasNoPoints()
    {
        using var path = VisualizationRenderer.BuildLissajousPath(
            new float[4], new float[4], 2, 2, cx: 0, cy: 0, half: 50, gain: 1);
        Assert.Empty(path.Points);
    }
}
