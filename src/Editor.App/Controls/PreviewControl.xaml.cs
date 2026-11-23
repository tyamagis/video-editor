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
                IsAntialias = true
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
    }
}
