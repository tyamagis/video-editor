// 描画まわりの小さな共有ヘルパー（色パース・ブレンドモード写像・ブラーフィルタ・ノイズハッシュ）。
// FrameRenderer.cs から切り出した。VisualizationRenderer・ClipCompositor・
// TextClipRenderer・各エフェクトヘルパーから使う。

using Editor.Core;
using SkiaSharp;

namespace Editor.Render;

/// <summary>描画まわりの小さな共有ヘルパー（状態を持たない）。</summary>
internal static class RenderHelpers
{
    /// <summary>クリップのブレンドモードを SkiaSharp のブレンドモードに写す。</summary>
    internal static SKBlendMode MapBlendMode(BlendMode mode) => mode switch
    {
        BlendMode.Add => SKBlendMode.Plus,
        BlendMode.Screen => SKBlendMode.Screen,
        BlendMode.Multiply => SKBlendMode.Multiply,
        BlendMode.Overlay => SKBlendMode.Overlay,
        _ => SKBlendMode.SrcOver
    };

    internal static SKColor ParseColor(string hex, double opacity)
    {
        if (SKColor.TryParse(hex, out var color))
            return color.WithAlpha((byte)Math.Clamp(opacity * 255, 0, 255));
        return SKColors.White.WithAlpha((byte)Math.Clamp(opacity * 255, 0, 255));
    }

    /// <summary>ぼかし量が 0 より大きいときだけブラーフィルタを作る（0 以下は null）。</summary>
    internal static SKImageFilter? CreateBlurFilter(double blur)
        => blur > 0 ? SKImageFilter.CreateBlur((float)blur, (float)blur) : null;

    /// <summary>粒の位置とフレームから決まる 0〜1 の疑似乱数（ハッシュ関数。時刻が変わると粒が変わる）。</summary>
    internal static double NoiseValue(int blockX, int blockY, int frame)
    {
        uint h = (uint)(blockX * 374761393 + blockY * 668265263 + frame * 974634211);
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;
        return (h & 0xFFFF) / 65535.0;
    }
}
