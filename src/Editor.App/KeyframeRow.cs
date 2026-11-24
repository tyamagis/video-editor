// キーフレーム1行の表示・編集用ラッパー。MainViewModel.cs から分離（型・名前空間・公開メンバーは不変）。

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

/// <summary>キーフレーム1行の表示・編集用ラッパー。モデルの Keyframe を直接書き換える。</summary>
public sealed class KeyframeRow : INotifyPropertyChanged
{
    private readonly MainViewModel _owner;
    private readonly Clip _clip;
    private readonly string _parameter;

    public KeyframeRow(MainViewModel owner, Clip clip, string parameter, Keyframe keyframe)
    {
        _owner = owner;
        _clip = clip;
        _parameter = parameter;
        Keyframe = keyframe;
    }

    public Keyframe Keyframe { get; }

    /// <summary>クリップ先頭からの相対秒。0〜クリップの長さに丸める。</summary>
    public double TimeSeconds
    {
        get => Keyframe.TimeSeconds;
        set
        {
            var clamped = Math.Clamp(value, 0, Math.Max(0, _clip.DurationSeconds));
            if (Math.Abs(Keyframe.TimeSeconds - clamped) < 1e-9)
                return;
            Keyframe.TimeSeconds = clamped;
            OnPropertyChanged();
            // 時刻の書き換えで順序が崩れることがあるため、トラックを並べ替え直す
            // （KeyframeTrack は時刻順を不変条件とし、Evaluate はそれを前提とする）。
            _clip.KeyframeTracks
                .FirstOrDefault(t => t.ParameterName == _parameter)
                ?.SortKeyframes();
            _owner.NotifyTimelineChanged();
        }
    }

    /// <summary>値。不透明度は 0〜1、エフェクトのパラメータは種類ごとの範囲に丸める（描画側でも丸めている）。</summary>
    public double Value
    {
        get => Keyframe.Value;
        set
        {
            var clamped = MainViewModel.ClampKeyframeValue(_parameter, value);
            if (Math.Abs(Keyframe.Value - clamped) < 1e-9)
                return;
            Keyframe.Value = clamped;
            OnPropertyChanged();
            _owner.NotifyTimelineChanged();
        }
    }

    /// <summary>
    /// 補間方法。離散的な変更なので、代入前にアンドゥ用スナップショットを取る。
    /// （代入の前なら Keyframe はまだ旧値なので、BeginEdit のスナップショットは正しい。）
    /// </summary>
    public Interpolation Interpolation
    {
        get => Keyframe.Interpolation;
        set
        {
            if (Keyframe.Interpolation == value)
                return;
            _owner.BeginEdit();
            Keyframe.Interpolation = value;
            OnPropertyChanged();
            _owner.NotifyTimelineChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
