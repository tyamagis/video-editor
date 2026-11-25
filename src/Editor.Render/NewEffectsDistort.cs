// 追加エフェクト7種（歪み系）の描画ヘルパー。
// ClipCompositor から呼ぶ前提の internal 実装。
// 契約：src は変更せず、常に新しい SKBitmap を返す。効果が無効な量のときは src のコピーを返す。
// 幾何変換は逆写像＋バイリニアで、範囲外は透明。Premul を壊さないよう RGB はアルファ以下に丸める。
// 画素を直接いじる箇所は Marshal.Copy でバイト配列に取り出す FrameRenderer の流儀に倣う。

using System.Runtime.InteropServices;
using Editor.Core;
using SkiaSharp;

namespace Editor.Render;

/// <summary>追加エフェクト7種（PixelSort / Wave / Mirror / Twirl / Displacement / Bulge / WaterRipple）の描画本体。</summary>
internal static class NewEffectsDistort
{
    /// <summary>src のコピー（Bgra8888・Premul）。src は変更しない。</summary>
    private static SKBitmap CopyBitmap(SKBitmap src)
    {
        var dst = new SKBitmap(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bytes = new byte[src.RowBytes * src.Height];
        Marshal.Copy(src.GetPixels(), bytes, 0, bytes.Length);
        Marshal.Copy(bytes, 0, dst.GetPixels(), bytes.Length);
        return dst;
    }

    /// <summary>NaN・無限大なら fallback、そうでなければ [min, max] に丸める（範囲外の入力の最終防衛）。</summary>
    private static double ClampFinite(double value, double min, double max, double fallback)
        => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    /// <summary>バイリニアの1タップ分の重み付き和（チャンネル offset：0=B, 1=G, 2=R, 3=A）。</summary>
    private static double SampleWeighted(
        byte[] src, int i00, int i10, int i01, int i11, int offset,
        double w00, double w10, double w01, double w11)
        => src[i00 + offset] * w00 + src[i10 + offset] * w10
            + src[i01 + offset] * w01 + src[i11 + offset] * w11;

    /// <summary>
    /// バイリニアで1画素を読み、dst の di 番地へ BGRA で書く。座標が画素中心の範囲
    /// [0, width-1]×[0, height-1] の外（または NaN）なら透明を書く。
    /// 端では重み 0 のタップを端の画素で丸める（重みが 0 なので値は変わらない）。
    /// RGB はアルファを超えないよう丸める（Premul を壊さないため）。
    /// </summary>
    private static void SampleBilinear(
        byte[] src, int rowBytes, int width, int height, double x, double y, byte[] dst, int di)
    {
        if (double.IsNaN(x) || double.IsNaN(y) || x < 0 || y < 0 || x > width - 1 || y > height - 1)
        {
            dst[di] = 0;
            dst[di + 1] = 0;
            dst[di + 2] = 0;
            dst[di + 3] = 0;
            return;
        }

        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var fx = x - x0;
        var fy = y - y0;
        var x1 = Math.Min(x0 + 1, width - 1);
        var y1 = Math.Min(y0 + 1, height - 1);
        var w00 = (1 - fx) * (1 - fy);
        var w10 = fx * (1 - fy);
        var w01 = (1 - fx) * fy;
        var w11 = fx * fy;
        var i00 = y0 * rowBytes + x0 * 4;
        var i10 = y0 * rowBytes + x1 * 4;
        var i01 = y1 * rowBytes + x0 * 4;
        var i11 = y1 * rowBytes + x1 * 4;

        var a = (byte)Math.Clamp(Math.Round(SampleWeighted(src, i00, i10, i01, i11, 3, w00, w10, w01, w11)), 0, 255);
        dst[di] = (byte)Math.Clamp(Math.Round(SampleWeighted(src, i00, i10, i01, i11, 0, w00, w10, w01, w11)), 0, a);
        dst[di + 1] = (byte)Math.Clamp(Math.Round(SampleWeighted(src, i00, i10, i01, i11, 1, w00, w10, w01, w11)), 0, a);
        dst[di + 2] = (byte)Math.Clamp(Math.Round(SampleWeighted(src, i00, i10, i01, i11, 2, w00, w10, w01, w11)), 0, a);
        dst[di + 3] = a;
    }

    /// <summary>幾何変換用の作業バッファ（src のバイト列と、0 で初期化された出力先）を用意する。</summary>
    private static (byte[] SrcBytes, byte[] DstBytes) PrepareBuffers(SKBitmap src)
    {
        var srcBytes = new byte[src.RowBytes * src.Height];
        Marshal.Copy(src.GetPixels(), srcBytes, 0, srcBytes.Length);
        return (srcBytes, new byte[srcBytes.Length]);
    }

    /// <summary>作業バッファから新しいビットマップを作る。</summary>
    private static SKBitmap BuildBitmap(byte[] dstBytes, int width, int height)
    {
        var dst = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        Marshal.Copy(dstBytes, 0, dst.GetPixels(), dstBytes.Length);
        return dst;
    }

    /// <summary>src の (x, y) の画素を dstBytes の同じ位置へコピーする（歪みの影響範囲外の画素用）。</summary>
    private static void CopyPixel(byte[] srcBytes, byte[] dstBytes, int rowBytes, int x, int y)
    {
        var i = y * rowBytes + x * 4;
        dstBytes[i] = srcBytes[i];
        dstBytes[i + 1] = srcBytes[i + 1];
        dstBytes[i + 2] = srcBytes[i + 2];
        dstBytes[i + 3] = srcBytes[i + 3];
    }

    /// <summary>
    /// ピクセルソート：行ごとに、しきい値以上の明るさが続く区間の画素を明るさの昇順
    /// （暗い→明るい）に並べ替える。区間が最大ソート長より長い場合はその長さごとの
    /// チャンクに分けて並べ替える（1回のソートで流れるのは最大ソート長まで）。
    /// Amount 0 は「しきい値 0 = 全部対象」ではなく「ソートなし」と解釈する。
    /// しきい値 0 で全部対象にすると、Amount 0 が「量 0 で変化なし」の約束を破るため。
    /// </summary>
    internal static SKBitmap ApplyPixelSort(SKBitmap src, ClipEffect effect)
    {
        var threshold = ClampFinite(effect.Amount, 0, 1, 0);
        if (threshold <= 0)
            return CopyBitmap(src);

        var maxRun = (int)Math.Clamp(Math.Round(ClampFinite(effect.Radius, 8, 256, 64)), 8, 256);
        var width = src.Width;
        var height = src.Height;
        var rowBytes = src.RowBytes;
        var bytes = new byte[rowBytes * height];
        Marshal.Copy(src.GetPixels(), bytes, 0, bytes.Length);
        var threshold255 = threshold * 255;

        for (var y = 0; y < height; y++)
        {
            var row = y * rowBytes;
            var x = 0;
            while (x < width)
            {
                if (Luminance(bytes, row + x * 4) < threshold255)
                {
                    x++;
                    continue;
                }
                var runStart = x;
                while (x < width && Luminance(bytes, row + x * 4) >= threshold255)
                    x++;
                var runEnd = x;
                for (var chunk = runStart; chunk < runEnd; chunk += maxRun)
                {
                    var chunkEnd = Math.Min(chunk + maxRun, runEnd);
                    if (chunkEnd - chunk >= 2)
                        SortPixelsByLuminance(bytes, row, chunk, chunkEnd);
                }
            }
        }

        return BuildBitmap(bytes, width, height);
    }

    /// <summary>輝度（Premul の RGB から。0.213R + 0.715G + 0.072B）。</summary>
    private static double Luminance(byte[] bytes, int i)
        => 0.213 * bytes[i + 2] + 0.715 * bytes[i + 1] + 0.072 * bytes[i];

    /// <summary>1行の [start, end) の画素を輝度の昇順に並べ替える（輝度が同じなら元の順序を保つ＝安定ソート）。</summary>
    private static void SortPixelsByLuminance(byte[] bytes, int row, int start, int end)
    {
        var order = new (double Luminance, int Index)[end - start];
        for (var k = 0; k < order.Length; k++)
            order[k] = (Luminance(bytes, row + (start + k) * 4), start + k);
        Array.Sort(order, static (a, b) => a.Luminance != b.Luminance
            ? a.Luminance.CompareTo(b.Luminance)
            : a.Index.CompareTo(b.Index));

        var sorted = new byte[(end - start) * 4];
        for (var k = 0; k < order.Length; k++)
            Buffer.BlockCopy(bytes, row + order[k].Index * 4, sorted, k * 4, 4);
        Buffer.BlockCopy(sorted, 0, bytes, row + start * 4, sorted.Length);
    }

    /// <summary>
    /// ウェーブ：横方向のサイン波うねり。Warp は y のサインで X 方向へずらす
    /// （ClipCompositor.StripOffsetX）のに対し、こちらはそれと垂直に、x のサインで
    /// Y 方向へずらす（波の位相は横方向に進み、ずれは縦方向）。量 0 で変化なし。
    /// ずれは x だけに依存するため、列ごとのずれを先に計算しておく。
    /// </summary>
    internal static SKBitmap ApplyWave(SKBitmap src, ClipEffect effect)
    {
        var amplitude = ClampFinite(effect.Amount, 0, 100, 0);
        if (amplitude <= 0)
            return CopyBitmap(src);
        var wavelength = ClampFinite(effect.Radius, 20, 1000, 240);

        var width = src.Width;
        var height = src.Height;
        var rowBytes = src.RowBytes;
        var (srcBytes, dstBytes) = PrepareBuffers(src);

        var offsets = new double[width];
        for (var x = 0; x < width; x++)
            offsets[x] = amplitude * Math.Sin(2 * Math.PI * x / wavelength);

        for (var y = 0; y < height; y++)
        {
            var row = y * rowBytes;
            for (var x = 0; x < width; x++)
                SampleBilinear(srcBytes, rowBytes, width, height, x, y - offsets[x], dstBytes, row + x * 4);
        }

        return BuildBitmap(dstBytes, width, height);
    }

    /// <summary>
    /// ミラー：Amount = 分割線の位置（0〜1）。Radius を四捨五入して方式を決める。
    /// 0 = 左右反転（位置は無視して全体を反転）、1 = 左右分割ミラー（分割線より左はそのまま、
    /// 右は左側の鏡像）、2 = 上下分割ミラー（分割線より上はそのまま、下は上側の鏡像）。
    /// 鏡像の元が画面外になる画素は透明になる（分割線が端に寄った場合など）。
    /// 方式 1・2 で位置が 1 のときは分割線が画面の端に来るため、全体がそのままになる。
    /// </summary>
    internal static SKBitmap ApplyMirror(SKBitmap src, ClipEffect effect)
    {
        var mode = (int)Math.Clamp(Math.Round(ClampFinite(effect.Radius, 0, 2, 1)), 0, 2);
        var position = ClampFinite(effect.Amount, 0, 1, 0.5);

        var width = src.Width;
        var height = src.Height;
        var rowBytes = src.RowBytes;
        var (srcBytes, dstBytes) = PrepareBuffers(src);

        for (var y = 0; y < height; y++)
        {
            var row = y * rowBytes;
            for (var x = 0; x < width; x++)
            {
                double sx, sy;
                switch (mode)
                {
                    case 0: // 左右反転。
                        sx = width - 1 - x;
                        sy = y;
                        break;
                    case 2: // 上下分割ミラー。
                        var splitY = position * height;
                        sx = x;
                        sy = y < splitY ? y : 2 * splitY - y;
                        break;
                    default: // 左右分割ミラー。
                        var splitX = position * width;
                        sx = x < splitX ? x : 2 * splitX - x;
                        sy = y;
                        break;
                }
                SampleBilinear(srcBytes, rowBytes, width, height, sx, sy, dstBytes, row + x * 4);
            }
        }

        return BuildBitmap(dstBytes, width, height);
    }

    /// <summary>
    /// トゥワール：中心を軸にねじれる。+で時計回り。影響半径（画面の半対角線に対する割合）
    /// の外は歪まない。ねじれ角は中心で最大、境界で 0 まで (1-d²)² でなめらかに減衰する
    /// （中心と境界の両方で傾きが 0 になるため、境界に段差が出ない）。量 0 で変化なし。
    /// </summary>
    internal static SKBitmap ApplyTwirl(SKBitmap src, ClipEffect effect)
    {
        var angleDegrees = ClampFinite(effect.Amount, -360, 360, 0);
        if (Math.Abs(angleDegrees) < 1e-9)
            return CopyBitmap(src);
        var radiusFraction = ClampFinite(effect.Radius, 0.2, 1.0, 0.6);

        var width = src.Width;
        var height = src.Height;
        var rowBytes = src.RowBytes;
        var (srcBytes, dstBytes) = PrepareBuffers(src);

        var cx = width / 2.0;
        var cy = height / 2.0;
        var halfDiag = Math.Sqrt(cx * cx + cy * cy);
        var radius = radiusFraction * halfDiag;
        var angle = angleDegrees * Math.PI / 180;

        for (var y = 0; y < height; y++)
        {
            var dy = y - cy;
            var row = y * rowBytes;
            for (var x = 0; x < width; x++)
            {
                var dx = x - cx;
                var dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist >= radius)
                {
                    CopyPixel(srcBytes, dstBytes, rowBytes, x, y);
                    continue;
                }

                var d = dist / radius;
                var falloff = 1 - d * d;
                falloff *= falloff;
                var twist = angle * falloff;
                var cos = Math.Cos(twist);
                var sin = Math.Sin(twist);
                // +で時計回り（画面座標での時計回りの回転行列 R(θ)=[[cos,-sin],[sin,cos]]）。
                // 逆写像は R(-θ)=[[cos,sin],[-sin,cos]]。
                var sx = cx + dx * cos + dy * sin;
                var sy = cy - dx * sin + dy * cos;
                SampleBilinear(srcBytes, rowBytes, width, height, sx, sy, dstBytes, row + x * 4);
            }
        }

        return BuildBitmap(dstBytes, width, height);
    }

    /// <summary>
    /// ディスプレイスメント：手続き的な値ノイズ（ハッシュ＋smoothstep 補間）で x・y をずらす。
    /// 時刻には依存せず、同じ入力なら同じ結果になる。Radius が大きいほどノイズのセルが
    /// 小さく（細かい歪み）。量 0 で変化なし。
    /// </summary>
    internal static SKBitmap ApplyDisplacement(SKBitmap src, ClipEffect effect)
    {
        var strength = ClampFinite(effect.Amount, 0, 100, 0);
        if (strength <= 0)
            return CopyBitmap(src);
        var scale = ClampFinite(effect.Radius, 0.5, 4.0, 1.0);
        var cell = 32.0 / scale; // 1セル 32px が既定。倍率が大きいほどセルが小さく、細かい歪みになる。

        var width = src.Width;
        var height = src.Height;
        var rowBytes = src.RowBytes;
        var (srcBytes, dstBytes) = PrepareBuffers(src);

        for (var y = 0; y < height; y++)
        {
            var row = y * rowBytes;
            for (var x = 0; x < width; x++)
            {
                // x・y のずれは別の種のノイズから取る（同じノイズだと斜め方向だけにずれる）。
                var nx = ValueNoise(x / cell, y / cell, 0);
                var ny = ValueNoise(x / cell, y / cell, 101);
                var sx = x - strength * (2 * nx - 1);
                var sy = y - strength * (2 * ny - 1);
                SampleBilinear(srcBytes, rowBytes, width, height, sx, sy, dstBytes, row + x * 4);
            }
        }

        return BuildBitmap(dstBytes, width, height);
    }

    /// <summary>
    /// バルジ：中心をレンズ状に膨らませる。影響半径の外は歪まない。中心では
    /// (1-Amount) 倍の位置からサンプルする（＝Amount 倍だけ拡大）ため、Amount 1 で
    /// 中心は1点に潰れる。境界では (1-d²)² でなめらかに 1 へ戻る。量 0 で変化なし。
    /// </summary>
    internal static SKBitmap ApplyBulge(SKBitmap src, ClipEffect effect)
    {
        var bulge = ClampFinite(effect.Amount, 0, 1, 0);
        if (bulge <= 0)
            return CopyBitmap(src);
        var radiusFraction = ClampFinite(effect.Radius, 0.2, 1.0, 0.6);

        var width = src.Width;
        var height = src.Height;
        var rowBytes = src.RowBytes;
        var (srcBytes, dstBytes) = PrepareBuffers(src);

        var cx = width / 2.0;
        var cy = height / 2.0;
        var halfDiag = Math.Sqrt(cx * cx + cy * cy);
        var radius = radiusFraction * halfDiag;

        for (var y = 0; y < height; y++)
        {
            var dy = y - cy;
            var row = y * rowBytes;
            for (var x = 0; x < width; x++)
            {
                var dx = x - cx;
                var dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist >= radius)
                {
                    CopyPixel(srcBytes, dstBytes, rowBytes, x, y);
                    continue;
                }

                var d = dist / radius;
                var f = 1 - d * d;
                var s = 1 - bulge * f * f; // 中心で 1-bulge、境界で 1（連続・傾き 0）。
                var sx = cx + dx * s;
                var sy = cy + dy * s;
                SampleBilinear(srcBytes, rowBytes, width, height, sx, sy, dstBytes, row + x * 4);
            }
        }

        return BuildBitmap(dstBytes, width, height);
    }

    /// <summary>
    /// ウォーターリップル：中心からの同心円状のサイン波で放射方向にずらす。
    /// 位相は timeSeconds で 1Hz で進む（1秒で波が波長1つ分だけ外へ進む）。
    /// 中心（方向が定まらない点）はずらさない。量 0 で変化なし。
    /// </summary>
    internal static SKBitmap ApplyWaterRipple(SKBitmap src, ClipEffect effect, double timeSeconds)
    {
        var amplitude = ClampFinite(effect.Amount, 0, 50, 0);
        if (amplitude <= 0)
            return CopyBitmap(src);
        var wavelength = ClampFinite(effect.Radius, 10, 200, 40);
        if (!double.IsFinite(timeSeconds))
            timeSeconds = 0;
        var phase = 2 * Math.PI * timeSeconds;

        var width = src.Width;
        var height = src.Height;
        var rowBytes = src.RowBytes;
        var (srcBytes, dstBytes) = PrepareBuffers(src);

        var cx = width / 2.0;
        var cy = height / 2.0;

        for (var y = 0; y < height; y++)
        {
            var dy = y - cy;
            var row = y * rowBytes;
            for (var x = 0; x < width; x++)
            {
                var dx = x - cx;
                var dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist < 1e-9)
                {
                    CopyPixel(srcBytes, dstBytes, rowBytes, x, y);
                    continue;
                }

                var offset = amplitude * Math.Sin(2 * Math.PI * dist / wavelength - phase);
                var sx = x - offset * dx / dist;
                var sy = y - offset * dy / dist;
                SampleBilinear(srcBytes, rowBytes, width, height, sx, sy, dstBytes, row + x * 4);
            }
        }

        return BuildBitmap(dstBytes, width, height);
    }

    /// <summary>格子点と種から決まる 0〜1 の疑似乱数（RenderHelpers.NoiseValue と同じハッシュ流儀。時刻には依存しない）。</summary>
    private static double Hash01(int x, int y, int seed)
    {
        uint h = (uint)(x * 374761393 + y * 668265263 + seed * 974634211);
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;
        return (h & 0xFFFF) / 65535.0;
    }

    /// <summary>値ノイズ：格子点のハッシュ値を smoothstep で補間した 0〜1 の滑らかな乱数場。</summary>
    private static double ValueNoise(double x, double y, int seed)
    {
        var ix = (int)Math.Floor(x);
        var iy = (int)Math.Floor(y);
        var fx = x - ix;
        var fy = y - iy;
        var sx = fx * fx * (3 - 2 * fx);
        var sy = fy * fy * (3 - 2 * fy);
        var v00 = Hash01(ix, iy, seed);
        var v10 = Hash01(ix + 1, iy, seed);
        var v01 = Hash01(ix, iy + 1, seed);
        var v11 = Hash01(ix + 1, iy + 1, seed);
        var a = v00 + (v10 - v00) * sx;
        var b = v01 + (v11 - v01) * sx;
        return a + (b - a) * sy;
    }
}
