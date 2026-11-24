// エフェクト1行の表示・編集用ラッパー。MainViewModel.cs から分離（型・名前空間・公開メンバーは不変）。

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

/// <summary>エフェクト1つの表示・編集用ラッパー。モデルの ClipEffect を直接書き換える。</summary>
public sealed class EffectRow : INotifyPropertyChanged
{
    private readonly MainViewModel _owner;

    public EffectRow(MainViewModel owner, ClipEffect effect)
    {
        _owner = owner;
        Effect = effect;
    }

    public ClipEffect Effect { get; }

    /// <summary>
    /// 種類。離散的な変更なので、代入前にアンドゥ用スナップショットを取る。
    /// （代入の前なら Effect はまだ旧値なので、BeginEdit のスナップショットは正しい。）
    /// 種類が変わると表示するパラメータ欄も変わるため、関連プロパティも通知する。
    /// 種類を変えると旧種類のエフェクト用キーフレームは残るが、評価では使われなくなる
    /// （新しい種類のトラックを作り直す必要がある）。
    /// </summary>
    public EffectKind Kind
    {
        get => Effect.Kind;
        set
        {
            if (Effect.Kind == value)
                return;
            _owner.BeginEdit();
            Effect.Kind = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AmountLabel));
            OnPropertyChanged(nameof(RadiusLabel));
            OnPropertyChanged(nameof(ShowRadius));
            OnPropertyChanged(nameof(MidtonesLabel));
            OnPropertyChanged(nameof(ShowMidtones));
            OnPropertyChanged(nameof(ShowKeyColor));
            _owner.RefreshKeyframeRows();
            _owner.RefreshAudioBindingRows(); // 種類が変わるとバインドの対象に選べるエフェクト量も変わる。
            _owner.NotifyTimelineChanged();
        }
    }

    /// <summary>主パラメータ。種類ごとに意味と範囲が違う（ClipEffect の説明を参照）。</summary>
    public double Amount
    {
        get => Effect.Amount;
        set
        {
            var clamped = ClipEffect.ClampAmount(Effect.Kind, value);
            if (Math.Abs(Effect.Amount - clamped) < 1e-9)
                return;
            Effect.Amount = clamped;
            OnPropertyChanged();
            _owner.NotifyTimelineChanged();
        }
    }

    /// <summary>副パラメータ（グロー：ぼかし半径、ワープ：波長、クロマキー：なめらかさ、他は ClipEffect の説明を参照）。</summary>
    public double Radius
    {
        get => Effect.Radius;
        set
        {
            var clamped = ClipEffect.ClampRadius(Effect.Kind, value);
            if (Math.Abs(Effect.Radius - clamped) < 1e-9)
                return;
            Effect.Radius = clamped;
            OnPropertyChanged();
            _owner.NotifyTimelineChanged();
        }
    }

    /// <summary>第3パラメータ。トーンカーブの中間調（-1〜1。+で中間を持ち上げ、-で沈める）。他の種類では使わない。</summary>
    public double Midtones
    {
        get => Effect.Midtones;
        set
        {
            var clamped = ClipEffect.ClampMidtones(Effect.Kind, value);
            if (Math.Abs(Effect.Midtones - clamped) < 1e-9)
                return;
            Effect.Midtones = clamped;
            OnPropertyChanged();
            _owner.NotifyTimelineChanged();
        }
    }

    /// <summary>クロマキーのキー色（"#00FF00" など）。離散的な変更なので代入前にスナップショットを取る。</summary>
    public string KeyColorHex
    {
        get => Effect.KeyColorHex;
        set
        {
            if (Effect.KeyColorHex == value)
                return;
            _owner.BeginEdit();
            Effect.KeyColorHex = value;
            OnPropertyChanged();
            _owner.NotifyTimelineChanged();
        }
    }

    /// <summary>主パラメータ欄の見出し（種類ごと）。</summary>
    public string AmountLabel => MainViewModel.EffectAmountLabel(Effect.Kind);

    /// <summary>副パラメータ欄の見出し（種類ごと）。</summary>
    public string RadiusLabel => MainViewModel.EffectRadiusLabel(Effect.Kind);

    /// <summary>副パラメータ欄を表示するか（Radius を使う種類だけ）。</summary>
    public bool ShowRadius => ClipEffect.UsesRadius(Effect.Kind);

    /// <summary>第3パラメータ欄の見出し（種類ごと。今はトーンカーブの中間調だけ）。</summary>
    public string MidtonesLabel => MainViewModel.EffectThirdLabel(Effect.Kind);

    /// <summary>第3パラメータ欄を表示するか（第3パラメータを使う種類だけ）。</summary>
    public bool ShowMidtones => ClipEffect.UsesThird(Effect.Kind);

    /// <summary>キー色欄を表示するか（クロマキーだけ使う）。</summary>
    public bool ShowKeyColor => Effect.Kind == EffectKind.ChromaKey;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
