// MainViewModel の分割ファイル（partial）。テキストクリップのアニメーション（イン／アウト）設定（インスペクタ）。

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using Editor.Audio;
using Editor.Core;
using Editor.Media;
using Editor.Render;
using SkiaSharp;

namespace Editor.App;

public sealed partial class MainViewModel
{
    // ---- テキストアニメーション（インスペクタ） ----

    /// <summary>選択中クリップがテキストクリップか（テキストアニメーション欄の表示に使う）。</summary>
    public bool IsTextClipSelected => SelectedClip?.Kind == ClipKind.Text;

    /// <summary>選択中テキストクリップの登場アニメーション。テキストクリップ以外が選択されているときは null。</summary>
    private TextAnimation? SelectedTextAnimationIn
        => IsTextClipSelected ? SelectedClip!.AnimationIn : null;

    /// <summary>選択中テキストクリップの退場アニメーション。テキストクリップ以外が選択されているときは null。</summary>
    private TextAnimation? SelectedTextAnimationOut
        => IsTextClipSelected ? SelectedClip!.AnimationOut : null;

    /// <summary>登場アニメーションの種類。離散的な変更なので、代入前にアンドゥ用スナップショットを取る。種類が変わると方向・フェード・ハイライト色欄の表示も変わる。</summary>
    public TextAnimationKind SelectedTextAnimationInKind
    {
        get => SelectedTextAnimationIn?.Kind ?? TextAnimationKind.None;
        set
        {
            var anim = SelectedTextAnimationIn;
            if (anim is null || anim.Kind == value)
                return;
            BeginEdit();
            anim.Kind = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowInDirection));
            OnPropertyChanged(nameof(ShowInFade));
            OnPropertyChanged(nameof(ShowInHighlightColor));
            NotifyTimelineChanged();
        }
    }

    /// <summary>登場アニメーションのスライド方向（スライドのときだけ使う）。離散的な変更なので、代入前にアンドゥ用スナップショットを取る。</summary>
    public TextAnimationDirection SelectedTextAnimationInDirection
    {
        get => SelectedTextAnimationIn?.Direction ?? TextAnimationDirection.Left;
        set
        {
            var anim = SelectedTextAnimationIn;
            if (anim is null || anim.Direction == value)
                return;
            BeginEdit();
            anim.Direction = value;
            OnPropertyChanged();
            NotifyTimelineChanged();
        }
    }

    /// <summary>登場アニメーションの継続時間（秒）。TextAnimation.ClampDuration で 0.05〜60 に丸める（NaN は 0.5）。</summary>
    public double SelectedTextAnimationInDurationSeconds
    {
        get => SelectedTextAnimationIn?.DurationSeconds ?? 0.5;
        set
        {
            var anim = SelectedTextAnimationIn;
            if (anim is null)
                return;
            var clamped = TextAnimation.ClampDuration(value);
            if (Math.Abs(anim.DurationSeconds - clamped) < 1e-9)
                return;
            BeginEdit();
            anim.DurationSeconds = clamped;
            OnPropertyChanged();
            NotifyTimelineChanged();
        }
    }

    /// <summary>登場アニメーションのイージング。離散的な変更なので、代入前にアンドゥ用スナップショットを取る。タイプライター・カラオケでは評価時に無視される。</summary>
    public TextAnimationEasing SelectedTextAnimationInEasing
    {
        get => SelectedTextAnimationIn?.Easing ?? TextAnimationEasing.EaseOut;
        set
        {
            var anim = SelectedTextAnimationIn;
            if (anim is null || anim.Easing == value)
                return;
            BeginEdit();
            anim.Easing = value;
            OnPropertyChanged();
            NotifyTimelineChanged();
        }
    }

    /// <summary>登場アニメーションで動きと同時にフェードするか（スライド・スケールのときだけ使う）。離散的な変更なので、代入前にアンドゥ用スナップショットを取る。</summary>
    public bool SelectedTextAnimationInFade
    {
        get => SelectedTextAnimationIn?.Fade ?? true;
        set
        {
            var anim = SelectedTextAnimationIn;
            if (anim is null || anim.Fade == value)
                return;
            BeginEdit();
            anim.Fade = value;
            OnPropertyChanged();
            NotifyTimelineChanged();
        }
    }

    /// <summary>登場アニメーションのカラオケ用ハイライト色（"#RRGGBB"。カラオケのときだけ使う）。離散的な変更なので、代入前にアンドゥ用スナップショットを取る。</summary>
    public string SelectedTextAnimationInHighlightColorHex
    {
        get => SelectedTextAnimationIn?.HighlightColorHex ?? "#FFFF00";
        set
        {
            var anim = SelectedTextAnimationIn;
            if (anim is null || anim.HighlightColorHex == value)
                return;
            BeginEdit();
            anim.HighlightColorHex = value;
            OnPropertyChanged();
            NotifyTimelineChanged();
        }
    }

    /// <summary>登場アニメーションで方向を選ぶ欄を表示するか（スライドのときだけ）。</summary>
    public bool ShowInDirection => SelectedTextAnimationIn?.Kind == TextAnimationKind.Slide;

    /// <summary>登場アニメーションで「動きと同時にフェードする」を表示するか（スライド・スケールのときだけ）。</summary>
    public bool ShowInFade => SelectedTextAnimationIn?.Kind is TextAnimationKind.Slide or TextAnimationKind.Scale;

    /// <summary>登場アニメーションでカラオケのハイライト色欄を表示するか（カラオケのときだけ）。</summary>
    public bool ShowInHighlightColor => SelectedTextAnimationIn?.Kind == TextAnimationKind.Karaoke;

    /// <summary>退場アニメーションの種類（タイプライター・カラオケは出さない。評価側でもカラオケのアウト指定は無視される）。離散的な変更なので、代入前にアンドゥ用スナップショットを取る。種類が変わると方向・フェード欄の表示も変わる。</summary>
    public TextAnimationKind SelectedTextAnimationOutKind
    {
        get => SelectedTextAnimationOut?.Kind ?? TextAnimationKind.None;
        set
        {
            var anim = SelectedTextAnimationOut;
            if (anim is null || anim.Kind == value)
                return;
            BeginEdit();
            anim.Kind = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowOutDirection));
            OnPropertyChanged(nameof(ShowOutFade));
            NotifyTimelineChanged();
        }
    }

    /// <summary>退場アニメーションのスライド方向（スライドのときだけ使う）。離散的な変更なので、代入前にアンドゥ用スナップショットを取る。</summary>
    public TextAnimationDirection SelectedTextAnimationOutDirection
    {
        get => SelectedTextAnimationOut?.Direction ?? TextAnimationDirection.Left;
        set
        {
            var anim = SelectedTextAnimationOut;
            if (anim is null || anim.Direction == value)
                return;
            BeginEdit();
            anim.Direction = value;
            OnPropertyChanged();
            NotifyTimelineChanged();
        }
    }

    /// <summary>退場アニメーションの継続時間（秒）。TextAnimation.ClampDuration で 0.05〜60 に丸める（NaN は 0.5）。</summary>
    public double SelectedTextAnimationOutDurationSeconds
    {
        get => SelectedTextAnimationOut?.DurationSeconds ?? 0.5;
        set
        {
            var anim = SelectedTextAnimationOut;
            if (anim is null)
                return;
            var clamped = TextAnimation.ClampDuration(value);
            if (Math.Abs(anim.DurationSeconds - clamped) < 1e-9)
                return;
            BeginEdit();
            anim.DurationSeconds = clamped;
            OnPropertyChanged();
            NotifyTimelineChanged();
        }
    }

    /// <summary>退場アニメーションのイージング。離散的な変更なので、代入前にアンドゥ用スナップショットを取る。</summary>
    public TextAnimationEasing SelectedTextAnimationOutEasing
    {
        get => SelectedTextAnimationOut?.Easing ?? TextAnimationEasing.EaseOut;
        set
        {
            var anim = SelectedTextAnimationOut;
            if (anim is null || anim.Easing == value)
                return;
            BeginEdit();
            anim.Easing = value;
            OnPropertyChanged();
            NotifyTimelineChanged();
        }
    }

    /// <summary>退場アニメーションで動きと同時にフェードするか（スライド・スケールのときだけ使う）。離散的な変更なので、代入前にアンドゥ用スナップショットを取る。</summary>
    public bool SelectedTextAnimationOutFade
    {
        get => SelectedTextAnimationOut?.Fade ?? true;
        set
        {
            var anim = SelectedTextAnimationOut;
            if (anim is null || anim.Fade == value)
                return;
            BeginEdit();
            anim.Fade = value;
            OnPropertyChanged();
            NotifyTimelineChanged();
        }
    }

    /// <summary>退場アニメーションで方向を選ぶ欄を表示するか（スライドのときだけ）。</summary>
    public bool ShowOutDirection => SelectedTextAnimationOut?.Kind == TextAnimationKind.Slide;

    /// <summary>退場アニメーションで「動きと同時にフェードする」を表示するか（スライド・スケールのときだけ）。</summary>
    public bool ShowOutFade => SelectedTextAnimationOut?.Kind is TextAnimationKind.Slide or TextAnimationKind.Scale;
}
