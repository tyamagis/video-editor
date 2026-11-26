// MainViewModel の分割ファイル（partial）。キーフレーム編集とエフェクト編集（行の再構築・追加・削除・パラメータ定義）。

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
    /// <summary>キーフレーム編集で選んでいるパラメータ（"Opacity" など）。一覧を作り直す際に ComboBox から null が書き戻されることがあるので、空値は無視する。</summary>
    public string KeyframeParameter
    {
        get => _keyframeParameter;
        set
        {
            if (string.IsNullOrEmpty(value) || _keyframeParameter == value)
                return;
            _keyframeParameter = value;
            OnPropertyChanged();
            RefreshKeyframeRows();
        }
    }

    /// <summary>キーフレーム編集で選べるパラメータ（基本5つ＋選択中クリップのエフェクトの Amount / Radius / 第3パラメータ）。</summary>
    public ObservableCollection<KeyframeParameterOption> KeyframeParameterOptions { get; } = new();

    /// <summary>選択中クリップのキーフレーム行。</summary>
    public ObservableCollection<KeyframeRow> KeyframeRows { get; } = new();

    /// <summary>選択中クリップのエフェクト行。</summary>
    public ObservableCollection<EffectRow> EffectRows { get; } = new();

    /// <summary>「エフェクトを追加」で選んでいる種類（既定はブラー）。</summary>
    public EffectKind NewEffectKind
    {
        get => _newEffectKind;
        set
        {
            if (_newEffectKind == value)
                return;
            _newEffectKind = value;
            OnPropertyChanged();
        }
    }

    /// <summary>再生ヘッド位置にキーフレームを追加できるか（選択クリップが再生ヘッド位置で有効なとき）。</summary>
    public bool CanAddKeyframeAtPlayhead => SelectedClip is not null && SelectedClip.IsActiveAt(PlayheadSeconds);

    /// <summary>選択中クリップ・選択中パラメータのキーフレーム行を作り直す（時刻順）。ついでに選べるパラメータ一覧も作り直す。</summary>
    public void RefreshKeyframeRows()
    {
        RefreshKeyframeParameterOptions();
        KeyframeRows.Clear();
        var clip = SelectedClip;
        if (clip is null)
            return;
        var track = clip.KeyframeTracks.FirstOrDefault(t => t.ParameterName == KeyframeParameter);
        if (track is null)
            return;
        foreach (var keyframe in track.Keyframes)
            KeyframeRows.Add(new KeyframeRow(this, clip, KeyframeParameter, keyframe));
    }

    /// <summary>
    /// キーフレーム編集で選べるパラメータ一覧を作り直す。基本5つに、選択中クリップに
    /// 付いているエフェクトの Amount / Radius / 第3パラメータ（使う種類だけ）と、
    /// 可視化クリップのゲイン・線の太さ・表示区間・残像（種類ごと）を足す。
    /// 今選んでいるパラメータが一覧に無くなったら（エフェクトを消した等）、不透明度に戻す。
    /// </summary>
    private void RefreshKeyframeParameterOptions()
    {
        KeyframeParameterOptions.Clear();
        KeyframeParameterOptions.Add(new KeyframeParameterOption { Display = "不透明度", Value = "Opacity" });
        KeyframeParameterOptions.Add(new KeyframeParameterOption { Display = "拡大率", Value = "Scale" });
        KeyframeParameterOptions.Add(new KeyframeParameterOption { Display = "回転", Value = "Rotation" });
        KeyframeParameterOptions.Add(new KeyframeParameterOption { Display = "位置 X", Value = "PositionX" });
        KeyframeParameterOptions.Add(new KeyframeParameterOption { Display = "位置 Y", Value = "PositionY" });

        var clip = SelectedClip;
        if (clip is not null)
        {
            foreach (var option in EffectParameterOptions())
                KeyframeParameterOptions.Add(option);

            // 可視化クリップ：ゲインは全種、線の太さ・表示区間・残像は種類ごと。
            var viz = clip.Visualization;
            if (viz is not null)
            {
                KeyframeParameterOptions.Add(new KeyframeParameterOption
                {
                    Display = "可視化：ゲイン",
                    Value = VisualizationSettings.KeyframeParameterName("Gain")
                });
                if (viz.Kind is VisualizationKind.Waveform or VisualizationKind.Lissajous)
                {
                    KeyframeParameterOptions.Add(new KeyframeParameterOption
                    {
                        Display = "可視化：線の太さ",
                        Value = VisualizationSettings.KeyframeParameterName("LineWidth")
                    });
                }
                if (viz.Kind == VisualizationKind.Waveform)
                {
                    KeyframeParameterOptions.Add(new KeyframeParameterOption
                    {
                        Display = "可視化：表示区間",
                        Value = VisualizationSettings.KeyframeParameterName("WindowSeconds")
                    });
                }
                if (viz.Kind == VisualizationKind.Lissajous)
                {
                    KeyframeParameterOptions.Add(new KeyframeParameterOption
                    {
                        Display = "可視化：残像",
                        Value = VisualizationSettings.KeyframeParameterName("TrailSeconds")
                    });
                }
            }
        }

        if (KeyframeParameterOptions.All(o => o.Value != _keyframeParameter))
        {
            _keyframeParameter = "Opacity";
            OnPropertyChanged(nameof(KeyframeParameter));
        }
    }

    /// <summary>
    /// キーフレームの値をパラメータの範囲に丸める。不透明度は 0〜1、
    /// エフェクト・可視化のパラメータは種類ごとの範囲、その他はそのまま。
    /// </summary>
    public static double ClampKeyframeValue(string parameterName, double value)
    {
        if (parameterName == "Opacity")
            return Math.Clamp(value, 0, 1);
        if (VisualizationSettings.TryParseKeyframeParameter(parameterName, out var vizField))
            return VisualizationSettings.ClampKeyframeValue(vizField, value);
        if (TryParseEffectParameter(parameterName, out var kind, out var field))
            return field switch
            {
                "Amount" => ClipEffect.ClampAmount(kind, value),
                "Radius" => ClipEffect.ClampRadius(kind, value),
                "Midtones" => ClipEffect.ClampMidtones(kind, value),
                _ => value
            };
        return value;
    }

    /// <summary>キーフレームのパラメータ名 "Effect:{種類}.Amount/Radius/Midtones" を分解する。エフェクト用でなければ false。field は "Amount" / "Radius" / "Midtones"（中間調はトーンカーブのみ）。</summary>
    public static bool TryParseEffectParameter(string parameterName, out EffectKind kind, out string field)
    {
        kind = default;
        field = string.Empty;
        const string prefix = "Effect:";
        if (!parameterName.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var rest = parameterName[prefix.Length..];
        var dot = rest.LastIndexOf('.');
        if (dot <= 0)
            return false;
        if (!Enum.TryParse<EffectKind>(rest[..dot], out kind))
            return false;
        field = rest[(dot + 1)..];
        return field is "Amount" or "Radius" or "Midtones";
    }

    /// <summary>
    /// 再生ヘッド位置（クリップ相対時刻）にキーフレームを追加する。
    /// 値はその時刻の評価値（キーフレームが無ければ描画の既定値）なので、
    /// 追加してもアニメーションの形は変わらない。同じ時刻が既にあれば何もしない。
    /// </summary>
    public void AddKeyframeAtPlayhead()
    {
        var clip = SelectedClip;
        if (clip is null || !clip.IsActiveAt(PlayheadSeconds))
            return;
        var local = PlayheadSeconds - clip.StartSeconds;
        var existing = clip.KeyframeTracks.FirstOrDefault(t => t.ParameterName == KeyframeParameter);
        if (existing?.Keyframes.Any(k => Math.Abs(k.TimeSeconds - local) < 1e-6) == true)
            return; // 同じ時刻のキーフレームが既にある。

        BeginEdit();
        var defaultValue = KeyframeParameter switch
        {
            "Opacity" => 1.0,
            "Scale" => 1.0,
            _ => 0.0
        };
        // エフェクトのパラメータは、その時刻の評価値（キーフレームが無ければ固定値）を使う。
        if (TryParseEffectParameter(KeyframeParameter, out var effectKind, out var field))
        {
            var effect = clip.Effects.FirstOrDefault(e => e.Kind == effectKind);
            if (effect is not null)
                defaultValue = field switch
                {
                    "Amount" => effect.Amount,
                    "Radius" => effect.Radius,
                    "Midtones" => effect.Midtones,
                    _ => defaultValue
                };
        }
        // 可視化のパラメータも、その時刻の評価値（キーフレームが無ければ固定値）を使う。
        if (VisualizationSettings.TryParseKeyframeParameter(KeyframeParameter, out var vizField))
        {
            var viz = clip.Visualization;
            if (viz is not null)
            {
                defaultValue = vizField switch
                {
                    "Gain" => viz.Gain,
                    "LineWidth" => viz.LineWidth,
                    "WindowSeconds" => viz.WindowSeconds,
                    "TrailSeconds" => viz.TrailSeconds,
                    _ => 0.0
                };
            }
        }
        var value = clip.EvaluateParameter(KeyframeParameter, local, defaultValue);
        var track = clip.EnsureTrack(KeyframeParameter);
        track.AddKeyframe(new Keyframe(local, value));
        RefreshKeyframeRows();
        NotifyTimelineChanged();
        StatusMessage = $"キーフレームを追加しました：{KeyframeParameter} {local:F2}s = {value:F2}";
    }

    /// <summary>キーフレームを削除する。</summary>
    public void DeleteKeyframe(KeyframeRow row)
    {
        var clip = SelectedClip;
        if (clip is null)
            return;
        var track = clip.KeyframeTracks.FirstOrDefault(t => t.ParameterName == KeyframeParameter);
        if (track is null || !track.Keyframes.Contains(row.Keyframe))
            return;
        BeginEdit();
        track.RemoveKeyframe(row.Keyframe);
        RefreshKeyframeRows();
        NotifyTimelineChanged();
    }

    /// <summary>選択中クリップのエフェクト行を作り直す（追加した順）。</summary>
    public void RefreshEffectRows()
    {
        EffectRows.Clear();
        var clip = SelectedClip;
        if (clip is null)
            return;
        foreach (var effect in clip.Effects)
            EffectRows.Add(new EffectRow(this, effect));
    }

    /// <summary>
    /// 選択中クリップにエフェクトを追加する。同じ種類が既にあれば何もしない
    /// （同じ種類の重複は描画側で1つしか使わないため）。
    /// </summary>
    public void AddEffect(EffectKind kind)
    {
        var clip = SelectedClip;
        if (clip is null || clip.Kind == ClipKind.Audio || clip.Kind == ClipKind.Visualization)
            return;
        // キー系（背景抜き）はテキストの透明背景には意味が無く、文字色を消すだけなので付けない。
        if (clip.Kind == ClipKind.Text && !EffectDefinitions.AppliesToText(kind))
        {
            StatusMessage = $"「{EffectDisplayName(kind)}」はテキストクリップには使えません（背景を抜くエフェクトのため）。";
            return;
        }
        if (clip.Effects.Any(e => e.Kind == kind))
        {
            StatusMessage = "そのエフェクトは既に付いています。";
            return;
        }

        BeginEdit();
        clip.Effects.Add(CreateEffect(kind));
        RefreshEffectRows();
        RefreshKeyframeRows(); // エフェクトが増えるとキーフレームで選べるパラメータも増える。
        RefreshAudioBindingRows(); // バインドの対象に選べるエフェクト量も増える。
        NotifyTimelineChanged();
        StatusMessage = $"エフェクトを追加しました：{EffectDisplayName(kind)}";
    }

    /// <summary>エフェクトを削除する。</summary>
    public void DeleteEffect(EffectRow row)
    {
        var clip = SelectedClip;
        if (clip is null || !clip.Effects.Contains(row.Effect))
            return;
        BeginEdit();
        clip.Effects.Remove(row.Effect);
        RefreshEffectRows();
        RefreshKeyframeRows(); // エフェクトが減るとキーフレームで選べるパラメータも減る。
        RefreshAudioBindingRows(); // バインドの対象に選べるエフェクト量も減る（対象が無くなった既存バインドは残り、評価では無視される）。
        NotifyTimelineChanged();
    }

    /// <summary>種類ごとの既定パラメータでエフェクトを作る。既定値は EffectDefinitions の1テーブルが持つ（KeyColorHex は ClipEffect の既定値 "#00FF00" のまま）。</summary>
    private static ClipEffect CreateEffect(EffectKind kind)
    {
        var definition = EffectDefinitions.Get(kind);
        return new ClipEffect
        {
            Kind = kind,
            Amount = definition?.DefaultAmount ?? 0,
            Radius = definition?.DefaultRadius ?? 0,
            Midtones = definition?.DefaultThird ?? 0
        };
    }

    /// <summary>エフェクト種類の表示名。表示名は EffectDefinitions の1テーブルが持つ。</summary>
    public static string EffectDisplayName(EffectKind kind)
        => EffectDefinitions.Get(kind)?.DisplayName ?? kind.ToString();

    /// <summary>エフェクトの主パラメータ欄の見出し（種類ごと）。見出しは EffectDefinitions の1テーブルが持つ。</summary>
    public static string EffectAmountLabel(EffectKind kind)
        => EffectDefinitions.Get(kind)?.AmountLabel ?? "量";

    /// <summary>エフェクト量のパラメータ名 "Effect:{種類}.Amount/Radius/Midtones" の表示名（"エフェクト：グロー（強さ）" など）。エフェクト用でなければ null。キーフレーム欄とバインド欄で共通。</summary>
    public static string? EffectParameterLabel(string parameterName)
    {
        if (!TryParseEffectParameter(parameterName, out var kind, out var field))
            return null;
        var label = field switch
        {
            "Amount" => EffectAmountLabel(kind),
            "Radius" => EffectRadiusLabel(kind),
            "Midtones" => EffectThirdLabel(kind),
            _ => string.Empty
        };
        return $"エフェクト：{EffectDisplayName(kind)}（{label}）";
    }

    /// <summary>選択中クリップに付いているエフェクトの Amount / Radius / 第3パラメータ（トーンカーブの中間調）のパラメータ選択肢（Radius・第3パラメータは使う種類だけ）。キーフレーム欄とバインド欄で共通。</summary>
    private IEnumerable<KeyframeParameterOption> EffectParameterOptions()
    {
        var clip = SelectedClip;
        if (clip is null)
            yield break;
        foreach (var effect in clip.Effects)
        {
            // テキストに意味のないキー系はキーフレームの選択肢にも出さない
            // （手書きプロジェクトに入っていても描画では無視される）。
            if (clip.Kind == ClipKind.Text && !EffectDefinitions.AppliesToText(effect.Kind))
                continue;
            var amountName = ClipEffect.KeyframeParameterName(effect.Kind, isAmount: true);
            yield return new KeyframeParameterOption
            {
                Display = EffectParameterLabel(amountName)!,
                Value = amountName
            };
            if (ClipEffect.UsesRadius(effect.Kind))
            {
                var radiusName = ClipEffect.KeyframeParameterName(effect.Kind, isAmount: false);
                yield return new KeyframeParameterOption
                {
                    Display = EffectParameterLabel(radiusName)!,
                    Value = radiusName
                };
            }
            if (ClipEffect.UsesThird(effect.Kind))
            {
                var thirdName = ClipEffect.KeyframeParameterName(effect.Kind, "Midtones");
                yield return new KeyframeParameterOption
                {
                    Display = EffectParameterLabel(thirdName)!,
                    Value = thirdName
                };
            }
        }
    }

    /// <summary>エフェクトの副パラメータ欄の見出し（種類ごと。Radius を使わない種類は空）。見出しは EffectDefinitions の1テーブルが持つ。</summary>
    public static string EffectRadiusLabel(EffectKind kind)
        => EffectDefinitions.Get(kind)?.RadiusLabel ?? string.Empty;

    /// <summary>エフェクトの第3パラメータ欄の見出し（種類ごと。第3パラメータを使わない種類は空。今はトーンカーブの中間調だけ）。見出しは EffectDefinitions の1テーブルが持つ。</summary>
    public static string EffectThirdLabel(EffectKind kind)
        => EffectDefinitions.Get(kind)?.ThirdLabel ?? string.Empty;
}
