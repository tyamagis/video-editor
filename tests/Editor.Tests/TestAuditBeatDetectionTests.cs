// テスト監査（2026-11-25）で追加：BPM・ビート検出（AudioBeatAnalyzer と
// AutoBpmDetector / WaveformBeatDetector の委譲口）は専用テストが無かった。
// 既知 BPM のクリックトラックで検出値を検証し、失敗経路とキャッシュも対象とする。

using Editor.Audio;
using Xunit;

namespace Editor.Tests;

/// <summary>BPM・ビート検出（AudioBeatAnalyzer 系）のテスト。</summary>
public sealed class TestAuditBeatDetectionTests
{
    private const int SampleRate = 22050;

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

    /// <summary>指定 BPM のクリックトラック（短い高音バーストを拍間隔で並べたもの）。</summary>
    private static float[] ClickTrack(double bpm, int seconds)
    {
        var samples = new float[SampleRate * seconds];
        var interval = 60.0 / bpm;
        for (var beat = 0; beat * interval < seconds; beat++)
        {
            var start = (int)(beat * interval * SampleRate);
            for (var i = 0; i < 1200 && start + i < samples.Length; i++)
                samples[start + i] = 0.9f * (float)Math.Sin(2 * Math.PI * 2000 * i / SampleRate) * (1f - i / 1200f);
        }
        return samples;
    }

    [Fact]
    public async Task Analyze_120BpmClickTrack_DetectsBpmAndBeats()
    {
        using var temp = new TempDirectory();
        var path = temp.File("click120.wav");
        WriteWav(path, ClickTrack(120, 12));

        var r = await new AudioBeatAnalyzer().AnalyzeAsync(path);

        Assert.True(r.Detected, r.FailureReason);
        Assert.NotNull(r.Bpm);
        // フレーム刻み（256サンプルホップ）の量子化で最大 ±2 BPM 程度ずれる。
        Assert.InRange(r.Bpm!.Value, 118, 122);
        Assert.InRange(r.Confidence, 0.5, 1.0);
        // 12秒・120BPM なら拍は約24個、間隔は約0.5秒。
        Assert.InRange(r.BeatTimes.Count, 22, 26);
        for (var i = 1; i < r.BeatTimes.Count; i++)
            Assert.Equal(0.5, r.BeatTimes[i] - r.BeatTimes[i - 1], 1);
    }

    [Fact]
    public async Task Analyze_90BpmClickTrack_DetectsBpm()
    {
        using var temp = new TempDirectory();
        var path = temp.File("click90.wav");
        WriteWav(path, ClickTrack(90, 12));

        var r = await new AudioBeatAnalyzer().AnalyzeAsync(path);

        Assert.True(r.Detected, r.FailureReason);
        Assert.NotNull(r.Bpm);
        Assert.InRange(r.Bpm!.Value, 88, 92);
    }

    [Fact]
    public async Task Analyze_SameFileTwice_ReturnsCachedInstance()
    {
        using var temp = new TempDirectory();
        var path = temp.File("click120.wav");
        WriteWav(path, ClickTrack(120, 12));
        var analyzer = new AudioBeatAnalyzer();

        var first = await analyzer.AnalyzeAsync(path);
        var second = await analyzer.AnalyzeAsync(path);

        Assert.Same(first, second);
    }

    [Fact]
    public async Task Delegates_OnClickTrack_ReturnBpmAndBeats()
    {
        using var temp = new TempDirectory();
        var path = temp.File("click120.wav");
        WriteWav(path, ClickTrack(120, 12));
        var analyzer = new AudioBeatAnalyzer();

        var bpm = await new AutoBpmDetector(analyzer).DetectBpmAsync(path);
        Assert.NotNull(bpm);
        Assert.InRange(bpm!.Value, 118, 122);

        var beats = await new WaveformBeatDetector(analyzer).DetectBeatsAsync(path);
        Assert.InRange(beats.Count, 22, 26);
    }

    // ---- 失敗経路 ----

    [Fact]
    public async Task Analyze_EmptyPath_Fails()
    {
        var r = await new AudioBeatAnalyzer().AnalyzeAsync("");
        Assert.False(r.Detected);
        Assert.NotNull(r.FailureReason);
    }

    [Fact]
    public async Task Analyze_MissingFile_Fails()
    {
        using var temp = new TempDirectory();
        var r = await new AudioBeatAnalyzer().AnalyzeAsync(temp.File("missing.wav"));
        Assert.False(r.Detected);
    }

    [Fact]
    public async Task Analyze_TooShort_Fails()
    {
        using var temp = new TempDirectory();
        var path = temp.File("short.wav");
        WriteWav(path, ClickTrack(120, 2)); // 最小 4 秒未満
        var r = await new AudioBeatAnalyzer().AnalyzeAsync(path);
        Assert.False(r.Detected);
    }

    [Fact]
    public async Task Analyze_Silence_Fails()
    {
        using var temp = new TempDirectory();
        var path = temp.File("sil.wav");
        WriteWav(path, new float[SampleRate * 6]);
        var r = await new AudioBeatAnalyzer().AnalyzeAsync(path);
        Assert.False(r.Detected);
    }

    [Fact]
    public async Task Analyze_ConstantTone_Fails()
    {
        using var temp = new TempDirectory();
        var path = temp.File("tone.wav");
        var samples = new float[SampleRate * 6];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = 0.5f * (float)Math.Sin(2 * Math.PI * 440 * i / SampleRate);
        WriteWav(path, samples);
        var r = await new AudioBeatAnalyzer().AnalyzeAsync(path);
        Assert.False(r.Detected);
    }

    [Fact]
    public async Task Delegates_OnFailure_ReturnNullAndEmpty()
    {
        using var temp = new TempDirectory();
        var missing = temp.File("missing.wav");
        var analyzer = new AudioBeatAnalyzer();

        Assert.Null(await new AutoBpmDetector(analyzer).DetectBpmAsync(missing));
        Assert.Empty(await new WaveformBeatDetector(analyzer).DetectBeatsAsync(missing));
    }
}
