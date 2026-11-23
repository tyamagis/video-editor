// プレビュー表示。PreviewRenderer が描いたビットマップを
// コントロール内にレターボックスで表示する。
// ビットマップの所有権は PreviewRenderer 側にある（ここでは破棄しない）。

using System.Windows.Controls;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace Editor.App.Controls;

public partial class PreviewControl : UserControl
{
    public SKBitmap? Bitmap { get; set; }

    /// <summary>スペクトル表示を重ねるか（プレビュー専用）。</summary>
    public bool ShowSpectrum { get; set; }

    /// <summary>リサジュー表示を重ねるか（プレビュー専用）。</summary>
    public bool ShowLissajous { get; set; }

    /// <summary>現在のスペクトル（帯域ごとの強さ 0.0〜1.0）を返す。解析前は null。</summary>
    public Func<double[]?>? SpectrumBandsProvider { get; set; }

    /// <summary>現在の直前のステレオ波形（リサジュー用）を返す。解析前は null。</summary>
    public Func<(float[] Left, float[] Right)?>? StereoWindowProvider { get; set; }

    public PreviewControl()
    {
        InitializeComponent();
    }

    public void InvalidatePreview() => PreviewElement?.InvalidateVisual();

    private void OnPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Black);

        if (Bitmap is null)
        {
            using var paint = new SKPaint
            {
                Color = SKColors.Gray,
                TextSize = 16,
                IsAntialias = true,
                Typeface = SKTypeface.FromFamilyName("Yu Gothic UI") ?? SKTypeface.Default
            };
            canvas.DrawText("素材を読み込んでください", 16, 32, paint);
            return;
        }

        // アスペクト比を保って全体が収まるように描く。
        var info = e.Info;
        var scale = Math.Min(
            (double)info.Width / Bitmap.Width,
            (double)info.Height / Bitmap.Height);
        var destWidth = (float)(Bitmap.Width * scale);
        var destHeight = (float)(Bitmap.Height * scale);
        var left = (info.Width - destWidth) / 2f;
        var top = (info.Height - destHeight) / 2f;

        canvas.DrawBitmap(Bitmap, new SKRect(left, top, left + destWidth, top + destHeight));

        // 音の可視化オーバーレイ（プレビュー専用。書き出しには入らない）。
        var dest = new SKRect(left, top, left + destWidth, top + destHeight);
        if (ShowSpectrum)
        {
            var bands = SpectrumBandsProvider?.Invoke();
            if (bands is { Length: > 0 })
                DrawSpectrumOverlay(canvas, dest, bands);
        }
        if (ShowLissajous)
        {
            var window = StereoWindowProvider?.Invoke();
            if (window.HasValue)
                DrawLissajousOverlay(canvas, dest, window.Value);
        }
    }

    /// <summary>スペクトルを映像の下端に棒グラフで重ねる。</summary>
    private static void DrawSpectrumOverlay(SKCanvas canvas, SKRect dest, double[] bands)
    {
        var stripHeight = dest.Height * 0.22f;
        var strip = new SKRect(dest.Left, dest.Bottom - stripHeight, dest.Right, dest.Bottom);
        using var bgPaint = new SKPaint { Color = new SKColor(0, 0, 0, 110) };
        canvas.DrawRect(strip, bgPaint);

        var count = bands.Length;
        const float gap = 2f;
        var barWidth = (strip.Width - gap * (count + 1)) / count;
        if (barWidth <= 0)
            return;
        using var barPaint = new SKPaint
        {
            Color = new SKColor(0x22, 0xD3, 0xEE, 220),
            IsAntialias = true
        };
        for (var i = 0; i < count; i++)
        {
            var value = Math.Clamp(bands[i], 0, 1);
            var h = (float)(value * (stripHeight - 6));
            if (h <= 0)
                continue;
            var x = strip.Left + gap + i * (barWidth + gap);
            canvas.DrawRect(new SKRect(x, strip.Bottom - 3 - h, x + barWidth, strip.Bottom - 3), barPaint);
        }
    }

    /// <summary>リサジュー図形（左チャンネル＝横、右チャンネル＝縦）を右上に重ねる。</summary>
    private static void DrawLissajousOverlay(SKCanvas canvas, SKRect dest, (float[] Left, float[] Right) window)
    {
        var (left, right) = window;
        var count = Math.Min(left.Length, right.Length);
        if (count < 2)
            return;

        var size = dest.Height * 0.22f;
        var square = new SKRect(dest.Right - size - 8, dest.Top + 8, dest.Right - 8, dest.Top + 8 + size);
        using var bgPaint = new SKPaint { Color = new SKColor(0, 0, 0, 110) };
        canvas.DrawRect(square, bgPaint);

        using var framePaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 60),
            StrokeWidth = 1,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };
        canvas.DrawRect(square, framePaint);
        var cx = square.MidX;
        var cy = square.MidY;
        canvas.DrawLine(cx, square.Top, cx, square.Bottom, framePaint);
        canvas.DrawLine(square.Left, cy, square.Right, cy, framePaint);

        var half = size / 2f * 0.9f;
        using var path = new SKPath();
        for (var i = 0; i < count; i++)
        {
            var x = cx + Math.Clamp(left[i], -1f, 1f) * half;
            var y = cy - Math.Clamp(right[i], -1f, 1f) * half;
            if (i == 0)
                path.MoveTo(x, y);
            else
                path.LineTo(x, y);
        }
        using var linePaint = new SKPaint
        {
            Color = new SKColor(0xFF, 0x2E, 0x88, 220),
            StrokeWidth = 1.5f,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };
        canvas.DrawPath(path, linePaint);
    }
}
