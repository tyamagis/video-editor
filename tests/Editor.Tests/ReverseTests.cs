// クリップの逆再生のテスト：素材時刻の写像・分割・トリミング・JSON・
// 書き出し用フィルタ・プレビュー描画のフレーム要求・音声バインドの
// 素材時刻・逆向き音声ストリームとデコード。

using Editor.Audio;
using Editor.Core;
using Editor.Render;
using NAudio.Wave;
using SkiaSharp;
using Xunit;

namespace Editor.Tests;

/// <summary>逆再生（Clip.IsReversed）のテスト。</summary>
public sealed class ReverseTests
{
    private static Clip MakeClip(
        double startSeconds = 2, double durationSeconds = 4, double inPointSeconds = 10,
        bool isReversed = false)
        => new()
        {
            Kind = ClipKind.Video,
            SourcePath = "dummy.mp4",
            StartSeconds = startSeconds,
            DurationSeconds = durationSeconds,
            InPointSeconds = inPointSeconds,
            IsReversed = isReversed
        };

    // ---- 素材時刻の写像 ----

    [Fact]
    public void GetSourceTimeSeconds_Forward_MapsElapsedForward()
    {
        var clip = MakeClip();
        Assert.Equal(10, clip.GetSourceTimeSeconds(2));
        Assert.Equal(12, clip.GetSourceTimeSeconds(4));
        Assert.Equal(14, clip.GetSourceTimeSeconds(6));
    }

    [Fact]
    public void GetSourceTimeSeconds_Reversed_MapsElapsedBackward()
    {
        var clip = MakeClip(isReversed: true);
        // クリップ先頭では素材の末尾、末尾では素材の先頭になる。
        Assert.Equal(14, clip.GetSourceTimeSeconds(2));
        Assert.Equal(12, clip.GetSourceTimeSeconds(4));
        Assert.Equal(10, clip.GetSourceTimeSeconds(6));
    }

    [Fact]
    public void GetSourceTimeSeconds_Reversed_ClampsOutsideClipRange()
    {
        var clip = MakeClip(isReversed: true);
        // トランジション区間など範囲外の時刻でも範囲内の素材時刻を返す。
        Assert.Equal(14, clip.GetSourceTimeSeconds(1));
        Assert.Equal(10, clip.GetSourceTimeSeconds(7));
    }

    [Fact]
    public void IsReversed_DefaultIsFalse()
    {
        Assert.False(new Clip().IsReversed);
    }

    [Fact]
    public void Clone_CopiesIsReversed()
    {
        var clip = MakeClip(isReversed: true);
        Assert.True(clip.Clone().IsReversed);
        Assert.False(MakeClip().Clone().IsReversed);
    }

    // ---- JSON 保存・読込 ----

    [Fact]
    public void Json_RoundTrip_PreservesIsReversed()
    {
        var project = new EditorProject();
        var track = project.AddTrack(TrackKind.Video, "V1");
        track.Clips.Add(MakeClip(isReversed: true));

        var restored = ProjectStore.Deserialize(ProjectStore.Serialize(project));
        Assert.True(restored.Tracks[0].Clips[0].IsReversed);
    }

    [Fact]
    public void Json_OldProjectWithoutFlag_OpensAsForward()
    {
        // 旧プロジェクト（IsReversed の項目が無い JSON）は通常再生として開ける。
        var json = """
            {"Version":1,"Name":"old","Width":1920,"Height":1080,"FrameRate":30,
             "Tracks":[{"Id":"t1","Name":"V1","Kind":0,"IsVisible":true,"IsMuted":false,
             "Clips":[{"Id":"c1","Name":"clip","Kind":0,"SourcePath":"a.mp4",
             "StartSeconds":0,"DurationSeconds":5,"InPointSeconds":0}]}]}
            """;
        var restored = ProjectStore.Deserialize(json);
        Assert.False(restored.Tracks[0].Clips[0].IsReversed);
    }

    // ---- 分割・トリミング ----

    [Fact]
    public void SplitClip_Reversed_SecondHalfKeepsInPoint()
    {
        // 素材範囲 [10, 14] を逆再生で使うクリップを真ん中（t=4）で分割する。
        // 前半（t=2〜4）は素材の後ろ半分 [12, 14]、後半（t=4〜6）は素材の前半分 [10, 12]。
        var track = new Track { Kind = TrackKind.Video };
        var clip = MakeClip(isReversed: true);
        track.Clips.Add(clip);

        var second = TimelineEditor.SplitClip(track, clip, 4);
        Assert.NotNull(second);
        Assert.Equal(2, clip.DurationSeconds);
        Assert.Equal(12, clip.InPointSeconds);
        Assert.Equal(4, second!.StartSeconds);
        Assert.Equal(2, second.DurationSeconds);
        Assert.Equal(10, second.InPointSeconds);
        Assert.True(second.IsReversed);
        // 分割後も両クリップの再生がつながる（前半の末尾＝素材 12、後半の先頭＝素材 12）。
        Assert.Equal(12, clip.GetSourceTimeSeconds(4));
        Assert.Equal(12, second.GetSourceTimeSeconds(4));
    }

    [Fact]
    public void SplitClip_Forward_Unchanged()
    {
        var track = new Track { Kind = TrackKind.Video };
        var clip = MakeClip();
        track.Clips.Add(clip);

        var second = TimelineEditor.SplitClip(track, clip, 4);
        Assert.NotNull(second);
        Assert.Equal(10, clip.InPointSeconds);
        Assert.Equal(2, clip.DurationSeconds);
        Assert.Equal(12, second!.InPointSeconds);
        Assert.Equal(2, second.DurationSeconds);
    }

    [Fact]
    public void TrimClipStart_Reversed_KeepsInPoint()
    {
        // 逆再生で先頭を 1 秒削ると、素材の末尾側が削れる（イン点は変わらない）。
        var clip = MakeClip(isReversed: true);
        TimelineEditor.TrimClipStart(clip, 3);
        Assert.Equal(3, clip.StartSeconds);
        Assert.Equal(3, clip.DurationSeconds);
        Assert.Equal(10, clip.InPointSeconds);
        Assert.Equal(13, clip.GetSourceTimeSeconds(3));
    }

    [Fact]
    public void TrimClipEnd_Reversed_MovesInPoint()
    {
        // 逆再生で末尾を 1 秒削ると、素材の先頭側が削れる（イン点が後ろへずれる）。
        var clip = MakeClip(isReversed: true);
        TimelineEditor.TrimClipEnd(clip, 5);
        Assert.Equal(3, clip.DurationSeconds);
        Assert.Equal(11, clip.InPointSeconds);
        Assert.Equal(14, clip.GetSourceTimeSeconds(2));
        Assert.Equal(11, clip.GetSourceTimeSeconds(5));
    }

    [Fact]
    public void TrimClipStart_Forward_Unchanged()
    {
        var clip = MakeClip();
        TimelineEditor.TrimClipStart(clip, 3);
        Assert.Equal(11, clip.InPointSeconds);
        Assert.Equal(3, clip.DurationSeconds);
    }

    // ---- 書き出し用フィルタ ----

    [Fact]
    public void BuildClipFilter_Reversed_ReversesTrimmedSegment()
    {
        var clip = MakeClip(isReversed: true);
        var filter = AudioExportPlanner.BuildClipFilter(1, clip, 10, 14, 500, "a1");
        Assert.Equal("[1:a]atrim=start=10.000:end=14.000,areverse,asetpts=PTS-STARTPTS,adelay=500:all=1[a1]", filter);
    }

    [Fact]
    public void BuildClipFilter_Forward_HasNoAreverse()
    {
        var clip = MakeClip();
        var filter = AudioExportPlanner.BuildClipFilter(1, clip, 10, 14, 0, "a1");
        Assert.Equal("[1:a]atrim=start=10.000:end=14.000,asetpts=PTS-STARTPTS,adelay=0:all=1[a1]", filter);
    }

    [Fact]
    public void BuildClipFilter_ReversedWithDenoise_KeepsOrder()
    {
        var clip = MakeClip(isReversed: true);
        clip.NoiseReductionEnabled = true;
        clip.NoiseReductionStrength = 0.5;
        var filter = AudioExportPlanner.BuildClipFilter(2, clip, 0, 5, 0, "a2");
        Assert.StartsWith("[2:a]afftdn=nr=15.5:nf=-50,atrim=start=0.000:end=5.000,areverse,", filter);
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
    public async Task RenderFrame_Reversed_RequestsFramesBackward()
    {
        var source = new RecordingFrameSource();
        var renderer = new TimelineFrameRenderer(source);
        var project = new EditorProject();
        var track = project.AddTrack(TrackKind.Video, "V1");
        track.Clips.Add(MakeClip(isReversed: true));

        using var frame1 = await renderer.RenderFrameAsync(project, 2, 64, 36);
        using var frame2 = await renderer.RenderFrameAsync(project, 4, 64, 36);
        using var frame3 = await renderer.RenderFrameAsync(project, 5.9, 64, 36);

        Assert.Equal(new[] { 14.0, 12.0, 10.1 }, source.RequestedTimes.Select(t => Math.Round(t, 3)));
        Assert.Equal(3, source.RequestedTimes.Count);
    }

    [Fact]
    public async Task RenderFrame_Forward_RequestsFramesForward()
    {
        var source = new RecordingFrameSource();
        var renderer = new TimelineFrameRenderer(source);
        var project = new EditorProject();
        var track = project.AddTrack(TrackKind.Video, "V1");
        track.Clips.Add(MakeClip());

        using var frame1 = await renderer.RenderFrameAsync(project, 2, 64, 36);
        using var frame2 = await renderer.RenderFrameAsync(project, 4, 64, 36);

        Assert.Equal(new[] { 10.0, 12.0 }, source.RequestedTimes);
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
    public void AudioBinding_ReversedSource_UsesReversedSourceTime()
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
            IsReversed = true
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

        // タイムライン 3 秒のとき、逆再生の音源は素材 13 秒の位置を流している。
        Assert.Contains(("audio1", 13.0), provider.Calls);
    }

    // ---- 逆向き音声ストリーム ----

    [Fact]
    public void ReversedAudioStream_ReadsSamplesBackward()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(8000, 1);
        var samples = Enumerable.Range(0, 10).Select(i => (float)i).ToArray();
        using var stream = new ReversedAudioStream(format, samples, startFrame: 7);

        var buffer = new byte[4 * 4];
        var read = stream.Read(buffer, 0, buffer.Length);
        Assert.Equal(16, read);
        var values = Enumerable.Range(0, 4)
            .Select(i => BitConverter.ToSingle(buffer, i * 4)).ToArray();
        Assert.Equal(new[] { 7f, 6f, 5f, 4f }, values);

        // 残りを読み切ると 0, 1, 2, 3 の順で出て、最後は 0 バイトになる。
        var rest = new List<float>();
        var chunk = new byte[4];
        int n;
        while ((n = stream.Read(chunk, 0, chunk.Length)) > 0)
            rest.Add(BitConverter.ToSingle(chunk, 0));
        Assert.Equal(new[] { 3f, 2f, 1f, 0f }, rest);
        Assert.Equal(8 * 4, stream.Length);
    }

    [Fact]
    public void ReversedAudioStream_StartFrameOutOfRange_Clamps()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(8000, 1);
        var samples = new float[] { 1f, 2f, 3f };
        using var stream = new ReversedAudioStream(format, samples, startFrame: 99);
        var buffer = new byte[4];
        Assert.Equal(4, stream.Read(buffer, 0, buffer.Length));
        Assert.Equal(3f, BitConverter.ToSingle(buffer, 0));
    }

    [Fact]
    public void ReversedAudioStream_EmptySamples_ReadsZero()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(8000, 1);
        using var stream = new ReversedAudioStream(format, Array.Empty<float>(), startFrame: 0);
        Assert.Equal(0, stream.Length);
        Assert.Equal(0, stream.Read(new byte[4], 0, 4));
    }

    // ---- 逆再生用デコード ----

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
    public void DecodeRange_ReturnsOnlyRequestedRange()
    {
        using var temp = new TempDirectory();
        var path = temp.File("ramp.wav");
        // 1秒（8000サンプル）のランプ：サンプル i の値は i。
        WritePcm16Wav(path, 8000, Enumerable.Range(0, 8000).Select(i => (short)i).ToArray());

        var (format, samples) = ReversedAudioDecoder.DecodeRange(path, 0.25, 0.75);

        Assert.Equal(8000, format.SampleRate);
        Assert.Equal(1, format.Channels);
        Assert.Equal(4000, samples.Length);
        Assert.Equal(2000 / 32768f, samples[0], precision: 4);
        Assert.Equal(5999 / 32768f, samples[^1], precision: 4);
    }

    [Fact]
    public void DecodeRange_EmptyRange_ReturnsEmpty()
    {
        using var temp = new TempDirectory();
        var path = temp.File("ramp.wav");
        WritePcm16Wav(path, 8000, Enumerable.Range(0, 8000).Select(i => (short)i).ToArray());

        var (_, samples) = ReversedAudioDecoder.DecodeRange(path, 0.5, 0.5);
        Assert.Empty(samples);
    }

    [Fact]
    public void CreateReversedReader_StartsAtPlayheadSourceTimeAndReadsBackward()
    {
        using var temp = new TempDirectory();
        var path = temp.File("ramp.wav");
        // 1秒（8000サンプル）のランプ：サンプル i の値は i。
        WritePcm16Wav(path, 8000, Enumerable.Range(0, 8000).Select(i => (short)i).ToArray());

        // クリップの使用区間 [0.25, 0.75)、再生ヘッド位置の素材時刻は 0.6 秒。
        var source = new AudioPreviewSource(path, 0.6, IsReversed: true, SourceStartSeconds: 0.25, SourceEndSeconds: 0.75);
        using var reader = AudioPreviewPlayer.CreateReversedReader(source);
        Assert.NotNull(reader);

        // 最初に読めるのは素材 0.6 秒のサンプル（4800）から、逆向きに 4799, 4798, …。
        var buffer = new byte[4 * 4];
        var read = reader!.Read(buffer, 0, buffer.Length);
        Assert.Equal(16, read);
        var values = Enumerable.Range(0, 4)
            .Select(i => BitConverter.ToSingle(buffer, i * 4)).ToArray();
        Assert.Equal(4800 / 32768f, values[0], precision: 4);
        Assert.Equal(4799 / 32768f, values[1], precision: 4);
        Assert.Equal(4798 / 32768f, values[2], precision: 4);
        Assert.Equal(4797 / 32768f, values[3], precision: 4);
    }
}
