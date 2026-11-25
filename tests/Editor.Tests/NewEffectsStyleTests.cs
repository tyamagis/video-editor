// 新エフェクト5種（EdgeDetect / Solarize / Kuwahara / TiltShift / Halftone）の描画テスト。
// 描画ヘルパー（NewEffectsStyle）を直接呼ぶ（FrameRenderer への配線はまだ無いため）。
// 実ファイル・FFmpeg には依存しない。画質の厳密な判定ではなく、
// 「効いている／壊れていない／無効な量では変わらない」の確認が目的。
// パターン作りは NewEffectsRenderTests.cs の流儀（黒背景＋白い円、横グラデーションなど）に倣う。

using System.Runtime.InteropServices;
using Editor.Core;
using Editor.Render;
using SkiaSharp;
using Xunit;

namespace Editor.Tests;

/// <summary>新エフェクト5種の描画テスト（NewEffectsStyle の直接呼び出し）。</summary>
public sealed class NewEffectsStyleTests
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

    /// <summary>黒白のチェッカー（1マス 8px）。画面全体に細かい模様があるため、ぼかし・油絵風の変化が見えやすい。</summary>
    private static SKBitmap BuildCheckerPattern(int cell = 8)
    {
        var bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        for (var y = 0; y < Height; y += cell)
            for (var x = 0; x < Width; x += cell)
            {
                var white = (x / cell + y / cell) % 2 == 0;
                using var paint = new SKPaint { Color = white ? SKColors.White : SKColors.Black };
                canvas.DrawRect(x, y, cell, cell, paint);
            }
        return bitmap;
    }

    /// <summary>左半分が黒・右半分が白（x=幅/2 で段差）。エッジ検出・ハーフトーンの境界用。</summary>
    private static SKBitmap BuildSplitPattern(int width = Width, int height = Height)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        using var paint = new SKPaint { Color = SKColors.White };
        canvas.DrawRect(width / 2, 0, width - width / 2, height, paint);
        return bitmap;
    }

    /// <summary>一色の平坦な画像（不透明度も指定できる）。</summary>
    private static SKBitmap BuildFlatPattern(byte value, byte alpha = 255, int width = Width, int height = Height)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(value, value, value, alpha));
        return bitmap;
    }

    /// <summary>透明背景に半透明（不透明度 128）の白い円と、不透明な赤い四角。Premul・透明背景の確認用。</summary>
    private static SKBitmap BuildTransparentPattern()
    {
        var bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = new SKColor(255, 255, 255, 128) };
        canvas.DrawCircle(Width / 2, Height / 2, 30, paint);
        using var red = new SKPaint { Color = new SKColor(255, 0, 0, 255) };
        canvas.DrawRect(10, 10, 20, 20, red);
        return bitmap;
    }

    /// <summary>2枚のビットマップで画素が違う数を数える（GetPixel の比較）。</summary>
    private static int CountDiff(SKBitmap a, SKBitmap b)
    {
        var count = 0;
        for (var y = 0; y < a.Height; y++)
            for (var x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y))
                    count++;
        return count;
    }

    /// <summary>指定した行の範囲（y0〜y1 を含む）で画素が違う数を数える。</summary>
    private static int CountDiffInRows(SKBitmap a, SKBitmap b, int y0, int y1)
    {
        var count = 0;
        for (var y = y0; y <= y1; y++)
            for (var x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y))
                    count++;
        return count;
    }

    /// <summary>生の BGRA バイトで Premul の不変条件（RGB ≤ アルファ）が全画素で成り立つこと。</summary>
    private static void AssertPremulValid(SKBitmap bitmap)
    {
        var bytes = new byte[bitmap.RowBytes * bitmap.Height];
        Marshal.Copy(bitmap.GetPixels(), bytes, 0, bytes.Length);
        for (var i = 0; i < bytes.Length; i += 4)
        {
            var a = bytes[i + 3];
            Assert.True(bytes[i] <= a, $"B {bytes[i]} > A {a} at byte {i}");
            Assert.True(bytes[i + 1] <= a, $"G {bytes[i + 1]} > A {a} at byte {i}");
            Assert.True(bytes[i + 2] <= a, $"R {bytes[i + 2]} > A {a} at byte {i}");
        }
    }

    /// <summary>1x1・2x2 の小さいビットマップでも例外が出ず、サイズが保たれること。</summary>
    private static void AssertTinyBitmapsWork(Func<SKBitmap, ClipEffect, SKBitmap> apply, ClipEffect effect)
    {
        using var one = BuildFlatPattern(128, 255, 1, 1);
        using var oneOut = apply(one, effect);
        Assert.Equal(1, oneOut.Width);
        Assert.Equal(1, oneOut.Height);

        using var two = new SKBitmap(2, 2, SKColorType.Bgra8888, SKAlphaType.Premul);
        two.SetPixel(0, 0, SKColors.Black);
        two.SetPixel(1, 0, SKColors.White);
        two.SetPixel(0, 1, SKColors.White);
        two.SetPixel(1, 1, SKColors.Black);
        using var twoOut = apply(two, effect);
        Assert.Equal(2, twoOut.Width);
        Assert.Equal(2, twoOut.Height);
    }

    // ===== EdgeDetect =====

    /// <summary>エッジ検出：既定パラメータで段差のある画像が見た目に変わること。</summary>
    [Fact]
    public void EdgeDetect_Default_ChangesSplitImage()
    {
        using var src = BuildSplitPattern();
        using var result = NewEffectsStyle.ApplyEdgeDetect(src, new ClipEffect { Kind = EffectKind.EdgeDetect, Amount = 0.8, Radius = 0.15 });
        Assert.True(CountDiff(src, result) > 100);
    }

    /// <summary>エッジ検出：Amount 0 で元の画像と同一（src のコピー）。</summary>
    [Fact]
    public void EdgeDetect_ZeroAmount_IsIdentity()
    {
        using var src = BuildSplitPattern();
        using var result = NewEffectsStyle.ApplyEdgeDetect(src, new ClipEffect { Kind = EffectKind.EdgeDetect, Amount = 0, Radius = 0.15 });
        Assert.Equal(0, CountDiff(src, result));
    }

    /// <summary>エッジ検出：Amount 1 でエッジマップそのものになる。平坦な画像では勾配が 0 のため全面黒になる。</summary>
    [Fact]
    public void EdgeDetect_AmountOne_FlatImageBecomesBlack()
    {
        using var src = BuildFlatPattern(128);
        using var result = NewEffectsStyle.ApplyEdgeDetect(src, new ClipEffect { Kind = EffectKind.EdgeDetect, Amount = 1, Radius = 0.15 });
        Assert.Equal(SKColors.Black, result.GetPixel(Width / 2, Height / 2));
        Assert.Equal(SKColors.Black, result.GetPixel(0, 0));
        Assert.Equal(SKColors.Black, result.GetPixel(Width - 1, Height - 1));
    }

    /// <summary>エッジ検出：範囲外のパラメータでも例外が出ず、サイズが保たれること（クランプされる）。</summary>
    [Fact]
    public void EdgeDetect_ExtremeParameters_DoNotThrow()
    {
        using var src = BuildSplitPattern();
        using var low = NewEffectsStyle.ApplyEdgeDetect(src, new ClipEffect { Kind = EffectKind.EdgeDetect, Amount = -5, Radius = 10 });
        Assert.Equal(Width, low.Width);
        Assert.Equal(Height, low.Height);
        using var high = NewEffectsStyle.ApplyEdgeDetect(src, new ClipEffect { Kind = EffectKind.EdgeDetect, Amount = 5, Radius = -1 });
        Assert.Equal(Width, high.Width);
        Assert.Equal(Height, high.Height);
    }

    /// <summary>エッジ検出：1x1・2x2 でも例外が出ないこと。</summary>
    [Fact]
    public void EdgeDetect_TinyBitmaps_DoNotThrow()
        => AssertTinyBitmapsWork(
            NewEffectsStyle.ApplyEdgeDetect,
            new ClipEffect { Kind = EffectKind.EdgeDetect, Amount = 0.8, Radius = 0.15 });

    // ===== Solarize =====

    /// <summary>ソラリゼーション：既定のしきい値 0.5 でグラデーション画像が見た目に変わること（128 以上の画素が反転する）。</summary>
    [Fact]
    public void Solarize_Default_ChangesGradient()
    {
        using var src = BuildGradientPattern();
        using var result = NewEffectsStyle.ApplySolarize(src, new ClipEffect { Kind = EffectKind.Solarize, Amount = 0.5 });
        Assert.True(CountDiff(src, result) > 1000);
    }

    /// <summary>ソラリゼーション：しきい値 1.0 では反転する画素がほぼ無い（128 の平坦画像では変化なし）。</summary>
    [Fact]
    public void Solarize_ThresholdOne_NearlyUnchanged()
    {
        using var src = BuildFlatPattern(128);
        using var result = NewEffectsStyle.ApplySolarize(src, new ClipEffect { Kind = EffectKind.Solarize, Amount = 1.0 });
        Assert.Equal(0, CountDiff(src, result));
    }

    /// <summary>ソラリゼーション：しきい値 0 では全画素が反転する（128 → 127）。</summary>
    [Fact]
    public void Solarize_ThresholdZero_InvertsEverything()
    {
        using var src = BuildFlatPattern(128);
        using var result = NewEffectsStyle.ApplySolarize(src, new ClipEffect { Kind = EffectKind.Solarize, Amount = 0 });
        Assert.Equal(new SKColor(127, 127, 127), result.GetPixel(Width / 2, Height / 2));
        Assert.Equal(new SKColor(127, 127, 127), result.GetPixel(0, 0));
    }

    /// <summary>ソラリゼーション：範囲外のパラメータでも例外が出ず、サイズが保たれること（クランプされる）。</summary>
    [Fact]
    public void Solarize_ExtremeParameters_DoNotThrow()
    {
        using var src = BuildGradientPattern();
        using var low = NewEffectsStyle.ApplySolarize(src, new ClipEffect { Kind = EffectKind.Solarize, Amount = -2 });
        Assert.Equal(Width, low.Width);
        Assert.Equal(Height, low.Height);
        using var high = NewEffectsStyle.ApplySolarize(src, new ClipEffect { Kind = EffectKind.Solarize, Amount = 5 });
        Assert.Equal(Width, high.Width);
        Assert.Equal(Height, high.Height);
    }

    /// <summary>ソラリゼーション：1x1・2x2 でも例外が出ないこと。</summary>
    [Fact]
    public void Solarize_TinyBitmaps_DoNotThrow()
        => AssertTinyBitmapsWork(
            NewEffectsStyle.ApplySolarize,
            new ClipEffect { Kind = EffectKind.Solarize, Amount = 0.5 });

    // ===== Kuwahara =====

    /// <summary>
    /// 油絵風：既定パラメータでグラデーションが見た目に変わること。
    /// チェッカーでは各画素に自分の色と一致する分散 0 の領域が必ずあるため変わらない
    /// （エッジ保存性の裏返し）。グラデーションなら最小分散の領域の平均が元の画素とずれる。
    /// </summary>
    [Fact]
    public void Kuwahara_Default_ChangesGradient()
    {
        using var src = BuildGradientPattern();
        using var result = NewEffectsStyle.ApplyKuwahara(src, new ClipEffect { Kind = EffectKind.Kuwahara, Amount = 3, Radius = 1 });
        Assert.True(CountDiff(src, result) > 100);
    }

    /// <summary>油絵風：強さ 0 で元の画像と同一。</summary>
    [Fact]
    public void Kuwahara_ZeroStrength_IsIdentity()
    {
        using var src = BuildCheckerPattern();
        using var result = NewEffectsStyle.ApplyKuwahara(src, new ClipEffect { Kind = EffectKind.Kuwahara, Amount = 3, Radius = 0 });
        Assert.Equal(0, CountDiff(src, result));
    }

    /// <summary>油絵風：強さ 0.5 の結果は、元の画像とも強さ 1 の結果とも違う（ブレンドの中間）。</summary>
    [Fact]
    public void Kuwahara_HalfStrength_IsBetweenSourceAndFull()
    {
        using var src = BuildGradientPattern();
        using var full = NewEffectsStyle.ApplyKuwahara(src, new ClipEffect { Kind = EffectKind.Kuwahara, Amount = 3, Radius = 1 });
        using var half = NewEffectsStyle.ApplyKuwahara(src, new ClipEffect { Kind = EffectKind.Kuwahara, Amount = 3, Radius = 0.5 });
        Assert.True(CountDiff(half, src) > 100);
        Assert.True(CountDiff(half, full) > 100);
    }

    /// <summary>油絵風：範囲外のパラメータでも例外が出ず、サイズが保たれること（半径は 1〜8・強さは 0〜1 にクランプされる）。</summary>
    [Fact]
    public void Kuwahara_ExtremeParameters_DoNotThrow()
    {
        using var src = BuildCheckerPattern();
        using var result = NewEffectsStyle.ApplyKuwahara(src, new ClipEffect { Kind = EffectKind.Kuwahara, Amount = 100, Radius = 2 });
        Assert.Equal(Width, result.Width);
        Assert.Equal(Height, result.Height);
        using var low = NewEffectsStyle.ApplyKuwahara(src, new ClipEffect { Kind = EffectKind.Kuwahara, Amount = -3, Radius = -1 });
        Assert.Equal(Width, low.Width);
        Assert.Equal(Height, low.Height);
    }

    /// <summary>油絵風：1x1・2x2 でも例外が出ないこと。</summary>
    [Fact]
    public void Kuwahara_TinyBitmaps_DoNotThrow()
        => AssertTinyBitmapsWork(
            NewEffectsStyle.ApplyKuwahara,
            new ClipEffect { Kind = EffectKind.Kuwahara, Amount = 3, Radius = 1 });

    // ===== TiltShift =====

    /// <summary>ティルトシフト：既定パラメータでチェッカーが見た目に変わること。</summary>
    [Fact]
    public void TiltShift_Default_ChangesChecker()
    {
        using var src = BuildCheckerPattern();
        using var result = NewEffectsStyle.ApplyTiltShift(src, new ClipEffect { Kind = EffectKind.TiltShift, Amount = 0.5, Radius = 0.3 });
        Assert.True(CountDiff(src, result) > 100);
    }

    /// <summary>ティルトシフト：Amount 0 で元の画像と同一。</summary>
    [Fact]
    public void TiltShift_ZeroAmount_IsIdentity()
    {
        using var src = BuildCheckerPattern();
        using var result = NewEffectsStyle.ApplyTiltShift(src, new ClipEffect { Kind = EffectKind.TiltShift, Amount = 0, Radius = 0.3 });
        Assert.Equal(0, CountDiff(src, result));
    }

    /// <summary>ティルトシフト：帯の中心（画面中央）の行は元の画像のままで、上下の端の行はぼかされて変わること。</summary>
    [Fact]
    public void TiltShift_CenterBandStaysSharp_TopRowsBlur()
    {
        using var src = BuildCheckerPattern();
        using var result = NewEffectsStyle.ApplyTiltShift(src, new ClipEffect { Kind = EffectKind.TiltShift, Amount = 0.5, Radius = 0.3 });
        // 帯の半幅は 0.3×90/2=13.5 行。中心付近の行（35〜55）は帯の内側で係数 0 のため完全一致する。
        Assert.Equal(0, CountDiffInRows(src, result, 35, 55));
        // 上端の行（0〜4）はほぼ完全にぼかし側のため、チェッカーがなまって変わる。
        Assert.True(CountDiffInRows(src, result, 0, 4) > 100);
    }

    /// <summary>ティルトシフト：範囲外のパラメータでも例外が出ず、サイズが保たれること（クランプされる）。</summary>
    [Fact]
    public void TiltShift_ExtremeParameters_DoNotThrow()
    {
        using var src = BuildCheckerPattern();
        using var high = NewEffectsStyle.ApplyTiltShift(src, new ClipEffect { Kind = EffectKind.TiltShift, Amount = 3, Radius = 5 });
        Assert.Equal(Width, high.Width);
        Assert.Equal(Height, high.Height);
        using var low = NewEffectsStyle.ApplyTiltShift(src, new ClipEffect { Kind = EffectKind.TiltShift, Amount = -1, Radius = 0 });
        Assert.Equal(Width, low.Width);
        Assert.Equal(Height, low.Height);
    }

    /// <summary>ティルトシフト：1x1・2x2 でも例外が出ないこと。</summary>
    [Fact]
    public void TiltShift_TinyBitmaps_DoNotThrow()
        => AssertTinyBitmapsWork(
            NewEffectsStyle.ApplyTiltShift,
            new ClipEffect { Kind = EffectKind.TiltShift, Amount = 0.5, Radius = 0.3 });

    // ===== Halftone =====

    /// <summary>ハーフトーン：既定パラメータで円パターンが見た目に変わること。</summary>
    [Fact]
    public void Halftone_Default_ChangesCircle()
    {
        using var src = BuildCirclePattern();
        using var result = NewEffectsStyle.ApplyHalftone(src, new ClipEffect { Kind = EffectKind.Halftone, Amount = 6, Radius = 45 });
        Assert.True(CountDiff(src, result) > 100);
    }

    /// <summary>ハーフトーン：角度 0 では、黒いセルはドットで中心が黒く、白いセルはドット無しで白のまま。</summary>
    [Fact]
    public void Halftone_AngleZero_BlackCellHasDot_WhiteCellHasNone()
    {
        using var src = BuildSplitPattern(60, 60);
        using var result = NewEffectsStyle.ApplyHalftone(src, new ClipEffect { Kind = EffectKind.Halftone, Amount = 6, Radius = 0 });
        // 左半分は黒（セル全面黒→最大ドットで中心は黒）、右半分は白（ドット無しで白のまま）。
        Assert.True(result.GetPixel(3, 3).Red < 30);
        Assert.Equal(SKColors.White, result.GetPixel(33, 3));
    }

    /// <summary>ハーフトーン：不透明な画像は白背景、透明画素を含む画像は透明背景になること（四隅のアルファで判定）。</summary>
    [Fact]
    public void Halftone_OpaqueSourceGetsWhiteBackground_TransparentSourceGetsTransparentBackground()
    {
        using var opaque = BuildCirclePattern();
        using var opaqueResult = NewEffectsStyle.ApplyHalftone(opaque, new ClipEffect { Kind = EffectKind.Halftone, Amount = 16, Radius = 0 });
        // 不透明な画像は白背景。ドットサイズ 16 なら、隙間の中心 (16,16) はどのドットからも
        // 8px 以上離れているためアンチエイリアスもかからず完全な白になる。
        // （ドットサイズ 6 では隅の画素にドットの AA がわずかにかかるため、この判定には使えない。）
        Assert.Equal(SKColors.White, opaqueResult.GetPixel(16, 16));

        using var transparent = BuildTransparentPattern();
        using var transparentResult = NewEffectsStyle.ApplyHalftone(transparent, new ClipEffect { Kind = EffectKind.Halftone, Amount = 16, Radius = 0 });
        // 透明画素を含む画像は透明背景。左上隅のセルは透明が大半で、隅 (0,0) にドットは届かない。
        Assert.Equal(0, transparentResult.GetPixel(0, 0).Alpha);
    }

    /// <summary>ハーフトーン：範囲外のパラメータでも例外が出ず、サイズが保たれること（クランプされる）。</summary>
    [Fact]
    public void Halftone_ExtremeParameters_DoNotThrow()
    {
        using var src = BuildCirclePattern();
        using var high = NewEffectsStyle.ApplyHalftone(src, new ClipEffect { Kind = EffectKind.Halftone, Amount = 100, Radius = 200 });
        Assert.Equal(Width, high.Width);
        Assert.Equal(Height, high.Height);
        using var low = NewEffectsStyle.ApplyHalftone(src, new ClipEffect { Kind = EffectKind.Halftone, Amount = 0, Radius = -30 });
        Assert.Equal(Width, low.Width);
        Assert.Equal(Height, low.Height);
    }

    /// <summary>ハーフトーン：1x1・2x2 でも例外が出ないこと。</summary>
    [Fact]
    public void Halftone_TinyBitmaps_DoNotThrow()
        => AssertTinyBitmapsWork(
            NewEffectsStyle.ApplyHalftone,
            new ClipEffect { Kind = EffectKind.Halftone, Amount = 6, Radius = 45 });

    // ===== 横断 =====

    /// <summary>全エフェクト：入力のビットマップを変更しないこと。戻り値は入力と別物であること。</summary>
    [Fact]
    public void SourceBitmap_IsNotModified_ByAnyEffect()
    {
        var cases = new (Func<SKBitmap, ClipEffect, SKBitmap> Apply, ClipEffect Effect)[]
        {
            (NewEffectsStyle.ApplyEdgeDetect, new ClipEffect { Kind = EffectKind.EdgeDetect, Amount = 0.8, Radius = 0.15 }),
            (NewEffectsStyle.ApplySolarize, new ClipEffect { Kind = EffectKind.Solarize, Amount = 0.5 }),
            (NewEffectsStyle.ApplyKuwahara, new ClipEffect { Kind = EffectKind.Kuwahara, Amount = 3, Radius = 1 }),
            (NewEffectsStyle.ApplyTiltShift, new ClipEffect { Kind = EffectKind.TiltShift, Amount = 0.5, Radius = 0.3 }),
            (NewEffectsStyle.ApplyHalftone, new ClipEffect { Kind = EffectKind.Halftone, Amount = 6, Radius = 45 }),
        };
        foreach (var (apply, effect) in cases)
        {
            using var src = BuildCirclePattern();
            using var snapshot = src.Copy();
            using var result = apply(src, effect);
            Assert.NotSame(src, result);
            Assert.Equal(0, CountDiff(src, snapshot));
        }
    }

    /// <summary>全エフェクト：半透明の入力でも Premul の不変条件（RGB ≤ アルファ）が保たれること。</summary>
    [Fact]
    public void PremulInvariant_Holds_ForAllEffects()
    {
        var cases = new (Func<SKBitmap, ClipEffect, SKBitmap> Apply, ClipEffect Effect)[]
        {
            (NewEffectsStyle.ApplyEdgeDetect, new ClipEffect { Kind = EffectKind.EdgeDetect, Amount = 0.8, Radius = 0.15 }),
            (NewEffectsStyle.ApplySolarize, new ClipEffect { Kind = EffectKind.Solarize, Amount = 0.5 }),
            (NewEffectsStyle.ApplyKuwahara, new ClipEffect { Kind = EffectKind.Kuwahara, Amount = 3, Radius = 1 }),
            (NewEffectsStyle.ApplyTiltShift, new ClipEffect { Kind = EffectKind.TiltShift, Amount = 0.5, Radius = 0.3 }),
            (NewEffectsStyle.ApplyHalftone, new ClipEffect { Kind = EffectKind.Halftone, Amount = 6, Radius = 45 }),
        };
        foreach (var (apply, effect) in cases)
        {
            using var src = BuildTransparentPattern();
            using var result = apply(src, effect);
            AssertPremulValid(result);
        }
    }
}
