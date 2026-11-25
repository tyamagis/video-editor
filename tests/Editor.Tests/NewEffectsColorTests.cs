// 新エフェクト4種（色温度・レベル補正・スプリットトーン・グラデーションマップ）の
// 描画ヘルパー（NewEffectsColor）の直接テスト。FrameRenderer への配線はまだ無いため
// ヘルパーを直接呼ぶ。画質の厳密な判定ではなく、「効いている／壊れていない／
// 無効な量では変わらない」の確認が目的。

using Editor.Core;
using Editor.Render;
using SkiaSharp;
using Xunit;

namespace Editor.Tests;

/// <summary>色温度・レベル補正・スプリットトーン・グラデーションマップの直接テスト。</summary>
public sealed class NewEffectsColorTests
{
    private const int Width = 160;
    private const int Height = 90;

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

    /// <summary>指定したグレーの一色で塗ったビットマップ（既定 16x16）。</summary>
    private static SKBitmap BuildSolidGray(byte value, int width = 16, int height = 16)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(value, value, value));
        return bitmap;
    }

    /// <summary>生の BGRA バイト列からビットマップを作る（半透明画素のテスト用。Premul の格納値をそのまま指定できる）。</summary>
    private static SKBitmap BuildFromBgra(int width, int height, byte[] bgra)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        System.Runtime.InteropServices.Marshal.Copy(bgra, 0, bitmap.GetPixels(), bgra.Length);
        return bitmap;
    }

    /// <summary>カラーマトリクスをビットマップに適用した新しいビットマップを作る（ColorTemp の画素確認用）。</summary>
    private static SKBitmap ApplyMatrix(SKBitmap src, float[] matrix)
    {
        var dst = new SKBitmap(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(dst);
        using var filter = SKColorFilter.CreateColorMatrix(matrix);
        using var paint = new SKPaint { ColorFilter = filter };
        canvas.DrawBitmap(src, new SKRect(0, 0, src.Width, src.Height), paint);
        return dst;
    }

    /// <summary>2枚のビットマップで色が違う画素の数。</summary>
    private static int CountDifferentPixels(SKBitmap a, SKBitmap b)
    {
        var count = 0;
        for (var y = 0; y < a.Height; y++)
            for (var x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y))
                    count++;
        return count;
    }

    /// <summary>2枚のビットマップの画素ごとの最大チャンネル差（0〜255）。</summary>
    private static int MaxChannelDifference(SKBitmap a, SKBitmap b)
    {
        var max = 0;
        for (var y = 0; y < a.Height; y++)
            for (var x = 0; x < a.Width; x++)
            {
                var pa = a.GetPixel(x, y);
                var pb = b.GetPixel(x, y);
                max = Math.Max(max, Math.Abs(pa.Red - pb.Red));
                max = Math.Max(max, Math.Abs(pa.Green - pb.Green));
                max = Math.Max(max, Math.Abs(pa.Blue - pb.Blue));
            }
        return max;
    }

    /// <summary>色温度：Amount 0 は単位行列（変化なし）。</summary>
    [Fact]
    public void ColorTemp_ZeroAmount_IsIdentityMatrix()
    {
        var matrix = NewEffectsColor.ColorTempMatrix(new ClipEffect { Kind = EffectKind.ColorTemp, Amount = 0 });
        Assert.Equal(20, matrix.Length);
        for (var row = 0; row < 4; row++)
            for (var col = 0; col < 5; col++)
                Assert.Equal(row == col ? 1f : 0f, matrix[row * 5 + col]);
    }

    /// <summary>色温度：暖色側（+1）で中間グレー（128）の R が上がり B が下がる。</summary>
    [Fact]
    public void ColorTemp_Warm_IncreasesRedDecreasesBlue()
    {
        var matrix = NewEffectsColor.ColorTempMatrix(new ClipEffect { Kind = EffectKind.ColorTemp, Amount = 1 });
        Assert.True(matrix[0] > 1); // R の倍率。
        Assert.True(matrix[12] < 1); // B の倍率。
        using var gray = BuildSolidGray(128);
        using var warmed = ApplyMatrix(gray, matrix);
        var pixel = warmed.GetPixel(8, 8); // 128 * 1.25 = 160、128 * 0.75 = 96。
        Assert.InRange(pixel.Red, 155, 165);
        Assert.InRange(pixel.Green, 126, 130);
        Assert.InRange(pixel.Blue, 91, 101);
    }

    /// <summary>色温度：冷色側（-1）で中間グレー（128）の R が下がり B が上がる。</summary>
    [Fact]
    public void ColorTemp_Cold_DecreasesRedIncreasesBlue()
    {
        var matrix = NewEffectsColor.ColorTempMatrix(new ClipEffect { Kind = EffectKind.ColorTemp, Amount = -1 });
        Assert.True(matrix[0] < 1);
        Assert.True(matrix[12] > 1);
        using var gray = BuildSolidGray(128);
        using var cooled = ApplyMatrix(gray, matrix);
        var pixel = cooled.GetPixel(8, 8); // 128 * 0.75 = 96、128 * 1.25 = 160。
        Assert.InRange(pixel.Red, 91, 101);
        Assert.InRange(pixel.Green, 126, 130);
        Assert.InRange(pixel.Blue, 155, 165);
    }

    /// <summary>色温度：アルファ行は変わらない（0, 0, 0, 1, 0）。</summary>
    [Fact]
    public void ColorTemp_AlphaRowUnchanged()
    {
        var matrix = NewEffectsColor.ColorTempMatrix(new ClipEffect { Kind = EffectKind.ColorTemp, Amount = 1 });
        Assert.Equal(new[] { 0f, 0f, 0f, 1f, 0f }, matrix.AsSpan(15, 5).ToArray());
    }

    /// <summary>色温度：範囲の端（±1）と範囲外（±5）の量でも例外が出ず、行列が有限値になる。</summary>
    [Fact]
    public void ColorTemp_ExtremeAmounts_NoException()
    {
        foreach (var amount in new[] { -1.0, 1.0, -5.0, 5.0, double.NaN })
        {
            var matrix = NewEffectsColor.ColorTempMatrix(new ClipEffect { Kind = EffectKind.ColorTemp, Amount = amount });
            Assert.Equal(20, matrix.Length);
            // NaN はクランプで 0 扱いにはならないため、ここでは有限値であることだけを見る
            // （NaN 入力は UI 側で弾く想定。描画側は例外を出さない）。
            if (!double.IsNaN(amount))
                Assert.All(matrix, value => Assert.True(float.IsFinite(value)));
        }
    }

    /// <summary>レベル補正：既定（黒点 0・白点 1.0・ガンマ 1.0）は恒等（1画素も変わらない）。</summary>
    [Fact]
    public void Levels_Default_IsIdentity()
    {
        using var src = BuildGradientPattern();
        using var dst = NewEffectsColor.ApplyLevels(
            src, new ClipEffect { Kind = EffectKind.Levels, Amount = 0, Radius = 1.0, Midtones = 1.0 });
        Assert.Equal(0, CountDifferentPixels(src, dst));
    }

    /// <summary>
    /// レベル補正：既定に近い値（黒点 0.01・白点 0.99・ガンマ 1.0）では見た目はほぼ変わらない。
    /// ほとんどの画素が ±1〜3 ずれるため「異なる画素数」ではなく最大チャンネル差で見る（≤4）。
    /// </summary>
    [Fact]
    public void Levels_NearDefault_AlmostIdentity()
    {
        using var src = BuildGradientPattern();
        using var dst = NewEffectsColor.ApplyLevels(
            src, new ClipEffect { Kind = EffectKind.Levels, Amount = 0.01, Radius = 0.99, Midtones = 1.0 });
        Assert.True(MaxChannelDifference(src, dst) <= 4);
    }

    /// <summary>レベル補正：黒点 0.2・白点 0.8・ガンマ 1.0 でグラデーションがはっきり変わる。</summary>
    [Fact]
    public void Levels_BlackWhite_ChangesImage()
    {
        using var src = BuildGradientPattern();
        using var dst = NewEffectsColor.ApplyLevels(
            src, new ClipEffect { Kind = EffectKind.Levels, Amount = 0.2, Radius = 0.8, Midtones = 1.0 });
        Assert.True(CountDifferentPixels(src, dst) > Width * Height / 2);
    }

    /// <summary>レベル補正：黒点 0.2 で 32 の暗いグレーは黒（0）に潰れる。</summary>
    [Fact]
    public void Levels_BlackPoint_CrushesShadows()
    {
        using var src = BuildSolidGray(32);
        using var dst = NewEffectsColor.ApplyLevels(
            src, new ClipEffect { Kind = EffectKind.Levels, Amount = 0.2, Radius = 1.0, Midtones = 1.0 });
        Assert.Equal(SKColors.Black, dst.GetPixel(8, 8));
    }

    /// <summary>レベル補正：白点 0.6 で 200 の明るいグレーは白（255）に飛ぶ。</summary>
    [Fact]
    public void Levels_WhitePoint_BlowsHighlights()
    {
        using var src = BuildSolidGray(200);
        using var dst = NewEffectsColor.ApplyLevels(
            src, new ClipEffect { Kind = EffectKind.Levels, Amount = 0, Radius = 0.6, Midtones = 1.0 });
        Assert.Equal(SKColors.White, dst.GetPixel(8, 8));
    }

    /// <summary>レベル補正：ガンマ 0.5（&lt;1）で中間グレー（128）が約181に明るくなる。</summary>
    [Fact]
    public void Levels_GammaBelowOne_Brightens()
    {
        using var src = BuildSolidGray(128);
        using var dst = NewEffectsColor.ApplyLevels(
            src, new ClipEffect { Kind = EffectKind.Levels, Amount = 0, Radius = 1.0, Midtones = 0.5 });
        var pixel = dst.GetPixel(8, 8);
        Assert.InRange(pixel.Red, 177, 185);
        Assert.Equal(pixel.Red, pixel.Green);
        Assert.Equal(pixel.Red, pixel.Blue);
    }

    /// <summary>レベル補正：ガンマ 2.0（&gt;1）で中間グレー（128）が約64に暗くなる。</summary>
    [Fact]
    public void Levels_GammaAboveOne_Darkens()
    {
        using var src = BuildSolidGray(128);
        using var dst = NewEffectsColor.ApplyLevels(
            src, new ClipEffect { Kind = EffectKind.Levels, Amount = 0, Radius = 1.0, Midtones = 2.0 });
        var pixel = dst.GetPixel(8, 8);
        Assert.InRange(pixel.Red, 60, 68);
    }

    /// <summary>
    /// レベル補正：黒点・白点の逆転（黒点 0.5・白点 0.5）でも例外が出ず破綻しない。
    /// 白点は黒点より少しだけ大きく補正される（0.51）ため、中間グレー（128）は
    /// (128/255 - 0.5) / 0.01 ≒ 0.2 → 約 50 になる。
    /// </summary>
    [Fact]
    public void Levels_InvertedBlackWhite_DoesNotBreak()
    {
        using var src = BuildSolidGray(128);
        using var dst = NewEffectsColor.ApplyLevels(
            src, new ClipEffect { Kind = EffectKind.Levels, Amount = 0.5, Radius = 0.5, Midtones = 1.0 });
        var pixel = dst.GetPixel(8, 8);
        Assert.InRange(pixel.Red, 40, 60);
        Assert.Equal(pixel.Red, pixel.Green);
        Assert.Equal(pixel.Red, pixel.Blue);
    }

    /// <summary>レベル補正：ガンマの端（0.2・5.0）でも例外が出ず、値が 0〜255 に収まる。</summary>
    [Fact]
    public void Levels_ExtremeGamma_NoException()
    {
        using var src = BuildGradientPattern();
        foreach (var gamma in new[] { 0.2, 5.0 })
        {
            using var dst = NewEffectsColor.ApplyLevels(
                src, new ClipEffect { Kind = EffectKind.Levels, Amount = 0, Radius = 1.0, Midtones = gamma });
            Assert.Equal(Width, dst.Width);
            Assert.Equal(Height, dst.Height);
        }
    }

    /// <summary>レベル補正：1x1・2x2 の小さいビットマップでも例外が出ない。</summary>
    [Fact]
    public void Levels_SmallBitmaps_NoException()
    {
        using var one = BuildSolidGray(128, 1, 1);
        using var oneOut = NewEffectsColor.ApplyLevels(
            one, new ClipEffect { Kind = EffectKind.Levels, Amount = 0.2, Radius = 0.8, Midtones = 0.5 });
        Assert.Equal(1, oneOut.Width);
        using var two = BuildSolidGray(200, 2, 2);
        using var twoOut = NewEffectsColor.ApplyLevels(
            two, new ClipEffect { Kind = EffectKind.Levels, Amount = 0.2, Radius = 0.8, Midtones = 0.5 });
        Assert.Equal(2, twoOut.Width);
    }

    /// <summary>スプリットトーン：既定（ハイライト45°・シャドウ220°・強さ0.5）でグラデーションがはっきり変わる。</summary>
    [Fact]
    public void SplitTone_Default_ChangesImage()
    {
        using var src = BuildGradientPattern();
        using var dst = NewEffectsColor.ApplySplitTone(
            src, new ClipEffect { Kind = EffectKind.SplitTone, Amount = 45, Radius = 220, Midtones = 0.5 });
        Assert.True(CountDifferentPixels(src, dst) > Width * Height / 2);
    }

    /// <summary>スプリットトーン：強さ 0 で変化なし（1画素も変わらない）。</summary>
    [Fact]
    public void SplitTone_ZeroStrength_IsIdentity()
    {
        using var src = BuildGradientPattern();
        using var dst = NewEffectsColor.ApplySplitTone(
            src, new ClipEffect { Kind = EffectKind.SplitTone, Amount = 45, Radius = 220, Midtones = 0 });
        Assert.Equal(0, CountDifferentPixels(src, dst));
    }

    /// <summary>スプリットトーン：暗いグレー（32）はシャドウ色（220°＝青）に寄り、B が R より大きくなる。</summary>
    [Fact]
    public void SplitTone_ShadowsTintedTowardShadowHue()
    {
        using var src = BuildSolidGray(32);
        using var dst = NewEffectsColor.ApplySplitTone(
            src, new ClipEffect { Kind = EffectKind.SplitTone, Amount = 45, Radius = 220, Midtones = 0.5 });
        var pixel = dst.GetPixel(8, 8);
        Assert.True(pixel.Blue > pixel.Red + 20);
    }

    /// <summary>スプリットトーン：明るいグレー（224）はハイライト色（45°＝橙）に寄り、R が B より大きくなる。</summary>
    [Fact]
    public void SplitTone_HighlightsTintedTowardHighlightHue()
    {
        using var src = BuildSolidGray(224);
        using var dst = NewEffectsColor.ApplySplitTone(
            src, new ClipEffect { Kind = EffectKind.SplitTone, Amount = 45, Radius = 220, Midtones = 0.5 });
        var pixel = dst.GetPixel(8, 8);
        Assert.True(pixel.Red > pixel.Blue + 20);
    }

    /// <summary>スプリットトーン：中間グレー（128）はほぼ元の色（各チャンネル ±1 以内）。</summary>
    [Fact]
    public void SplitTone_MidtoneNearlyUnchanged()
    {
        using var src = BuildSolidGray(128);
        using var dst = NewEffectsColor.ApplySplitTone(
            src, new ClipEffect { Kind = EffectKind.SplitTone, Amount = 45, Radius = 220, Midtones = 0.5 });
        var pixel = dst.GetPixel(8, 8);
        Assert.InRange(pixel.Red, 127, 129);
        Assert.InRange(pixel.Green, 127, 129);
        Assert.InRange(pixel.Blue, 127, 129);
    }

    /// <summary>スプリットトーン：色相の端（0°・360°）と強さ 1 でも例外が出ない。1x1 でも例外が出ない。</summary>
    [Fact]
    public void SplitTone_ExtremeAndSmall_NoException()
    {
        using var src = BuildGradientPattern();
        using var dst = NewEffectsColor.ApplySplitTone(
            src, new ClipEffect { Kind = EffectKind.SplitTone, Amount = 0, Radius = 360, Midtones = 1 });
        Assert.Equal(Width, dst.Width);
        using var one = BuildSolidGray(128, 1, 1);
        using var oneOut = NewEffectsColor.ApplySplitTone(
            one, new ClipEffect { Kind = EffectKind.SplitTone, Amount = 45, Radius = 220, Midtones = 0.5 });
        Assert.Equal(1, oneOut.Width);
    }

    /// <summary>グラデーションマップ：既定（暗部220°＝青・明部45°＝橙）でグラデーションがほぼ全面変わる。</summary>
    [Fact]
    public void GradientMap_Default_ChangesImage()
    {
        using var src = BuildGradientPattern();
        using var dst = NewEffectsColor.ApplyGradientMap(
            src, new ClipEffect { Kind = EffectKind.GradientMap, Amount = 220, Radius = 45 });
        Assert.True(CountDifferentPixels(src, dst) > Width * Height * 9 / 10);
    }

    /// <summary>グラデーションマップ：真っ黒は暗部色（220°・彩度0.6・明度0.6 → 約 (61, 92, 153)）になる。</summary>
    [Fact]
    public void GradientMap_BlackBecomesShadowColor()
    {
        using var src = BuildSolidGray(0);
        using var dst = NewEffectsColor.ApplyGradientMap(
            src, new ClipEffect { Kind = EffectKind.GradientMap, Amount = 220, Radius = 45 });
        var pixel = dst.GetPixel(8, 8);
        Assert.InRange(pixel.Red, 58, 64);
        Assert.InRange(pixel.Green, 89, 95);
        Assert.InRange(pixel.Blue, 150, 156);
    }

    /// <summary>グラデーションマップ：真っ白は明部色（45°・彩度0.6・明度0.6 → 約 (153, 130, 61)）になる。</summary>
    [Fact]
    public void GradientMap_WhiteBecomesHighlightColor()
    {
        using var src = BuildSolidGray(255);
        using var dst = NewEffectsColor.ApplyGradientMap(
            src, new ClipEffect { Kind = EffectKind.GradientMap, Amount = 220, Radius = 45 });
        var pixel = dst.GetPixel(8, 8);
        Assert.InRange(pixel.Red, 150, 156);
        Assert.InRange(pixel.Green, 127, 133);
        Assert.InRange(pixel.Blue, 58, 64);
    }

    /// <summary>グラデーションマップ：中間グレー（128）は暗部色と明部色の中間（約 (107, 111, 107)）になる。</summary>
    [Fact]
    public void GradientMap_MidGrayInterpolates()
    {
        using var src = BuildSolidGray(128);
        using var dst = NewEffectsColor.ApplyGradientMap(
            src, new ClipEffect { Kind = EffectKind.GradientMap, Amount = 220, Radius = 45 });
        var pixel = dst.GetPixel(8, 8);
        Assert.InRange(pixel.Red, 104, 110);
        Assert.InRange(pixel.Green, 108, 114);
        Assert.InRange(pixel.Blue, 104, 110);
    }

    /// <summary>グラデーションマップ：色相の端（0°・360°）でも例外が出ない。1x1・2x2 でも例外が出ない。</summary>
    [Fact]
    public void GradientMap_ExtremeAndSmall_NoException()
    {
        using var src = BuildGradientPattern();
        using var dst = NewEffectsColor.ApplyGradientMap(
            src, new ClipEffect { Kind = EffectKind.GradientMap, Amount = 0, Radius = 360 });
        Assert.Equal(Width, dst.Width);
        using var one = BuildSolidGray(128, 1, 1);
        using var oneOut = NewEffectsColor.ApplyGradientMap(
            one, new ClipEffect { Kind = EffectKind.GradientMap, Amount = 220, Radius = 45 });
        Assert.Equal(1, oneOut.Width);
        using var two = BuildSolidGray(200, 2, 2);
        using var twoOut = NewEffectsColor.ApplyGradientMap(
            two, new ClipEffect { Kind = EffectKind.GradientMap, Amount = 220, Radius = 45 });
        Assert.Equal(2, twoOut.Width);
    }

    /// <summary>3つの画素エフェクトはいずれも入力ビットマップを変更しない（新しいビットマップを返す）。</summary>
    [Fact]
    public void BitmapEffects_DoNotModifySource()
    {
        using var src = BuildGradientPattern();
        using var before = BuildGradientPattern();
        using var levels = NewEffectsColor.ApplyLevels(
            src, new ClipEffect { Kind = EffectKind.Levels, Amount = 0.2, Radius = 0.8, Midtones = 0.5 });
        using var split = NewEffectsColor.ApplySplitTone(
            src, new ClipEffect { Kind = EffectKind.SplitTone, Amount = 45, Radius = 220, Midtones = 0.5 });
        using var gradient = NewEffectsColor.ApplyGradientMap(
            src, new ClipEffect { Kind = EffectKind.GradientMap, Amount = 220, Radius = 45 });
        Assert.Equal(0, CountDifferentPixels(src, before));
    }

    /// <summary>
    /// 半透明画素（Premul 格納値 B=G=R=128, A=128）にグラデーションマップをかけても、
    /// アルファは 128 のままで RGB がアルファを超えない（Premul が壊れない）。
    /// RGB ≤ A の判定は生バイトで確認する（GetPixel は非乗算に戻すため使えない）。
    /// </summary>
    [Fact]
    public void GradientMap_PreservesAlphaAndPremul()
    {
        // 2x2、左上だけ半透明グレー (128,128,128,128)、他は不透明黒。
        var bgra = new byte[2 * 2 * 4];
        bgra[0] = 128; bgra[1] = 128; bgra[2] = 128; bgra[3] = 128;
        using var src = BuildFromBgra(2, 2, bgra);
        using var dst = NewEffectsColor.ApplyGradientMap(
            src, new ClipEffect { Kind = EffectKind.GradientMap, Amount = 220, Radius = 45 });
        var raw = new byte[2 * 2 * 4];
        System.Runtime.InteropServices.Marshal.Copy(dst.GetPixels(), raw, 0, raw.Length);
        Assert.Equal(128, raw[3]);
        Assert.True(raw[0] <= raw[3] && raw[1] <= raw[3] && raw[2] <= raw[3]);
        var pixel = dst.GetPixel(0, 0);
        Assert.True(pixel.Red > 0 && pixel.Blue > 0); // 暗部色と明部色の中間色が入る。
    }

    /// <summary>半透明画素にスプリットトーンをかけてもアルファは変わらず RGB がアルファを超えない（生バイトで確認。GetPixel は非乗算に戻すためこの判定には使えない）。</summary>
    [Fact]
    public void SplitTone_PreservesAlphaAndPremul()
    {
        var bgra = new byte[2 * 2 * 4];
        bgra[0] = 128; bgra[1] = 128; bgra[2] = 128; bgra[3] = 128;
        using var src = BuildFromBgra(2, 2, bgra);
        using var dst = NewEffectsColor.ApplySplitTone(
            src, new ClipEffect { Kind = EffectKind.SplitTone, Amount = 45, Radius = 220, Midtones = 1 });
        var raw = new byte[2 * 2 * 4];
        System.Runtime.InteropServices.Marshal.Copy(dst.GetPixels(), raw, 0, raw.Length);
        Assert.Equal(128, raw[3]);
        Assert.True(raw[0] <= raw[3] && raw[1] <= raw[3] && raw[2] <= raw[3]);
    }
}
