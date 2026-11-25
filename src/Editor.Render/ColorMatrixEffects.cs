// 色調系エフェクトのカラーマトリクス合成（色反転・色相・彩度・モノクロ・セピア・色温度）。
// FrameRenderer.cs から切り出した、状態を持たない静的ヘルパー。
// マトリクスはネイティブ側で適用されるため、画素ループより速い。

using Editor.Core;
using SkiaSharp;

namespace Editor.Render;

/// <summary>色調系エフェクトのカラーマトリクス（状態を持たない静的ヘルパー）。</summary>
internal static class ColorMatrixEffects
{
    /// <summary>
    /// 色調系エフェクト（色反転・色相・彩度・モノクロ・セピア）を1つのカラーマトリクスに合成する。
    /// 該当するエフェクトが無ければ null。マトリクスはネイティブ側で適用されるため、画素ループより速い。
    /// </summary>
    internal static float[]? BuildColorMatrix(IReadOnlyList<ClipEffect> effects)
    {
        float[]? matrix = null;
        foreach (var effect in effects)
        {
            float[]? next = effect.Kind switch
            {
                EffectKind.Invert when effect.Amount > 0 => InvertMatrix(effect.Amount),
                EffectKind.HueShift when Math.Abs(effect.Amount) > 1e-6 => HueRotateMatrix(effect.Amount),
                EffectKind.Saturation when Math.Abs(effect.Amount - 1) > 1e-6 => SaturationMatrix(effect.Amount),
                EffectKind.Monochrome when effect.Amount > 0 => MonochromeMatrix(effect.Amount),
                EffectKind.Sepia when effect.Amount > 0 => SepiaMatrix(effect.Amount),
                EffectKind.ColorTemp when Math.Abs(effect.Amount) > 1e-6 => NewEffectsColor.ColorTempMatrix(effect),
                _ => null
            };
            if (next is null)
                continue;
            // 後に来るエフェクトを後に適用する（matrix は先に適用される側）。
            matrix = matrix is null ? next : MultiplyColorMatrix(next, matrix);
        }
        return matrix;
    }

    /// <summary>4x5 カラーマトリクス同士の合成（a∘b：b を先に、a を後に適用する）。</summary>
    private static float[] MultiplyColorMatrix(float[] a, float[] b)
    {
        var result = new float[20];
        for (var row = 0; row < 4; row++)
        {
            for (var col = 0; col < 4; col++)
            {
                var sum = 0f;
                for (var k = 0; k < 4; k++)
                    sum += a[row * 5 + k] * b[k * 5 + col];
                result[row * 5 + col] = sum;
            }
            var offset = a[row * 5 + 4];
            for (var k = 0; k < 4; k++)
                offset += a[row * 5 + k] * b[k * 5 + 4];
            result[row * 5 + 4] = offset;
        }
        return result;
    }

    /// <summary>
    /// 色反転のマトリクス（量 t：0 で元の色、1 で完全反転）。
    /// Skia のカラーマトリクスは 0〜1 の浮動小数で扱うため、オフセットも 0〜1
    /// スケールにする（255 だとクランプで全白になる。旧版のバグ）。
    /// </summary>
    private static float[] InvertMatrix(double t)
    {
        var f = (float)(1 - 2 * t);
        var offset = (float)t;
        return new float[]
        {
            f, 0, 0, 0, offset,
            0, f, 0, 0, offset,
            0, 0, f, 0, offset,
            0, 0, 0, 1, 0
        };
    }

    /// <summary>色相回転のマトリクス（CSS/SVG の hue-rotate と同じ式。角度は度）。</summary>
    private static float[] HueRotateMatrix(double degrees)
    {
        var radians = degrees * Math.PI / 180;
        var c = (float)Math.Cos(radians);
        var s = (float)Math.Sin(radians);
        return new float[]
        {
            0.213f + 0.787f * c - 0.213f * s, 0.715f - 0.715f * c - 0.715f * s, 0.072f - 0.072f * c + 0.928f * s, 0, 0,
            0.213f - 0.213f * c + 0.143f * s, 0.715f + 0.285f * c + 0.140f * s, 0.072f - 0.072f * c - 0.283f * s, 0, 0,
            0.213f - 0.213f * c - 0.787f * s, 0.715f - 0.715f * c + 0.715f * s, 0.072f + 0.928f * c + 0.072f * s, 0, 0,
            0, 0, 0, 1, 0
        };
    }

    /// <summary>彩度のマトリクス（倍率 s：0 でモノクロ、1 で元のまま）。</summary>
    private static float[] SaturationMatrix(double s)
    {
        var f = (float)s;
        return new float[]
        {
            0.213f + 0.787f * f, 0.715f - 0.715f * f, 0.072f - 0.072f * f, 0, 0,
            0.213f - 0.213f * f, 0.715f + 0.285f * f, 0.072f - 0.072f * f, 0, 0,
            0.213f - 0.213f * f, 0.715f - 0.715f * f, 0.072f + 0.928f * f, 0, 0,
            0, 0, 0, 1, 0
        };
    }

    /// <summary>モノクロ化のマトリクス（量 t：0 で元の色、1 で完全モノクロ。輝度は 0.213R + 0.715G + 0.072B）。</summary>
    private static float[] MonochromeMatrix(double t)
    {
        var f = (float)(1 - t);
        var m = (float)t;
        return new float[]
        {
            f + 0.213f * m, 0.715f * m, 0.072f * m, 0, 0,
            0.213f * m, f + 0.715f * m, 0.072f * m, 0, 0,
            0.213f * m, 0.715f * m, f + 0.072f * m, 0, 0,
            0, 0, 0, 1, 0
        };
    }

    /// <summary>セピア化のマトリクス（量 t：0 で元の色、1 で完全セピア）。</summary>
    private static float[] SepiaMatrix(double t)
    {
        var f = (float)(1 - t);
        var m = (float)t;
        return new float[]
        {
            f + 0.393f * m, 0.769f * m, 0.189f * m, 0, 0,
            0.349f * m, f + 0.686f * m, 0.168f * m, 0, 0,
            0.272f * m, 0.534f * m, f + 0.131f * m, 0, 0,
            0, 0, 0, 1, 0
        };
    }

    /// <summary>カラーマトリクスを画像全体に適用した新しいビットマップを作る。</summary>
    internal static SKBitmap ApplyColorMatrix(SKBitmap src, float[] matrix)
    {
        var dst = new SKBitmap(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(dst);
        using var filter = SKColorFilter.CreateColorMatrix(matrix);
        using var paint = new SKPaint { ColorFilter = filter };
        canvas.DrawBitmap(src, new SKRect(0, 0, src.Width, src.Height), paint);
        return dst;
    }
}
