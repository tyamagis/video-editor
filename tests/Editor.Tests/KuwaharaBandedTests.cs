// クワハラ（油絵風）の SAT バンド分割のビット同一性テスト。
// 帯高を強制的に小さくしても（帯境界が大量にできても）、自動決定（＝従来の
// 全体 SAT と同じ結果になる帯高を含む）の出力と画素単位で完全一致することを担保する。
// 模様は決定的（固定シードのノイズ＋グラデーション＋図形）で、帯境界の前後で
// 半径分の重なりが効くよう、境界付近にも変化のある絵にする。

using System.Runtime.InteropServices;
using Editor.Core;
using Editor.Render;
using SkiaSharp;
using Xunit;

namespace Editor.Tests;

/// <summary>クワハラのバンド分割が従来出力とビット単位で同一であることのテスト。</summary>
public sealed class KuwaharaBandedTests
{
    /// <summary>決定的なテスト画像（対角グラデーション＋固定シードノイズ＋円・矩形）。</summary>
    private static SKBitmap BuildPattern(int width, int height)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bytes = new byte[bitmap.RowBytes * height];
        var rng = new Random(12345);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = y * bitmap.RowBytes + x * 4;
            var gx = (double)x / Math.Max(1, width - 1);
            var gy = (double)y / Math.Max(1, height - 1);
            var r = Math.Clamp((int)Math.Round(255 * gx) + rng.Next(-32, 33), 0, 255);
            var g = Math.Clamp((int)Math.Round(255 * gy) + rng.Next(-32, 33), 0, 255);
            var b = Math.Clamp((int)Math.Round(255 * (1 - gx) * gy) + rng.Next(-32, 33), 0, 255);
            bytes[i] = (byte)b;
            bytes[i + 1] = (byte)g;
            bytes[i + 2] = (byte)r;
            bytes[i + 3] = 255;
        }
        // 円と矩形（帯境界をまたぐ位置に置く）。
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var dx = x - width / 3;
            var dy = y - height / 2;
            var inCircle = dx * dx + dy * dy <= (height / 4) * (height / 4);
            var inRect = x >= width / 2 && x < width * 3 / 4 && y >= height / 3 && y < height * 2 / 3;
            if (!inCircle && !inRect)
                continue;
            var i = y * bitmap.RowBytes + x * 4;
            bytes[i] = 60;
            bytes[i + 1] = 200;
            bytes[i + 2] = 220;
            bytes[i + 3] = 255;
        }
        Marshal.Copy(bytes, 0, bitmap.GetPixels(), bytes.Length);
        return bitmap;
    }

    private static byte[] Pixels(SKBitmap bitmap)
    {
        var bytes = new byte[bitmap.RowBytes * bitmap.Height];
        Marshal.Copy(bitmap.GetPixels(), bytes, 0, bytes.Length);
        return bytes;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(16)]
    public void Banded_MatchesAutomatic_AnyBandHeight(int bandHeight)
    {
        using var src = BuildPattern(96, 64);
        var effect = new ClipEffect { Kind = EffectKind.Kuwahara, Amount = 8, Radius = 1.0 };

        using var expected = NewEffectsStyle.ApplyKuwahara(src, effect);
        using var actual = NewEffectsStyle.ApplyKuwaharaBanded(src, effect, bandHeight);

        Assert.Equal(Pixels(expected), Pixels(actual));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Banded_MatchesAutomatic_Radius3HalfStrength(int bandHeight)
    {
        using var src = BuildPattern(80, 50);
        var effect = new ClipEffect { Kind = EffectKind.Kuwahara, Amount = 3, Radius = 0.5 };

        using var expected = NewEffectsStyle.ApplyKuwahara(src, effect);
        using var actual = NewEffectsStyle.ApplyKuwaharaBanded(src, effect, bandHeight);

        Assert.Equal(Pixels(expected), Pixels(actual));
    }

    [Fact]
    public void Banded_TallerThanBand_HeightNotMultipleOfBand()
    {
        // 高さが帯高で割り切れない場合（最後の帯が短い）も一致する。
        using var src = BuildPattern(64, 61);
        var effect = new ClipEffect { Kind = EffectKind.Kuwahara, Amount = 8, Radius = 1.0 };

        using var expected = NewEffectsStyle.ApplyKuwahara(src, effect);
        using var actual = NewEffectsStyle.ApplyKuwaharaBanded(src, effect, 8);

        Assert.Equal(Pixels(expected), Pixels(actual));
    }

    [Fact]
    public void Banded_SmallImage_HeightBelowRadius()
    {
        // 高さが半径より小さい画像でも帯処理が壊れず一致する。
        using var src = BuildPattern(40, 5);
        var effect = new ClipEffect { Kind = EffectKind.Kuwahara, Amount = 8, Radius = 1.0 };

        using var expected = NewEffectsStyle.ApplyKuwahara(src, effect);
        using var actual = NewEffectsStyle.ApplyKuwaharaBanded(src, effect, 2);

        Assert.Equal(Pixels(expected), Pixels(actual));
    }
}
