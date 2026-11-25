// キーイング（クロマキー・輝度キー）。
// FrameRenderer.cs から切り出した、状態を持たない静的ヘルパー。
// 画素単位で抜くため、入力ビットマップを直接書き換える。

using System.Runtime.InteropServices;
using Editor.Core;
using SkiaSharp;

namespace Editor.Render;

/// <summary>キーイング（状態を持たない静的ヘルパー）。</summary>
internal static class KeyingEffects
{
    /// <summary>
    /// クロマキー：キー色に近い画素を透明にする（画素単位の CPU 処理）。
    /// しきい値以下は透明、しきい値＋なめらかさ以上は不透明、その間は滑らかに変化する。
    /// 抜けた分だけキー色のスピル（色かぶり）も落とす。Premul ビットマップのため
    /// RGB も新しいアルファに合わせて掛け直す。
    /// </summary>
    internal static void ApplyChromaKey(SKBitmap bitmap, ClipEffect effect)
    {
        if (!SKColor.TryParse(effect.KeyColorHex, out var keyColor))
            keyColor = new SKColor(0, 255, 0);

        var threshold = Math.Clamp(effect.Amount, 0, 1);
        var softness = Math.Max(effect.Radius, 0.01);
        var kr = keyColor.Red;
        var kg = keyColor.Green;
        var kb = keyColor.Blue;

        // 色距離の二乗で判定する（sqrt は遷移帯の画素だけで済ませる）。
        // 距離は 0〜1 に正規化する（最大値 sqrt(3*255^2) = 441.673）。
        const double maxDist = 441.673;
        var t0 = threshold * maxDist;
        var t1 = (threshold + softness) * maxDist;
        var t0Sq = t0 * t0;
        var t1Sq = t1 * t1;

        // キー色で最も強いチャンネル（緑なら G）。スピル除去で使う。
        var dominant = kr >= kg && kr >= kb ? 0 : kg >= kb ? 1 : 2;

        var rowBytes = bitmap.RowBytes;
        var height = bitmap.Height;
        var width = bitmap.Width;
        var bytes = new byte[rowBytes * height];
        Marshal.Copy(bitmap.GetPixels(), bytes, 0, bytes.Length);

        for (var y = 0; y < height; y++)
        {
            var row = y * rowBytes;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                var b = bytes[i];
                var g = bytes[i + 1];
                var r = bytes[i + 2];
                var a = bytes[i + 3];

                var dr = r - kr;
                var dg = g - kg;
                var db = b - kb;
                var distSq = dr * dr + dg * dg + db * db;
                if (distSq >= t1Sq)
                    continue; // キー色から遠い（不透明のまま）。

                double keep;
                if (distSq <= t0Sq)
                {
                    keep = 0; // キー色そのもの（透明）。
                }
                else
                {
                    var dist = Math.Sqrt(distSq);
                    keep = (dist - t0) / (t1 - t0);
                }

                var na = (int)Math.Round(a * keep);

                // スピル除去：抜けた分だけ、キー色の強いチャンネルを他のチャンネルの最大値まで落とす。
                // （int で持つ。byte のままだと (int) への代入と Math.Clamp の解決ができない。）
                var spill = 1 - keep;
                if (spill > 0)
                {
                    var ri = (int)r;
                    var gi = (int)g;
                    var bi = (int)b;
                    var otherMax = dominant == 0 ? Math.Max(gi, bi)
                        : dominant == 1 ? Math.Max(ri, bi)
                        : Math.Max(ri, gi);
                    if (dominant == 0)
                        ri = (int)Math.Round(ri - (ri - otherMax) * spill);
                    else if (dominant == 1)
                        gi = (int)Math.Round(gi - (gi - otherMax) * spill);
                    else
                        bi = (int)Math.Round(bi - (bi - otherMax) * spill);
                    r = (byte)Math.Clamp(ri, 0, 255);
                    g = (byte)Math.Clamp(gi, 0, 255);
                    b = (byte)Math.Clamp(bi, 0, 255);
                }

                // Premul に合わせる：RGB も新しいアルファに掛け直す。
                var scale = a > 0 ? (double)na / a : 0;
                bytes[i] = (byte)Math.Clamp(b * scale, 0, 255);
                bytes[i + 1] = (byte)Math.Clamp(g * scale, 0, 255);
                bytes[i + 2] = (byte)Math.Clamp(r * scale, 0, 255);
                bytes[i + 3] = (byte)na;
            }
        }

        Marshal.Copy(bytes, 0, bitmap.GetPixels(), bytes.Length);
    }

    /// <summary>
    /// 輝度キー：明るい画素を透明にする（クロマキーの明暗版。白背景の除去など）。
    /// しきい値より明るい画素を抜き始め、しきい値＋なめらかさより明るい画素を完全に抜く。
    /// その間は滑らかに変化する。Premul ビットマップのため RGB も新しいアルファに合わせて掛け直す。
    /// </summary>
    internal static void ApplyLuminanceKey(SKBitmap bitmap, ClipEffect effect)
    {
        var threshold = Math.Clamp(effect.Amount, 0, 1);
        var softness = Math.Max(effect.Radius, 0.01);
        var t0 = threshold * 255;
        var t1 = Math.Min((threshold + softness) * 255, 255);

        var rowBytes = bitmap.RowBytes;
        var height = bitmap.Height;
        var width = bitmap.Width;
        var bytes = new byte[rowBytes * height];
        Marshal.Copy(bitmap.GetPixels(), bytes, 0, bytes.Length);

        for (var y = 0; y < height; y++)
        {
            var row = y * rowBytes;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                var b = bytes[i];
                var g = bytes[i + 1];
                var r = bytes[i + 2];
                var a = bytes[i + 3];

                // 輝度（Premul の RGB から。0.213R + 0.715G + 0.072B）。
                var luminance = 0.213 * r + 0.715 * g + 0.072 * b;
                if (luminance <= t0)
                    continue; // 暗いまま（不透明）。

                double keep;
                if (luminance >= t1)
                {
                    keep = 0; // 明るい（透明）。
                }
                else
                {
                    keep = (t1 - luminance) / (t1 - t0);
                }

                var na = (int)Math.Round(a * keep);

                // Premul に合わせる：RGB も新しいアルファに掛け直す。
                var scale = a > 0 ? (double)na / a : 0;
                bytes[i] = (byte)Math.Clamp(b * scale, 0, 255);
                bytes[i + 1] = (byte)Math.Clamp(g * scale, 0, 255);
                bytes[i + 2] = (byte)Math.Clamp(r * scale, 0, 255);
                bytes[i + 3] = (byte)na;
            }
        }

        Marshal.Copy(bytes, 0, bitmap.GetPixels(), bytes.Length);
    }
}
