// 新エフェクト5種（ブルーム／ライトリーク／オールドフィルム／レターボックス／
// スキャンライン）の描画テスト：NewEffectsLight のヘルパーを直接呼ぶ
// （FrameRenderer への配線はまだ無いため）。実ファイル・FFmpeg には依存しない。
// 画質の厳密な判定ではなく、「効いている／壊れていない／無効な量では変わらない」
// の確認が目的。NewEffectsRenderTests.cs のパターン作りの流儀に倣う。

using Editor.Core;
using Editor.Render;
using SkiaSharp;
using Xunit;

namespace Editor.Tests;

/// <summary>新エフェクト5種の描画テスト（NewEffectsLight 直接呼び）。</summary>
public sealed class NewEffectsLightTests
{
    private const int Width = 160;
    private const int Height = 90;

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

    /// <summary>一様な中間グレー（128）のフレーム。</summary>
    private static SKBitmap BuildGrayPattern()
    {
        var bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(128, 128, 128));
        return bitmap;
    }

    /// <summary>2枚のビットマップで違う画素の数。</summary>
    private static int CountDiff(SKBitmap a, SKBitmap b)
    {
        var count = 0;
        for (var y = 0; y < a.Height; y++)
            for (var x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y))
                    count++;
        return count;
    }

    /// <summary>2枚のビットマップが全画素一致すること。</summary>
    private static bool PixelsEqual(SKBitmap a, SKBitmap b) => CountDiff(a, b) == 0;

    /// <summary>入力が変更されていないこと（ヘルパーは src を変更せず新しいビットマップを返す契約）。</summary>
    private static void AssertSrcUnchanged(SKBitmap before, SKBitmap src)
        => Assert.True(PixelsEqual(before, src));

    // ---- ブルーム ----

    /// <summary>ブルーム：既定パラメータで円の縁の外側ににじみが出る。</summary>
    [Fact]
    public void Bloom_BleedsBrightCircleOutward()
    {
        using var src = BuildCirclePattern();
        using var before = src.Copy();
        using var bloomed = NewEffectsLight.ApplyBloom(src, new ClipEffect { Kind = EffectKind.Bloom, Amount = 0.5, Radius = 16 });
        Assert.Equal(SKColors.Black, src.GetPixel(103, 68));
        Assert.True(bloomed.GetPixel(103, 68).Red > 8);
        Assert.True(CountDiff(src, bloomed) >= 100);
        AssertSrcUnchanged(before, src);
        Assert.NotSame(src, bloomed);
    }

    /// <summary>ブルーム：Amount 0 で変化なし（コピーを返す）。</summary>
    [Fact]
    public void Bloom_AmountZeroIsIdentity()
    {
        using var src = BuildCirclePattern();
        using var bloomed = NewEffectsLight.ApplyBloom(src, new ClipEffect { Kind = EffectKind.Bloom, Amount = 0, Radius = 16 });
        Assert.True(PixelsEqual(src, bloomed));
        Assert.NotSame(src, bloomed);
    }

    /// <summary>ブルーム：極端なパラメータでも例外が出ず、範囲の端に丸められる（Amount 5 は 1 と同じ）。</summary>
    [Fact]
    public void Bloom_ExtremeParametersAreClamped()
    {
        using var src = BuildCirclePattern();
        using var over = NewEffectsLight.ApplyBloom(src, new ClipEffect { Kind = EffectKind.Bloom, Amount = 5, Radius = 10000 });
        using var max = NewEffectsLight.ApplyBloom(src, new ClipEffect { Kind = EffectKind.Bloom, Amount = 1, Radius = 100 });
        Assert.Equal(src.Width, over.Width);
        Assert.Equal(src.Height, over.Height);
        Assert.True(PixelsEqual(over, max));
        using var under = NewEffectsLight.ApplyBloom(src, new ClipEffect { Kind = EffectKind.Bloom, Amount = -5, Radius = -100 });
        Assert.True(PixelsEqual(src, under));
    }

    /// <summary>ブルーム：小さいビットマップでも例外が出ない。</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public void Bloom_TinyBitmapDoesNotThrow(int width, int height)
    {
        using var src = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(src);
        canvas.Clear(SKColors.White);
        using var bloomed = NewEffectsLight.ApplyBloom(src, new ClipEffect { Kind = EffectKind.Bloom, Amount = 0.5, Radius = 16 });
        Assert.Equal(width, bloomed.Width);
        Assert.Equal(height, bloomed.Height);
    }

    // ---- ライトリーク ----

    /// <summary>ライトリーク：既定パラメータで左上が暖かく明るくなる。</summary>
    [Fact]
    public void LightLeak_WarmsTopLeftCorner()
    {
        using var src = BuildCirclePattern();
        using var before = src.Copy();
        using var leaked = NewEffectsLight.ApplyLightLeak(src, new ClipEffect { Kind = EffectKind.LightLeak, Amount = 0.5, Radius = 1.0 });
        var corner = leaked.GetPixel(0, 0);
        Assert.True(corner.Red > 30);
        Assert.True(corner.Red > corner.Blue); // 暖かい色かぶり（赤が青より強い）。
        Assert.True(CountDiff(src, leaked) >= 100);
        AssertSrcUnchanged(before, src);
        Assert.NotSame(src, leaked);
    }

    /// <summary>ライトリーク：光の届かない右下は変わらない。</summary>
    [Fact]
    public void LightLeak_FarCornerUnchanged()
    {
        using var src = BuildGradientPattern();
        using var leaked = NewEffectsLight.ApplyLightLeak(src, new ClipEffect { Kind = EffectKind.LightLeak, Amount = 0.5, Radius = 1.0 });
        Assert.Equal(src.GetPixel(Width - 1, Height - 1), leaked.GetPixel(Width - 1, Height - 1));
    }

    /// <summary>ライトリーク：Amount 0 で変化なし（コピーを返す）。</summary>
    [Fact]
    public void LightLeak_AmountZeroIsIdentity()
    {
        using var src = BuildGrayPattern();
        using var leaked = NewEffectsLight.ApplyLightLeak(src, new ClipEffect { Kind = EffectKind.LightLeak, Amount = 0, Radius = 1.0 });
        Assert.True(PixelsEqual(src, leaked));
        Assert.NotSame(src, leaked);
    }

    /// <summary>ライトリーク：極端なパラメータでも例外が出ず、範囲の端に丸められる（Amount 5 は 1 と同じ）。</summary>
    [Fact]
    public void LightLeak_ExtremeParametersAreClamped()
    {
        using var src = BuildGrayPattern();
        using var over = NewEffectsLight.ApplyLightLeak(src, new ClipEffect { Kind = EffectKind.LightLeak, Amount = 5, Radius = 100 });
        using var max = NewEffectsLight.ApplyLightLeak(src, new ClipEffect { Kind = EffectKind.LightLeak, Amount = 1, Radius = 2.0 });
        Assert.Equal(src.Width, over.Width);
        Assert.Equal(src.Height, over.Height);
        Assert.True(PixelsEqual(over, max));
        using var under = NewEffectsLight.ApplyLightLeak(src, new ClipEffect { Kind = EffectKind.LightLeak, Amount = -5, Radius = -100 });
        Assert.True(PixelsEqual(src, under));
    }

    /// <summary>ライトリーク：小さいビットマップでも例外が出ない。</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public void LightLeak_TinyBitmapDoesNotThrow(int width, int height)
    {
        using var src = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(src);
        canvas.Clear(new SKColor(128, 128, 128));
        using var leaked = NewEffectsLight.ApplyLightLeak(src, new ClipEffect { Kind = EffectKind.LightLeak, Amount = 0.5, Radius = 1.0 });
        Assert.Equal(width, leaked.Width);
        Assert.Equal(height, leaked.Height);
    }

    // ---- オールドフィルム ----

    /// <summary>オールドフィルム：既定パラメータで傷・ほこり・ちらつきにより見た目が変わる。</summary>
    [Fact]
    public void OldFilm_AddsScratchesAndDust()
    {
        using var src = BuildCirclePattern();
        using var before = src.Copy();
        using var aged = NewEffectsLight.ApplyOldFilm(src, new ClipEffect { Kind = EffectKind.OldFilm, Amount = 0.5, Radius = 0.5 }, 0.5);
        Assert.True(CountDiff(src, aged) >= 50);
        AssertSrcUnchanged(before, src);
        Assert.NotSame(src, aged);
    }

    /// <summary>オールドフィルム：Amount 0 で変化なし（Radius が大きくても劣化しない）。</summary>
    [Fact]
    public void OldFilm_AmountZeroIsIdentity()
    {
        using var src = BuildCirclePattern();
        using var aged = NewEffectsLight.ApplyOldFilm(src, new ClipEffect { Kind = EffectKind.OldFilm, Amount = 0, Radius = 1 }, 0.5);
        Assert.True(PixelsEqual(src, aged));
        Assert.NotSame(src, aged);
    }

    /// <summary>オールドフィルム：同じ時刻なら同じ傷になる（決定性）。</summary>
    [Fact]
    public void OldFilm_SameTimeGivesSameScratches()
    {
        using var src = BuildCirclePattern();
        var effect = new ClipEffect { Kind = EffectKind.OldFilm, Amount = 0.5, Radius = 0.5 };
        using var first = NewEffectsLight.ApplyOldFilm(src, effect, 0.5);
        using var second = NewEffectsLight.ApplyOldFilm(src, effect, 0.5);
        Assert.True(PixelsEqual(first, second));
    }

    /// <summary>オールドフィルム：時刻が変わると傷・ちらつきが変わる。</summary>
    [Fact]
    public void OldFilm_DifferentTimeGivesDifferentScratches()
    {
        using var src = BuildCirclePattern();
        var effect = new ClipEffect { Kind = EffectKind.OldFilm, Amount = 0.5, Radius = 0.5 };
        using var atHalf = NewEffectsLight.ApplyOldFilm(src, effect, 0.5);
        using var atSixTenths = NewEffectsLight.ApplyOldFilm(src, effect, 0.6);
        Assert.False(PixelsEqual(atHalf, atSixTenths));
    }

    /// <summary>オールドフィルム：傷の密度 0 でもちらつきは付く（Amount だけに依存する部分）。</summary>
    [Fact]
    public void OldFilm_ZeroDensityStillFlickers()
    {
        // ちらつき係数は時刻のハッシュで決まる。時刻 0.5・Amount 1 での係数は約 0.988 のため
        // 一様グレー 128 は 126 になり、差分が出る（ハッシュ値を別途計算して確認済み）。
        using var src = BuildGrayPattern();
        using var aged = NewEffectsLight.ApplyOldFilm(src, new ClipEffect { Kind = EffectKind.OldFilm, Amount = 1, Radius = 0 }, 0.5);
        Assert.True(CountDiff(src, aged) > 0);
    }

    /// <summary>オールドフィルム：極端なパラメータでも例外が出ず、範囲の端に丸められる（Amount 5 は 1 と同じ）。</summary>
    [Fact]
    public void OldFilm_ExtremeParametersAreClamped()
    {
        using var src = BuildCirclePattern();
        using var over = NewEffectsLight.ApplyOldFilm(src, new ClipEffect { Kind = EffectKind.OldFilm, Amount = 5, Radius = 100 }, 0.5);
        using var max = NewEffectsLight.ApplyOldFilm(src, new ClipEffect { Kind = EffectKind.OldFilm, Amount = 1, Radius = 1 }, 0.5);
        Assert.Equal(src.Width, over.Width);
        Assert.Equal(src.Height, over.Height);
        Assert.True(PixelsEqual(over, max));
        using var under = NewEffectsLight.ApplyOldFilm(src, new ClipEffect { Kind = EffectKind.OldFilm, Amount = -5, Radius = -100 }, 0.5);
        Assert.True(PixelsEqual(src, under));
    }

    /// <summary>オールドフィルム：小さいビットマップでも例外が出ない。</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public void OldFilm_TinyBitmapDoesNotThrow(int width, int height)
    {
        using var src = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(src);
        canvas.Clear(new SKColor(128, 128, 128));
        using var aged = NewEffectsLight.ApplyOldFilm(src, new ClipEffect { Kind = EffectKind.OldFilm, Amount = 0.5, Radius = 0.5 }, 0.5);
        Assert.Equal(width, aged.Width);
        Assert.Equal(height, aged.Height);
    }

    // ---- レターボックス ----

    /// <summary>レターボックス：既定パラメータ（0.12）で上下に黒帯が付く。</summary>
    [Fact]
    public void Letterbox_AddsBlackBars()
    {
        using var src = BuildGradientPattern();
        using var before = src.Copy();
        using var boxed = NewEffectsLight.ApplyLetterbox(src, new ClipEffect { Kind = EffectKind.Letterbox, Amount = 0.12 });
        // 高さ 90 × 0.12 = 10.8 → 11px の帯。
        Assert.Equal(SKColors.Black, boxed.GetPixel(80, 0));
        Assert.Equal(SKColors.Black, boxed.GetPixel(80, 10));
        Assert.Equal(SKColors.Black, boxed.GetPixel(80, Height - 1));
        Assert.Equal(src.GetPixel(80, 45), boxed.GetPixel(80, 45)); // 中央は変わらない。
        Assert.Equal(src.GetPixel(80, 11), boxed.GetPixel(80, 11)); // 帯のすぐ下は変わらない。
        Assert.True(CountDiff(src, boxed) >= 1000);
        AssertSrcUnchanged(before, src);
        Assert.NotSame(src, boxed);
    }

    /// <summary>レターボックス：Amount 0 で変化なし（コピーを返す）。</summary>
    [Fact]
    public void Letterbox_AmountZeroIsIdentity()
    {
        using var src = BuildGradientPattern();
        using var boxed = NewEffectsLight.ApplyLetterbox(src, new ClipEffect { Kind = EffectKind.Letterbox, Amount = 0 });
        Assert.True(PixelsEqual(src, boxed));
        Assert.NotSame(src, boxed);
    }

    /// <summary>レターボックス：極端なパラメータでも例外が出ず、0.25 に丸められる（Amount 1 は 0.25 と同じ）。</summary>
    [Fact]
    public void Letterbox_ExtremeParametersAreClamped()
    {
        using var src = BuildGradientPattern();
        using var over = NewEffectsLight.ApplyLetterbox(src, new ClipEffect { Kind = EffectKind.Letterbox, Amount = 1 });
        using var max = NewEffectsLight.ApplyLetterbox(src, new ClipEffect { Kind = EffectKind.Letterbox, Amount = 0.25 });
        Assert.Equal(src.Width, over.Width);
        Assert.Equal(src.Height, over.Height);
        Assert.True(PixelsEqual(over, max));
        // 0.25 の帯（22px）：上端 22 行が黒く、中央は残る。
        Assert.Equal(SKColors.Black, over.GetPixel(80, 21));
        Assert.Equal(src.GetPixel(80, 45), over.GetPixel(80, 45));
        using var under = NewEffectsLight.ApplyLetterbox(src, new ClipEffect { Kind = EffectKind.Letterbox, Amount = -1 });
        Assert.True(PixelsEqual(src, under));
    }

    /// <summary>レターボックス：小さいビットマップでも例外が出ない。</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public void Letterbox_TinyBitmapDoesNotThrow(int width, int height)
    {
        using var src = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(src);
        canvas.Clear(SKColors.White);
        using var boxed = NewEffectsLight.ApplyLetterbox(src, new ClipEffect { Kind = EffectKind.Letterbox, Amount = 0.12 });
        Assert.Equal(width, boxed.Width);
        Assert.Equal(height, boxed.Height);
    }

    // ---- スキャンライン ----

    /// <summary>スキャンライン：既定パラメータ（濃さ 0.5、間隔 3）で横線が暗くなる。</summary>
    [Fact]
    public void Scanlines_DarkenEveryThirdRow()
    {
        using var src = BuildCirclePattern();
        using var before = src.Copy();
        using var scanned = NewEffectsLight.ApplyScanlines(src, new ClipEffect { Kind = EffectKind.Scanlines, Amount = 0.5, Radius = 3 });
        // 線の行（y=17 は 17%3==2）：円の内側の白が半分ほどに暗くなる。
        var line = scanned.GetPixel(80, 17);
        Assert.InRange(line.Red, 100, 160);
        // 線でない行（y=16,18）は円の内側でも変わらない。
        Assert.Equal(src.GetPixel(80, 16), scanned.GetPixel(80, 16));
        Assert.Equal(src.GetPixel(80, 18), scanned.GetPixel(80, 18));
        Assert.True(CountDiff(src, scanned) >= 100);
        AssertSrcUnchanged(before, src);
        Assert.NotSame(src, scanned);
    }

    /// <summary>スキャンライン：濃さ 1 で線が真っ黒になる。</summary>
    [Fact]
    public void Scanlines_FullAmountMakesBlackLines()
    {
        using var src = BuildCirclePattern();
        using var scanned = NewEffectsLight.ApplyScanlines(src, new ClipEffect { Kind = EffectKind.Scanlines, Amount = 1, Radius = 3 });
        Assert.Equal(SKColors.Black, scanned.GetPixel(80, 17));
        Assert.Equal(src.GetPixel(80, 16), scanned.GetPixel(80, 16));
    }

    /// <summary>スキャンライン：Amount 0 で変化なし（コピーを返す）。</summary>
    [Fact]
    public void Scanlines_AmountZeroIsIdentity()
    {
        using var src = BuildCirclePattern();
        using var scanned = NewEffectsLight.ApplyScanlines(src, new ClipEffect { Kind = EffectKind.Scanlines, Amount = 0, Radius = 3 });
        Assert.True(PixelsEqual(src, scanned));
        Assert.NotSame(src, scanned);
    }

    /// <summary>スキャンライン：極端なパラメータでも例外が出ず、範囲の端に丸められる（Amount 5 は 1 と同じ、間隔 100 は 8 と同じ）。</summary>
    [Fact]
    public void Scanlines_ExtremeParametersAreClamped()
    {
        using var src = BuildCirclePattern();
        using var over = NewEffectsLight.ApplyScanlines(src, new ClipEffect { Kind = EffectKind.Scanlines, Amount = 5, Radius = 100 });
        using var max = NewEffectsLight.ApplyScanlines(src, new ClipEffect { Kind = EffectKind.Scanlines, Amount = 1, Radius = 8 });
        Assert.Equal(src.Width, over.Width);
        Assert.Equal(src.Height, over.Height);
        Assert.True(PixelsEqual(over, max));
        using var under = NewEffectsLight.ApplyScanlines(src, new ClipEffect { Kind = EffectKind.Scanlines, Amount = -5, Radius = -100 });
        Assert.True(PixelsEqual(src, under));
    }

    /// <summary>スキャンライン：小さいビットマップでも例外が出ない。</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public void Scanlines_TinyBitmapDoesNotThrow(int width, int height)
    {
        using var src = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(src);
        canvas.Clear(SKColors.White);
        using var scanned = NewEffectsLight.ApplyScanlines(src, new ClipEffect { Kind = EffectKind.Scanlines, Amount = 0.5, Radius = 3 });
        Assert.Equal(width, scanned.Width);
        Assert.Equal(height, scanned.Height);
    }

    // ---- 横断 ----

    /// <summary>5種すべてが src を変更しない（入力の不変性は全エフェクト共通の契約）。</summary>
    [Fact]
    public void AllEffects_LeaveSourceUnchanged()
    {
        using var src = BuildCirclePattern();
        using var before = src.Copy();
        using var bloom = NewEffectsLight.ApplyBloom(src, new ClipEffect { Kind = EffectKind.Bloom, Amount = 0.5, Radius = 16 });
        using var leak = NewEffectsLight.ApplyLightLeak(src, new ClipEffect { Kind = EffectKind.LightLeak, Amount = 0.5, Radius = 1.0 });
        using var aged = NewEffectsLight.ApplyOldFilm(src, new ClipEffect { Kind = EffectKind.OldFilm, Amount = 0.5, Radius = 0.5 }, 0.5);
        using var boxed = NewEffectsLight.ApplyLetterbox(src, new ClipEffect { Kind = EffectKind.Letterbox, Amount = 0.12 });
        using var scanned = NewEffectsLight.ApplyScanlines(src, new ClipEffect { Kind = EffectKind.Scanlines, Amount = 0.5, Radius = 3 });
        AssertSrcUnchanged(before, src);
    }
}
