// クリップの速度変更（Clip.Speed）のテスト：素材時刻の写像（順／逆 × 速度）、
// 速度変更時の長さ、分割・トリミングとの整合、JSON、書き出しフィルタ
// （タイムライン書き出し・単一クリップ書き出し）、プレビュー描画のフレーム要求、
// 音声バインドの素材時刻、プレビュー音声（リサンプリング・逆再生ステップ）。

using Editor.Audio;
using Editor.Core;
using Editor.Media;
using Editor.Render;
using NAudio.Wave;
using SkiaSharp;
using Xunit;

namespace Editor.Tests;

/// <summary>速度変更（Clip.Speed）のテスト。</summary>
public sealed class SpeedTests
{
    private static Clip MakeClip(
        double startSeconds = 2, double durationSeconds = 4, double inPointSeconds = 10,
        bool isReversed = false, double speed = 1.0)
        => new()
        {
            Kind = ClipKind.Video,
            SourcePath = "dummy.mp4",
            StartSeconds = startSeconds,
            DurationSeconds = durationSeconds,
            InPointSeconds = inPointSeconds,
            IsReversed = isReversed,
            Speed = speed
        };

    // ---- 素材時刻の写像 ----

    [Fact]
    public void GetSourceTimeSeconds_ForwardSpeed2_AdvancesTwiceAsFast()
    {
        // タイムライン 4 秒で素材 8 秒分進む（使用範囲は [10, 18]）。
        var clip = MakeClip(speed: 2.0);
        Assert.Equal(10, clip.GetSourceTimeSeconds(2));
        Assert.Equal(14, clip.GetSourceTimeSeconds(4));
        Assert.Equal(18, clip.GetSourceTimeSeconds(6));
    }

    [Fact]
    public void GetSourceTimeSeconds_ForwardSpeedHalf_AdvancesHalf()
    {
        // タイムライン 4 秒で素材 2 秒分進む（使用範囲は [10, 12]）。
        var clip = MakeClip(speed: 0.5);
        Assert.Equal(10, clip.GetSourceTimeSeconds(2));
        Assert.Equal(11, clip.GetSourceTimeSeconds(4));
        Assert.Equal(12, clip.GetSourceTimeSeconds(6));
    }

    [Fact]
    public void GetSourceTimeSeconds_ReversedSpeed2_MapsBackwardTwiceAsFast()
    {
        // 逆再生＋2倍速：先頭では素材の末尾（18）、末尾では素材の先頭（10）。
        var clip = MakeClip(isReversed: true, speed: 2.0);
        Assert.Equal(18, clip.GetSourceTimeSeconds(2));
        Assert.Equal(14, clip.GetSourceTimeSeconds(4));
        Assert.Equal(10, clip.GetSourceTimeSeconds(6));
    }

    [Fact]
    public void GetSourceTimeSeconds_ReversedSpeedHalf_MapsBackwardHalf()
    {
        // 逆再生＋0.5倍速：使用範囲は [10, 12]。先頭では 12、末尾では 10。
        var clip = MakeClip(isReversed: true, speed: 0.5);
        Assert.Equal(12, clip.GetSourceTimeSeconds(2));
        Assert.Equal(11, clip.GetSourceTimeSeconds(4));
        Assert.Equal(10, clip.GetSourceTimeSeconds(6));
    }

    [Fact]
    public void GetSourceTimeSeconds_SpedClip_ClampsOutsideClipRange()
    {
        var clip = MakeClip(isReversed: true, speed: 2.0);
        Assert.Equal(18, clip.GetSourceTimeSeconds(0));
        Assert.Equal(10, clip.GetSourceTimeSeconds(99));
    }

    [Fact]
    public void GetTimelineTimeSeconds_IsInverseOfGetSourceTimeSeconds()
    {
        // 順／逆 × 速度の全組み合わせで、素材→タイムライン→素材が往復すること。
        foreach (var reversed in new[] { false, true })
        foreach (var speed in new[] { 0.25, 0.5, 1.0, 2.0, 4.0 })
        {
            var clip = MakeClip(isReversed: reversed, speed: speed);
            for (var t = clip.StartSeconds; t <= clip.EndSeconds; t += 0.5)
            {
                var source = clip.GetSourceTimeSeconds(t);
                Assert.Equal(t, clip.GetTimelineTimeSeconds(source), precision: 9);
            }
        }
    }

    [Fact]
    public void SourceDurationSeconds_IsDurationTimesSpeed()
    {
        Assert.Equal(8, MakeClip(speed: 2.0).SourceDurationSeconds);
        Assert.Equal(2, MakeClip(speed: 0.5).SourceDurationSeconds);
        Assert.Equal(4, MakeClip().SourceDurationSeconds);
    }

    // ---- 速度の範囲 ----

    [Fact]
    public void Speed_DefaultIsOne()
    {
        Assert.Equal(1.0, new Clip().Speed);
    }

    [Fact]
    public void Speed_SetterClampsToRange()
    {
        var clip = new Clip();
        clip.Speed = 100;
        Assert.Equal(Clip.MaxSpeed, clip.Speed);
        clip.Speed = 0;
        Assert.Equal(Clip.MinSpeed, clip.Speed);
        clip.Speed = double.NaN;
        Assert.Equal(1.0, clip.Speed);
    }

    [Fact]
    public void Clone_CopiesSpeed()
    {
        Assert.Equal(0.5, MakeClip(speed: 0.5).Clone().Speed);
        Assert.Equal(1.0, MakeClip().Clone().Speed);
    }

    // ---- JSON 保存・読込 ----

    [Fact]
    public void Json_RoundTrip_PreservesSpeed()
    {
        var project = new EditorProject();
        var track = project.AddTrack(TrackKind.Video, "V1");
        track.Clips.Add(MakeClip(speed: 0.5));

        var restored = ProjectStore.Deserialize(ProjectStore.Serialize(project));
        Assert.Equal(0.5, restored.Tracks[0].Clips[0].Speed);
    }

    [Fact]
    public void Json_OldProjectWithoutSpeed_OpensAtNormalSpeed()
    {
        // 旧プロジェクト（Speed の項目が無い JSON）は等速（1.0倍）として開ける。
        var json = """
            {"Version":1,"Name":"old","Width":1920,"Height":1080,"FrameRate":30,
             "Tracks":[{"Id":"t1","Name":"V1","Kind":0,"IsVisible":true,"IsMuted":false,
             "Clips":[{"Id":"c1","Name":"clip","Kind":0,"SourcePath":"a.mp4",
             "StartSeconds":0,"DurationSeconds":5,"InPointSeconds":0}]}]}
            """;
        var restored = ProjectStore.Deserialize(json);
        Assert.Equal(1.0, restored.Tracks[0].Clips[0].Speed);
    }

    [Fact]
    public void Json_OutOfRangeSpeed_IsClampedOnLoad()
    {
        // 手編集などで範囲外の値が保存されていても、安全な範囲で開ける。
        var json = """
            {"Version":1,"Name":"bad","Width":1920,"Height":1080,"FrameRate":30,
             "Tracks":[{"Id":"t1","Name":"V1","Kind":0,"IsVisible":true,"IsMuted":false,
             "Clips":[{"Id":"c1","Name":"clip","Kind":0,"SourcePath":"a.mp4",
             "StartSeconds":0,"DurationSeconds":5,"InPointSeconds":0,"Speed":99}]}]}
            """;
        var restored = ProjectStore.Deserialize(json);
        Assert.Equal(Clip.MaxSpeed, restored.Tracks[0].Clips[0].Speed);
    }

    // ---- 速度変更（長さの扱い） ----

    [Fact]
    public void ChangeSpeed_DoublingSpeed_HalvesDurationAndKeepsSourceRange()
    {
        var clip = MakeClip();
        TimelineEditor.ChangeSpeed(clip, 2.0);
        Assert.Equal(2.0, clip.Speed);
        Assert.Equal(2, clip.DurationSeconds); // タイムライン上の長さは半分。
        Assert.Equal(4, clip.SourceDurationSeconds); // 素材の使用範囲は不変。
        Assert.Equal(10, clip.InPointSeconds);
        Assert.Equal(2, clip.StartSeconds);
    }

    [Fact]
    public void ChangeSpeed_HalvingSpeed_DoublesDuration()
    {
        var clip = MakeClip();
        TimelineEditor.ChangeSpeed(clip, 0.5);
        Assert.Equal(0.5, clip.Speed);
        Assert.Equal(8, clip.DurationSeconds);
        Assert.Equal(4, clip.SourceDurationSeconds);
    }

    [Fact]
    public void ChangeSpeed_BackToOne_RestoresDuration()
    {
        var clip = MakeClip();
        TimelineEditor.ChangeSpeed(clip, 2.0);
        TimelineEditor.ChangeSpeed(clip, 0.25);
        TimelineEditor.ChangeSpeed(clip, 1.0);
        Assert.Equal(1.0, clip.Speed);
        Assert.Equal(4, clip.DurationSeconds);
    }

    [Fact]
    public void ChangeSpeed_ClampsToRange()
    {
        var clip = MakeClip();
        TimelineEditor.ChangeSpeed(clip, 100);
        Assert.Equal(Clip.MaxSpeed, clip.Speed);
        Assert.Equal(1, clip.DurationSeconds); // 使用範囲 4 秒 ÷ 4倍 = 1 秒。
    }

    // ---- 分割・トリミングとの整合 ----

    [Fact]
    public void SplitClip_ForwardSpeed2_SecondHalfStartsAtScaledInPoint()
    {
        // 使用範囲 [10, 18]（2倍速・タイムライン 4 秒）を t=4 で分割する。
        // 前半は素材 [10, 14]、後半は素材 [14, 18]。
        var track = new Track { Kind = TrackKind.Video };
        var clip = MakeClip(speed: 2.0);
        track.Clips.Add(clip);

        var second = TimelineEditor.SplitClip(track, clip, 4);
        Assert.NotNull(second);
        Assert.Equal(2, clip.DurationSeconds);
        Assert.Equal(10, clip.InPointSeconds);
        Assert.Equal(4, second!.StartSeconds);
        Assert.Equal(2, second.DurationSeconds);
        Assert.Equal(14, second.InPointSeconds);
        Assert.Equal(2.0, second.Speed);
        // 分割後も両クリップの再生がつながる。
        Assert.Equal(14, clip.GetSourceTimeSeconds(4));
        Assert.Equal(14, second.GetSourceTimeSeconds(4));
    }

    [Fact]
    public void SplitClip_ReversedSpeed2_KeepsContinuity()
    {
        // 使用範囲 [10, 18] を逆再生＋2倍速で使うクリップを t=4 で分割する。
        // 前半（t=2〜4）は素材の後ろ半分 [14, 18]、後半（t=4〜6）は素材の前半分 [10, 14]。
        var track = new Track { Kind = TrackKind.Video };
        var clip = MakeClip(isReversed: true, speed: 2.0);
        track.Clips.Add(clip);

        var second = TimelineEditor.SplitClip(track, clip, 4);
        Assert.NotNull(second);
        Assert.Equal(2, clip.DurationSeconds);
        Assert.Equal(14, clip.InPointSeconds);
        Assert.Equal(2, second!.DurationSeconds);
        Assert.Equal(10, second.InPointSeconds);
        Assert.True(second.IsReversed);
        Assert.Equal(2.0, second.Speed);
        Assert.Equal(14, clip.GetSourceTimeSeconds(4));
        Assert.Equal(14, second.GetSourceTimeSeconds(4));
    }

    [Fact]
    public void TrimClipStart_ForwardSpeed2_MovesInPointByScaledDelta()
    {
        // 2倍速で先頭を 1 秒（タイムライン）削ると、素材は 2 秒分進む。
        var clip = MakeClip(speed: 2.0);
        TimelineEditor.TrimClipStart(clip, 3);
        Assert.Equal(3, clip.StartSeconds);
        Assert.Equal(3, clip.DurationSeconds);
        Assert.Equal(12, clip.InPointSeconds);
        Assert.Equal(12, clip.GetSourceTimeSeconds(3));
    }

    [Fact]
    public void TrimClipStart_ReversedSpeed2_KeepsInPoint()
    {
        // 逆再生で先頭を削ると素材の末尾側が削れる（イン点は変わらない）。
        var clip = MakeClip(isReversed: true, speed: 2.0);
        TimelineEditor.TrimClipStart(clip, 3);
        Assert.Equal(3, clip.DurationSeconds);
        Assert.Equal(10, clip.InPointSeconds);
        // 使用範囲は [10, 16] になり、先頭は素材 16 から始まる。
        Assert.Equal(16, clip.GetSourceTimeSeconds(3));
    }

    [Fact]
    public void TrimClipEnd_ReversedSpeed2_MovesInPointByScaledDelta()
    {
        // 逆再生＋2倍速で末尾を 1 秒（タイムライン）削ると、素材の先頭側が 2 秒分削れる。
        var clip = MakeClip(isReversed: true, speed: 2.0);
        TimelineEditor.TrimClipEnd(clip, 5);
        Assert.Equal(3, clip.DurationSeconds);
        Assert.Equal(12, clip.InPointSeconds);
        Assert.Equal(18, clip.GetSourceTimeSeconds(2));
        Assert.Equal(12, clip.GetSourceTimeSeconds(5));
    }

    [Fact]
    public void TrimClipEnd_ForwardSpeed2_KeepsInPoint()
    {
        var clip = MakeClip(speed: 2.0);
        TimelineEditor.TrimClipEnd(clip, 5);
        Assert.Equal(3, clip.DurationSeconds);
        Assert.Equal(10, clip.InPointSeconds);
        Assert.Equal(16, clip.GetSourceTimeSeconds(5));
    }

    // ---- 書き出し用フィルタ（タイムライン書き出し） ----

    [Fact]
    public void BuildClipFilter_Speed2_AddsAtempoAfterAtrim()
    {
        var clip = MakeClip(speed: 2.0);
        var filter = AudioExportPlanner.BuildClipFilter(1, clip, 10, 18, 0, "a1");
        Assert.Equal("[1:a]atrim=start=10.000:end=18.000,atempo=2.000,asetpts=PTS-STARTPTS,adelay=0:all=1[a1]", filter);
    }

    [Fact]
    public void BuildClipFilter_SpeedHalf_AddsAtempoHalf()
    {
        var clip = MakeClip(speed: 0.5);
        var filter = AudioExportPlanner.BuildClipFilter(1, clip, 10, 12, 0, "a1");
        Assert.Contains("atempo=0.500,", filter);
    }

    [Fact]
    public void BuildClipFilter_ReversedSpeed2_AreverseBeforeAtempo()
    {
        var clip = MakeClip(isReversed: true, speed: 2.0);
        var filter = AudioExportPlanner.BuildClipFilter(1, clip, 10, 18, 500, "a1");
        Assert.Equal("[1:a]atrim=start=10.000:end=18.000,areverse,atempo=2.000,asetpts=PTS-STARTPTS,adelay=500:all=1[a1]", filter);
    }

    [Fact]
    public void BuildClipFilter_NormalSpeed_HasNoAtempo()
    {
        var clip = MakeClip();
        var filter = AudioExportPlanner.BuildClipFilter(1, clip, 10, 14, 0, "a1");
        Assert.DoesNotContain("atempo", filter);
    }

    [Theory]
    [InlineData(1.0, "")]
    [InlineData(2.0, "atempo=2.000,")]
    [InlineData(3.0, "atempo=2.000,atempo=1.500,")]
    [InlineData(4.0, "atempo=2.000,atempo=2.000,")]
    [InlineData(0.5, "atempo=0.500,")]
    [InlineData(0.25, "atempo=0.500,atempo=0.500,")]
    [InlineData(0.1, "atempo=0.500,atempo=0.500,atempo=0.500,atempo=0.800,")]
    public void AtempoChain_DecomposesIntoFilterRange(double speed, string expected)
    {
        Assert.Equal(expected, AudioExportPlanner.AtempoChain(speed));
    }

    /// <summary>固定の MediaInfo を返すテスト用プローブ。</summary>
    private sealed class FakeProbe : IMediaProbe
    {
        public Task<MediaInfo> ProbeAsync(string path, CancellationToken ct = default)
            => Task.FromResult(new MediaInfo(path, TimeSpan.FromSeconds(60), 0, 0, 0, false, true, null, null));
    }

    [Fact]
    public async Task BuildAudioPlanAsync_Speed2_TrimsSourceRangeByMapping()
    {
        // クリップ全体（タイムライン 2〜6 秒、2倍速）の書き出し。
        // 素材範囲は [10, 18]、遅延は書き出し開始（0秒）から 2 秒。
        var project = new EditorProject();
        var track = project.AddTrack(TrackKind.Video, "V1");
        track.Clips.Add(MakeClip(speed: 2.0));
        var planner = new AudioExportPlanner(new FakeProbe());

        var (inputs, _) = await planner.BuildAudioPlanAsync(project, 0, 10, CancellationToken.None);

        Assert.Single(inputs);
        Assert.Equal(
            "[1:a]atrim=start=10.000:end=18.000,atempo=2.000,asetpts=PTS-STARTPTS,adelay=2000:all=1[a1]",
            inputs[0].Filter);
    }

    [Fact]
    public async Task BuildAudioPlanAsync_ReversedSpeed2_PartialOverlap_TrimsMappedRange()
    {
        // 書き出し範囲 3〜5 秒は、逆再生＋2倍速では素材 [12, 16] に対応する
        // （t=3 → 素材 16、t=5 → 素材 12。逆向きなので小さい方を開始にする）。
        var project = new EditorProject();
        var track = project.AddTrack(TrackKind.Video, "V1");
        track.Clips.Add(MakeClip(isReversed: true, speed: 2.0));
        var planner = new AudioExportPlanner(new FakeProbe());

        var (inputs, _) = await planner.BuildAudioPlanAsync(project, 3, 5, CancellationToken.None);

        Assert.Single(inputs);
        Assert.Equal(
            "[1:a]atrim=start=12.000:end=16.000,areverse,atempo=2.000,asetpts=PTS-STARTPTS,adelay=0:all=1[a1]",
            inputs[0].Filter);
    }

    // ---- 書き出し用フィルタ（単一クリップ書き出し） ----

    [Fact]
    public void SingleClipFilterArguments_None_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ExportService.BuildFilterArguments(reverse: false, speed: 1.0));
    }

    [Fact]
    public void SingleClipFilterArguments_SpeedOnly_UsesSetptsAndAtempo()
    {
        Assert.Equal(" -vf setpts=PTS/2.000 -af atempo=2.000",
            ExportService.BuildFilterArguments(reverse: false, speed: 2.0));
        Assert.Equal(" -vf setpts=PTS/0.250 -af atempo=0.500,atempo=0.500",
            ExportService.BuildFilterArguments(reverse: false, speed: 0.25));
    }

    [Fact]
    public void SingleClipFilterArguments_ReverseAndSpeed_ReverseFirst()
    {
        Assert.Equal(" -vf reverse,setpts=PTS/2.000 -af areverse,atempo=2.000",
            ExportService.BuildFilterArguments(reverse: true, speed: 2.0));
        Assert.Equal(" -vf reverse -af areverse",
            ExportService.BuildFilterArguments(reverse: true, speed: 1.0));
    }

    // ---- プレビュー描画（フレーム要求の時刻） ----

    /// <summary>要求された素材時刻を記録し、単色 PNG を返すテスト用フレーム取得。</summary>
    private sealed class RecordingFrameSource : IVideoFrameSource
    {
        public List<double> RequestedTimes { get; } = new();

        public Task<byte[]?> GetFramePngAsync(
            string sourcePath, double timeSeconds, int maxWidth, CancellationToken ct = default)
        {
            RequestedTimes.Add(timeSeconds);
            using var bitmap = new SKBitmap(4, 4, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(bitmap))
                canvas.Clear(SKColors.Red);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return Task.FromResult<byte[]?>(data.ToArray());
        }
    }

    [Fact]
    public async Task RenderFrame_ForwardSpeed2_RequestsFramesAtDoubleRate()
    {
        var source = new RecordingFrameSource();
        var renderer = new TimelineFrameRenderer(source);
        var project = new EditorProject();
        var track = project.AddTrack(TrackKind.Video, "V1");
        track.Clips.Add(MakeClip(speed: 2.0));

        using var frame1 = await renderer.RenderFrameAsync(project, 2, 64, 36);
        using var frame2 = await renderer.RenderFrameAsync(project, 3, 64, 36);
        using var frame3 = await renderer.RenderFrameAsync(project, 5, 64, 36);

        Assert.Equal(new[] { 10.0, 12.0, 16.0 }, source.RequestedTimes);
    }

    [Fact]
    public async Task RenderFrame_ReversedSpeedHalf_RequestsFramesBackwardSlowly()
    {
        var source = new RecordingFrameSource();
        var renderer = new TimelineFrameRenderer(source);
        var project = new EditorProject();
        var track = project.AddTrack(TrackKind.Video, "V1");
        track.Clips.Add(MakeClip(isReversed: true, speed: 0.5));

        using var frame1 = await renderer.RenderFrameAsync(project, 2, 64, 36);
        using var frame2 = await renderer.RenderFrameAsync(project, 4, 64, 36);

        // 使用範囲 [10, 12] を逆向きにゆっくり進む。
        Assert.Equal(new[] { 12.0, 11.0 }, source.RequestedTimes);
    }

    // ---- 音声バインドの素材時刻 ----

    /// <summary>固定の帯域エネルギーを返し、呼び出し引数を記録するテスト用スタブ。</summary>
    private sealed class FixedEnergyProvider : IAudioBandEnergyProvider
    {
        public List<(string ClipId, double TimeSeconds)> Calls { get; } = new();

        public void Precompute(string clipId, string sourcePath, double frameRate)
        {
        }

        public double GetBandEnergy(string clipId, double timeSeconds, double lowHz, double highHz)
        {
            Calls.Add((clipId, timeSeconds));
            return 0.5;
        }
    }

    [Fact]
    public void AudioBinding_SpedSource_UsesSpedSourceTime()
    {
        var project = new EditorProject();
        var track = project.AddTrack(TrackKind.Video, "V1");
        var audio = new Clip
        {
            Id = "audio1",
            Kind = ClipKind.Audio,
            SourcePath = "music.mp3",
            StartSeconds = 2,
            InPointSeconds = 10,
            DurationSeconds = 4,
            Speed = 2.0
        };
        var target = MakeClip();
        target.AudioBindings.Add(new AudioBinding
        {
            ParameterName = "Scale",
            SourceClipId = "audio1",
            LowHz = 20,
            HighHz = 80
        });
        track.Clips.Add(audio);
        track.Clips.Add(target);

        var provider = new FixedEnergyProvider();
        TimelineEvaluator.EvaluateProject(project, 3, provider);

        // タイムライン 3 秒のとき、2倍速の音源は素材 12 秒の位置を流している。
        Assert.Contains(("audio1", 12.0), provider.Calls);
    }

    // ---- プレビュー音声（リサンプリング） ----

    /// <summary>固定サンプルを返すテスト用サンプルプロバイダ（モノラル）。</summary>
    private sealed class StubSampleProvider : ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;

        public StubSampleProvider(float[] samples)
            => _samples = samples;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(8000, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            var n = Math.Min(count, _samples.Length - _position);
            if (n <= 0)
                return 0;
            Array.Copy(_samples, _position, buffer, offset, n);
            _position += n;
            return n;
        }
    }

    private static float[] ReadAll(ISampleProvider provider, int chunk = 64)
    {
        var result = new List<float>();
        var buffer = new float[chunk];
        int n;
        while ((n = provider.Read(buffer, 0, buffer.Length)) > 0)
            result.AddRange(buffer.AsSpan(0, n).ToArray());
        return result.ToArray();
    }

    [Fact]
    public void TempoSampleProvider_Speed2_SkipsEveryOtherFrame()
    {
        var input = Enumerable.Range(0, 10).Select(i => (float)i).ToArray();
        var provider = new TempoSampleProvider(new StubSampleProvider(input), 2.0);
        Assert.Equal(new[] { 0f, 2f, 4f, 6f, 8f }, ReadAll(provider));
    }

    [Fact]
    public void TempoSampleProvider_SpeedHalf_Interpolates()
    {
        var input = Enumerable.Range(0, 10).Select(i => (float)i).ToArray();
        var provider = new TempoSampleProvider(new StubSampleProvider(input), 0.5);
        var output = ReadAll(provider);
        // 位置 0, 0.5, 1, 1.5, … と線形補間され、末尾（9）で止まる。
        Assert.Equal(20, output.Length);
        Assert.Equal(new[] { 0f, 0.5f, 1f, 1.5f, 2f }, output.Take(5).ToArray());
        Assert.Equal(9f, output[^1]);
    }

    [Fact]
    public void TempoSampleProvider_AppliesGain()
    {
        var input = new[] { 1f, 2f, 3f };
        var provider = new TempoSampleProvider(new StubSampleProvider(input), 1.0, gain: 2.0);
        Assert.Equal(new[] { 2f, 4f, 6f }, ReadAll(provider));
    }

    // ---- プレビュー音声（逆再生＋速度） ----

    [Fact]
    public void ReversedAudioStream_Step2_ReadsEveryOtherFrameBackward()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(8000, 1);
        var samples = Enumerable.Range(0, 10).Select(i => (float)i).ToArray();
        using var stream = new ReversedAudioStream(format, samples, startFrame: 7, step: 2.0);

        Assert.Equal(4 * 4, stream.Length); // 出力 4 フレーム。
        var values = new List<float>();
        var chunk = new byte[4];
        int n;
        while ((n = stream.Read(chunk, 0, chunk.Length)) > 0)
            values.Add(BitConverter.ToSingle(chunk, 0));
        Assert.Equal(new[] { 7f, 5f, 3f, 1f }, values);
    }

    [Fact]
    public void ReversedAudioStream_StepHalf_RepeatsFramesBackward()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(8000, 1);
        var samples = Enumerable.Range(0, 10).Select(i => (float)i).ToArray();
        using var stream = new ReversedAudioStream(format, samples, startFrame: 3, step: 0.5);

        // 素材位置 3, 2.5, 2, 1.5, 1, 0.5, 0 を点サンプリングする（floor でフレームを取る）。
        Assert.Equal(7 * 4, stream.Length); // 出力 7 フレーム。
        var values = new List<float>();
        var chunk = new byte[4];
        int n;
        while ((n = stream.Read(chunk, 0, chunk.Length)) > 0)
            values.Add(BitConverter.ToSingle(chunk, 0));
        Assert.Equal(new[] { 3f, 2f, 2f, 1f, 1f, 0f, 0f }, values);
    }

    /// <summary>PCM16 モノラル WAV を書き出す（NAudio の AudioFileReader で読める形式）。</summary>
    private static void WritePcm16Wav(string path, int sampleRate, short[] samples)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        var dataBytes = samples.Length * 2;
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)1); // モノラル
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8.ToArray());
        writer.Write(dataBytes);
        foreach (var sample in samples)
            writer.Write(sample);
    }

    [Fact]
    public void CreateSpeededReader_Speed2_StartsAtPlayheadAndSkips()
    {
        using var temp = new TempDirectory();
        var path = temp.File("ramp.wav");
        WritePcm16Wav(path, 8000, Enumerable.Range(0, 8000).Select(i => (short)i).ToArray());

        var source = new AudioPreviewSource(path, 0, Speed: 2.0);
        var (provider, reader) = AudioPreviewPlayer.CreateSpeededReader(source);
        using var _ = reader;

        // 2倍速：出力フレーム k は素材フレーム 2k（サンプル値 = フレーム番号）。
        var buffer = new byte[4 * 4];
        var read = provider.Read(buffer, 0, buffer.Length);
        Assert.Equal(16, read);
        var values = Enumerable.Range(0, 4)
            .Select(i => BitConverter.ToSingle(buffer, i * 4)).ToArray();
        Assert.Equal(0f, values[0], precision: 4);
        Assert.Equal(2 / 32768f, values[1], precision: 4);
        Assert.Equal(4 / 32768f, values[2], precision: 4);
        Assert.Equal(6 / 32768f, values[3], precision: 4);
    }

    [Fact]
    public void CreateReversedReader_Speed2_StepsBackwardByTwo()
    {
        using var temp = new TempDirectory();
        var path = temp.File("ramp.wav");
        WritePcm16Wav(path, 8000, Enumerable.Range(0, 8000).Select(i => (short)i).ToArray());

        // 使用区間 [0.25, 0.75)、再生ヘッド位置の素材時刻 0.6 秒、逆再生＋2倍速。
        var source = new AudioPreviewSource(
            path, 0.6, IsReversed: true, SourceStartSeconds: 0.25, SourceEndSeconds: 0.75, Speed: 2.0);
        using var reader = AudioPreviewPlayer.CreateReversedReader(source);
        Assert.NotNull(reader);

        // 素材 0.6 秒（サンプル 4800）から、2フレームずつ逆向きに進む。
        var buffer = new byte[4 * 4];
        var read = reader!.Read(buffer, 0, buffer.Length);
        Assert.Equal(16, read);
        var values = Enumerable.Range(0, 4)
            .Select(i => BitConverter.ToSingle(buffer, i * 4)).ToArray();
        Assert.Equal(4800 / 32768f, values[0], precision: 4);
        Assert.Equal(4798 / 32768f, values[1], precision: 4);
        Assert.Equal(4796 / 32768f, values[2], precision: 4);
        Assert.Equal(4794 / 32768f, values[3], precision: 4);
    }
}
