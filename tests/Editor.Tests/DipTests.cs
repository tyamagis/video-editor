// Render→Media の DIP 分離のテスト。
// フェイクの IMediaProbe / IFfmpegConfiguration を差し込み、
// Render が Media の具象型なしで動くことと、アセンブリ参照が
// Editor.Media を含まないことを直接確認する。

using Editor.Core;
using Editor.Media;
using Editor.Render;
using SkiaSharp;
using Xunit;

namespace Editor.Tests;

/// <summary>Render→Media の依存性逆転（DIP）のテスト。</summary>
public sealed class DipTests
{
    /// <summary>パスごとに音声あり／なし／例外を返すフェイクのプローブ。</summary>
    private sealed class FakeMediaProbe : IMediaProbe
    {
        private readonly Dictionary<string, MediaInfo> _infos = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _throwing = new(StringComparer.OrdinalIgnoreCase);

        public List<string> ProbedPaths { get; } = new();

        public void Add(string path, bool hasAudio)
            => _infos[path] = new MediaInfo(path, TimeSpan.FromSeconds(1), 0, 0, 0, false, hasAudio, null, null);

        public void AddThrowing(string path) => _throwing.Add(path);

        public Task<MediaInfo> ProbeAsync(string path, CancellationToken ct = default)
        {
            ProbedPaths.Add(path);
            if (_throwing.Contains(path))
                throw new InvalidOperationException("読めない素材");
            return Task.FromResult(_infos[path]);
        }
    }

    /// <summary>ffmpeg のパスだけを返すフェイクの設定。</summary>
    private sealed class FakeFfmpegConfiguration : IFfmpegConfiguration
    {
        public string FfmpegExePath { get; init; } = "ffmpeg";
    }

    /// <summary>描画しない最小のフレームレンダラー（構築テスト用）。</summary>
    private sealed class StubFrameRenderer : IFrameRenderer
    {
        public Task<SKBitmap> RenderFrameAsync(
            EditorProject project, double timeSeconds, int width, int height,
            CancellationToken ct = default)
            => Task.FromResult(new SKBitmap(1, 1));
    }

    private static EditorProject ProjectWithClips(params (string Path, double Start)[] clips)
    {
        var project = new EditorProject();
        var track = project.AddTrack(TrackKind.Audio, "A1");
        foreach (var (path, start) in clips)
        {
            track.Clips.Add(new Clip
            {
                Kind = ClipKind.Audio,
                SourcePath = path,
                StartSeconds = start,
                DurationSeconds = 1.0
            });
        }
        return project;
    }

    [Fact]
    public async Task BuildAudioPlanAsync_FakeProbe_IncludesOnlyAudioClips()
    {
        var probe = new FakeMediaProbe();
        probe.Add("audio.wav", hasAudio: true);
        probe.Add("video.mp4", hasAudio: false);
        probe.AddThrowing("broken.wav");
        var project = ProjectWithClips(("audio.wav", 0), ("video.mp4", 0), ("broken.wav", 0));
        var planner = new AudioExportPlanner(probe);

        var (inputs, mapLabel) = await planner.BuildAudioPlanAsync(project, 0, 1.0, CancellationToken.None);

        // 音声ありだけが入力に含まれ、音声なし・読めない素材はスキップされる。
        Assert.Single(inputs);
        Assert.Equal("audio.wav", inputs[0].Path);
        Assert.Equal("a1", mapLabel);
        // 3素材すべてにプローブが走っていること（例外で止まっていない）。
        Assert.Equal(3, probe.ProbedPaths.Count);
    }

    [Fact]
    public async Task BuildAudioPlanAsync_FakeProbe_AllUnreadable_ReturnsNoAudio()
    {
        var probe = new FakeMediaProbe();
        probe.AddThrowing("broken.wav");
        var project = ProjectWithClips(("broken.wav", 0));
        var planner = new AudioExportPlanner(probe);

        var (inputs, mapLabel) = await planner.BuildAudioPlanAsync(project, 0, 1.0, CancellationToken.None);

        Assert.Empty(inputs);
        Assert.Null(mapLabel);
    }

    [Fact]
    public void FrameSequenceExporter_CanBeConstructedWithFakes()
    {
        ITimelineExporter exporter = new FrameSequenceExporter(
            new StubFrameRenderer(), new FakeFfmpegConfiguration(), new FakeMediaProbe());

        Assert.NotNull(exporter);
    }

    [Fact]
    public void RenderAssembly_DoesNotReferenceMedia()
    {
        var referenced = typeof(FrameSequenceExporter).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToList();

        // 分離の直接の証明：Render のアセンブリ参照に Editor.Media が無い。
        Assert.DoesNotContain("Editor.Media", referenced);
        // 抽象は Core にあるので Core への参照は残る。
        Assert.Contains("Editor.Core", referenced);
    }

    [Fact]
    public void ConcreteTypes_ImplementAbstractions()
    {
        // App.xaml.cs の配線（具象型をそのまま渡す）が通ることの担保。
        IMediaProbe probe = new MediaProbe(new FfmpegSettings());
        IFfmpegConfiguration config = new FfmpegSettings();

        Assert.NotNull(probe);
        Assert.Equal("ffmpeg", config.FfmpegExePath);
    }

    [Fact]
    public void ProcessStartInfoFactory_CreateStartInfo_MatchesPreviousBehavior()
    {
        var fromCore = ProcessStartInfoFactory.CreateStartInfo(
            "ffmpeg", "-version", redirectStandardInput: true);
        var fromMediaWrapper = FfmpegProcess.CreateStartInfo(
            "ffmpeg", "-version", redirectStandardInput: true);

        foreach (var info in new[] { fromCore, fromMediaWrapper })
        {
            Assert.Equal("ffmpeg", info.FileName);
            Assert.Equal("-version", info.Arguments);
            Assert.False(info.UseShellExecute);
            Assert.True(info.CreateNoWindow);
            Assert.True(info.RedirectStandardInput);
            Assert.True(info.RedirectStandardOutput);
            Assert.True(info.RedirectStandardError);
        }

        // 既定では標準入力のリダイレクトは無効。
        Assert.False(ProcessStartInfoFactory.CreateStartInfo("ffmpeg", "-version").RedirectStandardInput);
    }
}
