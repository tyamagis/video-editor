// MainViewModel の分割ファイル（partial）。音連動バインドの編集と、バインド／可視化に必要な音声解析の実行管理。

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
    public void RefreshAudioBindingRows()
    {
        RefreshAudioBindingParameterOptions();
        AudioBindingRows.Clear();
        var clip = SelectedClip;
        if (clip is null)
            return;
        foreach (var binding in clip.AudioBindings)
            AudioBindingRows.Add(new AudioBindingRow(this, binding));
    }

    /// <summary>バインドの対象に選べるパラメータ（基本5つ＋選択中クリップのエフェクトの Amount / Radius / 第3パラメータ）。</summary>
    public ObservableCollection<KeyframeParameterOption> AudioBindingParameterOptions { get; } = new();

    /// <summary>
    /// バインドの対象パラメータ一覧を作り直す。基本5つに、選択中クリップに
    /// 付いているエフェクトの Amount / Radius / 第3パラメータ（使う種類だけ）を足す。
    /// 今選んでいる追加対象が一覧に無くなったら（エフェクトを消した等）、拡大率に戻す。
    /// </summary>
    private void RefreshAudioBindingParameterOptions()
    {
        AudioBindingParameterOptions.Clear();
        AudioBindingParameterOptions.Add(new KeyframeParameterOption { Display = "不透明度", Value = "Opacity" });
        AudioBindingParameterOptions.Add(new KeyframeParameterOption { Display = "拡大率", Value = "Scale" });
        AudioBindingParameterOptions.Add(new KeyframeParameterOption { Display = "回転", Value = "Rotation" });
        AudioBindingParameterOptions.Add(new KeyframeParameterOption { Display = "位置 X", Value = "PositionX" });
        AudioBindingParameterOptions.Add(new KeyframeParameterOption { Display = "位置 Y", Value = "PositionY" });
        foreach (var option in EffectParameterOptions())
            AudioBindingParameterOptions.Add(option);

        if (AudioBindingParameterOptions.All(o => o.Value != _newAudioBindingParameter))
        {
            _newAudioBindingParameter = "Scale";
            OnPropertyChanged(nameof(NewAudioBindingParameter));
        }
    }

    /// <summary>選択中クリップの音連動バインド行。</summary>
    public ObservableCollection<AudioBindingRow> AudioBindingRows { get; } = new();

    /// <summary>「音連動バインドを追加」で選んでいるパラメータ（既定は拡大率）。一覧を作り直す際に ComboBox から null が書き戻されることがあるので、空値は無視する（キーフレーム欄と同じ対策）。</summary>
    public string NewAudioBindingParameter
    {
        get => _newAudioBindingParameter;
        set
        {
            if (string.IsNullOrEmpty(value) || _newAudioBindingParameter == value)
                return;
            _newAudioBindingParameter = value;
            OnPropertyChanged();
        }
    }

    /// <summary>バインドの音源に選べるクリップ（素材パスを持つ全クリップ。音声クリップと映像クリップ）。</summary>
    public IReadOnlyList<Clip> AudioSourceClips => Project.Tracks
        .SelectMany(t => t.Clips)
        .Where(c => !string.IsNullOrEmpty(c.SourcePath))
        .OrderBy(c => c.StartSeconds)
        .ToList();

    /// <summary>クリップId から音源クリップの名前を返す。見つからなければその旨。</summary>
    public string GetAudioClipName(string clipId)
    {
        if (string.IsNullOrEmpty(clipId))
            return "（未選択）";
        var clip = Project.Tracks.SelectMany(t => t.Clips).FirstOrDefault(c => c.Id == clipId);
        return clip?.Name ?? "（無効な音源）";
    }

    /// <summary>バインドの状態についての説明文（解析中・解析済み・音源なしなど）。</summary>
    public string AudioBindingHint
    {
        get
        {
            var clip = SelectedClip;
            if (clip is null)
                return string.Empty;
            if (clip.Kind == ClipKind.Audio)
                return "音声クリップ自体には映像パラメータが無いため、バインドは付けられません。";
            if (clip.AudioBindings.Count == 0)
                return "音の帯域に合わせてパラメータを動かせます（例：20-80Hz で拡大率、グローやブラーの量も可）。";
            var sources = clip.AudioBindings
                .Where(b => b.Enabled && !string.IsNullOrEmpty(b.SourceClipId))
                .Select(b => b.SourceClipId)
                .Distinct()
                .ToList();
            if (sources.Count == 0)
                return "バインドがありますが、音源が選ばれていません。";
            var allClips = Project.Tracks.SelectMany(t => t.Clips).ToList();
            if (sources.Any(id => allClips.All(c => c.Id != id)))
                return "音源クリップが見つかりません（削除された可能性があります）。";
            return sources.Any(id => !_bandProvider.IsAnalyzed(id))
                ? "音声を解析しています…（終わるまでバインドは動きません）。"
                : "解析済み：再生・書き出しで音に合わせて動きます。";
        }
    }

    /// <summary>選択中クリップに音連動バインドを追加する。音声クリップ自体には付けない。</summary>
    public void AddAudioBinding()
    {
        var clip = SelectedClip;
        if (clip is null || clip.Kind == ClipKind.Audio)
            return;

        BeginEdit();
        var parameter = NewAudioBindingParameter;
        var (combine, min, max) = DefaultBindingRange(parameter, clip);
        var binding = new AudioBinding
        {
            ParameterName = parameter,
            SourceClipId = FindDefaultAudioSourceClip()?.Id ?? string.Empty,
            LowHz = 20,
            HighHz = 80,
            MinValue = min,
            MaxValue = max,
            Gain = 1.0,
            Threshold = 0.0,
            Combine = combine,
            Enabled = true
        };
        clip.AudioBindings.Add(binding);
        RefreshAudioBindingRows();
        NotifyTimelineChanged();
        RequestBindingSourceAnalysis(binding);
        StatusMessage = "音連動バインドを追加しました。";
    }

    /// <summary>
    /// パラメータごとのバインド既定値（合成方法・出力範囲）。拡大率は設計プランの例（100%〜112%）に合わせる。
    /// エフェクト量は Add 合成で、無音時は今の固定値、最大音量で種類の範囲の上限まで動く
    /// （無音時にキーフレーム値のままになるよう、出力範囲の下限を今の固定値にする）。
    /// </summary>
    private static (AudioBindCombine Combine, double Min, double Max) DefaultBindingRange(string parameter, Clip? clip)
    {
        if (clip is not null && TryParseEffectParameter(parameter, out var kind, out var field))
        {
            var effect = clip.Effects.FirstOrDefault(e => e.Kind == kind);
            var definition = EffectDefinitions.Get(kind);
            if (effect is not null && definition is not null)
            {
                if (field == "Amount")
                    return (AudioBindCombine.Add, effect.Amount, definition.AmountMax);
                if (field == "Radius" && definition.RadiusMax is { } radiusMax)
                    return (AudioBindCombine.Add, effect.Radius, radiusMax);
                if (field == "Midtones" && definition.ThirdMax is { } thirdMax)
                    return (AudioBindCombine.Add, effect.Midtones, thirdMax);
            }
        }
        return parameter switch
        {
            "Opacity" => (AudioBindCombine.Override, 0.0, 1.0),
            "Scale" => (AudioBindCombine.Add, 1.0, 1.12),
            "Rotation" => (AudioBindCombine.Add, -10.0, 10.0),
            "PositionX" => (AudioBindCombine.Add, -100.0, 100.0),
            "PositionY" => (AudioBindCombine.Add, -100.0, 100.0),
            _ => (AudioBindCombine.Add, 0.0, 1.0)
        };
    }

    /// <summary>音連動バインドを削除する。</summary>
    public void DeleteAudioBinding(AudioBindingRow row)
    {
        var clip = SelectedClip;
        if (clip is null || !clip.AudioBindings.Contains(row.Binding))
            return;
        BeginEdit();
        clip.AudioBindings.Remove(row.Binding);
        RefreshAudioBindingRows();
        NotifyTimelineChanged();
    }

    /// <summary>バインド追加時の音源の既定値。音声クリップを優先し、無ければ選択中クリップ、次に映像クリップ。</summary>
    private Clip? FindDefaultAudioSourceClip()
    {
        var audio = Project.Tracks
            .SelectMany(t => t.Clips)
            .FirstOrDefault(c => c.Kind == ClipKind.Audio && !string.IsNullOrEmpty(c.SourcePath));
        if (audio is not null)
            return audio;
        if (SelectedClip is not null && !string.IsNullOrEmpty(SelectedClip.SourcePath))
            return SelectedClip;
        return Project.Tracks
            .SelectMany(t => t.Clips)
            .FirstOrDefault(c => c.Kind == ClipKind.Video && !string.IsNullOrEmpty(c.SourcePath));
    }

    /// <summary>バインドの音源クリップの解析を（未解析なら）バックグラウンドで走らせる。</summary>
    internal void RequestBindingSourceAnalysis(AudioBinding binding)
    {
        if (string.IsNullOrEmpty(binding.SourceClipId))
            return;
        var source = Project.Tracks
            .SelectMany(t => t.Clips)
            .FirstOrDefault(c => c.Id == binding.SourceClipId);
        if (source is not null)
            _ = EnsureAnalysisAsync(source);
    }

    /// <summary>
    /// 指定クリップの音声解析を（未解析なら）バックグラウンドで実行する。
    /// 同じクリップの重複した解析は1回にまとめる。完了したらプレビューを更新する。
    /// </summary>
    private Task EnsureAnalysisAsync(Clip clip)
    {
        if (string.IsNullOrEmpty(clip.SourcePath))
            return Task.CompletedTask;

        lock (_analysisTasks)
        {
            if (_analysisTasks.TryGetValue(clip.Id, out var existing))
                return existing; // 実行中・完了済み。失敗したものは完了時に辞書から消す。
            if (_bandProvider.IsAnalyzed(clip.Id))
                return Task.CompletedTask;

            var task = Task.Run(() => _bandProvider.Precompute(clip.Id, clip.SourcePath, Project.FrameRate));
            _analysisTasks[clip.Id] = task;
            _ = task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    // 失敗した解析は辞書から消す（次回また試せるようにする）。
                    lock (_analysisTasks)
                    {
                        _analysisTasks.Remove(clip.Id);
                    }
                    var message = t.Exception?.GetBaseException().Message ?? "不明なエラー";
                    _uiDispatcher.InvokeAsync(() =>
                        StatusMessage = $"音声解析に失敗しました（{clip.Name}）：{message}");
                }
                _uiDispatcher.InvokeAsync(() =>
                {
                    OnPropertyChanged(nameof(AudioBindingHint));
                    NotifyTimelineChanged();
                });
            }, TaskScheduler.Default);
            return task;
        }
    }

    /// <summary>
    /// バインドと可視化の音源になっている全クリップの音声解析が終わるまで待つ（書き出し前など）。
    /// 解析に失敗しても例外は出さない（そのバインド・可視化は無音扱いになる）。
    /// </summary>
    public async Task EnsureBindingAnalysesAsync()
    {
        var sourceIds = Project.Tracks.SelectMany(t => t.Clips)
            .SelectMany(c => c.AudioBindings)
            .Where(b => b.Enabled && !string.IsNullOrEmpty(b.SourceClipId))
            .Select(b => b.SourceClipId)
            .Concat(Project.Tracks.SelectMany(t => t.Clips)
                .Where(c => c.Visualization is not null)
                .Select(c => c.Visualization!.SourceClipId))
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct()
            .ToList();
        var sources = sourceIds
            .Select(id => Project.Tracks.SelectMany(t => t.Clips).FirstOrDefault(c => c.Id == id))
            .Where(c => c is not null && !string.IsNullOrEmpty(c!.SourcePath))
            .Select(c => c!)
            .ToList();
        foreach (var clip in sources)
        {
            try
            {
                await EnsureAnalysisAsync(clip).ConfigureAwait(true);
            }
            catch
            {
                // 解析失敗はバインド・可視化が無音扱いになるだけ。StatusMessage で通知済み。
            }
        }
    }
}
