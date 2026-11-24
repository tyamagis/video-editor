// 追加エフェクト9種の描画テスト：TimelineFrameRenderer（プレビュー・書き出し共通）で
// 各エフェクトが例外なく描け、目に見える変化が出ること。実ファイル・FFmpeg には依存しない
// （フレーム供給はテスト用スタブが PNG を返す）。画質の厳密な判定ではなく、
// 「効いている／壊れていない」の確認が目的。

using Editor.Core;
using Editor.Media;
using Editor.Render;
using SkiaSharp;
using Xunit;

namespace Editor.Tests;

/// <summary>追加エフェクト9種の描画テスト（TimelineFrameRenderer 経由）。</summary>
public sealed class NewEffectsRenderTests
{
    private const int Width = 160;
    private const int Height = 90;

    /// <summary>テスト用フレーム供給：SourcePath に応じてテストパターンの PNG を返す。</summary>
    private sealed class PatternFrameSource : IVideoFrameSource
    {
        public Task<byte[]?> GetFramePngAsync(string sourcePath, double timeSeconds, int maxWidth, CancellationToken ct = default)
        {
            using var bitmap = sourcePath == "pattern-square"
                ? BuildSquarePattern()
                : BuildCirclePattern();
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return Task.FromResult<byte[]?>(data.ToArray());
        }

        /// <summary>黒背景に白い円（中心 80,45、半径 30）。円の縁は全方向に放射状の変化がある。</summary>
        private static SKBitmap BuildCirclePattern()
        {
            var bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Black);
            using var paint = new SKPaint { Color = SKColors.White };
            canvas.DrawCircle(Width / 2, Height / 2, 30, paint);
            return bitmap;
        }

        /// <summary>黒背景に白い正方形（40,20)-(70,50)。ドロップシャドウ用。</summary>
        private static SKBitmap BuildSquarePattern()
        {
            var bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Black);
            using var paint = new SKPaint { Color = SKColors.White };
            canvas.DrawRect(40, 20, 30, 30, paint);
            return bitmap;
        }
    }

    private static async Task<SKBitmap> RenderAsync(ClipEffect effect, string pattern = "pattern-circle", double timeSeconds = 0.5)
    {
        var project = new EditorProject { Width = Width, Height = Height, FrameRate = 30 };
        var clip = new Clip
        {
            Kind = ClipKind.Video,
            StartSeconds = 0,
            DurationSeconds = 10,
            SourcePath = pattern
        };
        clip.Effects.Add(effect);
        project.Tracks.Add(new Track { Clips = { clip } });
        var renderer = new TimelineFrameRenderer(new PatternFrameSource());
        return await renderer.RenderFrameAsync(project, timeSeconds, Width, Height);
    }

    private static async Task<SKBitmap> RenderPlainAsync(string pattern = "pattern-circle")
        => await RenderAsync(new ClipEffect { Kind = EffectKind.Blur, Amount = 0 }, pattern);

    /// <summary>2枚のビットマップがどこか1画素でも違うこと。</summary>
    private static bool DiffersAnywhere(SKBitmap a, SKBitmap b)
    {
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y))
                    return true;
        return false;
    }

    /// <summary>色収差：円の縁（放射方向に色が変わる場所）に色のにじみが出る（R と B がずれる）。</summary>
    [Fact]
    public async Task ChromaticAberration_CreatesColorFringe()
    {
        using var plain = await RenderPlainAsync();
        using var aberrated = await RenderAsync(new ClipEffect { Kind = EffectKind.ChromaticAberration, Amount = 20 });
        Assert.True(DiffersAnywhere(plain, aberrated));
        // 円の縁の斜め 45° 付近（101,66 あたり）で R と B が食い違う。
        var found = false;
        for (var dx = -2; dx <= 2 && !found; dx++)
            for (var dy = -2; dy <= 2 && !found; dy++)
            {
                var pixel = aberrated.GetPixel(101 + dx, 66 + dy);
                if (Math.Abs(pixel.Red - pixel.Blue) > 8)
                    found = true;
            }
        Assert.True(found);
    }

    /// <summary>魚眼：円の外側の点が内側からサンプルされ、白くなる（膨らみ）。</summary>
    [Fact]
    public async Task Fisheye_BulgesCircleOutward()
    {
        using var plain = await RenderPlainAsync();
        using var fisheye = await RenderAsync(new ClipEffect { Kind = EffectKind.Fisheye, Amount = 1, Radius = 1 });
        Assert.Equal(SKColors.Black, plain.GetPixel(105, 70));
        Assert.True(fisheye.GetPixel(105, 70).Red > 200);
    }

    /// <summary>VHSグリッチ：走査線で円の内側が暗くなる行がある。</summary>
    [Fact]
    public async Task VhsGlitch_ScanlinesDarkenRows()
    {
        using var plain = await RenderPlainAsync();
        using var vhs = await RenderAsync(new ClipEffect { Kind = EffectKind.VhsGlitch, Amount = 1, Radius = 3 });
        Assert.True(DiffersAnywhere(plain, vhs));
        // 走査線の行（y=17 は 17%3==2）は円の内側でも暗くなる。
        var scanline = vhs.GetPixel(80, 17);
        Assert.True(scanline.Red < 200);
    }

    /// <summary>ドロップシャドウ：白い正方形の右下に影が出る。</summary>
    [Fact]
    public async Task DropShadow_CastsShadowDownRight()
    {
        using var plain = await RenderPlainAsync("pattern-square");
        using var shadow = await RenderAsync(
            new ClipEffect { Kind = EffectKind.DropShadow, Amount = 1, Radius = 4 }, "pattern-square");
        Assert.Equal(SKColors.Black, plain.GetPixel(76, 56));
        var shadowPixel = shadow.GetPixel(76, 56);
        Assert.True(shadowPixel.Red > 8);
    }

    /// <summary>フィルムグレイン：円の内側の画素値が揺れる。</summary>
    [Fact]
    public async Task FilmGrain_AddsGrain()
    {
        using var plain = await RenderPlainAsync();
        using var grain = await RenderAsync(new ClipEffect { Kind = EffectKind.FilmGrain, Amount = 1, Radius = 1 });
        Assert.True(DiffersAnywhere(plain, grain));
    }

    /// <summary>ズームブラー：円の縁が外側へにじむ（境界のすぐ外が白っぽくなる）。</summary>
    [Fact]
    public async Task ZoomBlur_SoftensEdge()
    {
        using var plain = await RenderPlainAsync();
        using var blurred = await RenderAsync(new ClipEffect { Kind = EffectKind.ZoomBlur, Amount = 1, Radius = 8 });
        Assert.Equal(SKColors.Black, plain.GetPixel(103, 68));
        Assert.True(blurred.GetPixel(103, 68).Red > 8);
    }

    /// <summary>モーションブラー：水平方向に円の縁がなまる（境界の左側に白がにじむ）。</summary>
    [Fact]
    public async Task MotionBlur_SoftensEdgeHorizontally()
    {
        using var plain = await RenderPlainAsync();
        using var blurred = await RenderAsync(new ClipEffect { Kind = EffectKind.MotionBlur, Amount = 32, Radius = 0 });
        Assert.Equal(SKColors.Black, plain.GetPixel(45, 45));
        Assert.True(blurred.GetPixel(45, 45).Red > 8);
    }

    /// <summary>輝度キー：白い円が抜けて黒いキャンバスが見える。黒い背景は残る。</summary>
    [Fact]
    public async Task LuminanceKey_RemovesBrightCircle()
    {
        using var keyed = await RenderAsync(new ClipEffect { Kind = EffectKind.LuminanceKey, Amount = 0.5, Radius = 0.15 });
        var circle = keyed.GetPixel(80, 45);
        Assert.True(circle.Red < 8 && circle.Green < 8 && circle.Blue < 8);
        var background = keyed.GetPixel(10, 45);
        Assert.Equal(SKColors.Black, background);
    }

    /// <summary>トーンカーブ：シャドウ +0.5 で真っ黒が中間のグレーに持ち上がる。</summary>
    [Fact]
    public async Task ToneCurve_LiftsShadows()
    {
        using var plain = await RenderPlainAsync();
        using var curved = await RenderAsync(new ClipEffect { Kind = EffectKind.ToneCurve, Amount = 0.5, Radius = 0 });
        Assert.Equal(SKColors.Black, plain.GetPixel(10, 45));
        var lifted = curved.GetPixel(10, 45);
        Assert.InRange(lifted.Red, 100, 155);
    }

    /// <summary>トーンカーブ：両方 0 なら変化なし（恒等）。</summary>
    [Fact]
    public async Task ToneCurve_ZeroIsIdentity()
    {
        using var plain = await RenderPlainAsync();
        using var curved = await RenderAsync(new ClipEffect { Kind = EffectKind.ToneCurve, Amount = 0, Radius = 0 });
        Assert.False(DiffersAnywhere(plain, curved));
    }
}
