// フレーム描画（設計プラン 5章）。
// 時刻 t のフレームを SkiaSharp で描く関数を1つだけ作る。
// プレビューと書き出しは同じ描画コードを使うため、表示の一致が保たれる。
// 映像フレームは Editor.Media から取得し、テキストは直接描く。

using System.IO;
using Editor.Core;
using Editor.Media;
using SkiaSharp;

namespace Editor.Render;

/// <summary>時刻 t のフレームを描く口。</summary>
public interface IFrameRenderer
{
    Task<SKBitmap> RenderFrameAsync(
        EditorProject project, double timeSeconds, int width, int height,
        CancellationToken ct = default);
}

/// <summary>タイムラインの評価結果を1フレームに描く。</summary>
public sealed class TimelineFrameRenderer : IFrameRenderer
{
    private readonly IVideoFrameSource _frameSource;

    public TimelineFrameRenderer(IVideoFrameSource frameSource)
    {
        _frameSource = frameSource;
    }

    public async Task<SKBitmap> RenderFrameAsync(
        EditorProject project, double timeSeconds, int width, int height,
        CancellationToken ct = default)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);

        var states = TimelineEvaluator.EvaluateProject(project, timeSeconds);
        foreach (var state in states)
        {
            ct.ThrowIfCancellationRequested();
            var clip = state.Clip;

            switch (clip.Kind)
            {
                case ClipKind.Video:
                    await DrawVideoClipAsync(canvas, clip, state, timeSeconds, width, height, ct).ConfigureAwait(false);
                    break;
                case ClipKind.Image:
                    DrawImageClip(canvas, clip, state, width, height);
                    break;
                case ClipKind.Text:
                    DrawTextClip(canvas, clip, state, width, height);
                    break;
                case ClipKind.Audio:
                    // 音声クリップは映像を持たない。
                    break;
            }
        }

        return bitmap;
    }

    private async Task DrawVideoClipAsync(
        SKCanvas canvas, Clip clip, EvaluatedClipState state, double timeSeconds,
        int width, int height, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(clip.SourcePath))
            return;

        // 素材内の時刻 ＝ イン点 ＋ タイムライン上での経過時間。
        var sourceTime = clip.InPointSeconds + timeSeconds - clip.StartSeconds;

        var png = await _frameSource.GetFramePngAsync(clip.SourcePath, sourceTime, width, ct)
            .ConfigureAwait(false);
        if (png is null)
            return;

        using var frame = SKBitmap.Decode(png);
        if (frame is null)
            return;

        DrawBitmapWithTransform(canvas, frame, state, width, height);
    }

    private static void DrawImageClip(
        SKCanvas canvas, Clip clip, EvaluatedClipState state, int width, int height)
    {
        if (string.IsNullOrEmpty(clip.SourcePath) || !File.Exists(clip.SourcePath))
            return;

        using var image = SKBitmap.Decode(clip.SourcePath);
        if (image is null)
            return;

        DrawBitmapWithTransform(canvas, image, state, width, height);
    }

    private static void DrawBitmapWithTransform(
        SKCanvas canvas, SKBitmap image, EvaluatedClipState state, int width, int height)
    {
        // フレームに収まる倍率 × クリップの拡大率。
        var fit = Math.Min((double)width / image.Width, (double)height / image.Height);
        var scale = fit * state.Scale;

        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(255, 255, 255, (byte)Math.Clamp(state.Opacity * 255, 0, 255)),
            ColorFilter = ColorCorrectionFilter(state.Brightness, state.Contrast)
        };

        canvas.Save();
        canvas.Translate((float)(width / 2.0 + state.PositionX), (float)(height / 2.0 + state.PositionY));
        canvas.RotateDegrees((float)state.RotationDegrees);
        canvas.Scale((float)scale, (float)scale);
        canvas.DrawBitmap(image, new SKRect(-image.Width / 2f, -image.Height / 2f, image.Width / 2f, image.Height / 2f), paint);
        canvas.Restore();
    }

    private static void DrawTextClip(
        SKCanvas canvas, Clip clip, EvaluatedClipState state, int width, int height)
    {
        if (string.IsNullOrEmpty(clip.Text))
            return;

        using var typeface = SKTypeface.FromFamilyName(clip.FontFamily);
        using var paint = new SKPaint
        {
            Typeface = typeface,
            TextSize = (float)(clip.FontSize * state.Scale),
            Color = ParseColor(clip.FontColorHex, state.Opacity),
            IsAntialias = true
        };

        var textWidth = paint.MeasureText(clip.Text);
        canvas.Save();
        canvas.Translate((float)(width / 2.0 + state.PositionX), (float)(height / 2.0 + state.PositionY));
        canvas.RotateDegrees((float)state.RotationDegrees);
        canvas.DrawText(clip.Text, -textWidth / 2, paint.TextSize * 0.35f, paint);
        canvas.Restore();
    }

    /// <summary>明るさ・コントラストのカラーマトリクス。フェーズ1の色調補正の土台。</summary>
    internal static SKColorFilter? ColorCorrectionFilter(double brightness, double contrast)
    {
        if (Math.Abs(brightness) < 1e-6 && Math.Abs(contrast - 1.0) < 1e-6)
            return null;

        var c = (float)contrast;
        var offset = (float)(128 * (1 - contrast) + brightness * 255);
        float[] matrix =
        {
            c, 0, 0, 0, offset,
            0, c, 0, 0, offset,
            0, 0, c, 0, offset,
            0, 0, 0, 1, 0
        };
        return SKColorFilter.CreateColorMatrix(matrix);
    }

    private static SKColor ParseColor(string hex, double opacity)
    {
        if (SKColor.TryParse(hex, out var color))
            return color.WithAlpha((byte)Math.Clamp(opacity * 255, 0, 255));
        return SKColors.White.WithAlpha((byte)Math.Clamp(opacity * 255, 0, 255));
    }
}

/// <summary>
/// プレビュー用レンダラー。指定した解像度で描き、直近の結果をキャッシュする。
/// 返すビットマップはこのクラスが所有する（呼び出し側で破棄しないこと）。
/// </summary>
public sealed class PreviewRenderer : IDisposable
{
    private readonly IFrameRenderer _inner;
    private SKBitmap? _lastBitmap;
    private (double TimeSeconds, PreviewResolution Resolution)? _lastKey;

    public PreviewRenderer(IFrameRenderer inner)
    {
        _inner = inner;
    }

    public async Task<SKBitmap> RenderPreviewAsync(
        EditorProject project, double timeSeconds, PreviewResolution resolution,
        CancellationToken ct = default)
    {
        var key = (timeSeconds, resolution);
        if (_lastKey == key && _lastBitmap is not null)
            return _lastBitmap;

        var (width, height) = resolution.ApplyTo(project.Width, project.Height);
        var bitmap = await _inner.RenderFrameAsync(project, timeSeconds, width, height, ct)
            .ConfigureAwait(false);

        _lastBitmap?.Dispose();
        _lastBitmap = bitmap;
        _lastKey = key;
        return bitmap;
    }

    public void Dispose()
    {
        _lastBitmap?.Dispose();
        _lastBitmap = null;
    }
}
