// テスト監査（2026-11-25）で追加：
// - 並列化した Sharpen / Emboss の決定性（同じ入力→同じ出力の再現性）
// - Clip.Clone の独立性（複製側の変更が元に漏れないこと）
// - ExportService の単一クリップ書き出しの実経路（実 ffmpeg、既存テストと同じガード方式）

using Editor.Core;
using Editor.Media;
using Editor.Render;
using SkiaSharp;
using Xunit;

namespace Editor.Tests;

/// <summary>監査追加テスト（決定性 / Clone / 書き出し実経路）。</summary>
public sealed class TestAuditRenderAndExportTests
{
    // ---- 並列化の決定性 ----

    private static SKBitmap BuildPattern()
    {
        var bitmap = new SKBitmap(96, 64, SKColorType.Bgra8888, SKAlphaType.Premul);
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var v = (byte)(128 + 100 * Math.Sin(x * 0.3) * Math.Cos(y * 0.2));
                bitmap.SetPixel(x, y, new SKColor(v, (byte)(255 - v), 128, 255));
            }
        return bitmap;
    }

    [Fact]
    public void Sharpen_Parallelized_IsDeterministicAcrossRuns()
    {
        using var src = BuildPattern();
        var effect = new ClipEffect { Kind = EffectKind.Sharpen, Amount = 1.5, Radius = 6 };
        using var first = NewEffectsDetail.ApplySharpen(src, effect);
        using var second = NewEffectsDetail.ApplySharpen(src, effect);
        Assert.Equal(first.Bytes, second.Bytes);
    }

    [Fact]
    public void Emboss_Parallelized_IsDeterministicAcrossRuns()
    {
        using var src = BuildPattern();
        var effect = new ClipEffect { Kind = EffectKind.Emboss, Amount = 1.5, Radius = 3 };
        using var first = NewEffectsDetail.ApplyEmboss(src, effect);
        using var second = NewEffectsDetail.ApplyEmboss(src, effect);
        Assert.Equal(first.Bytes, second.Bytes);
    }

    // ---- Clip.Clone の独立性 ----

    [Fact]
    public void ClipClone_MutationsOnCopy_DoNotAffectOriginal()
    {
        var clip = new Clip
        {
            Name = "元",
            Kind = ClipKind.Video,
            StartSeconds = 1,
            DurationSeconds = 5
        };
        clip.Effects.Add(new ClipEffect { Kind = EffectKind.Blur, Amount = 0.5, Radius = 2 });
        clip.AudioBindings.Add(new AudioBinding { ParameterName = "Scale", SourceClipId = "a1" });
        clip.KeyframeTracks.Add(new KeyframeTrack
        {
            ParameterName = "Opacity",
            Keyframes = new List<Keyframe> { new(0, 0.25) }
        });
        clip.Visualization = new VisualizationSettings { Gain = 2.0 };

        var copy = clip.Clone();

        Assert.NotEqual(clip.Id, copy.Id);
        Assert.NotSame(clip.Effects, copy.Effects);
        Assert.NotSame(clip.Effects[0], copy.Effects[0]);
        Assert.NotSame(clip.KeyframeTracks[0].Keyframes, copy.KeyframeTracks[0].Keyframes);
        Assert.NotSame(clip.Visualization, copy.Visualization);

        copy.Effects[0].Amount = 0.99;
        copy.AudioBindings[0].ParameterName = "Rotation";
        copy.KeyframeTracks[0].Keyframes[0] = new Keyframe(0, 0.75);
        copy.Visualization!.Gain = 3.5;
        copy.Effects.Add(new ClipEffect { Kind = EffectKind.Glow });

        Assert.Equal(0.5, clip.Effects[0].Amount);
        Assert.Equal("Scale", clip.AudioBindings[0].ParameterName);
        Assert.Equal(0.25, clip.KeyframeTracks[0].Keyframes[0].Value);
        Assert.Equal(2.0, clip.Visualization!.Gain);
        Assert.Single(clip.Effects);
    }

    // ---- ExportService の実経路（実 ffmpeg） ----

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

    [Fact]
    public async Task ExportSingleClip_ProducesFileWithExpectedDuration()
    {
        // ffmpeg が無ければこのテストは対象外（環境に ffmpeg が無いだけ）。
        if (!await IsFfmpegAvailableAsync())
            return;

        using var temp = new TempDirectory();
        var source = temp.File("src.mp4");
        await MakeVideoWithAudioAsync(source, 2.0);
        var output = temp.File("out.mp4");

        var service = new ExportService(new FfmpegSettings());
        await service.ExportSingleClipAsync(source, 0.5, 1.0, output, new ExportSettings());

        Assert.True(File.Exists(output));
        var info = await new MediaProbe(new FfmpegSettings()).ProbeAsync(output);
        Assert.Equal(1.0, info.Duration.TotalSeconds, 1);
        Assert.Equal(1920, info.Width);
    }

    [Fact]
    public async Task ExportSingleClip_Speed2_HalvesDuration()
    {
        if (!await IsFfmpegAvailableAsync())
            return;

        using var temp = new TempDirectory();
        var source = temp.File("src.mp4");
        await MakeVideoWithAudioAsync(source, 2.0);
        var output = temp.File("out.mp4");

        var service = new ExportService(new FfmpegSettings());
        await service.ExportSingleClipAsync(source, 0, 2.0, output, new ExportSettings(), speed: 2.0);

        var info = await new MediaProbe(new FfmpegSettings()).ProbeAsync(output);
        Assert.Equal(1.0, info.Duration.TotalSeconds, 1);
    }

    [Fact]
    public async Task ExportSingleClip_MissingSource_ThrowsFileNotFound()
    {
        using var temp = new TempDirectory();
        var service = new ExportService(new FfmpegSettings());
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.ExportSingleClipAsync(temp.File("missing.mp4"), 0, 1.0, temp.File("out.mp4"), new ExportSettings()));
    }
}
