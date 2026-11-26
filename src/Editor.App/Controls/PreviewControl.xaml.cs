// プレビュー表示。PreviewRenderer が描いたビットマップを
// コントロール内にレターボックスで表示する。
// ビットマップの所有権は PreviewRenderer 側にある（ここでは破棄しない）。

using System.Windows.Controls;
using Editor.Render;
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

    // 映像エリアはステージとして読めるよう、ウィンドウ背景より暗い近黒にする（UI色。映像描画には影響しない）。
    private static readonly SKColor StageBackground = new(0x0D, 0x0D, 0x0F);
    private static readonly SKColor PlaceholderText = new(0xB4, 0xB6, 0xB8);

    private void OnPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(StageBackground);

        if (Bitmap is null)
        {
            using var paint = new SKPaint
            {
                Color = PlaceholderText,
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

    /// <summary>
    /// スペクトルを映像の下端に棒グラフで重ねる。
    /// 棒の描き方は可視化クリップと同じ共通処理（VisualizationRenderer.DrawSpectrumBars）。
    /// 帯の範囲・色はオーバーレイ専用の既定値（下端 22% の帯、シアン）。
    /// </summary>
    private static void DrawSpectrumOverlay(SKCanvas canvas, SKRect dest, double[] bands)
    {
        var stripHeight = dest.Height * 0.22f;
        var strip = new SKRect(dest.Left, dest.Bottom - stripHeight, dest.Right, dest.Bottom);
        using var bgPaint = new SKPaint { Color = new SKColor(0, 0, 0, 110) };
        canvas.DrawRect(strip, bgPaint);

        var barArea = new SKRect(
            strip.Left, strip.Bottom - 3 - (stripHeight - 6),
            strip.Right, strip.Bottom - 3);
        VisualizationRenderer.DrawSpectrumBars(
            canvas, barArea, bands, new SKColor(0x22, 0xD3, 0xEE, 220));
    }

    /// <summary>
    /// リサジュー図形（左チャンネル＝横、右チャンネル＝縦）を右上に重ねる。
    /// 枠・十字線と軌跡パスは可視化クリップと同じ共通処理。
    /// 位置・大きさ・色はオーバーレイ専用の既定値（右上 22% の正方形、マゼンタ）。
    /// </summary>
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

        VisualizationRenderer.DrawLissajousFrame(canvas, square, new SKColor(255, 255, 255, 60));

        var half = size / 2f * 0.9f;
        using var path = VisualizationRenderer.BuildLissajousPath(
            left, right, 0, count, square.MidX, square.MidY, half, 1f);
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
