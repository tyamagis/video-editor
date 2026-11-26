// テスト監査（2026-11-25）で追加：無音検出（SilenceDetector）は専用テストが無かった。
// 設定の検証経路と、実 WAV での検出（区間・マージン・イン点相対）を対象とする。
// NaN 系は「範囲チェックをすり抜けて無限ループ／誤検出になる」実バグの回帰テスト。

using Editor.Audio;
using Xunit;

namespace Editor.Tests;

/// <summary>無音区間検出（SilenceDetector）のテスト。</summary>
public sealed class TestAuditSilenceTests
{
    private const int SampleRate = 22050;

    /// <summary>PCM16・モノラル WAV を書く。</summary>
    private static void WriteWav(string path, float[] mono, int sampleRate = SampleRate)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF".ToCharArray());
        writer.Write(36 + mono.Length * 2);
        writer.Write("WAVE".ToCharArray());
        writer.Write("fmt ".ToCharArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data".ToCharArray());
        writer.Write(mono.Length * 2);
        foreach (var s in mono)
            writer.Write((short)(Math.Clamp(s, -1f, 1f) * short.MaxValue));
    }

    /// <summary>秒ごとの振幅を指定して波形を作る（0 = 無音、0.5 = 440Hzトーン）。</summary>
    private static float[] BuildBySeconds(params double[] amplitudePerSecond)
    {
        var samples = new float[SampleRate * amplitudePerSecond.Length];
        for (var sec = 0; sec < amplitudePerSecond.Length; sec++)
        {
            if (amplitudePerSecond[sec] == 0)
                continue;
            for (var i = 0; i < SampleRate; i++)
            {
                var idx = sec * SampleRate + i;
                samples[idx] = (float)(amplitudePerSecond[sec] * Math.Sin(2 * Math.PI * 440 * idx / SampleRate));
            }
        }
        return samples;
    }

    private static async Task<SilenceDetectionResult> Analyze(
        string path, double inPoint = 0, double duration = 3.0, SilenceDetectionSettings? settings = null)
        => await new SilenceDetector().AnalyzeAsync(path, inPoint, duration, settings ?? new SilenceDetectionSettings());

    /// <summary>区間境界の時刻比較（10ms 窓の量子化 ±1 窓を許容）。</summary>
    private static void AssertTime(double expected, double actual)
        => Assert.True(Math.Abs(expected - actual) <= 0.011,
            $"時刻が期待とずれています。期待 {expected:F3}、実際 {actual:F3}");

    // ---- 検証経路 ----

    [Fact]
    public async Task Analyze_EmptyPath_Fails()
    {
        var r = await Analyze("");
        Assert.False(r.Detected);
        Assert.NotNull(r.FailureReason);
    }

    [Fact]
    public async Task Analyze_MissingFile_Fails()
    {
        using var temp = new TempDirectory();
        var r = await Analyze(temp.File("missing.wav"));
        Assert.False(r.Detected);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public async Task Analyze_NonPositiveDuration_Fails(double duration)
    {
        using var temp = new TempDirectory();
        var path = temp.File("tone.wav");
        WriteWav(path, BuildBySeconds(0.5));
        var r = await Analyze(path, duration: duration);
        Assert.False(r.Detected);
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(-91.0)]
    public async Task Analyze_ThresholdOutOfRange_Fails(double thresholdDb)
    {
        using var temp = new TempDirectory();
        var path = temp.File("tone.wav");
        WriteWav(path, BuildBySeconds(0.5));
        var r = await Analyze(path, duration: 1.0, settings: new SilenceDetectionSettings { ThresholdDb = thresholdDb });
        Assert.False(r.Detected);
    }

    [Fact]
    public async Task Analyze_TooSmallMinSilence_Fails()
    {
        using var temp = new TempDirectory();
        var path = temp.File("tone.wav");
        WriteWav(path, BuildBySeconds(0.5));
        var r = await Analyze(path, duration: 1.0, settings: new SilenceDetectionSettings { MinSilenceSeconds = 0.01 });
        Assert.False(r.Detected);
    }

    [Fact]
    public async Task Analyze_NegativeMargin_Fails()
    {
        using var temp = new TempDirectory();
        var path = temp.File("tone.wav");
        WriteWav(path, BuildBySeconds(0.5));
        var r = await Analyze(path, duration: 1.0, settings: new SilenceDetectionSettings { MarginSeconds = -0.1 });
        Assert.False(r.Detected);
    }

    // ---- NaN・無限大の回帰（修正前は無限ループ／誤検出／NaN結果） ----

    [Fact]
    public async Task Analyze_NaNThreshold_FailsInsteadOfHanging()
    {
        using var temp = new TempDirectory();
        var path = temp.File("tone.wav");
        WriteWav(path, BuildBySeconds(0.5));
        var r = await Analyze(path, duration: 1.0, settings: new SilenceDetectionSettings { ThresholdDb = double.NaN });
        Assert.False(r.Detected);
    }

    [Fact]
    public async Task Analyze_NaNMinSilence_Fails()
    {
        using var temp = new TempDirectory();
        var path = temp.File("tone.wav");
        WriteWav(path, BuildBySeconds(0.5));
        var r = await Analyze(path, duration: 1.0, settings: new SilenceDetectionSettings { MinSilenceSeconds = double.NaN });
        Assert.False(r.Detected);
    }

    [Fact]
    public async Task Analyze_NaNMargins_Fails()
    {
        using var temp = new TempDirectory();
        var path = temp.File("tone.wav");
        WriteWav(path, BuildBySeconds(0.5));
        var r = await Analyze(path, duration: 1.0, settings: new SilenceDetectionSettings { MarginSeconds = double.NaN });
        Assert.False(r.Detected);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task Analyze_NonFiniteDuration_Fails(double duration)
    {
        using var temp = new TempDirectory();
        var path = temp.File("tone.wav");
        WriteWav(path, BuildBySeconds(0.5));
        var r = await Analyze(path, duration: duration);
        Assert.False(r.Detected);
    }

    // ---- 実検出 ----

    [Fact]
    public async Task Analyze_SilenceToneSilence_FindsTwoSegmentsWithMargins()
    {
        using var temp = new TempDirectory();
        var path = temp.File("mix.wav");
        WriteWav(path, BuildBySeconds(0, 0.5, 0));
        var r = await Analyze(path, duration: 3.0);
        Assert.True(r.Detected);
        Assert.Equal(2, r.Segments.Count);
        // 既定マージン 0.1 秒を両端から引いた範囲になる。
        // 境界は 10ms 窓の量子化で ±1 窓ずれるため、0.011 秒の許容を持たせる。
        AssertTime(0.1, r.Segments[0].StartSeconds);
        AssertTime(0.9, r.Segments[0].EndSeconds);
        AssertTime(2.1, r.Segments[1].StartSeconds);
        AssertTime(2.9, r.Segments[1].EndSeconds);
        Assert.Equal(3.0, r.AnalyzedDurationSeconds);
    }

    [Fact]
    public async Task Analyze_AllSilence_SingleSegmentTrimmedByMargins()
    {
        using var temp = new TempDirectory();
        var path = temp.File("sil.wav");
        WriteWav(path, BuildBySeconds(0, 0, 0));
        var r = await Analyze(path, duration: 3.0);
        Assert.True(r.Detected);
        var seg = Assert.Single(r.Segments);
        Assert.Equal(0.1, seg.StartSeconds, 2);
        Assert.Equal(2.9, seg.EndSeconds, 2);
    }

    [Fact]
    public async Task Analyze_AllTone_NoSegments()
    {
        using var temp = new TempDirectory();
        var path = temp.File("tone.wav");
        WriteWav(path, BuildBySeconds(0.5, 0.5, 0.5));
        var r = await Analyze(path, duration: 3.0);
        Assert.True(r.Detected);
        Assert.Empty(r.Segments);
        Assert.Equal(0, r.TotalSilenceSeconds);
    }

    [Fact]
    public async Task Analyze_ShortGapBelowMinSilence_IsIgnored()
    {
        using var temp = new TempDirectory();
        var path = temp.File("gap.wav");
        var samples = BuildBySeconds(0.5, 0.5);
        // 2秒目の中ほどに 0.2 秒だけ無音を入れる（最短 0.5 秒未満なので残す）。
        for (var i = SampleRate / 2; i < SampleRate / 2 + SampleRate / 5; i++)
            samples[SampleRate + i] = 0;
        WriteWav(path, samples);
        var r = await Analyze(path, duration: 2.0);
        Assert.True(r.Detected);
        Assert.Empty(r.Segments);
    }

    [Fact]
    public async Task Analyze_InPoint_SegmentsAreRelativeToInPoint()
    {
        using var temp = new TempDirectory();
        var path = temp.File("mix.wav");
        WriteWav(path, BuildBySeconds(0.5, 0, 0.5));
        // イン点 1 秒から 1 秒分（無音区間そのもの）を読むと、相対 0.1〜0.9 が返る。
        var r = await Analyze(path, inPoint: 1.0, duration: 1.0);
        Assert.True(r.Detected);
        var seg = Assert.Single(r.Segments);
        Assert.Equal(0.1, seg.StartSeconds, 2);
        Assert.Equal(0.9, seg.EndSeconds, 2);
    }

    [Fact]
    public async Task Analyze_MarginLargerThanSegment_DropsSegment()
    {
        using var temp = new TempDirectory();
        var path = temp.File("mix.wav");
        WriteWav(path, BuildBySeconds(0, 0.5, 0.5));
        // 無音 1 秒に対し前後 0.6 秒ずつ余白を取ると、切る範囲が消える。
        var r = await Analyze(path, duration: 3.0, settings: new SilenceDetectionSettings { MarginSeconds = 0.6 });
        Assert.True(r.Detected);
        Assert.Empty(r.Segments);
    }
}
