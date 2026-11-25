// 新エフェクト5種（ブルーム／ライトリーク／オールドフィルム／レターボックス／
// スキャンライン）の描画本体。ClipCompositor へ配線される前の独立ヘルパー。
// 契約：src は変更せず、必ず新しい SKBitmap を返す（効果が無効な量のときは src のコピー）。
// ビットマップ形式は Bgra8888・Premul。画素を直接いじる場合は Marshal.Copy で
// バイト配列に取り出す既存の流儀（CpuPixelEffects.Apply）に倣い、
// RGB がアルファを超えないよう丸める（Premul を壊さない）。

using System.Runtime.InteropServices;
using Editor.Core;
using SkiaSharp;

namespace Editor.Render;

/// <summary>新エフェクト5種の描画ヘルパー（ClipCompositor から呼ばれる想定）。</summary>
internal static class NewEffectsLight
{
    /// <summary>
    /// ブルーム：明るい部分だけを抜いてぼかし、加算で重ねる。
    /// Amount = にじみの強さ（0〜1。0 で効果なし）、Radius = ぼかし半径 px（1〜100）。
    /// 明暗の抜き出しのしきい値は輝度 128 固定（定義テーブルにしきい値の項目が無いため）。
    /// </summary>
    internal static SKBitmap ApplyBloom(SKBitmap src, ClipEffect effect)
    {
        var strength = Math.Clamp(effect.Amount, 0, 1);
        var radius = Math.Clamp(effect.Radius, 1, 100);
        var dst = src.Copy();
        if (strength <= 0)
            return dst;

        using var bright = ExtractBright(src);
        using var blurred = new SKBitmap(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var blurCanvas = new SKCanvas(blurred))
        using (var blurFilter = SKImageFilter.CreateBlur((float)radius, (float)radius))
        using (var blurPaint = new SKPaint { ImageFilter = blurFilter })
            blurCanvas.DrawBitmap(bright, new SKRect(0, 0, src.Width, src.Height), blurPaint);

        // ぼかした明部を加算で重ねる（Glow と同じく、ペイントのアルファで強さを掛ける）。
        using var canvas = new SKCanvas(dst);
        using var plusPaint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, (byte)Math.Round(strength * 255)),
            BlendMode = SKBlendMode.Plus
        };
        canvas.DrawBitmap(blurred, new SKRect(0, 0, src.Width, src.Height), plusPaint);
        return dst;
    }

    /// <summary>輝度 128 より明るい画素だけを残したビットマップ（それ以外は透明）。</summary>
    private static SKBitmap ExtractBright(SKBitmap src)
    {
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
                var luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                if (luminance > 128)
                {
                    dstBytes[i] = b;
                    dstBytes[i + 1] = g;
                    dstBytes[i + 2] = r;
                    dstBytes[i + 3] = a;
                }
            }
        }

        var dst = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        Marshal.Copy(dstBytes, 0, dst.GetPixels(), dstBytes.Length);
        return dst;
    }

    /// <summary>
    /// ライトリーク：画面の端（左上）から暖かい光が漏れた色かぶりをスクリーン合成で付ける。
    /// Amount = 光漏れの強さ（0〜1。0 で効果なし）、Radius = 漏れの広がり（0.5〜2.0。
    /// 1.0 が既定の広がり。大きいほど画面の奥まで届く）。
    /// 光源は左上の1点のみ（「主に左上」に従い、右上など第2の光源は付けない）。
    /// 届く距離は広がり × 対角線の 0.6 倍。中心の色は暖かい橙 (255, 190, 110)。
    /// </summary>
    internal static SKBitmap ApplyLightLeak(SKBitmap src, ClipEffect effect)
    {
        var strength = Math.Clamp(effect.Amount, 0, 1);
        var spread = Math.Clamp(effect.Radius, 0.5, 2.0);
        var dst = src.Copy();
        if (strength <= 0)
            return dst;

        var diagonal = Math.Sqrt((double)src.Width * src.Width + (double)src.Height * src.Height);
        var reach = (float)(spread * 0.6 * diagonal);
        var center = new SKColor(255, 190, 110, (byte)Math.Round(strength * 255));
        var edge = new SKColor(255, 190, 110, 0);
        using var shader = SKShader.CreateRadialGradient(
            new SKPoint(0, 0), reach,
            new[] { center, edge }, new[] { 0f, 1f }, SKShaderTileMode.Clamp);
        using var canvas = new SKCanvas(dst);
        // Color は白にしておく（シェーダーの色をそのまま使う。万一ペイント色で変調される
        // 実装でも、白なら色は変わらない）。
        using var paint = new SKPaint { Color = SKColors.White, Shader = shader, BlendMode = SKBlendMode.Screen };
        canvas.DrawRect(new SKRect(0, 0, src.Width, src.Height), paint);
        return dst;
    }

    /// <summary>
    /// オールドフィルム：縦の傷・ほこりの斑点・ちらつき（全体の明るさの揺れ）を付ける。
    /// Amount = 劣化の強さ（0〜1。0 で劣化なし。傷・ほこり・ちらつきがまとめて強くなる）、
    /// Radius = 傷の密度（0〜1。0 で傷なし。多いほど傷・ほこりが増える）。
    /// 傷とちらつきは timeSeconds（ミリ秒に丸めたフレーム番号）から決まるハッシュで決まるため、
    /// 同じ時刻なら同じ傷になり、時刻が変わると傷・ちらつきが変わる（VHSグリッチ・ノイズと同じ流儀）。
    /// ちらつきは Amount だけに依存し、Radius = 0 でも付く（傷・ほこりだけが Radius 依存）。
    /// </summary>
    internal static SKBitmap ApplyOldFilm(SKBitmap src, ClipEffect effect, double timeSeconds)
    {
        var strength = Math.Clamp(effect.Amount, 0, 1);
        var density = Math.Clamp(effect.Radius, 0, 1);
        var width = src.Width;
        var height = src.Height;
        var rowBytes = src.RowBytes;
        var srcBytes = new byte[rowBytes * height];
        Marshal.Copy(src.GetPixels(), srcBytes, 0, srcBytes.Length);
        var dstBytes = (byte[])srcBytes.Clone();
        if (strength <= 0)
        {
            var copy = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            Marshal.Copy(dstBytes, 0, copy.GetPixels(), dstBytes.Length);
            return copy;
        }

        // ノイズ・VHSグリッチと同じく、時刻はミリ秒単位のフレーム番号にする。
        var frameIndex = (int)Math.Round(timeSeconds * 1000);

        // ちらつき：全体の明るさを ±10% × 強さの範囲で揺らす（時刻で決まる）。
        var flicker = 1 + (HashValue(0, 0, frameIndex) - 0.5) * 2 * 0.10 * strength;

        // ほこり：画素ごとのハッシュで決まる斑点（白または黒）。密度 1 で画素の 0.4%。
        var dustProbability = density * 0.004;
        for (var y = 0; y < height; y++)
        {
            var row = y * rowBytes;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                var a = dstBytes[i + 3];
                var r = (byte)Math.Clamp(dstBytes[i + 2] * flicker, 0, a);
                var g = (byte)Math.Clamp(dstBytes[i + 1] * flicker, 0, a);
                var b = (byte)Math.Clamp(dstBytes[i] * flicker, 0, a);
                if (dustProbability > 0 && HashValue(x, y, frameIndex + 12345) < dustProbability)
                {
                    // 白い斑点か黒い斑点かもハッシュで決める（時刻が変わると入れ替わる）。
                    if (HashValue(x, y, frameIndex + 54321) < 0.5)
                    {
                        var add = (byte)Math.Round(180 * strength);
                        r = (byte)Math.Min(a, r + add);
                        g = (byte)Math.Min(a, g + add);
                        b = (byte)Math.Min(a, b + add);
                    }
                    else
                    {
                        r = (byte)(r * 0.3);
                        g = (byte)(g * 0.3);
                        b = (byte)(b * 0.3);
                    }
                }
                dstBytes[i] = b;
                dstBytes[i + 1] = g;
                dstBytes[i + 2] = r;
            }
        }

        // 縦の傷：画面の高さ方向に1px幅で通る線。明るい傷が7割、暗い傷が3割。
        // 本数は密度に比例（幅160で密度0.5なら約10本）。位置・明暗・濃さは時刻で決まる。
        var scratchCount = (int)Math.Round(density * width * 0.12);
        for (var s = 0; s < scratchCount; s++)
        {
            var x = (int)(HashValue(s, 7, frameIndex) * width);
            if (x < 0 || x >= width)
                continue;
            var isBright = HashValue(s, 13, frameIndex) > 0.3;
            var intensity = 0.6 + 0.4 * HashValue(s, 29, frameIndex); // 傷ごとの濃さのばらつき。
            for (var y = 0; y < height; y++)
            {
                var i = y * rowBytes + x * 4;
                var a = dstBytes[i + 3];
                if (isBright)
                {
                    var add = (byte)Math.Round(140 * strength * intensity);
                    dstBytes[i] = (byte)Math.Min(a, dstBytes[i] + add);
                    dstBytes[i + 1] = (byte)Math.Min(a, dstBytes[i + 1] + add);
                    dstBytes[i + 2] = (byte)Math.Min(a, dstBytes[i + 2] + add);
                }
                else
                {
                    var factor = 1 - 0.6 * strength * intensity;
                    dstBytes[i] = (byte)(dstBytes[i] * factor);
                    dstBytes[i + 1] = (byte)(dstBytes[i + 1] * factor);
                    dstBytes[i + 2] = (byte)(dstBytes[i + 2] * factor);
                }
            }
        }

        var dst = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        Marshal.Copy(dstBytes, 0, dst.GetPixels(), dstBytes.Length);
        return dst;
    }

    /// <summary>
    /// レターボックス：上下に黒帯を描く。
    /// Amount = 上下の黒帯の高さ（画面高さに対する割合、0〜0.25。既定0.12 = 上下各12%。0 で帯なし）。
    /// Radius は使わない（定義テーブルどおり）。
    /// </summary>
    internal static SKBitmap ApplyLetterbox(SKBitmap src, ClipEffect effect)
    {
        var fraction = Math.Clamp(effect.Amount, 0, 0.25);
        var dst = src.Copy();
        if (fraction <= 0)
            return dst;

        var barHeight = (int)Math.Round(src.Height * fraction);
        if (barHeight <= 0)
            return dst;

        using var canvas = new SKCanvas(dst);
        using var paint = new SKPaint { Color = SKColors.Black };
        canvas.DrawRect(new SKRect(0, 0, src.Width, barHeight), paint);
        canvas.DrawRect(new SKRect(0, src.Height - barHeight, src.Width, src.Height), paint);
        return dst;
    }

    /// <summary>
    /// スキャンライン：横線を暗くする。
    /// Amount = 線の濃さ（0〜1。0 で線なし、1 で真っ黒な線）、
    /// Radius = 線の間隔 px（2〜8。小さいほど細かい走査線）。
    /// 線は y = 間隔-1, 2×間隔-1, … の行（VHSグリッチの走査線と同じ位置の決め方）。
    /// 画素ループではなく黒い矩形の描画で暗くするため、4K でも軽い。
    /// </summary>
    internal static SKBitmap ApplyScanlines(SKBitmap src, ClipEffect effect)
    {
        var darkness = Math.Clamp(effect.Amount, 0, 1);
        var spacing = (int)Math.Clamp(Math.Round(effect.Radius), 2, 8);
        var dst = src.Copy();
        if (darkness <= 0)
            return dst;

        using var canvas = new SKCanvas(dst);
        using var paint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, (byte)Math.Round(darkness * 255))
        };
        for (var y = spacing - 1; y < src.Height; y += spacing)
            canvas.DrawRect(new SKRect(0, y, src.Width, y + 1), paint);
        return dst;
    }

    /// <summary>位置とフレームから決まる 0〜1 の疑似乱数（RenderHelpers.NoiseValue と同じハッシュ）。</summary>
    private static double HashValue(int x, int y, int frame)
    {
        uint h = (uint)(x * 374761393 + y * 668265263 + frame * 974634211);
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;
        return (h & 0xFFFF) / 65535.0;
    }
}
