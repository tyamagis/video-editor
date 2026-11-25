// 画素ループ系のエフェクト（ポスタリゼーション・ノイズ・フィルムグレイン・
// トーンカーブ・ビネット）。FrameRenderer.cs から切り出した、
// 状態を持たない静的ヘルパー。入力ビットマップを直接書き換える。

using System.Runtime.InteropServices;
using Editor.Core;
using SkiaSharp;

namespace Editor.Render;

/// <summary>画素ループ系のエフェクト（状態を持たない静的ヘルパー）。</summary>
internal static class CpuPixelEffects
{
    /// <summary>
    /// ポスタリゼーション・ノイズ・フィルムグレイン・トーンカーブ・ビネットを1回の画素ループで適用する（Premul BGRA）。
    /// ポスタリゼーション：チャンネルごとに階調数へ量子化する。
    /// ノイズ：粒ごとに決まるハッシュ値で輝度を揺らす（時刻で変わる）。
    /// フィルムグレイン：ノイズより弱い振幅の粒状感（ノイズとは別の種でずらすため、両方付けても粒が重ならない）。
    /// トーンカーブ：シャドウ／中間調／ハイライトの3点調整（LUT。中間調は黒点・白点を動かさない山形の項）。
    /// ビネット：中心からの距離で周囲を暗くする。
    /// RGB はアルファを超えないよう丸める（Premul を壊さないため）。
    /// </summary>
    internal static void Apply(SKBitmap bitmap, IReadOnlyList<ClipEffect> effects, double timeSeconds)
    {
        var posterize = effects.FirstOrDefault(e => e.Kind == EffectKind.Posterize);
        var noise = effects.FirstOrDefault(e => e.Kind == EffectKind.Noise);
        var filmGrain = effects.FirstOrDefault(e => e.Kind == EffectKind.FilmGrain);
        var toneCurve = effects.FirstOrDefault(e => e.Kind == EffectKind.ToneCurve);
        var vignette = effects.FirstOrDefault(e => e.Kind == EffectKind.Vignette);
        if (posterize is null && noise is null && filmGrain is null && toneCurve is null && vignette is null)
            return;

        var levels = posterize is not null
            ? (int)Math.Clamp(Math.Round(posterize.Amount), 2, 16) : 0;
        var noiseAmount = noise?.Amount ?? 0;
        var grain = noise is not null ? (int)Math.Clamp(Math.Round(noise.Radius), 1, 8) : 1;
        var filmGrainAmount = filmGrain?.Amount ?? 0;
        var filmGrainSize = filmGrain is not null ? (int)Math.Clamp(Math.Round(filmGrain.Radius), 1, 8) : 1;
        var toneTable = toneCurve is not null
            ? BuildToneCurveTable(toneCurve.Amount, toneCurve.Midtones, toneCurve.Radius) : null;
        var vignetteAmount = vignette?.Amount ?? 0;
        var vignetteRadius = vignette is not null ? Math.Clamp(vignette.Radius, 0.5, 2) : 1;

        // ノイズはフレーム（時刻のミリ秒）ごとに変わる。同じ時刻なら同じ粒になる。
        var frameIndex = (int)Math.Round(timeSeconds * 1000);

        var rowBytes = bitmap.RowBytes;
        var height = bitmap.Height;
        var width = bitmap.Width;
        var bytes = new byte[rowBytes * height];
        Marshal.Copy(bitmap.GetPixels(), bytes, 0, bytes.Length);

        var step = levels > 0 ? 255.0 / (levels - 1) : 0;
        var halfWidth = Math.Max(1, width / 2.0);
        var halfHeight = Math.Max(1, height / 2.0);

        for (var y = 0; y < height; y++)
        {
            var row = y * rowBytes;
            var ny = (y - height / 2.0) / halfHeight;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                var b = bytes[i];
                var g = bytes[i + 1];
                var r = bytes[i + 2];
                var a = bytes[i + 3];

                if (levels > 0)
                {
                    r = (byte)Math.Clamp(Math.Round(r / step) * step, 0, 255);
                    g = (byte)Math.Clamp(Math.Round(g / step) * step, 0, 255);
                    b = (byte)Math.Clamp(Math.Round(b / step) * step, 0, 255);
                }

                if (toneTable is not null)
                {
                    r = toneTable[r];
                    g = toneTable[g];
                    b = toneTable[b];
                }

                if (vignetteAmount > 0)
                {
                    var nx = (x - width / 2.0) / halfWidth;
                    var d = Math.Sqrt(nx * nx + ny * ny);
                    var edge = Math.Clamp((d - vignetteRadius * 0.55) / (vignetteRadius * 0.45 + 0.05), 0, 1);
                    var factor = 1 - vignetteAmount * edge * edge;
                    r = (byte)(r * factor);
                    g = (byte)(g * factor);
                    b = (byte)(b * factor);
                }

                if (noiseAmount > 0 || filmGrainAmount > 0)
                {
                    // ノイズとフィルムグレインは両方あれば足し合わせる（粒の種が違うため相関しない）。
                    var delta = 0.0;
                    if (noiseAmount > 0)
                        delta += (RenderHelpers.NoiseValue(x / grain, y / grain, frameIndex) - 0.5) * 2 * noiseAmount * 96;
                    if (filmGrainAmount > 0)
                        delta += (RenderHelpers.NoiseValue(x / filmGrainSize, y / filmGrainSize, frameIndex + 7919) - 0.5) * 2 * filmGrainAmount * 48;
                    r = (byte)Math.Clamp(r + delta, 0, a);
                    g = (byte)Math.Clamp(g + delta, 0, a);
                    b = (byte)Math.Clamp(b + delta, 0, a);
                }
                else
                {
                    // ノイズ類が無くても、ポスタリゼーション・トーンカーブで RGB がアルファを超えた分を丸める。
                    r = (byte)Math.Min(r, a);
                    g = (byte)Math.Min(g, a);
                    b = (byte)Math.Min(b, a);
                }

                bytes[i] = b;
                bytes[i + 1] = g;
                bytes[i + 2] = r;
            }
        }

        Marshal.Copy(bytes, 0, bitmap.GetPixels(), bytes.Length);
    }

    /// <summary>
    /// トーンカーブの LUT（256段）。シャドウ s・中間調 m・ハイライト h は -1〜1。
    /// y = x + s(1-x)^2 + m·4x(1-x) + h x^2。
    /// 中間調の項 4x(1-x) は x=0.5 で 1（最大）、両端で 0 のため、黒点・白点は動かさず
    /// 中間だけを持ち上げ／沈める。m = 0 のとき式は旧2点版 y = x + s(1-x)^2 + h x^2 と
    /// 完全に一致するため、旧プロジェクト（Midtones なし＝0）の描画結果は変わらない。
    /// 3つとも 0 なら null（変化なし）。
    /// なお旧版と同じく、3点が極端に逆向き（例：s=1, h=-1）のとき曲線は単調でなくなり
    /// 階調が反転し得る。範囲は 0〜255 に丸めるので値は暴走しない。
    /// </summary>
    private static byte[]? BuildToneCurveTable(double shadows, double midtones, double highlights)
    {
        if (Math.Abs(shadows) < 1e-6 && Math.Abs(midtones) < 1e-6 && Math.Abs(highlights) < 1e-6)
            return null;
        var s = Math.Clamp(shadows, -1, 1);
        var m = Math.Clamp(midtones, -1, 1);
        var h = Math.Clamp(highlights, -1, 1);
        var table = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var x = i / 255.0;
            var y = x + s * (1 - x) * (1 - x) + m * 4 * x * (1 - x) + h * x * x;
            table[i] = (byte)Math.Clamp(Math.Round(y * 255), 0, 255);
        }
        return table;
    }
}
