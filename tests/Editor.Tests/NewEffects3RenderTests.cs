// 新エフェクト第3弾（21種）の描画テスト：TimelineFrameRenderer（プレビュー・書き出し共通）で
// 各エフェクトが配線され、目に見える変化が出ること。実ファイル・FFmpeg には依存しない
// （フレーム供給はテスト用スタブが PNG を返す）。画質の厳密な判定ではなく、
// 「効いている／壊れていない」の確認が目的。各エフェクトの詳細な振る舞いは
// ヘルパー直接呼びのテスト（NewEffectsLightTests / NewEffectsDistortTests /
// NewEffectsColorTests / NewEffectsStyleTests）で確認する。

using Editor.Core;
using Editor.Render;
using SkiaSharp;
using Xunit;

namespace Editor.Tests;

/// <summary>新エフェクト第3弾21種の描画テスト（TimelineFrameRenderer 経由）。</summary>
public sealed class NewEffects3RenderTests
{
    private const int Width = 160;
    private const int Height = 90;

    /// <summary>テスト用フレーム供給：SourcePath に応じてテストパターンの PNG を返す。</summary>
    private sealed class PatternFrameSource : IVideoFrameSource
    {
        public Task<byte[]?> GetFramePngAsync(string sourcePath, double timeSeconds, int maxWidth, CancellationToken ct = default)
        {
            using var bitmap = sourcePath switch
            {
                "pattern-square" => BuildSquarePattern(),
                "pattern-gradient" => BuildGradientPattern(),
                "pattern-noise" => BuildNoisePattern(),
                "pattern-black" => BuildBlackPattern(),
                _ => BuildCirclePattern()
            };
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return Task.FromResult<byte[]?>(data.ToArray());
        }

        /// <summary>黒背景に白い円（中心 80,45、半径 30）。</summary>
        private static SKBitmap BuildCirclePattern()
        {
            var bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Black);
            using var paint = new SKPaint { Color = SKColors.White };
            canvas.DrawCircle(Width / 2, Height / 2, 30, paint);
            return bitmap;
        }

        /// <summary>黒背景に白い正方形（40,20)-(70,50)。中心から外れた形（トゥワール用）。</summary>
        private static SKBitmap BuildSquarePattern()
        {
            var bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Black);
            using var paint = new SKPaint { Color = SKColors.White };
            canvas.DrawRect(40, 20, 30, 30, paint);
            return bitmap;
        }

        /// <summary>左（黒）から右（白）への横グラデーション。画素 x の値は round(x * 255 / 159)。</summary>
        private static SKBitmap BuildGradientPattern()
        {
            var bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            for (var x = 0; x < Width; x++)
            {
                var v = (byte)Math.Round(x * 255.0 / (Width - 1));
                using var paint = new SKPaint { Color = new SKColor(v, v, v) };
                canvas.DrawRect(x, 0, 1, Height, paint);
            }
            return bitmap;
        }

        /// <summary>決定的な疑似ランダム輝度（ピクセルソート用。行ごとに明暗が不規則に並び、しきい値以上の区間が降順になるためソートで変わる）。</summary>
        private static SKBitmap BuildNoisePattern()
        {
            var bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            for (var y = 0; y < Height; y++)
                for (var x = 0; x < Width; x++)
                {
                    var v = (byte)((255 - (x * 41 + y * 29) % 256 + 256) % 256);
                    bitmap.SetPixel(x, y, new SKColor(v, v, v));
                }
            return bitmap;
        }

        /// <summary>真っ黒なフレーム（テキストの下のトラック用）。</summary>
        private static SKBitmap BuildBlackPattern()
        {
            var bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Black);
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

    /// <summary>下に黒い映像クリップ、上にテキストクリップを重ねて描く。</summary>
    private static async Task<SKBitmap> RenderTextLayeredAsync(ClipEffect? effect, double timeSeconds = 0.5)
    {
        var project = new EditorProject { Width = Width, Height = Height, FrameRate = 30 };
        var video = new Clip
        {
            Kind = ClipKind.Video,
            StartSeconds = 0,
            DurationSeconds = 10,
            SourcePath = "pattern-black"
        };
        project.Tracks.Add(new Track { Clips = { video } });
        var text = new Clip
        {
            Kind = ClipKind.Text,
            StartSeconds = 0,
            DurationSeconds = 10,
            Text = "A",
            FontFamily = string.Empty,
            FontSize = 48,
            FontColorHex = "#FFFFFFFF"
        };
        if (effect is not null)
            text.Effects.Add(effect);
        project.Tracks.Add(new Track { Clips = { text } });
        var renderer = new TimelineFrameRenderer(new PatternFrameSource());
        return await renderer.RenderFrameAsync(project, timeSeconds, Width, Height);
    }

    /// <summary>2枚のビットマップがどこか1画素でも違うこと。</summary>
    private static bool DiffersAnywhere(SKBitmap a, SKBitmap b)
    {
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y))
                    return true;
        return false;
    }

    /// <summary>ブルーム：白い円の縁ににじみが出る。</summary>
    [Fact]
    public async Task Bloom_GlowsAroundBrightCircle()
    {
        using var plain = await RenderPlainAsync();
        using var bloomed = await RenderAsync(new ClipEffect { Kind = EffectKind.Bloom, Amount = 1, Radius = 8 });
        Assert.Equal(SKColors.Black, plain.GetPixel(103, 68));
        Assert.True(bloomed.GetPixel(103, 68).Red > 8);
    }

    /// <summary>ライトリーク：左上が暖かく染まる。</summary>
    [Fact]
    public async Task LightLeak_WarmsTopLeftCorner()
    {
        using var plain = await RenderPlainAsync("pattern-gradient");
        using var leaked = await RenderAsync(new ClipEffect { Kind = EffectKind.LightLeak, Amount = 1, Radius = 1 }, "pattern-gradient");
        Assert.True(DiffersAnywhere(plain, leaked));
        var pixel = leaked.GetPixel(5, 5);
        Assert.True(pixel.Red > pixel.Blue);
    }

    /// <summary>ピクセルソート：不規則な輝度の行が並べ替わる。</summary>
    [Fact]
    public async Task PixelSort_SortsBrightRuns()
    {
        using var plain = await RenderPlainAsync("pattern-noise");
        using var sorted = await RenderAsync(new ClipEffect { Kind = EffectKind.PixelSort, Amount = 0.5, Radius = 64 }, "pattern-noise");
        Assert.True(DiffersAnywhere(plain, sorted));
    }

    /// <summary>ウェーブ：円の縁がうねる。</summary>
    [Fact]
    public async Task Wave_WobblesCircleEdge()
    {
        using var plain = await RenderPlainAsync();
        using var waved = await RenderAsync(new ClipEffect { Kind = EffectKind.Wave, Amount = 20, Radius = 240 });
        Assert.True(DiffersAnywhere(plain, waved));
    }

    /// <summary>ミラー：グラデーションの右半分が左半分の鏡像になる。</summary>
    [Fact]
    public async Task Mirror_MirrorsRightHalf()
    {
        using var plain = await RenderPlainAsync("pattern-gradient");
        using var mirrored = await RenderAsync(new ClipEffect { Kind = EffectKind.Mirror, Amount = 0.5, Radius = 1 }, "pattern-gradient");
        Assert.True(DiffersAnywhere(plain, mirrored));
        // 右端（x=159）は左端の鏡像＝黒になる。
        Assert.True(mirrored.GetPixel(159, 45).Red < 8);
    }

    /// <summary>トゥワール：中心から外れた正方形がねじれて動く。</summary>
    [Fact]
    public async Task Twirl_TwistsSquare()
    {
        using var plain = await RenderPlainAsync("pattern-square");
        using var twirled = await RenderAsync(new ClipEffect { Kind = EffectKind.Twirl, Amount = 180, Radius = 0.6 }, "pattern-square");
        Assert.True(DiffersAnywhere(plain, twirled));
    }

    /// <summary>ディスプレイスメント：円の縁がノイズでずれる。</summary>
    [Fact]
    public async Task Displacement_ShiftsCircleEdge()
    {
        using var plain = await RenderPlainAsync();
        using var displaced = await RenderAsync(new ClipEffect { Kind = EffectKind.Displacement, Amount = 30, Radius = 1 });
        Assert.True(DiffersAnywhere(plain, displaced));
    }

    /// <summary>バルジ：円の外側の点が内側からサンプルされ、白くなる（膨らむ）。</summary>
    [Fact]
    public async Task Bulge_BulgesCircleOutward()
    {
        using var plain = await RenderPlainAsync();
        using var bulged = await RenderAsync(new ClipEffect { Kind = EffectKind.Bulge, Amount = 1, Radius = 1 });
        Assert.Equal(SKColors.Black, plain.GetPixel(105, 70));
        Assert.True(bulged.GetPixel(105, 70).Red > 200);
    }

    /// <summary>ウォーターリップル：円の縁が波でずれる（時刻で動く）。</summary>
    [Fact]
    public async Task WaterRipple_RipplesCircleEdge()
    {
        using var plain = await RenderPlainAsync();
        using var rippled = await RenderAsync(new ClipEffect { Kind = EffectKind.WaterRipple, Amount = 8, Radius = 40 });
        Assert.True(DiffersAnywhere(plain, rippled));
    }

    /// <summary>色温度：暖色側で R が上がり B が下がる。</summary>
    [Fact]
    public async Task ColorTemp_WarmRaisesRedLowersBlue()
    {
        using var plain = await RenderPlainAsync();
        using var warm = await RenderAsync(new ClipEffect { Kind = EffectKind.ColorTemp, Amount = 1 });
        Assert.Equal(SKColors.White, plain.GetPixel(80, 45));
        var pixel = warm.GetPixel(80, 45);
        Assert.Equal(255, pixel.Red);
        Assert.True(pixel.Blue < 220);
    }

    /// <summary>レベル補正：黒点 0.2 でグラデーションの暗い側が潰れる。</summary>
    [Fact]
    public async Task Levels_CrushesBlacks()
    {
        using var plain = await RenderPlainAsync("pattern-gradient");
        using var leveled = await RenderAsync(new ClipEffect { Kind = EffectKind.Levels, Amount = 0.2, Radius = 1, Midtones = 1 }, "pattern-gradient");
        Assert.True(DiffersAnywhere(plain, leveled));
        Assert.Equal(SKColors.Black, leveled.GetPixel(20, 45));
    }

    /// <summary>スプリットトーン：明部が暖色・暗部が寒色に寄る。</summary>
    [Fact]
    public async Task SplitTone_TintsHighlightsAndShadows()
    {
        using var plain = await RenderPlainAsync("pattern-gradient");
        using var toned = await RenderAsync(new ClipEffect { Kind = EffectKind.SplitTone, Amount = 45, Radius = 220, Midtones = 1 }, "pattern-gradient");
        Assert.True(DiffersAnywhere(plain, toned));
        var bright = toned.GetPixel(150, 45);
        Assert.True(bright.Red > bright.Blue);
        var dark = toned.GetPixel(10, 45);
        Assert.True(dark.Blue > dark.Red);
    }

    /// <summary>グラデーションマップ：輝度が色に置き換わる。</summary>
    [Fact]
    public async Task GradientMap_ReplacesLuminanceWithColor()
    {
        using var plain = await RenderPlainAsync("pattern-gradient");
        using var mapped = await RenderAsync(new ClipEffect { Kind = EffectKind.GradientMap, Amount = 220, Radius = 45 }, "pattern-gradient");
        Assert.True(DiffersAnywhere(plain, mapped));
        var dark = mapped.GetPixel(10, 45);
        Assert.True(dark.Blue > dark.Red);
        var bright = mapped.GetPixel(150, 45);
        Assert.True(bright.Red > bright.Blue);
    }

    /// <summary>オールドフィルム：ちらつきと傷で画素値が揺れる。</summary>
    [Fact]
    public async Task OldFilm_AddsFlickerAndScratches()
    {
        using var plain = await RenderPlainAsync();
        using var aged = await RenderAsync(new ClipEffect { Kind = EffectKind.OldFilm, Amount = 1, Radius = 1 });
        Assert.True(DiffersAnywhere(plain, aged));
    }

    /// <summary>レターボックス：上下に黒帯が入る。</summary>
    [Fact]
    public async Task Letterbox_AddsBlackBars()
    {
        using var plain = await RenderPlainAsync("pattern-gradient");
        using var boxed = await RenderAsync(new ClipEffect { Kind = EffectKind.Letterbox, Amount = 0.12 }, "pattern-gradient");
        Assert.True(DiffersAnywhere(plain, boxed));
        Assert.Equal(SKColors.Black, boxed.GetPixel(80, 3));
        Assert.Equal(SKColors.Black, boxed.GetPixel(80, 86));
    }

    /// <summary>スキャンライン：円の内側が暗くなる行がある。</summary>
    [Fact]
    public async Task Scanlines_DarkenRows()
    {
        using var plain = await RenderPlainAsync();
        using var scanned = await RenderAsync(new ClipEffect { Kind = EffectKind.Scanlines, Amount = 1, Radius = 3 });
        Assert.True(DiffersAnywhere(plain, scanned));
        var scanline = scanned.GetPixel(80, 17);
        Assert.True(scanline.Red < 200);
    }

    /// <summary>ハーフトーン：グラデーションがドットに変わる。</summary>
    [Fact]
    public async Task Halftone_TurnsGradientIntoDots()
    {
        using var plain = await RenderPlainAsync("pattern-gradient");
        using var dotted = await RenderAsync(new ClipEffect { Kind = EffectKind.Halftone, Amount = 6, Radius = 45 }, "pattern-gradient");
        Assert.True(DiffersAnywhere(plain, dotted));
    }

    /// <summary>エッジ検出：円の縁が浮かび、他が暗くなる。</summary>
    [Fact]
    public async Task EdgeDetect_HighlightsCircleEdge()
    {
        using var plain = await RenderPlainAsync();
        using var edged = await RenderAsync(new ClipEffect { Kind = EffectKind.EdgeDetect, Amount = 1, Radius = 0.15 });
        Assert.True(DiffersAnywhere(plain, edged));
        // 円の内側の平坦な白はエッジマップでは黒に近くなる。
        Assert.True(edged.GetPixel(80, 45).Red < 100);
    }

    /// <summary>ソラリゼーション：しきい値以上の明るさが反転する。</summary>
    [Fact]
    public async Task Solarize_InvertsBrightHalf()
    {
        using var plain = await RenderPlainAsync("pattern-gradient");
        using var solarized = await RenderAsync(new ClipEffect { Kind = EffectKind.Solarize, Amount = 0.5 }, "pattern-gradient");
        Assert.True(DiffersAnywhere(plain, solarized));
        // x=150（明るい側）は反転して暗くなる。
        Assert.True(solarized.GetPixel(150, 45).Red < 100);
        // x=10（暗い側）は変わらない。
        Assert.Equal(plain.GetPixel(10, 45), solarized.GetPixel(10, 45));
    }

    /// <summary>油絵風：グラデーションの画素値がなまる。</summary>
    [Fact]
    public async Task Kuwahara_SoftensGradient()
    {
        using var plain = await RenderPlainAsync("pattern-gradient");
        using var painted = await RenderAsync(new ClipEffect { Kind = EffectKind.Kuwahara, Amount = 3, Radius = 1 }, "pattern-gradient");
        Assert.True(DiffersAnywhere(plain, painted));
    }

    /// <summary>ティルトシフト：円の上下がぼけて、中心はシャープなまま。</summary>
    [Fact]
    public async Task TiltShift_BlursTopAndBottom()
    {
        using var plain = await RenderPlainAsync();
        using var shifted = await RenderAsync(new ClipEffect { Kind = EffectKind.TiltShift, Amount = 1, Radius = 0.3 });
        Assert.True(DiffersAnywhere(plain, shifted));
        // 中心帯（y=45）は元画像のまま。
        Assert.Equal(plain.GetPixel(80, 45), shifted.GetPixel(80, 45));
    }

    /// <summary>テキスト＋ブルーム：黒背景の白文字ににじみが出る（テキストへの適用）。</summary>
    [Fact]
    public async Task TextWithBloom_GlowsAroundText()
    {
        using var plain = await RenderTextLayeredAsync(null);
        using var bloomed = await RenderTextLayeredAsync(new ClipEffect { Kind = EffectKind.Bloom, Amount = 1, Radius = 8 });
        Assert.True(DiffersAnywhere(plain, bloomed));
    }
}
