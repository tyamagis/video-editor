// テキストクリップの描画。FrameRenderer.cs から切り出した、状態を持たない静的ヘルパー。
// エフェクト無しなら直接描き、エフェクト付きならフルフレームの透明オフスクリーンに描いてから
// ClipCompositor（映像クリップと同じエフェクト適用経路）に通す。

using System.Globalization;
using Editor.Core;
using SkiaSharp;

namespace Editor.Render;

/// <summary>テキストクリップの描画（状態を持たない静的ヘルパー）。</summary>
internal static class TextClipRenderer
{
    /// <summary>
    /// テキストクリップを描く。エフェクト無し（かつ色調補正も既定値）なら従来どおり
    /// 直接描く。エフェクトが付いていれば、テキストをフルフレームの透明オフスクリーンに
    /// 最終サイズで描いてから、映像クリップと同じ DrawBitmapWithTransform に通す。
    /// フルフレームにするのは、ビネットなどフレーム相対のエフェクトの意味と fit 倍率
    /// （=1）を映像クリップと揃えるため。位置・回転・不透明度・色調補正はパイプライン側が
    /// 付けるので、オフスクリーンでは中心に回転無し・不透明で描く（ブラーもパイプライン側）。
    /// プレビューと書き出しは同じ RenderFrameAsync を通るため、両方に同じく反映される。
    /// キーフレーム・音連動バインドは Evaluator が state.Effects に評価済みで入れてある
    /// ため、ここでは何もしない（映像クリップと同じ）。
    /// </summary>
    internal static void Draw(
        SKCanvas canvas, Clip clip, EvaluatedClipState state, int width, int height,
        ClipCompositor compositor)
    {
        if (string.IsNullOrEmpty(clip.Text))
            return;

        // テキストアニメーション（Evaluator が state.TextAnimation に評価済み）。
        // IsIdentity なら従来の経路をそのまま通す（アニメーション無しの描画結果を
        // 従来とビット単位で変えない）。非Identity の場合の実効値は
        // 不透明度 = state.Opacity * anim.Opacity、Scale = state.Scale * anim.Scale、
        // 位置 = PositionX/Y + (OffsetXFraction/OffsetYFraction * height)。
        var anim = state.TextAnimation;

        // エフェクト無し（かつ色調補正も既定値）なら直接描画する。オフスクリーン経由では
        // Premul の丸めが ±1 入るため、エフェクト無しの描画結果を従来とビット単位で
        // 同じに保つには直接描画が要る。
        if (!HasActiveTextEffect(state.Effects)
            && Math.Abs(state.Brightness) < 1e-6 && Math.Abs(state.Contrast - 1.0) < 1e-6)
        {
            if (anim.IsIdentity)
                DrawTextDirect(canvas, clip, state, width, height);
            else
                DrawTextDirectAnimated(canvas, clip, state, width, height, anim);
            return;
        }

        using var offscreen = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var offCanvas = new SKCanvas(offscreen))
        {
            offCanvas.Clear(SKColors.Transparent);
            DrawTextGlyphs(offCanvas, clip, state, width, height, anim);
        }

        // パイプラインに渡す状態：Scale はオフスクリーン描画時に既に掛けたので 1 にする
        // （二重に掛けると文字が Scale^2 倍になる）。不透明度と位置にはアニメーションの
        // 実効値を入れる。TextAnimation はここで適用済みのため Identity にする。
        // テキストに意味のないキー系エフェクトはここで外す（UIで付けられないが、
        // 手書きのプロジェクトに入っていても無視される。文字色を消すだけのため）。
        var pipelineState = new EvaluatedClipState
        {
            Clip = state.Clip,
            TimeSeconds = state.TimeSeconds,
            Opacity = state.Opacity * anim.Opacity,
            Scale = 1.0,
            RotationDegrees = state.RotationDegrees,
            PositionX = state.PositionX + anim.OffsetXFraction * height,
            PositionY = state.PositionY + anim.OffsetYFraction * height,
            Brightness = state.Brightness,
            Contrast = state.Contrast,
            BlendMode = state.BlendMode,
            Effects = state.Effects.Where(e => EffectDefinitions.AppliesToText(e.Kind)).ToList(),
            Visualization = state.Visualization,
            TextAnimation = TextAnimationState.Identity
        };
        compositor.DrawBitmapWithTransform(canvas, offscreen, pipelineState, width, height);
    }

    /// <summary>
    /// テキストに付いたエフェクトのうち、描画に実際に変化を与えるものがあるか。
    /// 判定は DrawBitmapWithTransform 側のガード（量 0 なら何もしない等）と揃えてある。
    /// キー系はテキストでは無効なので数えない。
    /// </summary>
    private static bool HasActiveTextEffect(IReadOnlyList<ClipEffect> effects)
    {
        foreach (var effect in effects)
        {
            if (!EffectDefinitions.AppliesToText(effect.Kind))
                continue;
            if (TextEffectIsActive(effect))
                return true;
        }
        return false;
    }

    /// <summary>そのエフェクトが量 0 などで実質無効か。DrawBitmapWithTransform 内の各ガードと対応する。</summary>
    private static bool TextEffectIsActive(ClipEffect effect) => effect.Kind switch
    {
        EffectKind.Blur => effect.Amount > 0,
        EffectKind.Glow => effect.Amount > 0,
        EffectKind.RgbShift => effect.Amount > 0,
        EffectKind.Slice => effect.Amount > 0,
        EffectKind.Warp => effect.Amount > 0,
        EffectKind.Mosaic => effect.Amount >= 2,
        EffectKind.Vignette => effect.Amount > 0,
        EffectKind.Echo => effect.Amount > 0,
        EffectKind.Invert => effect.Amount > 0,
        EffectKind.HueShift => Math.Abs(effect.Amount) > 1e-6,
        EffectKind.Saturation => Math.Abs(effect.Amount - 1) > 1e-6,
        EffectKind.Monochrome => effect.Amount > 0,
        EffectKind.Sepia => effect.Amount > 0,
        EffectKind.Posterize => true,
        EffectKind.Noise => effect.Amount > 0,
        EffectKind.ChromaticAberration => effect.Amount > 0,
        EffectKind.Fisheye => effect.Amount > 0,
        EffectKind.VhsGlitch => effect.Amount > 0,
        EffectKind.DropShadow => effect.Amount > 0,
        EffectKind.FilmGrain => effect.Amount > 0,
        EffectKind.ZoomBlur => effect.Amount > 0,
        EffectKind.MotionBlur => effect.Amount > 0,
        EffectKind.ToneCurve => Math.Abs(effect.Amount) > 1e-6
            || Math.Abs(effect.Midtones) > 1e-6 || Math.Abs(effect.Radius) > 1e-6,
        EffectKind.Kaleidoscope => true,
        EffectKind.Bloom => effect.Amount > 0,
        EffectKind.LightLeak => effect.Amount > 0,
        EffectKind.PixelSort => effect.Amount > 0,
        EffectKind.Wave => effect.Amount > 0,
        EffectKind.Mirror => true,
        EffectKind.Twirl => Math.Abs(effect.Amount) > 1e-6,
        EffectKind.Displacement => effect.Amount > 0,
        EffectKind.Bulge => effect.Amount > 0,
        EffectKind.WaterRipple => effect.Amount > 0,
        EffectKind.ColorTemp => Math.Abs(effect.Amount) > 1e-6,
        EffectKind.Levels => effect.Amount > 0 || effect.Radius < 1 || Math.Abs(effect.Midtones - 1) > 1e-6,
        EffectKind.SplitTone => effect.Midtones > 0,
        EffectKind.GradientMap => true,
        EffectKind.OldFilm => effect.Amount > 0,
        EffectKind.Letterbox => effect.Amount > 0,
        EffectKind.Scanlines => effect.Amount > 0,
        EffectKind.Halftone => true,
        EffectKind.EdgeDetect => effect.Amount > 0,
        EffectKind.Solarize => effect.Amount < 1,
        EffectKind.Kuwahara => effect.Radius > 0,
        EffectKind.TiltShift => effect.Amount > 0,
        EffectKind.Sharpen => effect.Amount > 0,
        EffectKind.Emboss => effect.Amount > 0,
        EffectKind.Lut => effect.Amount > 0,
        // ChromaKey / LuminanceKey は HasActiveTextEffect で弾かれる。ここに来たら無効扱い。
        _ => false
    };

    /// <summary>従来のテキスト直接描画（ブレンドモードとブラーだけ）。エフェクト無しの経路で、見た目を従来と変えないためのもの。</summary>
    private static void DrawTextDirect(
        SKCanvas canvas, Clip clip, EvaluatedClipState state, int width, int height)
    {
        var blur = state.Effects
            .Where(e => e.Kind == EffectKind.Blur)
            .Select(e => e.Amount)
            .DefaultIfEmpty(0)
            .Max();

        using var typeface = SKTypeface.FromFamilyName(clip.FontFamily);
        using var blurFilter = RenderHelpers.CreateBlurFilter(blur);
        using var paint = new SKPaint
        {
            Typeface = typeface,
            TextSize = (float)(clip.FontSize * state.Scale),
            Color = RenderHelpers.ParseColor(clip.FontColorHex, state.Opacity),
            IsAntialias = true,
            BlendMode = RenderHelpers.MapBlendMode(state.BlendMode),
            ImageFilter = blurFilter
        };

        var textWidth = paint.MeasureText(clip.Text);
        canvas.Save();
        canvas.Translate((float)(width / 2.0 + state.PositionX), (float)(height / 2.0 + state.PositionY));
        canvas.RotateDegrees((float)state.RotationDegrees);
        canvas.DrawText(clip.Text, -textWidth / 2, paint.TextSize * 0.35f, paint);
        canvas.Restore();
    }

    /// <summary>
    /// アニメーション付きテキスト直接描画（エフェクト無しの経路）。
    /// 実効不透明度・実効Scale・実効位置（オフセット加算）を反映し、表示文字数・
    /// ハイライトは DrawTextSegments で分割描画する。回転・ブレンドモード・ブラーの
    /// 扱いは従来の DrawTextDirect と同じ。
    /// </summary>
    private static void DrawTextDirectAnimated(
        SKCanvas canvas, Clip clip, EvaluatedClipState state, int width, int height,
        TextAnimationState anim)
    {
        var effectiveOpacity = state.Opacity * anim.Opacity;
        var effectiveScale = state.Scale * anim.Scale;
        if (effectiveOpacity <= 0 || effectiveScale <= 0)
            return; // 見えないので描かない。

        var blur = state.Effects
            .Where(e => e.Kind == EffectKind.Blur)
            .Select(e => e.Amount)
            .DefaultIfEmpty(0)
            .Max();

        using var typeface = SKTypeface.FromFamilyName(clip.FontFamily);
        using var blurFilter = RenderHelpers.CreateBlurFilter(blur);
        using var paint = new SKPaint
        {
            Typeface = typeface,
            TextSize = (float)(clip.FontSize * effectiveScale),
            Color = RenderHelpers.ParseColor(clip.FontColorHex, effectiveOpacity),
            IsAntialias = true,
            BlendMode = RenderHelpers.MapBlendMode(state.BlendMode),
            ImageFilter = blurFilter
        };

        var fullWidth = paint.MeasureText(clip.Text);
        canvas.Save();
        canvas.Translate(
            (float)(width / 2.0 + state.PositionX + anim.OffsetXFraction * height),
            (float)(height / 2.0 + state.PositionY + anim.OffsetYFraction * height));
        canvas.RotateDegrees((float)state.RotationDegrees);
        DrawTextSegments(canvas, paint, clip.Text, anim,
            ParseHighlightColor(clip.AnimationIn?.HighlightColorHex, effectiveOpacity),
            -fullWidth / 2, paint.TextSize * 0.35f);
        canvas.Restore();
    }

    /// <summary>
    /// オフスクリーン（フルフレーム・透明背景）へ文字だけを描く。中心に回転無し・不透明で、
    /// 最終サイズ（FontSize × Scale × アニメーションScale）で描く。位置・回転・不透明度・
    /// ブレンド・ブラーはパイプライン側（DrawBitmapWithTransform）が付けるため、ここでは付けない。
    /// アニメーション無し（IsIdentity）の場合は従来の描画をそのまま行う。
    /// ベースライン位置（中心 + TextSize*0.35）は従来の直接描画と同じ。
    /// </summary>
    private static void DrawTextGlyphs(
        SKCanvas canvas, Clip clip, EvaluatedClipState state, int width, int height,
        TextAnimationState anim)
    {
        var effectiveScale = state.Scale * anim.Scale;
        using var typeface = SKTypeface.FromFamilyName(clip.FontFamily);
        using var paint = new SKPaint
        {
            Typeface = typeface,
            TextSize = (float)(clip.FontSize * effectiveScale),
            Color = RenderHelpers.ParseColor(clip.FontColorHex, 1.0),
            IsAntialias = true
        };

        if (anim.IsIdentity)
        {
            // 従来通り：フルテキストを1回で描く。
            var textWidth = paint.MeasureText(clip.Text);
            canvas.DrawText(clip.Text,
                (float)(width / 2.0 - textWidth / 2),
                (float)(height / 2.0 + paint.TextSize * 0.35f),
                paint);
            return;
        }

        if (effectiveScale <= 0)
            return; // 見えないので描かない。

        var fullWidth = paint.MeasureText(clip.Text);
        DrawTextSegments(canvas, paint, clip.Text, anim,
            ParseHighlightColor(clip.AnimationIn?.HighlightColorHex, 1.0),
            (float)(width / 2.0 - fullWidth / 2),
            (float)(height / 2.0 + paint.TextSize * 0.35f));
    }

    /// <summary>
    /// テキストを StringInfo のテキスト要素単位に分割する（サロゲートペア・結合文字を
    /// 1文字として数える。評価側の TextAnimation.Evaluate と同じ数え方）。
    /// </summary>
    private static List<string> SplitTextElements(string text)
    {
        var elements = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
            elements.Add(enumerator.GetTextElement());
        return elements;
    }

    /// <summary>
    /// 分割描画の共通ヘルパー（直接パスとオフスクリーンパスの両方で使う）。
    /// 表示する先頭 VisibleCharCount 要素だけを描く（タイプライター。-1 = 全文字）。
    /// カラオケは先頭 HighlightedCharCount 要素をハイライト色で、残りを基本色（paint.Color）で描く。
    /// 表示部分はフルテキストの左端（leftX）に揃えて描く：中央揃えの基準はフルテキストの幅で
    /// 取るため、文字が増えても表示位置がブレない。ハイライト無し・全文字表示の場合は
    /// フルテキストを1回で描く（従来の描画と等しい）。
    /// 空文字・VisibleCharCount=0・charCount を超える指定では何も描かないか全文字を描き、クラッシュしない。
    /// </summary>
    private static void DrawTextSegments(
        SKCanvas canvas, SKPaint paint, string text, TextAnimationState anim,
        SKColor highlightColor, float leftX, float baselineY)
    {
        var elements = SplitTextElements(text);
        var total = elements.Count;
        if (total == 0)
            return;

        var visible = anim.VisibleCharCount < 0
            ? total
            : Math.Clamp(anim.VisibleCharCount, 0, total);
        if (visible == 0)
            return;

        var highlight = Math.Clamp(anim.HighlightedCharCount, 0, visible);

        if (highlight == 0 && visible == total)
        {
            // 分割不要：フルテキストを1回で描く。
            canvas.DrawText(text, leftX, baselineY, paint);
            return;
        }

        var x = leftX;
        if (highlight > 0)
        {
            var segment = string.Concat(elements.Take(highlight));
            var baseColor = paint.Color;
            paint.Color = highlightColor;
            canvas.DrawText(segment, x, baselineY, paint);
            paint.Color = baseColor;
            x += paint.MeasureText(segment);
        }
        if (visible > highlight)
        {
            var segment = string.Concat(elements.Skip(highlight).Take(visible - highlight));
            canvas.DrawText(segment, x, baselineY, paint);
        }
    }

    /// <summary>
    /// カラオケのハイライト色をパースする。ParseColor の流儀に倣い、アルファは
    /// 不透明度で上書きする。パース失敗時（不正な値・null・空文字）は黄色 #FFFF00。
    /// </summary>
    private static SKColor ParseHighlightColor(string? hex, double opacity)
    {
        var alpha = (byte)Math.Clamp(opacity * 255, 0, 255);
        if (!string.IsNullOrEmpty(hex) && SKColor.TryParse(hex, out var color))
            return color.WithAlpha(alpha);
        return new SKColor(255, 255, 0, alpha);
    }
}
