// 新エフェクト4種のうち色調系の描画本体（色温度・レベル補正・スプリットトーン・
// グラデーションマップ）。ClipCompositor への配線は別途行うため、ここは
// 描画ヘルパーだけを持つ。src は変更せず、常に新しい SKBitmap を返す。

using System.Runtime.InteropServices;
using Editor.Core;
using SkiaSharp;

namespace Editor.Render;

/// <summary>色温度・レベル補正・スプリットトーン・グラデーションマップの描画ヘルパー。</summary>
internal static class NewEffectsColor
{
    /// <summary>
    /// 色温度の 4x5 カラーマトリクス（20要素）。Amount = 温度（-1〜1。-1 で冷たい青方向、
    /// +1 で暖かい橙方向、0 で変化なし）。対角スケールのみで、黒は黒のまま。
    /// 暖色側は R を上げ B を下げ、冷色側はその逆。Skia のカラーマトリクスは
    /// 0〜1 スケールのためオフセットは使わない（255倍にすると全白になる旧バグに注意）。
    /// Amount 0 では単位行列を返す。
    /// </summary>
    internal static float[] ColorTempMatrix(ClipEffect effect)
    {
        var t = (float)Math.Clamp(effect.Amount, -1, 1);
        var rScale = 1 + 0.25f * t;
        var bScale = 1 - 0.25f * t;
        return new float[]
        {
            rScale, 0, 0, 0, 0,
            0, 1, 0, 0, 0,
            0, 0, bScale, 0, 0,
            0, 0, 0, 1, 0
        };
    }

    /// <summary>
    /// レベル補正。Amount = 黒点（0〜0.5。この明るさ以下を黒に潰す）、Radius = 白点
    /// （0.5〜1.0。この明るさ以上を白に飛ばす）、Midtones = ガンマ（0.2〜5.0。
    /// 1.0 で変化なし、&lt;1 で明るく、&gt;1 で暗く）。チャンネルごとの LUT（256段）で適用する。
    /// 黒点 0・白点 1.0・ガンマ 1.0 なら src のコピーを返す（恒等）。
    /// 黒点 &gt;= 白点の逆転入力は、白点を黒点より少しだけ大きく補正して破綻を防ぐ
    /// （白点 = min(1.0, 黒点 + 0.01)。入力の順序を入れ替えるのではなく、範囲を
    /// 潰さない最小限の補正に留める）。
    /// </summary>
    internal static SKBitmap ApplyLevels(SKBitmap src, ClipEffect effect)
    {
        var black = Math.Clamp(effect.Amount, 0, 0.5);
        var white = Math.Clamp(effect.Radius, 0.5, 1.0);
        var gamma = Math.Clamp(effect.Midtones, 0.2, 5.0);
        if (white <= black)
            white = Math.Min(1.0, black + 0.01);
        if (black <= 0 && white >= 1.0 && Math.Abs(gamma - 1.0) < 1e-9)
            return src.Copy();

        var table = BuildLevelsTable(black, white, gamma);
        var width = src.Width;
        var height = src.Height;
        var rowBytes = src.RowBytes;
        var srcBytes = new byte[rowBytes * height];
        Marshal.Copy(src.GetPixels(), srcBytes, 0, srcBytes.Length);
        var dstBytes = new byte[rowBytes * height];

        for (var y = 0; y < height; y++)
        {
            var row = y * rowBytes;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                var a = srcBytes[i + 3];
                // Premul を壊さないよう、RGB はアルファ以下に丸める。
                dstBytes[i] = (byte)Math.Min(table[srcBytes[i]], a);
                dstBytes[i + 1] = (byte)Math.Min(table[srcBytes[i + 1]], a);
                dstBytes[i + 2] = (byte)Math.Min(table[srcBytes[i + 2]], a);
                dstBytes[i + 3] = a;
            }
        }

        var dst = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        Marshal.Copy(dstBytes, 0, dst.GetPixels(), dstBytes.Length);
        return dst;
    }

    /// <summary>レベル補正の LUT（256段）。x を黒点〜白点の範囲で 0〜1 に伸ばし、ガンマを掛ける。</summary>
    private static byte[] BuildLevelsTable(double black, double white, double gamma)
    {
        var table = new byte[256];
        var range = white - black;
        for (var i = 0; i < 256; i++)
        {
            var x = i / 255.0;
            var y = Math.Clamp((x - black) / range, 0, 1);
            y = Math.Pow(y, gamma);
            table[i] = (byte)Math.Clamp(Math.Round(y * 255), 0, 255);
        }
        return table;
    }

    /// <summary>
    /// スプリットトーン。Amount = ハイライトの色相（度、0〜360。既定45 = 暖かい橙）、
    /// Radius = シャドウの色相（度、0〜360。既定220 = 冷たい青）、Midtones = 着色の強さ
    /// （0〜1。既定0.5）。輝度 0.5 を境に、明るい画素ほどハイライト色、暗い画素ほど
    /// シャドウ色へ寄せる（重みは輝度 0.5 で 0、両端で 1 の直線）。中間調はほぼ元の色。
    /// 強さ 0 なら src のコピーを返す。
    /// </summary>
    internal static SKBitmap ApplySplitTone(SKBitmap src, ClipEffect effect)
    {
        var strength = Math.Clamp(effect.Midtones, 0, 1);
        if (strength <= 0)
            return src.Copy();

        var highlight = ColorFromHue(effect.Amount);
        var shadow = ColorFromHue(effect.Radius);
        var width = src.Width;
        var height = src.Height;
        var rowBytes = src.RowBytes;
        var srcBytes = new byte[rowBytes * height];
        Marshal.Copy(src.GetPixels(), srcBytes, 0, srcBytes.Length);
        var dstBytes = new byte[rowBytes * height];

        for (var y = 0; y < height; y++)
        {
            var row = y * rowBytes;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                var b = srcBytes[i];
                var g = srcBytes[i + 1];
                var r = srcBytes[i + 2];
                var a = srcBytes[i + 3];

                var luminance = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
                var highlightWeight = Math.Clamp((luminance - 0.5) * 2, 0, 1) * strength;
                var shadowWeight = Math.Clamp((0.5 - luminance) * 2, 0, 1) * strength;

                double nr = r, ng = g, nb = b;
                if (highlightWeight > 0)
                {
                    nr += (highlight.R * 255 - r) * highlightWeight;
                    ng += (highlight.G * 255 - g) * highlightWeight;
                    nb += (highlight.B * 255 - b) * highlightWeight;
                }
                if (shadowWeight > 0)
                {
                    nr += (shadow.R * 255 - r) * shadowWeight;
                    ng += (shadow.G * 255 - g) * shadowWeight;
                    nb += (shadow.B * 255 - b) * shadowWeight;
                }

                // Premul を壊さないよう、RGB はアルファ以下に丸める。
                dstBytes[i] = (byte)Math.Clamp(Math.Round(nb), 0, a);
                dstBytes[i + 1] = (byte)Math.Clamp(Math.Round(ng), 0, a);
                dstBytes[i + 2] = (byte)Math.Clamp(Math.Round(nr), 0, a);
                dstBytes[i + 3] = a;
            }
        }

        var dst = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        Marshal.Copy(dstBytes, 0, dst.GetPixels(), dstBytes.Length);
        return dst;
    }

    /// <summary>
    /// グラデーションマップ。Amount = 暗部の色相（度、0〜360。既定220 = 青）、
    /// Radius = 明部の色相（度、0〜360。既定45 = 橙）。画素の輝度を 0〜1 として、
    /// 暗部色と明部色の間を線形補間した色で置き換える（暗い画素ほど暗部色、
    /// 明るい画素ほど明部色）。アルファはそのまま残し、置き換えた色をアルファで
    /// 掛けて Premul にする（半透明画素も壊れない）。
    /// </summary>
    internal static SKBitmap ApplyGradientMap(SKBitmap src, ClipEffect effect)
    {
        var shadow = ColorFromHue(effect.Amount);
        var highlight = ColorFromHue(effect.Radius);
        var width = src.Width;
        var height = src.Height;
        var rowBytes = src.RowBytes;
        var srcBytes = new byte[rowBytes * height];
        Marshal.Copy(src.GetPixels(), srcBytes, 0, srcBytes.Length);
        var dstBytes = new byte[rowBytes * height];

        for (var y = 0; y < height; y++)
        {
            var row = y * rowBytes;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                var a = srcBytes[i + 3];
                var luminance = (0.2126 * srcBytes[i + 2] + 0.7152 * srcBytes[i + 1] + 0.0722 * srcBytes[i]) / 255.0;
                var r = shadow.R + (highlight.R - shadow.R) * luminance;
                var g = shadow.G + (highlight.G - shadow.G) * luminance;
                var b = shadow.B + (highlight.B - shadow.B) * luminance;
                dstBytes[i] = (byte)Math.Round(b * a);
                dstBytes[i + 1] = (byte)Math.Round(g * a);
                dstBytes[i + 2] = (byte)Math.Round(r * a);
                dstBytes[i + 3] = a;
            }
        }

        var dst = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        Marshal.Copy(dstBytes, 0, dst.GetPixels(), dstBytes.Length);
        return dst;
    }

    /// <summary>
    /// 色相（度）→ RGB（各 0〜1）。彩度 0.6・明度 0.6 で固定（スプリットトーンの
    /// 着色色・グラデーションマップの両端色のどちらにも使う）。彩度・明度を 1 にすると
    /// 原色がきつく出過ぎ、0.6 なら着色として穏やかで、暗部・明部の区別も残る。
    /// 色相は 360 で丸め、範囲外（負・360超）も受け付ける。
    /// </summary>
    private static (double R, double G, double B) ColorFromHue(double hueDegrees)
    {
        const double saturation = 0.6;
        const double value = 0.6;
        var h = ((hueDegrees % 360) + 360) % 360 / 60.0; // 0〜6 の扇区。
        var c = value * saturation;
        var x = c * (1 - Math.Abs(h % 2 - 1));
        var m = value - c;
        var (r, g, b) = h switch
        {
            < 1 => (c, x, 0.0),
            < 2 => (x, c, 0.0),
            < 3 => (0.0, c, x),
            < 4 => (0.0, x, c),
            < 5 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return (r + m, g + m, b + m);
    }
}
