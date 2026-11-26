// 新エフェクト2種（Sharpen / Emboss）の描画本体。
// ClipCompositor から呼ばれる前提のヘルパー集。
// 契約：src は変更しない。常に新しい SKBitmap を返す（効果が無効な量のときは src のコピーを返す）。
// ビットマップ形式は Bgra8888・Premul。画素を直接いじる場合は Marshal.Copy で
// バイト配列に取り出し、RGB がアルファを超えないよう丸める（NewEffectsStyle と同じ流儀）。

using System.Runtime.InteropServices;
using Editor.Core;
using SkiaSharp;

namespace Editor.Render;

/// <summary>
/// 新エフェクト2種の描画ヘルパー（Sharpen / Emboss）。
/// すべて src を変更せず、新しい SKBitmap を返す。効果が無効な量のときは src のコピーを返す。
/// パラメータは定義テーブル（EffectDefinitions）の範囲に丸める。NaN・無限大は既定値扱いにする
/// （(int)NaN が int.MinValue になって画素ループが壊れるのを防ぐため）。
/// </summary>
internal static class NewEffectsDetail
{
    /// <summary>非有限値（NaN・無限大）を既定値に置き換える（パラメータの丸めの前処理）。</summary>
    private static double FiniteOr(double value, double fallback)
        => double.IsFinite(value) ? value : fallback;

    /// <summary>ビットマップの画素を BGRA のバイト配列に取り出す（Bgra8888・Premul 前提）。</summary>
    private static byte[] ReadBytes(SKBitmap bitmap)
    {
        var bytes = new byte[bitmap.RowBytes * bitmap.Height];
        Marshal.Copy(bitmap.GetPixels(), bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>BGRA のバイト配列から新しいビットマップを作る。</summary>
    private static SKBitmap BitmapFromBytes(byte[] bytes, int width, int height)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        Marshal.Copy(bytes, 0, bitmap.GetPixels(), bytes.Length);
        return bitmap;
    }

    /// <summary>
    /// シャープ（アンシャープマスク）：ぼかしたコピーとの差分（輪郭成分）を Amount 倍して
    /// 元画像に加算し、輪郭を強調する。畳み込みで書けばカーネル δ + Amount×(δ − ガウス) と等価。
    /// Amount = 強さ（0〜2、既定1。0 で変化なし）、Radius = ぼかし半径（px、1〜10、既定2。
    /// 大きいほど太い輪郭まで強調される）。
    /// ぼかしは自前の分離型ボックスブラー（3回適用でガウスぼかしを近似。端は画素の複製で処理）を
    /// Premul の RGB だけに適用する。Skia のブラーフィルタはタイルモードに Clamp を指定しても
    /// 端でアルファが落ちる実測挙動があり（平坦な画像でも端が変わってしまう）、
    /// 端の扱いを厳密に決めたいため自前にした。スライディングウィンドウで半径に依らず O(1)/画素。
    /// アルファは元のまま保持する（ぼかし側のアルファは使わない）。テキストのような
    /// 透明背景では、輪郭成分は Premul の RGB 差分として効くため、半透明の縁でも
    /// アルファが広がらず文字の形は変わらない。結果は Premul を壊さないよう
    /// アルファ以下（かつ 0 以上）に丸める。
    /// </summary>
    internal static SKBitmap ApplySharpen(SKBitmap src, ClipEffect effect)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(effect);
        var strength = Math.Clamp(FiniteOr(effect.Amount, 1), 0, 2);
        if (strength <= 0)
            return src.Copy();
        var radius = (int)Math.Clamp(Math.Round(FiniteOr(effect.Radius, 2)), 1, 10);

        var width = src.Width;
        var height = src.Height;
        var rowBytes = src.RowBytes;
        var srcBytes = ReadBytes(src);
        var blurBytes = BoxBlurRgb(srcBytes, width, height, rowBytes, radius);

        var dstBytes = new byte[rowBytes * height];
        Parallel.For(0, height, y =>
        {
            var row = y * rowBytes;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                var a = srcBytes[i + 3];
                dstBytes[i] = SharpenChannel(srcBytes[i], blurBytes[i], strength, a);
                dstBytes[i + 1] = SharpenChannel(srcBytes[i + 1], blurBytes[i + 1], strength, a);
                dstBytes[i + 2] = SharpenChannel(srcBytes[i + 2], blurBytes[i + 2], strength, a);
                dstBytes[i + 3] = a;
            }
        });
        return BitmapFromBytes(dstBytes, width, height);
    }

    /// <summary>
    /// Premul の RGB だけをぼかしたバイト列を返す（アルファはコピー元のまま使わない）。
    /// 分離型ボックスブラー（幅 2r+1）を横・縦に3往復適用してガウスぼかしを近似する。
    /// 端は座標を画像内に丸める（画素の複製）ため、平坦な画像は厳密に平坦なままになる
    /// （整数のスライディング和を幅で割るので、定数 c は和 = c×幅 → c に戻る）。
    /// </summary>
    private static byte[] BoxBlurRgb(byte[] srcBytes, int width, int height, int rowBytes, int radius)
    {
        var current = (byte[])srcBytes.Clone();
        var next = new byte[srcBytes.Length];
        for (var pass = 0; pass < 3; pass++)
        {
            BoxBlurHorizontal(current, next, width, height, rowBytes, radius);
            BoxBlurVertical(next, current, width, height, rowBytes, radius);
        }
        return current;
    }

    /// <summary>ボックスブラーの横パス（行ごとに BGR 3チャンネルのスライディング和。行は独立なので並列化する）。</summary>
    private static void BoxBlurHorizontal(byte[] src, byte[] dst, int width, int height, int rowBytes, int radius)
    {
        var count = 2 * radius + 1;
        var half = count / 2;
        Parallel.For(0, height, y =>
        {
            var row = y * rowBytes;
            int sumB = 0, sumG = 0, sumR = 0;
            for (var kx = -radius; kx <= radius; kx++)
            {
                var i = row + Math.Clamp(kx, 0, width - 1) * 4;
                sumB += src[i];
                sumG += src[i + 1];
                sumR += src[i + 2];
            }
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                dst[i] = (byte)((sumB + half) / count);
                dst[i + 1] = (byte)((sumG + half) / count);
                dst[i + 2] = (byte)((sumR + half) / count);
                dst[i + 3] = src[i + 3];
                var leave = row + Math.Clamp(x - radius, 0, width - 1) * 4;
                var enter = row + Math.Clamp(x + radius + 1, 0, width - 1) * 4;
                sumB += src[enter] - src[leave];
                sumG += src[enter + 1] - src[leave + 1];
                sumR += src[enter + 2] - src[leave + 2];
            }
        });
    }

    /// <summary>ボックスブラーの縦パス（列ごとに BGR 3チャンネルのスライディング和。列は独立なので並列化する）。</summary>
    private static void BoxBlurVertical(byte[] src, byte[] dst, int width, int height, int rowBytes, int radius)
    {
        var count = 2 * radius + 1;
        var half = count / 2;
        Parallel.For(0, width, x =>
        {
            var col = x * 4;
            int sumB = 0, sumG = 0, sumR = 0;
            for (var ky = -radius; ky <= radius; ky++)
            {
                var i = Math.Clamp(ky, 0, height - 1) * rowBytes + col;
                sumB += src[i];
                sumG += src[i + 1];
                sumR += src[i + 2];
            }
            for (var y = 0; y < height; y++)
            {
                var i = y * rowBytes + col;
                dst[i] = (byte)((sumB + half) / count);
                dst[i + 1] = (byte)((sumG + half) / count);
                dst[i + 2] = (byte)((sumR + half) / count);
                dst[i + 3] = src[i + 3];
                var leave = Math.Clamp(y - radius, 0, height - 1) * rowBytes + col;
                var enter = Math.Clamp(y + radius + 1, 0, height - 1) * rowBytes + col;
                sumB += src[enter] - src[leave];
                sumG += src[enter + 1] - src[leave + 1];
                sumR += src[enter + 2] - src[leave + 2];
            }
        });
    }

    /// <summary>1チャンネル分のアンシャープマスク（src + Amount×(src − blur)。0〜アルファに丸める）。</summary>
    private static byte SharpenChannel(byte value, byte blurred, double strength, byte alpha)
    {
        var result = value + strength * (value - blurred);
        return (byte)Math.Clamp(Math.Round(result), 0, alpha);
    }

    // エンボスの畳み込みカーネル（左上から光が当たる向き。中心は 0 でバイアス 128）。
    // 平坦な部分はカーネルの総和が 0 のためバイアスの 128（中間グレー）になる。
    private static readonly int[,] EmbossKernel =
    {
        { -2, -1, 0 },
        { -1, 0, 1 },
        { 0, 1, 2 }
    };

    /// <summary>
    /// エンボス：輝度に 3×3 のエンボスカーネルを畳み込み、128 をバイアスとした
    /// グレースケールの浮き彫りを作り、元画像と Amount でブレンドする
    /// （0 で元の画像、1 で浮き彫りそのもの）。
    /// Amount = 強さ（0〜1、既定0.8）、Radius = 浮き彫りの距離（px、1〜4、既定1。
    /// カーネルのサンプルを取る間隔で、大きいほど彫りが深く・太くなる）。
    /// 画像の端は座標を画像内に丸める（端の画素を複製する）ため、平坦な端は
    /// 中間グレーのままになり、暗い線は出ない。
    /// 浮き彫りのグレーは非乗算の値なので、Premul に合わせてアルファ比（a/255）を掛けてから
    /// ブレンドする。半透明の画素（テキストの縁など）でも浮き彫りがアルファを超えず、
    /// 透明な画素（a=0）は透明なままになる。アルファは元のまま保持する。
    /// </summary>
    internal static SKBitmap ApplyEmboss(SKBitmap src, ClipEffect effect)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(effect);
        var strength = Math.Clamp(FiniteOr(effect.Amount, 0.8), 0, 1);
        if (strength <= 0)
            return src.Copy();
        var distance = (int)Math.Clamp(Math.Round(FiniteOr(effect.Radius, 1)), 1, 4);

        var width = src.Width;
        var height = src.Height;
        var rowBytes = src.RowBytes;
        var srcBytes = ReadBytes(src);

        // 輝度（Rec.709。モノクロ化・エッジ検出と同じ係数）。Premul の RGB をそのまま使う。
        var luma = new int[width * height];
        Parallel.For(0, height, y =>
        {
            var row = y * rowBytes;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                luma[y * width + x] = (int)Math.Round(
                    0.213 * srcBytes[i + 2] + 0.715 * srcBytes[i + 1] + 0.072 * srcBytes[i]);
            }
        });

        // 端は座標を画像内に丸める（複製）。1画素しかない画像でも安全。
        int L(int x, int y)
            => luma[Math.Clamp(y, 0, height - 1) * width + Math.Clamp(x, 0, width - 1)];

        var dstBytes = new byte[rowBytes * height];
        Parallel.For(0, height, y =>
        {
            var row = y * rowBytes;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                var a = srcBytes[i + 3];
                var sum = 0;
                for (var ky = 0; ky < 3; ky++)
                    for (var kx = 0; kx < 3; kx++)
                    {
                        var weight = EmbossKernel[ky, kx];
                        if (weight != 0)
                            sum += weight * L(x + (kx - 1) * distance, y + (ky - 1) * distance);
                    }
                // 浮き彫りのグレー（非乗算 0〜255）を Premul スケールに直してブレンドする。
                var embossPremul = Math.Clamp(128 + sum, 0, 255) * (a / 255.0);
                var keep = 1 - strength;
                dstBytes[i] = (byte)Math.Clamp(Math.Round(srcBytes[i] * keep + embossPremul * strength), 0, a);
                dstBytes[i + 1] = (byte)Math.Clamp(Math.Round(srcBytes[i + 1] * keep + embossPremul * strength), 0, a);
                dstBytes[i + 2] = (byte)Math.Clamp(Math.Round(srcBytes[i + 2] * keep + embossPremul * strength), 0, a);
                dstBytes[i + 3] = a;
            }
        });
        return BitmapFromBytes(dstBytes, width, height);
    }
}
