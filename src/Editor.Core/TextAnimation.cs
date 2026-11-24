// テキストクリップのアニメーション（設計プラン 4章）。
// Editor.Core は UI・FFmpeg・NAudio・SkiaSharp に依存しない純粋なドメインモデル。
// ここはモデルと評価ロジックだけを持ち、描画はレンダラー側（SkiaSharp）が
// TextAnimationState を読んで行う。
// 文字数の数え方は StringInfo のテキスト要素数（LengthInTextElements）で統一する
// （レンダラー側も同じ数え方をすること。サロゲートペア・結合文字を1文字として数えるため）。

using System.Globalization;

namespace Editor.Core;

/// <summary>テキストアニメーションの種類。イン・アウトそれぞれに1つ指定する。</summary>
public enum TextAnimationKind
{
    None,
    Fade,
    Slide,
    Typewriter,
    Karaoke,
    Scale
}

/// <summary>スライドの「来る方向」。Left = 左側から現れて右へ移動する（最終位置の左側から始まる）。</summary>
public enum TextAnimationDirection
{
    Left,
    Right,
    Up,
    Down
}

/// <summary>テキストアニメーションのイージング。Fade / Slide / Scale の進行度にだけ適用する（タイプライター・カラオケはイージング無視）。</summary>
public enum TextAnimationEasing
{
    Linear,
    EaseIn,
    EaseOut,
    EaseInOut
}

/// <summary>テキストクリップのイン／アウトアニメーション1つ分の設定。</summary>
public sealed class TextAnimation
{
    /// <summary>アニメーション時間の下限（秒）。これより短い Duration は評価時にここへ丸める。</summary>
    public const double MinDurationSeconds = 0.05;

    /// <summary>アニメーション時間の上限（秒）。これより長い Duration は評価時にここへ丸める。</summary>
    public const double MaxDurationSeconds = 60;

    public TextAnimationKind Kind { get; set; }

    /// <summary>スライドの「来る方向」。既定は左側から現れる。</summary>
    public TextAnimationDirection Direction { get; set; } = TextAnimationDirection.Left;

    /// <summary>アニメーションの長さ（秒）。既定 0.5。評価時に 0.05〜60 秒へ丸める。</summary>
    public double DurationSeconds { get; set; } = 0.5;

    public TextAnimationEasing Easing { get; set; } = TextAnimationEasing.EaseOut;

    /// <summary>Slide / Scale と組み合わせるフェード（true なら動きと同時にフェードする）。Fade 種別自体はこの値に関係なく不透明度を動かす。</summary>
    public bool Fade { get; set; } = true;

    /// <summary>カラオケ用ハイライト色（"#FFFF00" など）。</summary>
    public string HighlightColorHex { get; set; } = "#FFFF00";

    public TextAnimation Clone() => (TextAnimation)MemberwiseClone();

    /// <summary>Duration を 0.05〜60 秒の範囲に丸める（NaN は既定の 0.5 秒にする）。</summary>
    public static double ClampDuration(double value)
        => double.IsNaN(value) ? 0.5 : Math.Clamp(value, MinDurationSeconds, MaxDurationSeconds);

    /// <summary>イージングを適用する。t は 0〜1 にクランプして返す。EaseIn は t*t、EaseOut は 1-(1-t)*(1-t)、EaseInOut は smoothstep t*t*(3-2t)。</summary>
    public static double Apply(TextAnimationEasing easing, double t)
    {
        t = double.IsNaN(t) ? 0 : Math.Clamp(t, 0, 1);
        return easing switch
        {
            TextAnimationEasing.EaseIn => t * t,
            TextAnimationEasing.EaseOut => 1 - (1 - t) * (1 - t),
            TextAnimationEasing.EaseInOut => t * t * (3 - 2 * t),
            _ => t
        };
    }

    /// <summary>
    /// イン・アウトのアニメーションを合成して評価する。
    /// イン：Kind != None かつ local &lt; Duration の間だけ適用。進行度 p = clamp(local / Duration, 0, 1)。
    /// Duration を過ぎれば恒等（カラオケだけは全文字ハイライトのまま）。
    /// アウト：残り時間 remaining = clipDuration - local が Duration 未満のとき適用。p = clamp(1 - remaining / Duration, 0, 1)。
    /// イージングは Fade / Slide / Scale の進行度にだけ適用する。タイプライターはステップ表示（イージング無視）、カラオケは線形スイープ（イージング無視）。
    /// インとアウトが重なる場合は両方を合成する：不透明度は掛け算、オフセットは加算、Scale は掛け算、VisibleCharCount は小さい方（-1 は全文字として扱う）、HighlightedCharCount は大きい方。
    /// local が負・clipDuration が 0 以下・charCount が負などの異常値は安全側に丸める（クラッシュしない）。
    /// </summary>
    public static TextAnimationState Evaluate(
        TextAnimation? inAnimation, TextAnimation? outAnimation,
        double localTimeSeconds, double clipDurationSeconds, int charCount)
    {
        var local = double.IsNaN(localTimeSeconds) ? 0 : Math.Max(0, localTimeSeconds);
        var clipDuration = double.IsNaN(clipDurationSeconds) ? 0 : Math.Max(0, clipDurationSeconds);
        var chars = Math.Max(0, charCount);

        var result = TextAnimationState.Identity;
        result = Combine(result, EvaluateSingle(inAnimation, local, clipDuration, chars, isOut: false), chars);
        result = Combine(result, EvaluateSingle(outAnimation, local, clipDuration, chars, isOut: true), chars);
        return result;
    }

    /// <summary>イン／アウト片側のアニメーションを評価する。適用範囲外・Kind=None・カラオケのアウト指定は恒等を返す。</summary>
    private static TextAnimationState EvaluateSingle(
        TextAnimation? animation, double local, double clipDuration, int chars, bool isOut)
    {
        if (animation is null || animation.Kind == TextAnimationKind.None)
            return TextAnimationState.Identity;

        var duration = ClampDuration(animation.DurationSeconds);

        if (isOut)
        {
            // カラオケのアウト指定は無視する（何もしない）。
            if (animation.Kind == TextAnimationKind.Karaoke)
                return TextAnimationState.Identity;

            var remaining = clipDuration - local;
            if (remaining >= duration)
                return TextAnimationState.Identity;
            var p = Math.Clamp(1 - remaining / duration, 0, 1);
            return EvaluateAt(animation, p, chars, isOut: true);
        }

        // イン側。
        if (local >= duration)
        {
            // Duration を過ぎれば恒等。カラオケだけは全文字ハイライトのまま。
            return animation.Kind == TextAnimationKind.Karaoke
                ? TextAnimationState.Identity with { HighlightedCharCount = chars }
                : TextAnimationState.Identity;
        }
        var progress = Math.Clamp(local / duration, 0, 1);
        return EvaluateAt(animation, progress, chars, isOut: false);
    }

    /// <summary>進行度 p（0〜1）における片側のアニメーション状態。イージングは Fade / Slide / Scale にだけ適用する。</summary>
    private static TextAnimationState EvaluateAt(TextAnimation animation, double p, int chars, bool isOut)
    {
        switch (animation.Kind)
        {
            case TextAnimationKind.Fade:
            {
                var eased = Apply(animation.Easing, p);
                return TextAnimationState.Identity with { Opacity = isOut ? 1 - eased : eased };
            }
            case TextAnimationKind.Slide:
            {
                var eased = Apply(animation.Easing, p);
                // ずれ量はフレーム高さの 0.25 倍。インは「来る方向」側に (1-p)*0.25 だけずれ、
                // 最終位置へ。アウトは最終位置からその方向へ p*0.25 だけずれる。
                var magnitude = (isOut ? eased : 1 - eased) * 0.25;
                var (offsetX, offsetY) = SlideOffset(animation.Direction, magnitude);
                return TextAnimationState.Identity with
                {
                    Opacity = animation.Fade ? (isOut ? 1 - eased : eased) : 1,
                    OffsetXFraction = offsetX,
                    OffsetYFraction = offsetY
                };
            }
            case TextAnimationKind.Scale:
            {
                var eased = Apply(animation.Easing, p);
                // インは 0.5→1、アウトは 1→0.5。
                var scale = isOut ? 1 - 0.5 * eased : 0.5 + 0.5 * eased;
                return TextAnimationState.Identity with
                {
                    Opacity = animation.Fade ? (isOut ? 1 - eased : eased) : 1,
                    Scale = scale
                };
            }
            case TextAnimationKind.Typewriter:
            {
                // ステップ表示（イージング無視）。インは先頭から出る、アウトは末尾から消える。
                var visible = isOut
                    ? (int)Math.Ceiling((1 - p) * chars)
                    : (int)Math.Floor(p * chars);
                return TextAnimationState.Identity with { VisibleCharCount = Math.Clamp(visible, 0, chars) };
            }
            case TextAnimationKind.Karaoke:
            {
                // 線形スイープ（イージング無視）。アウト側は EvaluateSingle で既に弾いている。
                var highlighted = (int)Math.Floor(p * chars);
                return TextAnimationState.Identity with { HighlightedCharCount = Math.Clamp(highlighted, 0, chars) };
            }
            default:
                return TextAnimationState.Identity;
        }
    }

    /// <summary>スライド方向とずれ量（フレーム高さに対する割合）から X / Y オフセットを返す。Left = 負の X、Up = 負の Y。</summary>
    private static (double OffsetX, double OffsetY) SlideOffset(TextAnimationDirection direction, double magnitude)
        => direction switch
        {
            TextAnimationDirection.Left => (-magnitude, 0),
            TextAnimationDirection.Right => (magnitude, 0),
            TextAnimationDirection.Up => (0, -magnitude),
            TextAnimationDirection.Down => (0, magnitude),
            _ => (0, 0)
        };

    /// <summary>
    /// インとアウトの合成：不透明度は掛け算、オフセットは加算、Scale は掛け算、
    /// VisibleCharCount は小さい方（-1 は全文字として扱う）、HighlightedCharCount は大きい方。
    /// </summary>
    private static TextAnimationState Combine(TextAnimationState a, TextAnimationState b, int chars)
    {
        var visibleA = a.VisibleCharCount < 0 ? chars : a.VisibleCharCount;
        var visibleB = b.VisibleCharCount < 0 ? chars : b.VisibleCharCount;
        var visible = Math.Min(visibleA, visibleB);
        return new TextAnimationState
        {
            Opacity = a.Opacity * b.Opacity,
            OffsetXFraction = a.OffsetXFraction + b.OffsetXFraction,
            OffsetYFraction = a.OffsetYFraction + b.OffsetYFraction,
            Scale = a.Scale * b.Scale,
            VisibleCharCount = visible >= chars ? -1 : visible,
            HighlightedCharCount = Math.Max(a.HighlightedCharCount, b.HighlightedCharCount)
        };
    }
}

/// <summary>
/// ある時刻におけるテキストアニメーションの確定状態。
/// OffsetXFraction / OffsetYFraction はフレーム高さに対する割合（0.25 = フレーム高さの 1/4）。
/// VisibleCharCount = -1 は全文字表示。文字数の数え方は StringInfo のテキスト要素数で統一する。
/// </summary>
public readonly struct TextAnimationState
{
    public double Opacity { get; init; }
    public double OffsetXFraction { get; init; }
    public double OffsetYFraction { get; init; }
    public double Scale { get; init; }
    public int VisibleCharCount { get; init; }
    public int HighlightedCharCount { get; init; }

    /// <summary>恒等状態：Opacity=1、オフセット 0、Scale=1、VisibleCharCount=-1（全文字表示）、HighlightedCharCount=0。</summary>
    public static TextAnimationState Identity { get; } = new()
    {
        Opacity = 1,
        OffsetXFraction = 0,
        OffsetYFraction = 0,
        Scale = 1,
        VisibleCharCount = -1,
        HighlightedCharCount = 0
    };

    /// <summary>恒等状態と等しいかどうか。</summary>
    public bool IsIdentity
        => Opacity == 1
            && OffsetXFraction == 0
            && OffsetYFraction == 0
            && Scale == 1
            && VisibleCharCount == -1
            && HighlightedCharCount == 0;
}
