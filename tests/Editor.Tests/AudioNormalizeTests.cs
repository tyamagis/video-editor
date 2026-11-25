// 音声ノーマライズの単体テスト（AudioNormalizer・PeakAnalyzer・
// AudioExportPlanner の volume フィルタ・Clip の JSON 往復）。
// 小さな WAV ファイルを実際に作り、ピーク検出の正確さを対象とする。
// プレビュー（AudioPreviewPlayer）は WaveOut を使うため Linux では
// 動かさず、ゲイン計算とピーク検出（プレビュー・書き出しの共通部分）で担保する。

using System.Text.Json.Nodes;
using Editor.Audio;
using Editor.Core;
using Editor.Media;
using Editor.Render;
using NAudio.Wave;
using Xunit;

namespace Editor.Tests;

/// <summary>音声ノーマライズ（AudioNormalizer・PeakAnalyzer・書き出しフィルタ）のテスト。</summary>
public sealed class AudioNormalizeTests
{
    /// <summary>テスト用の小さな WAV（PCM16・8000Hz）を作る。チャンネルごとのサンプル列を指定する。</summary>
    private static void WriteWav(string path, float[][] channels, int sampleRate = 8000)
    {
        var frames = channels[0].Length;
        var channelCount = channels.Length;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF".ToCharArray());
        writer.Write(36 + frames * channelCount * 2);
        writer.Write("WAVE".ToCharArray());
        writer.Write("fmt ".ToCharArray());
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)channelCount);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channelCount * 2);
        writer.Write((short)(channelCount * 2));
        writer.Write((short)16);
        writer.Write("data".ToCharArray());
        writer.Write(frames * channelCount * 2);
        for (var f = 0; f < frames; f++)
            for (var c = 0; c < channelCount; c++)
            {
                var clamped = Math.Clamp(channels[c][f], -1f, 1f);
                writer.Write((short)Math.Round(clamped * short.MaxValue));
            }
    }

    /// <summary>指定した振幅の定数サンプル列を作る（ピークがちょうど振幅になる）。</summary>
    private static float[] Constant(float amplitude, int frames = 8000)
    {
        var samples = new float[frames];
        Array.Fill(samples, amplitude);
        return samples;
    }

    // --- AudioNormalizer のゲイン計算 ---

    [Theory]
    [InlineData(0.5, -1.0, 1.7825)] // ピーク -6.02 dBFS → 目標 -1 dBFS（+5.02 dB）
    [InlineData(1.0, -1.0, 0.8913)] // フルスケール → 少し絞る（-1 dB）
    [InlineData(0.1, -1.0, 8.9125)] // ピーク -20 dBFS → +19 dB
    [InlineData(0.5, -6.0206, 1.0)] // 目標がピークと同じ → 等倍
    [InlineData(0.0, -1.0, 1.0)] // 無音 → 等倍（無限大にしない）
    public void ComputeGain_KnownCases(double peak, double targetDb, double expected)
    {
        Assert.Equal(expected, AudioNormalizer.ComputeGain(peak, targetDb), 4);
    }

    [Fact]
    public void ComputeGainDb_KnownCase()
    {
        // ピーク 0.5（-6.0206 dBFS）を目標 -1 dBFS にするゲインは +5.0206 dB。
        Assert.Equal(5.0206, AudioNormalizer.ComputeGainDb(0.5, -1.0), 3);
    }

    [Fact]
    public void ComputeGain_VeryQuietClip_CapsAt40Db()
    {
        // ピーク -80 dBFS を目標 -1 dBFS にするには +79 dB 要るが、上限 +40 dB（100倍）で止まる。
        Assert.Equal(40.0, AudioNormalizer.ComputeGainDb(0.0001, -1.0), 4);
        Assert.Equal(100.0, AudioNormalizer.ComputeGain(0.0001, -1.0), 4);
    }

    [Theory]
    [InlineData(-40.0, -30.0)] // 下限で丸める
    [InlineData(5.0, 0.0)] // 上限で丸める
    [InlineData(-1.0, -1.0)]
    public void ClampTargetDb_Range(double input, double expected)
    {
        Assert.Equal(expected, AudioNormalizer.ClampTargetDb(input));
    }

    [Fact]
    public void ComputeGain_TargetOutsideRange_UsesClampedTarget()
    {
        // 目標 -40 dBFS は -30 dBFS に丸められるため、同じゲインになる。
        Assert.Equal(
            AudioNormalizer.ComputeGain(1.0, -30.0),
            AudioNormalizer.ComputeGain(1.0, -40.0));
    }

    [Fact]
    public void PeakToDb_KnownCases()
    {
        Assert.Equal(0.0, AudioNormalizer.PeakToDb(1.0), 4);
        Assert.Equal(-6.0206, AudioNormalizer.PeakToDb(0.5), 3);
        Assert.Equal(double.NegativeInfinity, AudioNormalizer.PeakToDb(0.0));
    }

    // --- PeakAnalyzer のピーク検出 ---

    [Fact]
    public void AudioFileReader_Volume_ScalesSamples()
    {
        // プレビューのゲイン適用（AudioPreviewPlayer が AudioFileReader.Volume に
        // ゲインを渡す）の前提：1 超の Volume がサンプルにそのまま掛かること。
        using var temp = new TempDirectory();
        var path = temp.File("tone.wav");
        WriteWav(path, new[] { Constant(0.25f) });
        using var reader = new AudioFileReader(path) { Volume = 2.0f };
        var provider = reader.ToSampleProvider();
        var buffer = new float[8000];
        var read = provider.Read(buffer, 0, buffer.Length);

        Assert.True(read > 0);
        Assert.Equal(0.5, buffer.Take(read).Max(), 2);
    }

    [Fact]
    public async Task GetPeakAsync_MonoConstant_ReturnsAmplitude()
    {
        using var temp = new TempDirectory();
        var path = temp.File("tone.wav");
        WriteWav(path, new[] { Constant(0.5f) });
        var analyzer = new PeakAnalyzer();

        var peak = await analyzer.GetPeakAsync(path, 0, 1.0);

        Assert.True(peak.Available);
        Assert.Equal(0.5, peak.PeakLinear, 3);
    }

    [Fact]
    public async Task GetPeakAsync_HardPannedStereo_ReturnsChannelPeakNotMonoAverage()
    {
        // 左だけ 0.9・右は無音。モノラル平均で測ると 0.45 になってしまうが、
        // 正規化にはチャンネルごとのピーク（0.9）が要る。
        using var temp = new TempDirectory();
        var path = temp.File("pan.wav");
        WriteWav(path, new[] { Constant(0.9f), Constant(0.0f) });
        var analyzer = new PeakAnalyzer();

        var peak = await analyzer.GetPeakAsync(path, 0, 1.0);

        Assert.True(peak.Available);
        Assert.Equal(0.9, peak.PeakLinear, 3);
    }

    [Fact]
    public async Task GetPeakAsync_Range_MeasuresOnlyUsedRange()
    {
        // 前半は 0.1、後半に 0.8 のピークがある 1 秒の素材。
        using var temp = new TempDirectory();
        var path = temp.File("range.wav");
        var first = Constant(0.1f, 4000);
        var second = Constant(0.1f, 4000);
        second[2000] = 0.8f;
        WriteWav(path, new[] { first.Concat(second).ToArray() });
        var analyzer = new PeakAnalyzer();

        var firstHalf = await analyzer.GetPeakAsync(path, 0, 0.5);
        var secondHalf = await analyzer.GetPeakAsync(path, 0.5, 0.5);

        Assert.Equal(0.1, firstHalf.PeakLinear, 3);
        Assert.Equal(0.8, secondHalf.PeakLinear, 3);
    }

    [Fact]
    public async Task GetPeakAsync_Silence_ReturnsZeroAvailable()
    {
        using var temp = new TempDirectory();
        var path = temp.File("silence.wav");
        WriteWav(path, new[] { Constant(0.0f) });
        var analyzer = new PeakAnalyzer();

        var peak = await analyzer.GetPeakAsync(path, 0, 1.0);

        Assert.True(peak.Available);
        Assert.Equal(0.0, peak.PeakLinear);
    }

    [Fact]
    public async Task GetPeakAsync_MissingFile_ReturnsUnavailable()
    {
        var analyzer = new PeakAnalyzer();

        var peak = await analyzer.GetPeakAsync("no-such-file.wav", 0, 1.0);

        Assert.False(peak.Available);
        Assert.NotNull(peak.FailureReason);
    }

    [Fact]
    public async Task GetPeakAsync_SecondCall_ReusesCache()
    {
        using var temp = new TempDirectory();
        var path = temp.File("tone.wav");
        WriteWav(path, new[] { Constant(0.5f) });
        var analyzer = new PeakAnalyzer();

        var first = await analyzer.GetPeakAsync(path, 0, 1.0);
        // 素材を差し替えると身元（サイズ＋更新日時）が変わるため、再解析される。
        WriteWav(path, new[] { Constant(0.25f) });
        var afterReplace = await analyzer.GetPeakAsync(path, 0, 1.0);
        var second = await analyzer.GetPeakAsync(path, 0, 1.0);

        Assert.Equal(0.5, first.PeakLinear, 3);
        Assert.Equal(0.25, afterReplace.PeakLinear, 3);
        Assert.Equal(afterReplace.PeakLinear, second.PeakLinear, 4);
    }

    // --- 書き出しフィルタ（AudioExportPlanner） ---

    [Theory]
    [InlineData(1.0, "")]
    [InlineData(1.7825, "volume=1.7825,")]
    [InlineData(0.8913, "volume=0.8913,")]
    public void VolumeFilter_KnownCases(double gain, string expected)
    {
        Assert.Equal(expected, AudioExportPlanner.VolumeFilter(gain));
    }

    [Fact]
    public void BuildClipFilter_NormalizeGain_AddsVolumeBeforeAtrim()
    {
        var clip = new Clip { Kind = ClipKind.Audio, DurationSeconds = 1.0 };

        var filter = AudioExportPlanner.BuildClipFilter(1, clip, 0, 1.0, 0, "a1", 1.7825);

        Assert.StartsWith("[1:a]volume=1.7825,atrim=", filter);
        Assert.EndsWith("[a1]", filter);
    }

    [Fact]
    public void BuildClipFilter_NormalizeGainOne_AddsNoVolumeFilter()
    {
        var clip = new Clip { Kind = ClipKind.Audio, DurationSeconds = 1.0 };

        var filter = AudioExportPlanner.BuildClipFilter(1, clip, 0, 1.0, 0, "a1");

        Assert.DoesNotContain("volume=", filter);
    }

    [Fact]
    public void BuildClipFilter_NormalizeAndDenoise_BothBeforeAtrim()
    {
        var clip = new Clip
        {
            Kind = ClipKind.Audio,
            DurationSeconds = 1.0,
            NoiseReductionEnabled = true,
            NoiseReductionStrength = 0.5
        };

        var filter = AudioExportPlanner.BuildClipFilter(1, clip, 0, 1.0, 0, "a1", 2.0);

        Assert.Contains("afftdn=", filter);
        Assert.Contains("volume=2.0000,", filter);
        Assert.True(filter.IndexOf("afftdn=", StringComparison.Ordinal)
            < filter.IndexOf("volume=", StringComparison.Ordinal));
        Assert.True(filter.IndexOf("volume=", StringComparison.Ordinal)
            < filter.IndexOf("atrim=", StringComparison.Ordinal));
    }

    /// <summary>ピークが測れない提供口（フォールバックの確認用）。</summary>
    private sealed class UnavailablePeakProvider : IAudioPeakProvider
    {
        public Task<AudioPeakResult> GetPeakAsync(
            string sourcePath, double inPointSeconds, double durationSeconds,
            CancellationToken ct = default)
            => Task.FromResult(new AudioPeakResult(0, false, "測れませんでした"));
    }

    [Fact]
    public async Task BuildAudioPlanAsync_NormalizeEnabled_AddsVolumeFilter()
    {
        using var temp = new TempDirectory();
        var path = temp.File("tone.wav");
        WriteWav(path, new[] { Constant(0.5f) });
        var project = new EditorProject();
        var track = project.AddTrack(TrackKind.Audio, "A1");
        track.Clips.Add(new Clip
        {
            Kind = ClipKind.Audio,
            SourcePath = path,
            StartSeconds = 0,
            DurationSeconds = 1.0,
            NormalizeAudioEnabled = true,
            NormalizeTargetDb = -1.0
        });
        var planner = new AudioExportPlanner(new MediaProbe(new FfmpegSettings()), new PeakAnalyzer());

        var (inputs, mapLabel) = await planner.BuildAudioPlanAsync(project, 0, 1.0, CancellationToken.None);

        Assert.Single(inputs);
        Assert.Equal("a1", mapLabel);
        // ピーク 0.5（-6.02 dBFS）→ 目標 -1 dBFS のゲインは 1.7825。
        Assert.Contains("volume=1.7825,", inputs[0].Filter);
    }

    [Fact]
    public async Task BuildAudioPlanAsync_NormalizeDisabled_AddsNoVolumeFilter()
    {
        using var temp = new TempDirectory();
        var path = temp.File("tone.wav");
        WriteWav(path, new[] { Constant(0.5f) });
        var project = new EditorProject();
        var track = project.AddTrack(TrackKind.Audio, "A1");
        track.Clips.Add(new Clip
        {
            Kind = ClipKind.Audio,
            SourcePath = path,
            StartSeconds = 0,
            DurationSeconds = 1.0
        });
        var planner = new AudioExportPlanner(new MediaProbe(new FfmpegSettings()), new PeakAnalyzer());

        var (inputs, _) = await planner.BuildAudioPlanAsync(project, 0, 1.0, CancellationToken.None);

        Assert.Single(inputs);
        Assert.DoesNotContain("volume=", inputs[0].Filter);
    }

    [Fact]
    public async Task BuildAudioPlanAsync_PeakUnavailable_FallsBackToNoGain()
    {
        using var temp = new TempDirectory();
        var path = temp.File("tone.wav");
        WriteWav(path, new[] { Constant(0.5f) });
        var project = new EditorProject();
        var track = project.AddTrack(TrackKind.Audio, "A1");
        track.Clips.Add(new Clip
        {
            Kind = ClipKind.Audio,
            SourcePath = path,
            StartSeconds = 0,
            DurationSeconds = 1.0,
            NormalizeAudioEnabled = true,
            NormalizeTargetDb = -1.0
        });
        var planner = new AudioExportPlanner(new MediaProbe(new FfmpegSettings()), new UnavailablePeakProvider());

        var (inputs, _) = await planner.BuildAudioPlanAsync(project, 0, 1.0, CancellationToken.None);

        // ピークが測れなくても書き出しは止まらず、ゲイン無し（元の音量）で進む。
        Assert.Single(inputs);
        Assert.DoesNotContain("volume=", inputs[0].Filter);
    }

    // --- Clip の JSON 往復 ---

    [Fact]
    public void ProjectStore_RoundTrip_PreservesNormalizeSettings()
    {
        var project = new EditorProject();
        var track = project.AddTrack(TrackKind.Audio, "A1");
        track.Clips.Add(new Clip
        {
            Kind = ClipKind.Audio,
            SourcePath = "a.wav",
            DurationSeconds = 2.0,
            NormalizeAudioEnabled = true,
            NormalizeTargetDb = -3.0
        });

        var loaded = ProjectStore.Deserialize(ProjectStore.Serialize(project));

        var clip = loaded.Tracks[0].Clips[0];
        Assert.True(clip.NormalizeAudioEnabled);
        Assert.Equal(-3.0, clip.NormalizeTargetDb);
    }

    [Fact]
    public void ProjectStore_OldJsonWithoutNormalize_OpensAsDisabled()
    {
        var project = new EditorProject();
        var track = project.AddTrack(TrackKind.Audio, "A1");
        track.Clips.Add(new Clip
        {
            Kind = ClipKind.Audio,
            SourcePath = "a.wav",
            DurationSeconds = 2.0,
            NormalizeAudioEnabled = true,
            NormalizeTargetDb = -3.0
        });

        // 旧プロジェクト相当の JSON：ノーマライズの項目を落とす。
        var node = JsonNode.Parse(ProjectStore.Serialize(project))!.AsObject();
        var clipNode = node["Tracks"]![0]!["Clips"]![0]!.AsObject();
        Assert.True(clipNode.Remove("NormalizeAudioEnabled"));
        Assert.True(clipNode.Remove("NormalizeTargetDb"));

        var loaded = ProjectStore.LoadFromJson(node.ToJsonString());

        var clip = loaded.Tracks[0].Clips[0];
        Assert.False(clip.NormalizeAudioEnabled);
        Assert.Equal(-1.0, clip.NormalizeTargetDb);
    }

    [Fact]
    public void Clip_Defaults_NormalizeDisabled()
    {
        var clip = new Clip();

        Assert.False(clip.NormalizeAudioEnabled);
        Assert.Equal(-1.0, clip.NormalizeTargetDb);
    }
}
