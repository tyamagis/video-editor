// LUT（3D LUT）エフェクトの描画本体とパース結果キャッシュ。
// 契約：src は変更しない。常に新しい SKBitmap を返す（無効な量・ファイル未指定・
// 読み込み失敗のときは src のコピーを返す＝無効化して元の色のまま）。
// ビットマップ形式は Bgra8888・Premul。画素は非乗算（unpremultiply）してから
// LUT を引き、Amount で元の色と混ぜ、再び乗算して Premul に戻す。
// 透明画素（a=0）は色を持たないためそのまま透明に保つ。

using System.Runtime.InteropServices;
using Editor.Core;
using SkiaSharp;

namespace Editor.Render;

/// <summary>
/// LUTエフェクトの描画ヘルパーと、.cube のパース結果キャッシュ。
/// キャッシュは SourceIdentity（フルパス＋サイズ＋更新日時）で照合する
/// （素材キャッシュと同じ流儀）。ファイルが差し替われば再パースされ、
/// 見つからなければ照合自体が失敗して無効化される。読み込み失敗も成功と同じく
/// 身元とセットでキャッシュし、フレームごとに再パースしない。
/// </summary>
internal static class LutEffect
{
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, (SourceIdentity Identity, CubeLut? Lut)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// パスから LUT を引く（キャッシュ経由）。未指定・ファイル無し・パース失敗は null
    /// （＝LUT無効）。例外は出さない。
    /// </summary>
    internal static CubeLut? Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        if (!SourceIdentity.TryCapture(path, out var identity))
            return null; // ファイルが無い・パス不正 → 無効化。

        lock (CacheGate)
        {
            if (Cache.TryGetValue(identity.FullPath, out var entry) && entry.Identity.Matches(identity))
                return entry.Lut;
        }

        CubeLut.TryLoad(path, out var lut, out _);
        lock (CacheGate)
            Cache[identity.FullPath] = (identity, lut);
        return lut;
    }

    /// <summary>テスト・診断用：キャッシュを空にする。</summary>
    internal static void ClearCache()
    {
        lock (CacheGate)
            Cache.Clear();
    }

    /// <summary>
    /// LUT を適用する。Amount = 強さ（0〜1。0 で変化なし、1 で完全適用）。
    /// LUT が引けない（未指定・見つからない・不正）場合は src のコピーを返す。
    /// 画素ループは行単位で並列化する（既存の画素系エフェクトの流儀）。
    /// </summary>
    internal static SKBitmap ApplyLut(SKBitmap src, ClipEffect effect)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(effect);
        var amount = double.IsFinite(effect.Amount) ? Math.Clamp(effect.Amount, 0, 1) : 1;
        if (amount <= 0)
            return src.Copy();
        var lut = Resolve(effect.LutPath);
        if (lut is null)
            return src.Copy();

        var width = src.Width;
        var height = src.Height;
        var rowBytes = src.RowBytes;
        var srcBytes = new byte[rowBytes * height];
        Marshal.Copy(src.GetPixels(), srcBytes, 0, srcBytes.Length);
        var dstBytes = new byte[rowBytes * height];
        var mix = (float)amount;
        var keep = 1f - mix;

        Parallel.For(0, height, y =>
        {
            var row = y * rowBytes;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                var a = srcBytes[i + 3];
                if (a == 0)
                    continue; // 透明画素は色を持たない（dstBytes は 0 初期化＝透明のまま）。
                // Premul → 非乗算（0〜1）。
                var scale = 255f / a;
                var r = srcBytes[i + 2] * scale / 255f;
                var g = srcBytes[i + 1] * scale / 255f;
                var b = srcBytes[i] * scale / 255f;
                var (lr, lg, lb) = lut.Sample(r, g, b);
                // 元の色と混ぜ、0〜1 に丸めてから Premul に戻す（out × a ≤ a が保証される）。
                dstBytes[i] = (byte)Math.Round(Math.Clamp(b * keep + lb * mix, 0f, 1f) * a);
                dstBytes[i + 1] = (byte)Math.Round(Math.Clamp(g * keep + lg * mix, 0f, 1f) * a);
                dstBytes[i + 2] = (byte)Math.Round(Math.Clamp(r * keep + lr * mix, 0f, 1f) * a);
                dstBytes[i + 3] = a;
            }
        });

        var dst = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        Marshal.Copy(dstBytes, 0, dst.GetPixels(), dstBytes.Length);
        return dst;
    }
}
