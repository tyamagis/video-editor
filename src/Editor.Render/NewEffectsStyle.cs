// 新エフェクト5種（EdgeDetect / Solarize / Kuwahara / TiltShift / Halftone）の描画本体。
// ClipCompositor から呼ばれる前提のヘルパー集。配線（ClipCompositor への組み込み）は別途行う。
// 契約：src は変更しない。常に新しい SKBitmap を返す（効果が無効な量のときは src のコピーを返す）。
// ビットマップ形式は Bgra8888・Premul。画素を直接いじる場合は Marshal.Copy で
// バイト配列に取り出し、RGB がアルファを超えないよう丸める（CpuPixelEffects.Apply と同じ流儀）。

using System.Runtime.InteropServices;
using Editor.Core;
using SkiaSharp;

namespace Editor.Render;

/// <summary>
/// 新エフェクト5種の描画ヘルパー（EdgeDetect / Solarize / Kuwahara / TiltShift / Halftone）。
/// すべて src を変更せず、新しい SKBitmap を返す。効果が無効な量のときは src のコピーを返す。
/// パラメータは定義テーブル（EffectDefinitions）の範囲に丸める。NaN・無限大は既定値扱いにする
/// （(int)NaN が int.MinValue になって画素ループが壊れるのを防ぐため）。
/// </summary>
internal static class NewEffectsStyle
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
    /// エッジ検出：Sobel で勾配の大きさを求め、しきい値以上の勾配を白・それ以外を黒とした
    /// エッジマップを作り、元画像と Amount でブレンドする（0 で元の画像、1 でエッジマップそのもの）。
    /// Amount = エッジの強さ（0〜1、既定0.8）、Radius = 勾配のしきい値（0〜1、既定0.15。小さいほど細かいエッジも拾う）。
    /// しきい値は Sobel の勾配の大きさの最大値（√(1020²+1020²) ≒ 1442.5）に対する割合。
    /// 軸平行な段差の勾配は最大で 1020（≒0.707）のため、しきい値 0.8 以上では軸平行のエッジはほぼ出ない。
    /// 画像の端は勾配 0（エッジマップでは黒）として扱う。
    /// </summary>
    internal static SKBitmap ApplyEdgeDetect(SKBitmap src, ClipEffect effect)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(effect);
        var strength = Math.Clamp(FiniteOr(effect.Amount, 0.8), 0, 1);
        if (strength <= 0)
            return src.Copy();
        var threshold = Math.Clamp(FiniteOr(effect.Radius, 0.15), 0, 1);

        var width = src.Width;
        var height = src.Height;
        var rowBytes = src.RowBytes;
        var srcBytes = ReadBytes(src);

        // 輝度（Rec.709。モノクロ化と同じ係数）。Premul の RGB をそのまま使う。
        var luma = new int[width * height];
        for (var y = 0; y < height; y++)
        {
            var row = y * rowBytes;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                luma[y * width + x] = (int)Math.Round(
                    0.213 * srcBytes[i + 2] + 0.715 * srcBytes[i + 1] + 0.072 * srcBytes[i]);
            }
        }

        int L(int x, int y) => luma[y * width + x];

        // Sobel の勾配の大きさの最大値：|gx|・|gy| ともに最大 4×255=1020。
        const double maxMagnitude = 1020.0 * 1.4142135623730951;
        var thresholdMagnitude = threshold * maxMagnitude;

        var dstBytes = new byte[rowBytes * height];
        for (var y = 0; y < height; y++)
        {
            var row = y * rowBytes;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                var a = srcBytes[i + 3];
                double edge = 0;
                if (x > 0 && x < width - 1 && y > 0 && y < height - 1)
                {
                    var gx = -L(x - 1, y - 1) - 2 * L(x - 1, y) - L(x - 1, y + 1)
                        + L(x + 1, y - 1) + 2 * L(x + 1, y) + L(x + 1, y + 1);
                    var gy = -L(x - 1, y - 1) - 2 * L(x, y - 1) - L(x + 1, y - 1)
                        + L(x - 1, y + 1) + 2 * L(x, y + 1) + L(x + 1, y + 1);
                    var magnitude = Math.Sqrt((double)gx * gx + (double)gy * gy);
                    edge = magnitude >= thresholdMagnitude ? 255 : 0;
                }
                // エッジマップ（白黒）と元画像をブレンドする。アルファは元のまま。
                dstBytes[i] = (byte)Math.Clamp(Math.Round(srcBytes[i] * (1 - strength) + edge * strength), 0, a);
                dstBytes[i + 1] = (byte)Math.Clamp(Math.Round(srcBytes[i + 1] * (1 - strength) + edge * strength), 0, a);
                dstBytes[i + 2] = (byte)Math.Clamp(Math.Round(srcBytes[i + 2] * (1 - strength) + edge * strength), 0, a);
                dstBytes[i + 3] = a;
            }
        }
        return BitmapFromBytes(dstBytes, width, height);
    }

    /// <summary>
    /// ソラリゼーション：チャンネルごとに、しきい値（Amount×255）以上の値を反転する（v' = 255 - v）。
    /// Amount = 反転のしきい値（0〜1、既定0.5）。Radius は使わない。
    /// しきい値 1.0（=255）では v==255 の画素だけが反転する（ほぼ変化なし）。
    /// しきい値 0 では全画素が反転する。Premul を壊さないよう反転結果はアルファ以下に丸める。
    /// </summary>
    internal static SKBitmap ApplySolarize(SKBitmap src, ClipEffect effect)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(effect);
        var threshold = Math.Clamp(FiniteOr(effect.Amount, 0.5), 0, 1) * 255.0;

        var width = src.Width;
        var height = src.Height;
        var rowBytes = src.RowBytes;
        var srcBytes = ReadBytes(src);
        var dstBytes = new byte[rowBytes * height];
        for (var y = 0; y < height; y++)
        {
            var row = y * rowBytes;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                var a = srcBytes[i + 3];
                dstBytes[i] = SolarizeChannel(srcBytes[i], threshold, a);
                dstBytes[i + 1] = SolarizeChannel(srcBytes[i + 1], threshold, a);
                dstBytes[i + 2] = SolarizeChannel(srcBytes[i + 2], threshold, a);
                dstBytes[i + 3] = a;
            }
        }
        return BitmapFromBytes(dstBytes, width, height);
    }

    /// <summary>1チャンネル分のソラリゼーション（しきい値以上なら反転し、アルファ以下に丸める）。</summary>
    private static byte SolarizeChannel(byte value, double threshold, byte alpha)
    {
        var inverted = value >= threshold ? 255 - value : value;
        return (byte)Math.Min(inverted, alpha);
    }

    /// <summary>
    /// 油絵風（Kuwahara）：各画素について、重なる4つの (r+1)×(r+1) の方形領域
    /// （左上・右上・左下・右下）のうち分散（RGB 各チャンネルの分散の和）が最小の領域の
    /// 平均色を出力し、元画像と Radius でブレンドする（0 で元の画像、1 で完全な油絵風）。
    /// Amount = カーネル半径（px、1〜8、既定3）、Radius = 強さ（0〜1、既定1.0）。
    /// 高速化：RGB 各チャンネルの和と二乗和の積分画像（summed-area table、long 6枚）を作り、
    /// 各領域の和を O(1) で求める（半径に依存しないコスト）。積分画像の値は非負整数の和なので
    /// long で逐次加算と同一の和が得られ、平均・分散も旧実装と同一の double 演算
    /// （sum/n、sumSq/n - mean²、3チャンネルの和、負は 0 に丸める）で行うため、
    /// 出力は旧実装とビット単位で同一。画素ループは行方向に並列化する。
    /// 積分画像の構築も並列化する（パス1：行方向プレフィックスを行並列、パス2：列方向累積を
    /// 列タイル並列）。UI の半径上限 8 は変えない（SAT 化で半径依存コストはほぼ消えるが、
    /// 半径を上げても見た目の変化は頭打ちで、既存のキーフレーム・旧プロジェクト互換を
    /// 守るため。上限を上げる必要はない）。
    /// メモリ：6枚 × (W+1)×(H+1) × 8B。4K で約 400MB。int だと和が 4K で 2.115e9 と
    /// int.MaxValue（2.147e9）に肉薄し、より大きい画像で溢れるため long を選んだ。
    /// バンド分割はしない（400MB は許容範囲で、分割は再構築のオーバーヘッドと
    /// 境界処理の複雑さを増やすだけ）。
    /// 端の領域は画像内に丸める（サンプル数が減る分だけ平均・分散の分母も減らす）。
    /// </summary>
    internal static SKBitmap ApplyKuwahara(SKBitmap src, ClipEffect effect)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(effect);
        var strength = Math.Clamp(FiniteOr(effect.Radius, 1.0), 0, 1);
        if (strength <= 0)
            return src.Copy();
        var radius = (int)Math.Clamp(Math.Round(FiniteOr(effect.Amount, 3)), 1, 8);

        var width = src.Width;
        var height = src.Height;
        var rowBytes = src.RowBytes;
        var srcBytes = ReadBytes(src);
        var dstBytes = new byte[rowBytes * height];

        // ---- 積分画像（summed-area table）の構築 ----
        // sat[c][y * stride + x] = 画素 (0..y-1, 0..x-1) の和。0行目・0列目は 0。
        // c: 0=R, 1=G, 2=B, 3=R^2, 4=G^2, 5=B^2。すべて long（非負整数の和なので丸め誤差なし）。
        var stride = width + 1;
        var satSize = stride * (height + 1);
        var satR = new long[satSize];
        var satG = new long[satSize];
        var satB = new long[satSize];
        var satR2 = new long[satSize];
        var satG2 = new long[satSize];
        var satB2 = new long[satSize];

        // パス1：行方向プレフィックス（各行は独立なので行並列）。
        // sat[(y+1)*stride + (x+1)] に行 y の 0..x 列目の和を入れる。
        Parallel.For(0, height, y =>
        {
            var srcRow = y * rowBytes;
            var satRow = (y + 1) * stride;
            long runR = 0, runG = 0, runB = 0, runR2 = 0, runG2 = 0, runB2 = 0;
            for (var x = 0; x < width; x++)
            {
                var i = srcRow + x * 4;
                var b = srcBytes[i];
                var g = srcBytes[i + 1];
                var r = srcBytes[i + 2];
                runR += r;
                runG += g;
                runB += b;
                runR2 += (long)r * r;
                runG2 += (long)g * g;
                runB2 += (long)b * b;
                var j = satRow + x + 1;
                satR[j] = runR;
                satG[j] = runG;
                satB[j] = runB;
                satR2[j] = runR2;
                satG2[j] = runG2;
                satB2[j] = runB2;
            }
        });

        // パス2：列方向累積（各列は独立。キャッシュ効率のため列をタイルに分け、
        // タイル内では行方向に走査して連続アクセスにする）。
        const int tileWidth = 128;
        var tileCount = (stride + tileWidth - 1) / tileWidth;
        Parallel.For(0, tileCount, t =>
        {
            var x0 = t * tileWidth;
            var x1 = Math.Min(stride, x0 + tileWidth);
            for (var y = 0; y < height; y++)
            {
                var upper = y * stride;
                var lower = (y + 1) * stride;
                for (var x = x0; x < x1; x++)
                {
                    satR[lower + x] += satR[upper + x];
                    satG[lower + x] += satG[upper + x];
                    satB[lower + x] += satB[upper + x];
                    satR2[lower + x] += satR2[upper + x];
                    satG2[lower + x] += satG2[upper + x];
                    satB2[lower + x] += satB2[upper + x];
                }
            }
        });

        // ---- 画素ループ（行並列） ----
        // 1画素の4象限は SAT 座標で (sx0,sy0)-(sx1,sy1) / (sx2,sy0)-(sx3,sy1) /
        // (sx0,sy2)-(sx1,sy3) / (sx2,sy2)-(sx3,sy3)。矩形和は4隅の SAT 値から
        // S(x1,y1) - S(x0,y1) - S(x1,y0) + S(x0,y0) で求める（long の整数演算で厳密）。
        // 平均・分散の double 演算は旧 RegionStats と同一の式・順序にしてある。
        Parallel.For(0, height, y =>
        {
            var row = y * rowBytes;
            // y 方向の SAT 境界（行内で共通）。
            // 上側象限：[max(0,y-r) .. y]、下側象限：[y .. min(H-1,y+r)]（両端を含む）。
            var sy0 = Math.Max(0, y - radius);
            var sy1 = y + 1;
            var sy2 = y;
            var sy3 = Math.Min(height - 1, y + radius) + 1;
            var nTop = (double)(y - sy0 + 1);
            var nBottom = (double)(sy3 - y);
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                var a = srcBytes[i + 3];
                // x 方向の SAT 境界。左側象限：[max(0,x-r) .. x]、右側象限：[x .. min(W-1,x+r)]。
                var sx0 = Math.Max(0, x - radius);
                var sx1 = x + 1;
                var sx2 = x;
                var sx3 = Math.Min(width - 1, x + radius) + 1;
                var nLeft = (double)(x - sx0 + 1);
                var nRight = (double)(sx3 - x);

                var bestVariance = double.PositiveInfinity;
                double bestR = 0, bestG = 0, bestB = 0;
                // 4つの方形領域（画素 (x,y) を角で共有する (r+1)×(r+1) の正方形）。
                // q=0: 左上、q=1: 右上、q=2: 左下、q=3: 右下。タイブレークは旧実装と同じく
                // 最初（q の小さい方）を優先（variance < bestVariance の厳密比較）。
                for (var q = 0; q < 4; q++)
                {
                    var left = q % 2 == 0;
                    var top = q < 2;
                    var rx0 = left ? sx0 : sx2;
                    var ry0 = top ? sy0 : sy2;
                    var rx1 = left ? sx1 : sx3;
                    var ry1 = top ? sy1 : sy3;
                    var n = (left ? nLeft : nRight) * (top ? nTop : nBottom);
                    var o00 = ry0 * stride + rx0;
                    var o10 = ry0 * stride + rx1;
                    var o01 = ry1 * stride + rx0;
                    var o11 = ry1 * stride + rx1;
                    var sumR = satR[o11] - satR[o01] - satR[o10] + satR[o00];
                    var sumG = satG[o11] - satG[o01] - satG[o10] + satG[o00];
                    var sumB = satB[o11] - satB[o01] - satB[o10] + satB[o00];
                    var sumR2 = satR2[o11] - satR2[o01] - satR2[o10] + satR2[o00];
                    var sumG2 = satG2[o11] - satG2[o01] - satG2[o10] + satG2[o00];
                    var sumB2 = satB2[o11] - satB2[o01] - satB2[o10] + satB2[o00];
                    var meanR = sumR / n;
                    var meanG = sumG / n;
                    var meanB = sumB / n;
                    // 分散は E[x²] - E[x]² で求める（浮動小数誤差で負になった分は 0 に丸める）。
                    var variance = (sumR2 / n - meanR * meanR)
                        + (sumG2 / n - meanG * meanG)
                        + (sumB2 / n - meanB * meanB);
                    variance = Math.Max(0, variance);
                    if (variance < bestVariance)
                    {
                        bestVariance = variance;
                        bestR = meanR;
                        bestG = meanG;
                        bestB = meanB;
                    }
                }
                dstBytes[i] = (byte)Math.Clamp(Math.Round(srcBytes[i] * (1 - strength) + bestB * strength), 0, a);
                dstBytes[i + 1] = (byte)Math.Clamp(Math.Round(srcBytes[i + 1] * (1 - strength) + bestG * strength), 0, a);
                dstBytes[i + 2] = (byte)Math.Clamp(Math.Round(srcBytes[i + 2] * (1 - strength) + bestR * strength), 0, a);
                dstBytes[i + 3] = a;
            }
        });
        return BitmapFromBytes(dstBytes, width, height);
    }

    /// <summary>
    /// ティルトシフト：全体をぼかしたコピーと元画像を、行ごとの係数でブレンドする。
    /// Amount = ぼかしの強さ（0〜1、既定0.5。内部ではブラーのシグマ = Amount×20 px）。
    /// Radius = ピントの合う帯の幅（画面高さに対する割合、0.1〜0.8、既定0.3。帯の中心は画面中央）。
    /// 帯の中心では元画像（係数 0）、上下の端ではぼかし（係数 1）。帯の端から画面端へは
    /// smoothstep でなめらかに遷移する。Amount 0 で変化なし（src のコピーを返す）。
    /// </summary>
    internal static SKBitmap ApplyTiltShift(SKBitmap src, ClipEffect effect)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(effect);
        var strength = Math.Clamp(FiniteOr(effect.Amount, 0.5), 0, 1);
        if (strength <= 0)
            return src.Copy();
        var bandFraction = Math.Clamp(FiniteOr(effect.Radius, 0.3), 0.1, 0.8);

        var width = src.Width;
        var height = src.Height;
        var rowBytes = src.RowBytes;

        // 全体をぼかしたコピー（Skia のブラーフィルタ。Premul・透明度とも正しく扱われる）。
        // タイルモードは Clamp にする。既定（Decal）だと端の画素が透明に抜けて
        // 上下の端が暗く潰れてしまうため（実測：不透明な白を sigma=10 でぼかすと y=0 で A=133）。
        var sigma = (float)(strength * 20.0);
        using var blurred = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(blurred))
        using (var filter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp, null, null))
        using (var paint = new SKPaint { ImageFilter = filter })
        {
            canvas.DrawBitmap(src, new SKRect(0, 0, width, height), paint);
        }
        var srcBytes = ReadBytes(src);
        var blurBytes = ReadBytes(blurred);

        var halfHeight = height / 2.0;
        var halfBand = bandFraction * height / 2.0;
        // 帯の半幅は最大で 0.4H < 0.5H のため、遷移区間の幅（halfHeight - halfBand）は常に正。
        var dstBytes = new byte[rowBytes * height];
        for (var y = 0; y < height; y++)
        {
            var d = Math.Abs(y + 0.5 - halfHeight);
            double t;
            if (d <= halfBand)
            {
                t = 0;
            }
            else
            {
                var u = Math.Clamp((d - halfBand) / (halfHeight - halfBand), 0, 1);
                t = u * u * (3 - 2 * u); // smoothstep
            }
            var row = y * rowBytes;
            var s = 1 - t;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                var a = (byte)Math.Clamp(Math.Round(srcBytes[i + 3] * s + blurBytes[i + 3] * t), 0, 255);
                dstBytes[i] = (byte)Math.Clamp(Math.Round(srcBytes[i] * s + blurBytes[i] * t), 0, a);
                dstBytes[i + 1] = (byte)Math.Clamp(Math.Round(srcBytes[i + 1] * s + blurBytes[i + 1] * t), 0, a);
                dstBytes[i + 2] = (byte)Math.Clamp(Math.Round(srcBytes[i + 2] * s + blurBytes[i + 2] * t), 0, a);
                dstBytes[i + 3] = a;
            }
        }
        return BitmapFromBytes(dstBytes, width, height);
    }

    /// <summary>
    /// ハーフトーン：ドットサイズを一辺とする格子を Radius 度だけ回転させたセルごとに
    /// 平均輝度・平均色を求め、暗いほど大きいドットをセルの平均色で描く。
    /// ドットの直径の最大値はセルサイズ（= Amount）。輝度 1（白）のセルはドット無し。
    /// Amount = ドットの最大サイズ（px、2〜16、既定6）、Radius = スクリーン角度（度、0〜90、既定45）。
    /// 背景：四隅の画素のアルファがすべて 250 以上なら不透明とみなして白背景、
    /// 1つでも透明・半透明なら透明背景にする（映像・静止画は白、テキストの
    /// オフスクリーンは透明になる想定。四隅だけの判定のため、隅にだけ半透明の画素が
    /// ある画像では誤判定し得るが、実用上は隅が透明＝テキスト用という前提で十分）。
    /// ドットの色は Premul の平均色を非乗算（アンプレマルチプライ）に戻してから描く
    /// （SKColor は非乗算で渡す決まりのため。Premul のまま渡すと二重に暗くなる）。
    /// </summary>
    internal static SKBitmap ApplyHalftone(SKBitmap src, ClipEffect effect)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(effect);
        var dotSize = (int)Math.Clamp(Math.Round(FiniteOr(effect.Amount, 6)), 2, 16);
        var angle = Math.Clamp(FiniteOr(effect.Radius, 45), 0, 90) * Math.PI / 180.0;

        var width = src.Width;
        var height = src.Height;
        var rowBytes = src.RowBytes;
        var srcBytes = ReadBytes(src);

        var opaque = IsOpaqueAtCorners(srcBytes, rowBytes, width, height);
        var dst = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(dst);
        canvas.Clear(opaque ? SKColors.White : SKColors.Transparent);

        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        // 画像矩形の四隅を回転座標 (u,v) に写し、セルの範囲を求める。
        // u = x cosθ + y sinθ、v = -x sinθ + y cosθ。
        double minU = 0, maxU = 0, minV = 0, maxV = 0;
        var first = true;
        foreach (var (px, py) in new (double, double)[] { (0, 0), (width, 0), (0, height), (width, height) })
        {
            var u = px * cos + py * sin;
            var v = -px * sin + py * cos;
            if (first)
            {
                minU = maxU = u;
                minV = maxV = v;
                first = false;
            }
            else
            {
                minU = Math.Min(minU, u);
                maxU = Math.Max(maxU, u);
                minV = Math.Min(minV, v);
                maxV = Math.Max(maxV, v);
            }
        }
        var iuMin = (int)Math.Floor(minU / dotSize);
        var iuMax = (int)Math.Floor(maxU / dotSize);
        var ivMin = (int)Math.Floor(minV / dotSize);
        var ivMax = (int)Math.Floor(maxV / dotSize);

        using var paint = new SKPaint { IsAntialias = true };
        var s = (double)dotSize;
        for (var iv = ivMin; iv <= ivMax; iv++)
        {
            for (var iu = iuMin; iu <= iuMax; iu++)
            {
                // セルの中心（回転座標）→ 画像座標へ逆回転。
                var uc = (iu + 0.5) * s;
                var vc = (iv + 0.5) * s;
                var cx = uc * cos - vc * sin;
                var cy = uc * sin + vc * cos;

                // セルの AABB（四隅を逆回転）で走査範囲を絞る。所属判定は画素ごとに行う。
                double cellMinX = double.PositiveInfinity, cellMaxX = double.NegativeInfinity;
                double cellMinY = double.PositiveInfinity, cellMaxY = double.NegativeInfinity;
                foreach (var (cu, cv) in new (double, double)[]
                    { (iu * s, iv * s), ((iu + 1) * s, iv * s), (iu * s, (iv + 1) * s), ((iu + 1) * s, (iv + 1) * s) })
                {
                    var x = cu * cos - cv * sin;
                    var y = cu * sin + cv * cos;
                    cellMinX = Math.Min(cellMinX, x);
                    cellMaxX = Math.Max(cellMaxX, x);
                    cellMinY = Math.Min(cellMinY, y);
                    cellMaxY = Math.Max(cellMaxY, y);
                }
                var x0 = Math.Max(0, (int)Math.Floor(cellMinX));
                var x1 = Math.Min(width - 1, (int)Math.Floor(cellMaxX));
                var y0 = Math.Max(0, (int)Math.Floor(cellMinY));
                var y1 = Math.Min(height - 1, (int)Math.Floor(cellMaxY));
                if (x1 < x0 || y1 < y0)
                    continue;

                long sumR = 0, sumG = 0, sumB = 0, sumA = 0;
                var count = 0;
                for (var y = y0; y <= y1; y++)
                {
                    var row = y * rowBytes;
                    for (var x = x0; x <= x1; x++)
                    {
                        // 画素中心の回転座標で所属セルを判定する。
                        var u = (x + 0.5) * cos + (y + 0.5) * sin;
                        var v = -(x + 0.5) * sin + (y + 0.5) * cos;
                        if ((int)Math.Floor(u / s) != iu || (int)Math.Floor(v / s) != iv)
                            continue;
                        var i = row + x * 4;
                        sumB += srcBytes[i];
                        sumG += srcBytes[i + 1];
                        sumR += srcBytes[i + 2];
                        sumA += srcBytes[i + 3];
                        count++;
                    }
                }
                if (count == 0)
                    continue;
                var meanA = sumA / (double)count;
                if (meanA < 1)
                    continue; // ほぼ透明なセルはドット無し。
                var meanR = sumR / (double)count;
                var meanG = sumG / (double)count;
                var meanB = sumB / (double)count;
                var meanLuma = (0.213 * meanR + 0.715 * meanG + 0.072 * meanB) / 255.0;
                var radius = (1 - meanLuma) * (s / 2.0);
                if (radius < 0.5)
                    continue; // ほぼ白いセルはドット無し（背景のまま）。
                // Premul の平均色を非乗算に戻してから描く（SKColor は非乗算で渡す）。
                var unpremul = meanA >= 255 ? 1.0 : 255.0 / meanA;
                paint.Color = new SKColor(
                    (byte)Math.Clamp(Math.Round(meanR * unpremul), 0, 255),
                    (byte)Math.Clamp(Math.Round(meanG * unpremul), 0, 255),
                    (byte)Math.Clamp(Math.Round(meanB * unpremul), 0, 255),
                    (byte)Math.Clamp(Math.Round(meanA), 0, 255));
                canvas.DrawCircle((float)cx, (float)cy, (float)radius, paint);
            }
        }
        return dst;
    }

    /// <summary>四隅の画素のアルファがすべて 250 以上なら不透明とみなす（ハーフトーンの背景判定用）。</summary>
    private static bool IsOpaqueAtCorners(byte[] bytes, int rowBytes, int width, int height)
    {
        var corners = new (int X, int Y)[]
        {
            (0, 0), (width - 1, 0), (0, height - 1), (width - 1, height - 1),
        };
        foreach (var (x, y) in corners)
        {
            var a = bytes[y * rowBytes + x * 4 + 3];
            if (a < 250)
                return false;
        }
        return true;
    }
}
