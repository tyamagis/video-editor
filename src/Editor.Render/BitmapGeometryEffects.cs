// 幾何・時間系のビットマップエフェクト（モザイク・万華鏡・魚眼／色収差・
// ズームブラー・モーションブラー・VHSグリッチ）。
// FrameRenderer.cs から切り出した、状態を持たない静的ヘルパー。
// 入力ビットマップは書き換えず、新しいビットマップを返す
// （ApplyRadialWarp は両方無効なら入力をそのまま返す）。

using System.Runtime.InteropServices;
using Editor.Core;
using SkiaSharp;

namespace Editor.Render;

/// <summary>幾何・時間系のビットマップエフェクト（状態を持たない静的ヘルパー）。</summary>
internal static class BitmapGeometryEffects
{
    /// <summary>モザイク：小さく縮小してから最近傍で拡大し、ブロック状にする。</summary>
    internal static SKBitmap ApplyMosaic(SKBitmap src, double blockSize)
    {
        var block = (int)Math.Clamp(Math.Round(blockSize), 2, 64);
        var smallWidth = Math.Max(1, src.Width / block);
        var smallHeight = Math.Max(1, src.Height / block);
        using var small = new SKBitmap(smallWidth, smallHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(small))
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium })
            canvas.DrawBitmap(src, new SKRect(0, 0, smallWidth, smallHeight), paint);

        var dst = new SKBitmap(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var dstCanvas = new SKCanvas(dst);
        using var upscalePaint = new SKPaint { FilterQuality = SKFilterQuality.None };
        dstCanvas.DrawBitmap(small, new SKRect(0, 0, src.Width, src.Height), upscalePaint);
        return dst;
    }

    /// <summary>
    /// 万華鏡：画像を扇形に切り、隣り合う扇形を鏡像にして円状に並べる。
    /// 扇形の枚数だけ画像全体を描き直すため、分割数が多いほど重い（最大12枚）。
    /// transparentBackground が true（テキスト用）なら背景を透明で塗る。扇形の和集合は
    /// 画像全体を覆う円になるため、ズーム=1 では背景色はどこにも見えない。
    /// ズーム&lt;1 では扇形の外側に背景色が残る：映像では従来の黒、テキストでは透明
    /// （下のトラックが見える）が正しい。
    /// </summary>
    internal static SKBitmap ApplyKaleidoscope(SKBitmap src, ClipEffect effect, bool transparentBackground = false)
    {
        var segments = (int)Math.Clamp(Math.Round(effect.Radius), 3, 12);
        var zoom = (float)Math.Clamp(effect.Amount, 0.25, 2);
        var dst = new SKBitmap(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(dst);
        canvas.Clear(transparentBackground ? SKColors.Transparent : SKColors.Black);

        var cx = src.Width / 2f;
        var cy = src.Height / 2f;
        var radius = MathF.Sqrt(cx * cx + cy * cy) + 1;
        var wedgeAngle = 360f / segments;
        var half = wedgeAngle / 2 * (MathF.PI / 180f);
        using var wedge = new SKPath();
        wedge.MoveTo(0, 0);
        wedge.LineTo(radius * MathF.Cos(-half), radius * MathF.Sin(-half));
        wedge.LineTo(radius * MathF.Cos(half), radius * MathF.Sin(half));
        wedge.Close();

        var halfWidth = cx * zoom;
        var halfHeight = cy * zoom;
        for (var i = 0; i < segments; i++)
        {
            canvas.Save();
            canvas.Translate(cx, cy);
            canvas.RotateDegrees(i * wedgeAngle);
            if (i % 2 == 1)
                canvas.Scale(-1, 1); // 隣り合う扇形は鏡像にする。
            canvas.ClipPath(wedge);
            canvas.DrawBitmap(src, new SKRect(-halfWidth, -halfHeight, halfWidth, halfHeight));
            canvas.Restore();
        }
        return dst;
    }

    /// <summary>最近傍で1画素を読む（範囲外は端で止める）。Premul BGRA の (B, G, R, A)。</summary>
    private static (byte B, byte G, byte R, byte A) SampleNearest(
        byte[] bytes, int rowBytes, int width, int height, double x, double y)
    {
        var ix = (int)Math.Clamp(Math.Round(x), 0, width - 1);
        var iy = (int)Math.Clamp(Math.Round(y), 0, height - 1);
        var i = iy * rowBytes + ix * 4;
        return (bytes[i], bytes[i + 1], bytes[i + 2], bytes[i + 3]);
    }

    /// <summary>
    /// 魚眼・色収差を1回の画素ループで適用する（どちらも中心からの放射状の処理のため統合）。
    /// 魚眼：中心からの距離でサンプル位置を内側へ寄せる（膨らんだ見た目）。範囲外は透明になる。
    /// 色収差：中心からの距離に比例して R を中心側・B を外側からサンプルする（放射状の色ずれ。
    /// RGBシフトの一律ずれとは別物）。どちらも無効なら元のビットマップをそのまま返す
    /// （TrackIntermediate が破棄リストに載せないため、呼び出し側の所有権は変わらない）。
    /// キーで抜いた縁では、色収差の R/B が透明な隣から読まれて少し暗くなる場合がある。
    /// </summary>
    internal static SKBitmap ApplyRadialWarp(SKBitmap src, ClipEffect? fisheye, ClipEffect? chromaticAberration)
    {
        var distortion = fisheye is not null ? Math.Clamp(fisheye.Amount, 0, 1) : 0;
        var zoom = fisheye is not null ? Math.Clamp(fisheye.Radius, 1, 2) : 1;
        var caAmount = chromaticAberration is not null ? Math.Clamp(chromaticAberration.Amount, 0, 20) : 0;
        var caInner = chromaticAberration is not null ? Math.Clamp(chromaticAberration.Radius, 0, 0.5) : 0;
        if (distortion <= 0 && caAmount <= 0)
            return src;

        var width = src.Width;
        var height = src.Height;
        var rowBytes = src.RowBytes;
        var srcBytes = new byte[rowBytes * height];
        Marshal.Copy(src.GetPixels(), srcBytes, 0, srcBytes.Length);
        var dstBytes = new byte[rowBytes * height];

        var cx = width / 2.0;
        var cy = height / 2.0;
        var halfDiag = Math.Sqrt(cx * cx + cy * cy); // 正規化距離の分母（隅が 1）。

        for (var y = 0; y < height; y++)
        {
            var dy = y - cy;
            var row = y * rowBytes;
            for (var x = 0; x < width; x++)
            {
                var dx = x - cx;
                var i = row + x * 4;
                var dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist < 1e-9)
                {
                    // 中心：そのままコピー。
                    dstBytes[i] = srcBytes[i];
                    dstBytes[i + 1] = srcBytes[i + 1];
                    dstBytes[i + 2] = srcBytes[i + 2];
                    dstBytes[i + 3] = srcBytes[i + 3];
                    continue;
                }

                // 魚眼：サンプル位置を中心へ寄せる（1 + 歪み×r^2 で割る）。ズームはその後に掛ける。
                var r2 = (dx / cx) * (dx / cx) + (dy / cy) * (dy / cy);
                var factor = distortion > 0 ? zoom / (1 + distortion * r2) : 1;
                var sx = cx + dx * factor;
                var sy = cy + dy * factor;
                if (distortion > 0 && (sx < 0 || sx >= width || sy < 0 || sy >= height))
                    continue; // 魚眼で範囲外：透明（dst は 0 で初期化済み）。

                // 色収差：中心からの距離（半対角線に対する割合）に比例したずれ。
                var d = dist / halfDiag;
                var shift = caAmount > 0 ? caAmount * Math.Max(0, d - caInner) : 0;
                var ux = dx / dist;
                var uy = dy / dist;

                var g = SampleNearest(srcBytes, rowBytes, width, height, sx, sy);
                byte r = g.R;
                byte b = g.B;
                if (shift > 0)
                {
                    var rs = SampleNearest(srcBytes, rowBytes, width, height, sx - shift * ux, sy - shift * uy);
                    var bs = SampleNearest(srcBytes, rowBytes, width, height, sx + shift * ux, sy + shift * uy);
                    r = rs.R;
                    b = bs.B;
                }
                dstBytes[i] = b;
                dstBytes[i + 1] = g.G;
                dstBytes[i + 2] = r;
                dstBytes[i + 3] = g.A;
            }
        }

        var dst = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        Marshal.Copy(dstBytes, 0, dst.GetPixels(), dstBytes.Length);
        return dst;
    }

    /// <summary>
    /// ズームブラー：中心から少しずつ拡大したコピーを Plus 合成で平均化する
    /// （不透明度 1/N ずつ加算すると平均になり、透明度も正しく保たれる）。
    /// 強さが小さいほど拡大率の広がりも小さいため、0 に近いと元の画像に近づく。
    /// サンプル数（Radius）が多いほど滑らかだが、コピーの枚数だけ描画が重くなる。
    /// </summary>
    internal static SKBitmap ApplyZoomBlur(SKBitmap src, ClipEffect effect)
    {
        var strength = Math.Clamp(effect.Amount, 0, 1);
        var samples = (int)Math.Clamp(Math.Round(effect.Radius), 4, 16);
        var dst = new SKBitmap(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(dst);
        var cx = src.Width / 2f;
        var cy = src.Height / 2f;
        var rect = new SKRect(0, 0, src.Width, src.Height);
        var maxScale = 1f + (float)strength * 0.12f; // 端で最大 12% 広がる。
        var alpha = (byte)Math.Round(255.0 / samples);
        for (var i = 0; i < samples; i++)
        {
            var t = samples == 1 ? 0 : (float)i / (samples - 1);
            var scale = 1 + (maxScale - 1) * t;
            using var paint = new SKPaint
            {
                Color = new SKColor(255, 255, 255, alpha),
                BlendMode = SKBlendMode.Plus
            };
            canvas.Save();
            canvas.Translate(cx, cy);
            canvas.Scale(scale, scale);
            canvas.Translate(-cx, -cy);
            canvas.DrawBitmap(src, rect, paint);
            canvas.Restore();
        }
        return dst;
    }

    /// <summary>
    /// モーションブラー：指定した方向へ均等にずらしたコピー 8 枚を Plus 合成で平均化する
    /// （ボックスブラー近似）。長さが 0 なら呼ばれない。端ではサンプルが画面外へはみ出す分だけ
    /// 少し暗く・透明になる（ボックスブラーの端の性質）。
    /// </summary>
    internal static SKBitmap ApplyMotionBlur(SKBitmap src, ClipEffect effect)
    {
        var length = Math.Clamp(effect.Amount, 0, 64);
        const int samples = 8;
        var angle = Math.Clamp(effect.Radius, 0, 360) * Math.PI / 180;
        var dx = Math.Cos(angle);
        var dy = Math.Sin(angle);
        var dst = new SKBitmap(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(dst);
        var alpha = (byte)Math.Round(255.0 / samples);
        for (var i = 0; i < samples; i++)
        {
            var t = (double)i / (samples - 1) - 0.5; // -0.5〜+0.5
            var ox = (float)(dx * length * t);
            var oy = (float)(dy * length * t);
            using var paint = new SKPaint
            {
                Color = new SKColor(255, 255, 255, alpha),
                BlendMode = SKBlendMode.Plus
            };
            canvas.DrawBitmap(src, new SKRect(ox, oy, src.Width + ox, src.Height + oy), paint);
        }
        return dst;
    }

    /// <summary>
    /// VHSグリッチ：トラッキングずれ（横帯ごとのランダムな横ずれ）・走査線・色のにじみを
    /// 1回の画素ループで適用する。帯のずれは時刻で変わるため、再生するたびに揺れる。
    /// </summary>
    internal static SKBitmap ApplyVhsGlitch(SKBitmap src, ClipEffect effect, double timeSeconds)
    {
        var strength = Math.Clamp(effect.Amount, 0, 1);
        var spacing = (int)Math.Clamp(Math.Round(effect.Radius), 2, 8);
        var width = src.Width;
        var height = src.Height;
        var rowBytes = src.RowBytes;
        var srcBytes = new byte[rowBytes * height];
        Marshal.Copy(src.GetPixels(), srcBytes, 0, srcBytes.Length);
        var dstBytes = new byte[rowBytes * height];

        var frameIndex = (int)Math.Round(timeSeconds * 1000);
        const int bandHeight = 24;
        var bandCount = height / bandHeight + 1;
        var bandOffsets = new int[bandCount];
        var maxOffset = strength * 32;
        for (var band = 0; band < bandCount; band++)
        {
            // 帯の半分くらいがずれ、残りはそのまま（トラッキングが時々乱れる感じ）。
            bandOffsets[band] = RenderHelpers.NoiseValue(band, 0, frameIndex) < 0.5
                ? 0
                : (int)Math.Round((RenderHelpers.NoiseValue(band, 1, frameIndex) - 0.5) * 2 * maxOffset);
        }
        var bleed = 1 + (int)Math.Round(strength * 2); // R/B の横ずれ（px）。
        var scanlineDarken = 1 - 0.35 * strength;

        for (var y = 0; y < height; y++)
        {
            var offset = bandOffsets[y / bandHeight];
            var isScanline = y % spacing == spacing - 1;
            var row = y * rowBytes;
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Clamp(x - offset, 0, width - 1);
                var i = row + x * 4;
                var si = row + sx * 4;
                var g = srcBytes[si + 1];
                var a = srcBytes[si + 3];
                var r = srcBytes[row + Math.Clamp(sx + bleed, 0, width - 1) * 4 + 2];
                var b = srcBytes[row + Math.Clamp(sx - bleed, 0, width - 1) * 4];
                if (isScanline)
                {
                    r = (byte)(r * scanlineDarken);
                    g = (byte)(g * scanlineDarken);
                    b = (byte)(b * scanlineDarken);
                }
                // Premul を壊さないよう、RGB はアルファ以下に丸める。
                dstBytes[i] = (byte)Math.Min(b, a);
                dstBytes[i + 1] = (byte)Math.Min(g, a);
                dstBytes[i + 2] = (byte)Math.Min(r, a);
                dstBytes[i + 3] = a;
            }
        }

        var dst = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        Marshal.Copy(dstBytes, 0, dst.GetPixels(), dstBytes.Length);
        return dst;
    }
}
