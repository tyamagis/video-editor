// 音連動バインド1行の表示・編集用ラッパー。MainViewModel.cs から分離（型・名前空間・公開メンバーは不変）。

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


/// <summary>音連動バインド1つの表示・編集用ラッパー。モデルの AudioBinding を直接書き換える。</summary>
public sealed class AudioBindingRow : INotifyPropertyChanged
{
    private readonly MainViewModel _owner;

    public AudioBindingRow(MainViewModel owner, AudioBinding binding)
    {
        _owner = owner;
        Binding = binding;
    }

    public AudioBinding Binding { get; }

    /// <summary>動かすパラメータ。離散的な変更なので、代入前にアンドゥ用スナップショットを取る。一覧を作り直す際に ComboBox から null が書き戻されることがあるので、空値は無視する（キーフレーム欄と同じ対策）。</summary>
    public string ParameterName
    {
        get => Binding.ParameterName;
        set
        {
            if (string.IsNullOrEmpty(value) || Binding.ParameterName == value)
                return;
            _owner.BeginEdit();
            Binding.ParameterName = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ParameterLabel));
            OnPropertyChanged(nameof(ParameterOptions));
            _owner.NotifyTimelineChanged();
        }
    }

    /// <summary>パラメータの表示名。エフェクト量は "エフェクト：グロー（強さ）" のように表示する。</summary>
    public string ParameterLabel => ParameterName switch
    {
        "Opacity" => "不透明度",
        "Scale" => "拡大率",
        "Rotation" => "回転",
        "PositionX" => "位置 X",
        "PositionY" => "位置 Y",
        _ => MainViewModel.EffectParameterLabel(ParameterName) ?? ParameterName
    };

    /// <summary>
    /// この行のパラメータ選択肢。共有の一覧（基本5つ＋付いているエフェクトの量）に、
    /// 今の値が無ければその値も足す。エフェクトを消した・種類を変えた等で対象が無くなった
    /// 既存バインドでも、ComboBox が空白になったり値が消えたりしないようにするため。
    /// 対象が無い値は「（エフェクトなし）」付きで表示し、評価では無視される。
    /// </summary>
    public IReadOnlyList<KeyframeParameterOption> ParameterOptions
    {
        get
        {
            var options = _owner.AudioBindingParameterOptions.ToList();
            if (options.All(o => o.Value != Binding.ParameterName))
            {
                var display = MainViewModel.TryParseEffectParameter(Binding.ParameterName, out _, out _)
                    ? ParameterLabel + "（エフェクトなし）"
                    : ParameterLabel;
                options.Add(new KeyframeParameterOption { Display = display, Value = Binding.ParameterName });
            }
            return options;
        }
    }

    /// <summary>音源クリップの Id。離散的な変更なので、代入前にスナップショットを取る。変えたら解析を走らせる。</summary>
    public string SourceClipId
    {
        get => Binding.SourceClipId;
        set
        {
            if (Binding.SourceClipId == value)
                return;
            _owner.BeginEdit();
            Binding.SourceClipId = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SourceClipName));
            _owner.RequestBindingSourceAnalysis(Binding);
            _owner.NotifyTimelineChanged();
        }
    }

    /// <summary>音源クリップの名前（無効ならその旨）。</summary>
    public string SourceClipName => _owner.GetAudioClipName(Binding.SourceClipId);

    /// <summary>周波数帯の下限（Hz、1〜上限）。</summary>
    public double LowHz
    {
        get => Binding.LowHz;
        set
        {
            var clamped = Math.Clamp(value, 1, Math.Max(1, Binding.HighHz));
            if (Math.Abs(Binding.LowHz - clamped) < 1e-9)
                return;
            Binding.LowHz = clamped;
            OnPropertyChanged();
            _owner.NotifyTimelineChanged();
        }
    }

    /// <summary>周波数帯の上限（Hz、下限〜20000）。</summary>
    public double HighHz
    {
        get => Binding.HighHz;
        set
        {
            var clamped = Math.Clamp(value, Math.Max(1, Binding.LowHz), 20000);
            if (Math.Abs(Binding.HighHz - clamped) < 1e-9)
                return;
            Binding.HighHz = clamped;
            OnPropertyChanged();
            _owner.NotifyTimelineChanged();
        }
    }

    /// <summary>帯域エネルギー 0.0 のときの出力値。</summary>
    public double MinValue
    {
        get => Binding.MinValue;
        set
        {
            if (Math.Abs(Binding.MinValue - value) < 1e-9)
                return;
            Binding.MinValue = value;
            OnPropertyChanged();
            _owner.NotifyTimelineChanged();
        }
    }

    /// <summary>帯域エネルギー 1.0 のときの出力値。</summary>
    public double MaxValue
    {
        get => Binding.MaxValue;
        set
        {
            if (Math.Abs(Binding.MaxValue - value) < 1e-9)
                return;
            Binding.MaxValue = value;
            OnPropertyChanged();
            _owner.NotifyTimelineChanged();
        }
    }

    /// <summary>感度（0〜10）。</summary>
    public double Gain
    {
        get => Binding.Gain;
        set
        {
            var clamped = Math.Clamp(value, 0, 10);
            if (Math.Abs(Binding.Gain - clamped) < 1e-9)
                return;
            Binding.Gain = clamped;
            OnPropertyChanged();
            _owner.NotifyTimelineChanged();
        }
    }

    /// <summary>しきい値（0〜1）。これ以下の帯域エネルギーは 0 とみなす。</summary>
    public double Threshold
    {
        get => Binding.Threshold;
        set
        {
            var clamped = Math.Clamp(value, 0, 1);
            if (Math.Abs(Binding.Threshold - clamped) < 1e-9)
                return;
            Binding.Threshold = clamped;
            OnPropertyChanged();
            _owner.NotifyTimelineChanged();
        }
    }

    /// <summary>反転する（音が大きいほど出力が小さくなる）。離散的な変更なので代入前にスナップショットを取る。</summary>
    public bool Invert
    {
        get => Binding.Invert;
        set
        {
            if (Binding.Invert == value)
                return;
            _owner.BeginEdit();
            Binding.Invert = value;
            OnPropertyChanged();
            _owner.NotifyTimelineChanged();
        }
    }

    /// <summary>キーフレーム値との合成方法。離散的な変更なので代入前にスナップショットを取る。</summary>
    public AudioBindCombine Combine
    {
        get => Binding.Combine;
        set
        {
            if (Binding.Combine == value)
                return;
            _owner.BeginEdit();
            Binding.Combine = value;
            OnPropertyChanged();
            _owner.NotifyTimelineChanged();
        }
    }

    /// <summary>有効か。離散的な変更なので代入前にスナップショットを取る。</summary>
    public bool Enabled
    {
        get => Binding.Enabled;
        set
        {
            if (Binding.Enabled == value)
                return;
            _owner.BeginEdit();
            Binding.Enabled = value;
            OnPropertyChanged();
            _owner.NotifyTimelineChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
