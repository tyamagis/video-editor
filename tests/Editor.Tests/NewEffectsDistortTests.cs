// 追加エフェクト7種（歪み系）の描画テスト：NewEffectsDistort のヘルパーを直接呼ぶ
// （FrameRenderer への配線はまだ無いため）。画質の厳密な判定ではなく、
// 「効いている／壊れていない」の確認が目的。実行はこの VM では不可のため、ビルド確認まで。

using Editor.Core;
using Editor.Render;
using SkiaSharp;
using Xunit;

namespace Editor.Tests;

/// <summary>追加エフェクト7種（歪み系）の描画テスト（NewEffectsDistort 直接呼び）。</summary>
public sealed class NewEffectsDistortTests
{
    private const int Width = 160;
    private const int Height = 90;

    /// <summary>横グラデーション R と縦グラデーション G を持つパターン（B=128）。幾何変換の変化が見えやすい。</summary>
    private static SKBitmap BuildGradientPattern()
    {
        var bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                bitmap.SetPixel(x, y, new SKColor(
                    (byte)Math.Round(x * 255.0 / (Width - 1)),
                    (byte)Math.Round(y * 255.0 / (Height - 1)),
                    128, 255));
        return bitmap;
    }

    /// <summary>色が x だけに依存するパターン（縦方向のずれでは変わらない）。Wave の方向テスト用。</summary>
    private static SKBitmap BuildXGradient()
    {
        var bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                bitmap.SetPixel(x, y, new SKColor((byte)Math.Round(x * 255.0 / (Width - 1)), 0, 0, 255));
        return bitmap;
    }

    /// <summary>色が y だけに依存するパターン（横方向のずれでは変わらない）。Wave の方向テスト用。</summary>
    private static SKBitmap BuildYGradient()
    {
        var bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                bitmap.SetPixel(x, y, new SKColor(0, (byte)Math.Round(y * 255.0 / (Height - 1)), 0, 255));
        return bitmap;
    }

    /// <summary>ハッシュで決まるランダムなグレーのパターン（ピクセルソート用。明暗が不規則に並ぶ）。</summary>
    private static SKBitmap BuildRandomGrayPattern()
    {
        var bitmap = new SKBitmap(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                var v = (byte)(PatternHash(x, y) % 251);
                bitmap.SetPixel(x, y, new SKColor(v, v, v, 255));
            }
        return bitmap;
    }

    private static int PatternHash(int x, int y)
    {
        uint h = (uint)(x * 374761393 + y * 668265263);
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;
        return (int)(h & 0xFFFF);
    }

    /// <summary>1行のグレーパターン（ピクセルソートの順序テスト用）。</summary>
    private static SKBitmap BuildGrayRow(params int[] values)
    {
        var bitmap = new SKBitmap(values.Length, 1, SKColorType.Bgra8888, SKAlphaType.Premul);
        for (var x = 0; x < values.Length; x++)
        {
            var v = (byte)values[x];
            bitmap.SetPixel(x, 0, new SKColor(v, v, v, 255));
        }
        return bitmap;
    }

    private static int[] ReadGrayRow(SKBitmap bitmap)
    {
        var values = new int[bitmap.Width];
        for (var x = 0; x < bitmap.Width; x++)
            values[x] = bitmap.GetPixel(x, 0).Red;
        return values;
    }

    /// <summary>2枚のビットマップで違う画素の数（全体）。</summary>
    private static int CountDiff(SKBitmap a, SKBitmap b)
    {
        var count = 0;
        for (var y = 0; y < a.Height; y++)
            for (var x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y))
                    count++;
        return count;
    }

    /// <summary>2枚のビットマップで違う画素の数（指定した矩形の内側だけ。端の透明化の影響を避ける用）。</summary>
    private static int CountDiffInRect(SKBitmap a, SKBitmap b, int x0, int y0, int x1, int y1)
    {
        var count = 0;
        for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y))
                    count++;
        return count;
    }

    private static void AssertSameSize(SKBitmap src, SKBitmap dst)
    {
        Assert.Equal(src.Width, dst.Width);
        Assert.Equal(src.Height, dst.Height);
    }

    /// <summary>1x1・2x2 の小さいビットマップでも例外が出ず、サイズが保たれること。</summary>
    private static void AssertTinyBitmapsWork(Func<SKBitmap, SKBitmap> apply)
    {
        using var one = new SKBitmap(1, 1, SKColorType.Bgra8888, SKAlphaType.Premul);
        one.SetPixel(0, 0, new SKColor(200, 100, 50, 255));
        using var oneOut = apply(one);
        Assert.Equal(1, oneOut.Width);
        Assert.Equal(1, oneOut.Height);

        using var two = new SKBitmap(2, 2, SKColorType.Bgra8888, SKAlphaType.Premul);
        two.SetPixel(0, 0, SKColors.White);
        two.SetPixel(1, 0, SKColors.Black);
        two.SetPixel(0, 1, SKColors.Red);
        two.SetPixel(1, 1, SKColors.Blue);
        using var twoOut = apply(two);
        Assert.Equal(2, twoOut.Width);
        Assert.Equal(2, twoOut.Height);
    }

    /// <summary>src が変更されていないこと（結果と比べる前のスナップショットと一致する）。</summary>
    private static void AssertSourceUnchanged(SKBitmap before, SKBitmap after)
        => Assert.Equal(0, CountDiff(before, after));

    // ---- ピクセルソート ----

    /// <summary>ピクセルソート：既定値で見た目が変わる。</summary>
    [Fact]
    public void PixelSort_Default_ChangesImage()
    {
        using var src = BuildRandomGrayPattern();
        using var sorted = NewEffectsDistort.ApplyPixelSort(src,
            new ClipEffect { Kind = EffectKind.PixelSort, Amount = 0.5, Radius = 64 });
        Assert.True(CountDiff(src, sorted) >= 100);
    }

    /// <summary>ピクセルソート：Amount 0 はソートなし（変化なし）。</summary>
    [Fact]
    public void PixelSort_ZeroAmount_IsIdentity()
    {
        using var src = BuildRandomGrayPattern();
        using var sorted = NewEffectsDistort.ApplyPixelSort(src,
            new ClipEffect { Kind = EffectKind.PixelSort, Amount = 0, Radius = 64 });
        Assert.Equal(0, CountDiff(src, sorted));
    }

    /// <summary>ピクセルソート：しきい値以上の区間が明るさの昇順に並ぶ（降順の区間が昇順になる）。</summary>
    [Fact]
    public void PixelSort_SortsBrightRunAscending()
    {
        using var src = BuildGrayRow(200, 180, 160, 140, 120, 100, 90, 80);
        using var sorted = NewEffectsDistort.ApplyPixelSort(src,
            new ClipEffect { Kind = EffectKind.PixelSort, Amount = 0.5, Radius = 64 });
        // しきい値 127.5 以上の区間 [200,180,160,140] が昇順になる。120 はしきい値未満のためそのまま。
        Assert.Equal(new[] { 140, 160, 180, 200, 120, 100, 90, 80 }, ReadGrayRow(sorted));
    }

    /// <summary>ピクセルソート：最大ソート長より長い区間はその長さごとのチャンクに分けて並べ替える。</summary>
    [Fact]
    public void PixelSort_MaxRun_SplitsLongRun()
    {
        var values = new int[32];
        for (var x = 0; x < 24; x++)
            values[x] = 250 - 5 * x; // 250, 245, …, 135（しきい値以上が 24px 続く）。
        for (var x = 24; x < 32; x++)
            values[x] = 0;
        using var src = BuildGrayRow(values);
        using var sorted = NewEffectsDistort.ApplyPixelSort(src,
            new ClipEffect { Kind = EffectKind.PixelSort, Amount = 0.5, Radius = 8 });
        var row = ReadGrayRow(sorted);
        Assert.Equal(215, row[0]); // チャンク1 [250…215] → [215…250]。
        Assert.Equal(250, row[7]);
        Assert.Equal(175, row[8]); // チャンク2 [210…175] → [175…210]。
        Assert.Equal(210, row[15]);
        Assert.Equal(135, row[16]); // チャンク3 [170…135] → [135…170]。
        Assert.Equal(170, row[23]);
        Assert.Equal(0, row[24]); // 暗い部分はそのまま。
    }

    /// <summary>ピクセルソート：範囲の端・範囲外のパラメータでも例外が出ずクランプされる。</summary>
    [Fact]
    public void PixelSort_Extremes_DoNotThrow()
    {
        using var src = BuildRandomGrayPattern();
        using var a = NewEffectsDistort.ApplyPixelSort(src,
            new ClipEffect { Kind = EffectKind.PixelSort, Amount = 1, Radius = 256 });
        AssertSameSize(src, a);
        using var b = NewEffectsDistort.ApplyPixelSort(src,
            new ClipEffect { Kind = EffectKind.PixelSort, Amount = 0.01, Radius = 8 });
        AssertSameSize(src, b);
        using var c = NewEffectsDistort.ApplyPixelSort(src,
            new ClipEffect { Kind = EffectKind.PixelSort, Amount = 2, Radius = 1000 });
        AssertSameSize(src, c);
    }

    /// <summary>ピクセルソート：小さいビットマップでも例外が出ない。</summary>
    [Fact]
    public void PixelSort_TinyBitmaps_DoNotThrow()
        => AssertTinyBitmapsWork(src => NewEffectsDistort.ApplyPixelSort(src,
            new ClipEffect { Kind = EffectKind.PixelSort, Amount = 0.5, Radius = 64 }));

    /// <summary>ピクセルソート：src は変更されない。</summary>
    [Fact]
    public void PixelSort_DoesNotModifySource()
    {
        using var src = BuildRandomGrayPattern();
        using var before = src.Copy();
        using var sorted = NewEffectsDistort.ApplyPixelSort(src,
            new ClipEffect { Kind = EffectKind.PixelSort, Amount = 0.5, Radius = 64 });
        AssertSourceUnchanged(before, src);
    }

    // ---- ウェーブ ----

    /// <summary>ウェーブ：既定値で見た目が変わる。</summary>
    [Fact]
    public void Wave_Default_ChangesImage()
    {
        using var src = BuildGradientPattern();
        using var waved = NewEffectsDistort.ApplyWave(src,
            new ClipEffect { Kind = EffectKind.Wave, Amount = 20, Radius = 240 });
        Assert.True(CountDiff(src, waved) >= 100);
    }

    /// <summary>ウェーブ：量 0 で変化なし。</summary>
    [Fact]
    public void Wave_ZeroAmount_IsIdentity()
    {
        using var src = BuildGradientPattern();
        using var waved = NewEffectsDistort.ApplyWave(src,
            new ClipEffect { Kind = EffectKind.Wave, Amount = 0, Radius = 240 });
        Assert.Equal(0, CountDiff(src, waved));
    }

    /// <summary>
    /// ウェーブ：Warp とは垂直な方向にずれる。Warp は y のサインで X 方向へずらすため、
    /// Wave は x のサインで Y 方向へずらす。横グラデーション（色が x だけに依存）は
    /// 縦方向のずれでは変わらず、縦グラデーション（色が y だけに依存）は変わる。
    /// （端の透明化を避けるため内側の矩形だけで数える。）
    /// </summary>
    [Fact]
    public void Wave_DisplacesVertically_NotHorizontally()
    {
        using var xGrad = BuildXGradient();
        using var wavedX = NewEffectsDistort.ApplyWave(xGrad,
            new ClipEffect { Kind = EffectKind.Wave, Amount = 20, Radius = 240 });
        Assert.Equal(0, CountDiffInRect(xGrad, wavedX, 10, 25, 150, 65));

        using var yGrad = BuildYGradient();
        using var wavedY = NewEffectsDistort.ApplyWave(yGrad,
            new ClipEffect { Kind = EffectKind.Wave, Amount = 20, Radius = 240 });
        Assert.True(CountDiffInRect(yGrad, wavedY, 10, 25, 150, 65) >= 100);
    }

    /// <summary>ウェーブ：範囲の端・範囲外のパラメータでも例外が出ずクランプされる。</summary>
    [Fact]
    public void Wave_Extremes_DoNotThrow()
    {
        using var src = BuildGradientPattern();
        using var a = NewEffectsDistort.ApplyWave(src,
            new ClipEffect { Kind = EffectKind.Wave, Amount = 100, Radius = 20 });
        AssertSameSize(src, a);
        using var b = NewEffectsDistort.ApplyWave(src,
            new ClipEffect { Kind = EffectKind.Wave, Amount = 100, Radius = 1000 });
        AssertSameSize(src, b);
        using var c = NewEffectsDistort.ApplyWave(src,
            new ClipEffect { Kind = EffectKind.Wave, Amount = 1000, Radius = -5 });
        AssertSameSize(src, c);
    }

    /// <summary>ウェーブ：小さいビットマップでも例外が出ない。</summary>
    [Fact]
    public void Wave_TinyBitmaps_DoNotThrow()
        => AssertTinyBitmapsWork(src => NewEffectsDistort.ApplyWave(src,
            new ClipEffect { Kind = EffectKind.Wave, Amount = 20, Radius = 240 }));

    /// <summary>ウェーブ：src は変更されない。</summary>
    [Fact]
    public void Wave_DoesNotModifySource()
    {
        using var src = BuildGradientPattern();
        using var before = src.Copy();
        using var waved = NewEffectsDistort.ApplyWave(src,
            new ClipEffect { Kind = EffectKind.Wave, Amount = 20, Radius = 240 });
        AssertSourceUnchanged(before, src);
    }

    // ---- ミラー ----

    /// <summary>ミラー：方式 0 は左右反転（左端と右端が入れ替わり、行は変わらない）。</summary>
    [Fact]
    public void Mirror_Mode0_FlipsHorizontally()
    {
        using var src = BuildGradientPattern();
        using var mirrored = NewEffectsDistort.ApplyMirror(src,
            new ClipEffect { Kind = EffectKind.Mirror, Amount = 0.5, Radius = 0 });
        Assert.Equal(255, mirrored.GetPixel(0, 45).Red); // 元の右端 (159,45) の R。
        Assert.Equal(0, mirrored.GetPixel(159, 45).Red); // 元の左端 (0,45) の R。
        Assert.Equal(src.GetPixel(80, 45).Green, mirrored.GetPixel(79, 45).Green); // 行は変わらない。
    }

    /// <summary>ミラー：方式 1 は左右分割ミラー（中央より左はそのまま、右は左側の鏡像）。</summary>
    [Fact]
    public void Mirror_Mode1_SplitsAndMirrors()
    {
        using var src = BuildGradientPattern();
        using var mirrored = NewEffectsDistort.ApplyMirror(src,
            new ClipEffect { Kind = EffectKind.Mirror, Amount = 0.5, Radius = 1 });
        Assert.Equal(0, mirrored.GetPixel(0, 45).Red); // 左端はそのまま。
        Assert.Equal(64, mirrored.GetPixel(120, 45).Red); // 分割線 80 の鏡像：元の (40,45) の R。
        Assert.Equal(2, mirrored.GetPixel(159, 45).Red); // 元の (1,45) の R。
    }

    /// <summary>ミラー：方式 2 は上下分割ミラー（中央より上はそのまま、下は上側の鏡像）。</summary>
    [Fact]
    public void Mirror_Mode2_SplitsAndMirrorsVertically()
    {
        using var src = BuildGradientPattern();
        using var mirrored = NewEffectsDistort.ApplyMirror(src,
            new ClipEffect { Kind = EffectKind.Mirror, Amount = 0.5, Radius = 2 });
        Assert.Equal(0, mirrored.GetPixel(80, 0).Green); // 上端はそのまま。
        Assert.Equal(57, mirrored.GetPixel(80, 70).Green); // 分割線 45 の鏡像：元の (80,20) の G。
        Assert.Equal(3, mirrored.GetPixel(80, 89).Green); // 元の (80,1) の G。
    }

    /// <summary>ミラー：方式 1・2 で位置が 1 なら分割線が画面の端に来るため、全体がそのままになる。</summary>
    [Fact]
    public void Mirror_Position1_IsIdentity()
    {
        using var src = BuildGradientPattern();
        using var horizontal = NewEffectsDistort.ApplyMirror(src,
            new ClipEffect { Kind = EffectKind.Mirror, Amount = 1, Radius = 1 });
        Assert.Equal(0, CountDiff(src, horizontal));
        using var vertical = NewEffectsDistort.ApplyMirror(src,
            new ClipEffect { Kind = EffectKind.Mirror, Amount = 1, Radius = 2 });
        Assert.Equal(0, CountDiff(src, vertical));
    }

    /// <summary>ミラー：範囲の端・範囲外のパラメータでも例外が出ずクランプされる。</summary>
    [Fact]
    public void Mirror_Extremes_DoNotThrow()
    {
        using var src = BuildGradientPattern();
        using var a = NewEffectsDistort.ApplyMirror(src,
            new ClipEffect { Kind = EffectKind.Mirror, Amount = 0, Radius = 1 });
        AssertSameSize(src, a);
        using var b = NewEffectsDistort.ApplyMirror(src,
            new ClipEffect { Kind = EffectKind.Mirror, Amount = 1, Radius = 2 });
        AssertSameSize(src, b);
        using var c = NewEffectsDistort.ApplyMirror(src,
            new ClipEffect { Kind = EffectKind.Mirror, Amount = -1, Radius = 5 });
        AssertSameSize(src, c);
    }

    /// <summary>ミラー：小さいビットマップでも例外が出ない。</summary>
    [Fact]
    public void Mirror_TinyBitmaps_DoNotThrow()
        => AssertTinyBitmapsWork(src => NewEffectsDistort.ApplyMirror(src,
            new ClipEffect { Kind = EffectKind.Mirror, Amount = 0.5, Radius = 1 }));

    // ---- トゥワール ----

    /// <summary>トゥワール：既定値で見た目が変わる。</summary>
    [Fact]
    public void Twirl_Default_ChangesImage()
    {
        using var src = BuildGradientPattern();
        using var twirled = NewEffectsDistort.ApplyTwirl(src,
            new ClipEffect { Kind = EffectKind.Twirl, Amount = 180, Radius = 0.6 });
        Assert.True(CountDiff(src, twirled) >= 100);
    }

    /// <summary>トゥワール：量 0 で変化なし。</summary>
    [Fact]
    public void Twirl_ZeroAmount_IsIdentity()
    {
        using var src = BuildGradientPattern();
        using var twirled = NewEffectsDistort.ApplyTwirl(src,
            new ClipEffect { Kind = EffectKind.Twirl, Amount = 0, Radius = 0.6 });
        Assert.Equal(0, CountDiff(src, twirled));
    }

    /// <summary>
    /// トゥワール：+は時計回り。中心の東 (100,45) は時計回りにねじれ、北東側
    /// (65.7, 31.1) あたりからサンプルされる。逆回りなら南東側 (65.7, 58.9) からに
    /// なる。G は y だけに依存するため、G で判定できる（期待値 ≒89、逆回りなら ≒169）。
    /// </summary>
    [Fact]
    public void Twirl_PositiveAngle_RotatesClockwise()
    {
        using var src = BuildGradientPattern();
        using var twirled = NewEffectsDistort.ApplyTwirl(src,
            new ClipEffect { Kind = EffectKind.Twirl, Amount = 180, Radius = 0.6 });
        Assert.InRange(twirled.GetPixel(100, 45).Green, 70, 110);
    }

    /// <summary>トゥワール：範囲の端・範囲外のパラメータでも例外が出ずクランプされる。</summary>
    [Fact]
    public void Twirl_Extremes_DoNotThrow()
    {
        using var src = BuildGradientPattern();
        using var a = NewEffectsDistort.ApplyTwirl(src,
            new ClipEffect { Kind = EffectKind.Twirl, Amount = 360, Radius = 0.2 });
        AssertSameSize(src, a);
        using var b = NewEffectsDistort.ApplyTwirl(src,
            new ClipEffect { Kind = EffectKind.Twirl, Amount = -360, Radius = 1.0 });
        AssertSameSize(src, b);
        using var c = NewEffectsDistort.ApplyTwirl(src,
            new ClipEffect { Kind = EffectKind.Twirl, Amount = 720, Radius = 5 });
        AssertSameSize(src, c);
    }

    /// <summary>トゥワール：小さいビットマップでも例外が出ない。</summary>
    [Fact]
    public void Twirl_TinyBitmaps_DoNotThrow()
        => AssertTinyBitmapsWork(src => NewEffectsDistort.ApplyTwirl(src,
            new ClipEffect { Kind = EffectKind.Twirl, Amount = 180, Radius = 0.6 }));

    // ---- ディスプレイスメント ----

    /// <summary>ディスプレイスメント：既定値で見た目が変わる。</summary>
    [Fact]
    public void Displacement_Default_ChangesImage()
    {
        using var src = BuildGradientPattern();
        using var displaced = NewEffectsDistort.ApplyDisplacement(src,
            new ClipEffect { Kind = EffectKind.Displacement, Amount = 30, Radius = 1 });
        Assert.True(CountDiff(src, displaced) >= 100);
    }

    /// <summary>ディスプレイスメント：量 0 で変化なし。</summary>
    [Fact]
    public void Displacement_ZeroAmount_IsIdentity()
    {
        using var src = BuildGradientPattern();
        using var displaced = NewEffectsDistort.ApplyDisplacement(src,
            new ClipEffect { Kind = EffectKind.Displacement, Amount = 0, Radius = 1 });
        Assert.Equal(0, CountDiff(src, displaced));
    }

    /// <summary>ディスプレイスメント：時刻に依存せず、同じ入力なら同じ結果になる。</summary>
    [Fact]
    public void Displacement_IsDeterministic()
    {
        using var src = BuildGradientPattern();
        var effect = new ClipEffect { Kind = EffectKind.Displacement, Amount = 30, Radius = 1 };
        using var first = NewEffectsDistort.ApplyDisplacement(src, effect);
        using var second = NewEffectsDistort.ApplyDisplacement(src, effect);
        Assert.Equal(0, CountDiff(first, second));
    }

    /// <summary>ディスプレイスメント：範囲の端・範囲外のパラメータでも例外が出ずクランプされる。</summary>
    [Fact]
    public void Displacement_Extremes_DoNotThrow()
    {
        using var src = BuildGradientPattern();
        using var a = NewEffectsDistort.ApplyDisplacement(src,
            new ClipEffect { Kind = EffectKind.Displacement, Amount = 100, Radius = 4 });
        AssertSameSize(src, a);
        using var b = NewEffectsDistort.ApplyDisplacement(src,
            new ClipEffect { Kind = EffectKind.Displacement, Amount = 100, Radius = 0.5 });
        AssertSameSize(src, b);
        using var c = NewEffectsDistort.ApplyDisplacement(src,
            new ClipEffect { Kind = EffectKind.Displacement, Amount = 500, Radius = 10 });
        AssertSameSize(src, c);
    }

    /// <summary>ディスプレイスメント：小さいビットマップでも例外が出ない。</summary>
    [Fact]
    public void Displacement_TinyBitmaps_DoNotThrow()
        => AssertTinyBitmapsWork(src => NewEffectsDistort.ApplyDisplacement(src,
            new ClipEffect { Kind = EffectKind.Displacement, Amount = 30, Radius = 1 }));

    // ---- バルジ ----

    /// <summary>バルジ：既定値で見た目が変わる。</summary>
    [Fact]
    public void Bulge_Default_ChangesImage()
    {
        using var src = BuildGradientPattern();
        using var bulged = NewEffectsDistort.ApplyBulge(src,
            new ClipEffect { Kind = EffectKind.Bulge, Amount = 0.5, Radius = 0.6 });
        Assert.True(CountDiff(src, bulged) >= 100);
    }

    /// <summary>バルジ：量 0 で変化なし。</summary>
    [Fact]
    public void Bulge_ZeroAmount_IsIdentity()
    {
        using var src = BuildGradientPattern();
        using var bulged = NewEffectsDistort.ApplyBulge(src,
            new ClipEffect { Kind = EffectKind.Bulge, Amount = 0, Radius = 0.6 });
        Assert.Equal(0, CountDiff(src, bulged));
    }

    /// <summary>
    /// バルジ：中心が膨らむ（拡大）。中心の東 (100,45) は中心寄りの (92.5,45) あたり
    /// からサンプルされるため R が小さくなる（元の R(100)=160、期待値 ≒148）。
    /// 逆（縮小）なら R は大きくなる。中心そのものは変わらない。
    /// </summary>
    [Fact]
    public void Bulge_MagnifiesCenter()
    {
        using var src = BuildGradientPattern();
        using var bulged = NewEffectsDistort.ApplyBulge(src,
            new ClipEffect { Kind = EffectKind.Bulge, Amount = 0.5, Radius = 0.6 });
        Assert.True(bulged.GetPixel(100, 45).Red < 155);
        Assert.Equal(src.GetPixel(80, 45), bulged.GetPixel(80, 45));
    }

    /// <summary>バルジ：範囲の端・範囲外のパラメータでも例外が出ずクランプされる。</summary>
    [Fact]
    public void Bulge_Extremes_DoNotThrow()
    {
        using var src = BuildGradientPattern();
        using var a = NewEffectsDistort.ApplyBulge(src,
            new ClipEffect { Kind = EffectKind.Bulge, Amount = 1, Radius = 0.2 });
        AssertSameSize(src, a);
        using var b = NewEffectsDistort.ApplyBulge(src,
            new ClipEffect { Kind = EffectKind.Bulge, Amount = 1, Radius = 1.0 });
        AssertSameSize(src, b);
        using var c = NewEffectsDistort.ApplyBulge(src,
            new ClipEffect { Kind = EffectKind.Bulge, Amount = 5, Radius = 0.01 });
        AssertSameSize(src, c);
    }

    /// <summary>バルジ：小さいビットマップでも例外が出ない。</summary>
    [Fact]
    public void Bulge_TinyBitmaps_DoNotThrow()
        => AssertTinyBitmapsWork(src => NewEffectsDistort.ApplyBulge(src,
            new ClipEffect { Kind = EffectKind.Bulge, Amount = 0.5, Radius = 0.6 }));

    // ---- ウォーターリップル ----

    /// <summary>ウォーターリップル：既定値で見た目が変わる。</summary>
    [Fact]
    public void WaterRipple_Default_ChangesImage()
    {
        using var src = BuildGradientPattern();
        using var rippled = NewEffectsDistort.ApplyWaterRipple(src,
            new ClipEffect { Kind = EffectKind.WaterRipple, Amount = 8, Radius = 40 }, 0.5);
        Assert.True(CountDiff(src, rippled) >= 100);
    }

    /// <summary>ウォーターリップル：量 0 で変化なし。</summary>
    [Fact]
    public void WaterRipple_ZeroAmount_IsIdentity()
    {
        using var src = BuildGradientPattern();
        using var rippled = NewEffectsDistort.ApplyWaterRipple(src,
            new ClipEffect { Kind = EffectKind.WaterRipple, Amount = 0, Radius = 40 }, 0.5);
        Assert.Equal(0, CountDiff(src, rippled));
    }

    /// <summary>ウォーターリップル：時刻で位相が動き、見た目が変わる。</summary>
    [Fact]
    public void WaterRipple_TimeMovesPhase()
    {
        using var src = BuildGradientPattern();
        var effect = new ClipEffect { Kind = EffectKind.WaterRipple, Amount = 8, Radius = 40 };
        using var t0 = NewEffectsDistort.ApplyWaterRipple(src, effect, 0);
        using var tHalf = NewEffectsDistort.ApplyWaterRipple(src, effect, 0.5);
        Assert.True(CountDiff(t0, tHalf) >= 100);
        using var tQuarter = NewEffectsDistort.ApplyWaterRipple(src, effect, 0.25);
        Assert.True(CountDiff(t0, tQuarter) >= 100);
    }

    /// <summary>ウォーターリップル：同じ時刻なら同じ結果になる（決定的）。</summary>
    [Fact]
    public void WaterRipple_IsDeterministic()
    {
        using var src = BuildGradientPattern();
        var effect = new ClipEffect { Kind = EffectKind.WaterRipple, Amount = 8, Radius = 40 };
        using var first = NewEffectsDistort.ApplyWaterRipple(src, effect, 0.5);
        using var second = NewEffectsDistort.ApplyWaterRipple(src, effect, 0.5);
        Assert.Equal(0, CountDiff(first, second));
    }

    /// <summary>ウォーターリップル：範囲の端・範囲外のパラメータでも例外が出ずクランプされる。</summary>
    [Fact]
    public void WaterRipple_Extremes_DoNotThrow()
    {
        using var src = BuildGradientPattern();
        using var a = NewEffectsDistort.ApplyWaterRipple(src,
            new ClipEffect { Kind = EffectKind.WaterRipple, Amount = 50, Radius = 10 }, 0.5);
        AssertSameSize(src, a);
        using var b = NewEffectsDistort.ApplyWaterRipple(src,
            new ClipEffect { Kind = EffectKind.WaterRipple, Amount = 50, Radius = 200 }, 0.5);
        AssertSameSize(src, b);
        using var c = NewEffectsDistort.ApplyWaterRipple(src,
            new ClipEffect { Kind = EffectKind.WaterRipple, Amount = 500, Radius = 1 }, 0.5);
        AssertSameSize(src, c);
    }

    /// <summary>ウォーターリップル：小さいビットマップでも例外が出ない。</summary>
    [Fact]
    public void WaterRipple_TinyBitmaps_DoNotThrow()
        => AssertTinyBitmapsWork(src => NewEffectsDistort.ApplyWaterRipple(src,
            new ClipEffect { Kind = EffectKind.WaterRipple, Amount = 8, Radius = 40 }, 0.5));
}
