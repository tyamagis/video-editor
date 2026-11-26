// MainViewModel の分割ファイル（partial）。音の可視化クリップの設定（インスペクタ）と、可視化クリップの追加・解析依頼。

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
    // ---- 音の可視化クリップ（設計プラン 4章） ----

    /// <summary>可視化の状態についての説明文（解析中・解析済み・音源なしなど）。</summary>
    public string VisualizationHint
    {
        get
        {
            var viz = SelectedClip?.Visualization;
            if (viz is null)
                return string.Empty;
            if (string.IsNullOrEmpty(viz.SourceClipId))
                return "音源が選ばれていません。音声クリップをタイムラインに置いて、音源を選んでください。";
            var source = Project.Tracks.SelectMany(t => t.Clips).FirstOrDefault(c => c.Id == viz.SourceClipId);
            if (source is null)
                return "音源クリップが見つかりません（削除された可能性があります）。";
            return _bandProvider.IsAnalyzed(viz.SourceClipId)
                ? $"音源：{source.Name}（解析済み）"
                : $"音源：{source.Name}（解析中…終わるまで描かれません）";
        }
    }

    /// <summary>選択中可視化クリップの種類。離散的な変更なので、代入前にアンドゥ用スナップショットを取る。</summary>
    public VisualizationKind SelectedVisualizationKind
    {
        get => SelectedClip?.Visualization?.Kind ?? VisualizationKind.Spectrum;
        set
        {
            var viz = SelectedClip?.Visualization;
            if (viz is null || viz.Kind == value)
                return;
            BeginEdit();
            viz.Kind = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowSpectrumSettings));
            OnPropertyChanged(nameof(ShowWaveformSettings));
            OnPropertyChanged(nameof(ShowLissajousSettings));
            RefreshKeyframeRows(); // 種類が変わるとキーフレームで選べる項目も変わる。
            NotifyTimelineChanged();
        }
    }

    /// <summary>選択中可視化クリップの音源クリップの Id。離散的な変更なので、代入前にスナップショットを取る。変えたら解析を走らせる。</summary>
    public string SelectedVisualizationSourceClipId
    {
        get => SelectedClip?.Visualization?.SourceClipId ?? string.Empty;
        set
        {
            var viz = SelectedClip?.Visualization;
            if (viz is null)
                return;
            value ??= string.Empty;
            if (viz.SourceClipId == value)
                return;
            BeginEdit();
            viz.SourceClipId = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VisualizationHint));
            RequestVisualizationAnalysis(viz);
            NotifyTimelineChanged();
        }
    }

    /// <summary>選択中可視化クリップの描画色（"#22D3EE" など）。離散的な変更なので、代入前にスナップショットを取る。</summary>
    public string SelectedVisualizationColorHex
    {
        get => SelectedClip?.Visualization?.ColorHex ?? "#22D3EE";
        set
        {
            var viz = SelectedClip?.Visualization;
            if (viz is null || viz.ColorHex == value)
                return;
            BeginEdit();
            viz.ColorHex = value;
            OnPropertyChanged();
            NotifyTimelineChanged();
        }
    }

    /// <summary>スペクトルの対象帯域の下限（Hz、1〜上限）。</summary>
    public double SelectedVisualizationLowHz
    {
        get => SelectedClip?.Visualization?.LowHz ?? 20;
        set
        {
            var viz = SelectedClip?.Visualization;
            if (viz is null)
                return;
            var clamped = Math.Clamp(value, 1, Math.Max(1, viz.HighHz));
            if (Math.Abs(viz.LowHz - clamped) < 1e-9)
                return;
            viz.LowHz = clamped;
            OnPropertyChanged();
            NotifyTimelineChanged();
        }
    }

    /// <summary>スペクトルの対象帯域の上限（Hz、下限〜20000）。</summary>
    public double SelectedVisualizationHighHz
    {
        get => SelectedClip?.Visualization?.HighHz ?? 16000;
        set
        {
            var viz = SelectedClip?.Visualization;
            if (viz is null)
                return;
            var clamped = Math.Clamp(value, Math.Max(1, viz.LowHz), 20000);
            if (Math.Abs(viz.HighHz - clamped) < 1e-9)
                return;
            viz.HighHz = clamped;
            OnPropertyChanged();
            NotifyTimelineChanged();
        }
    }

    /// <summary>スペクトルの本数（4〜128）。</summary>
    public int SelectedVisualizationBandCount
    {
        get => SelectedClip?.Visualization?.BandCount ?? 32;
        set
        {
            var viz = SelectedClip?.Visualization;
            if (viz is null)
                return;
            var clamped = (int)Math.Clamp(value, 4, 128);
            if (viz.BandCount == clamped)
                return;
            viz.BandCount = clamped;
            OnPropertyChanged();
            NotifyTimelineChanged();
        }
    }

    /// <summary>スペクトルの帯域を対数間隔で分けるか（false なら線形間隔）。離散的な変更なので、代入前にスナップショットを取る。</summary>
    public bool SelectedVisualizationLogarithmic
    {
        get => SelectedClip?.Visualization?.Logarithmic ?? true;
        set
        {
            var viz = SelectedClip?.Visualization;
            if (viz is null || viz.Logarithmic == value)
                return;
            BeginEdit();
            viz.Logarithmic = value;
            OnPropertyChanged();
            NotifyTimelineChanged();
        }
    }

    /// <summary>高さ・振幅のゲイン（0.1〜4）。</summary>
    public double SelectedVisualizationGain
    {
        get => SelectedClip?.Visualization?.Gain ?? 1.0;
        set
        {
            var viz = SelectedClip?.Visualization;
            if (viz is null)
                return;
            var clamped = VisualizationSettings.ClampGain(value);
            if (Math.Abs(viz.Gain - clamped) < 1e-9)
                return;
            viz.Gain = clamped;
            OnPropertyChanged();
            NotifyTimelineChanged();
        }
    }

    /// <summary>線の太さ（px、0.5〜10）。波形・リサジューで使う。</summary>
    public double SelectedVisualizationLineWidth
    {
        get => SelectedClip?.Visualization?.LineWidth ?? 2.0;
        set
        {
            var viz = SelectedClip?.Visualization;
            if (viz is null)
                return;
            var clamped = VisualizationSettings.ClampLineWidth(value);
            if (Math.Abs(viz.LineWidth - clamped) < 1e-9)
                return;
            viz.LineWidth = clamped;
            OnPropertyChanged();
            NotifyTimelineChanged();
        }
    }

    /// <summary>波形の表示区間の長さ（秒、0.02〜1）。</summary>
    public double SelectedVisualizationWindowSeconds
    {
        get => SelectedClip?.Visualization?.WindowSeconds ?? 0.1;
        set
        {
            var viz = SelectedClip?.Visualization;
            if (viz is null)
                return;
            var clamped = VisualizationSettings.ClampWindowSeconds(value);
            if (Math.Abs(viz.WindowSeconds - clamped) < 1e-9)
                return;
            viz.WindowSeconds = clamped;
            OnPropertyChanged();
            NotifyTimelineChanged();
        }
    }

    /// <summary>リサジューの残像の長さ（秒、0〜1）。0 で残像なし。</summary>
    public double SelectedVisualizationTrailSeconds
    {
        get => SelectedClip?.Visualization?.TrailSeconds ?? 0.25;
        set
        {
            var viz = SelectedClip?.Visualization;
            if (viz is null)
                return;
            var clamped = VisualizationSettings.ClampTrailSeconds(value);
            if (Math.Abs(viz.TrailSeconds - clamped) < 1e-9)
                return;
            viz.TrailSeconds = clamped;
            OnPropertyChanged();
            NotifyTimelineChanged();
        }
    }

    /// <summary>「可視化を追加」で選んでいる種類（既定はスペクトル）。</summary>
    private VisualizationKind _newVisualizationKind = VisualizationKind.Spectrum;

    public VisualizationKind NewVisualizationKind
    {
        get => _newVisualizationKind;
        set
        {
            if (_newVisualizationKind == value)
                return;
            _newVisualizationKind = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 音の可視化クリップを再生ヘッド位置に追加する。可視化クリップは映像トラックに置き、
    /// 位置・拡大率は他のクリップと同じキーフレームで動かせる。
    /// 音源は再生ヘッド位置で有効な音声クリップ、無ければ最初の音声つきクリップ。
    /// 長さは音源クリップの残り（再生ヘッド位置で有効なとき）か、音源全体の長さ。
    /// </summary>
    public void AddVisualizationClip(VisualizationKind kind)
    {
        BeginEdit();
        var track = Project.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Video)
            ?? Project.AddTrack(TrackKind.Video, "ビデオ 1");

        var source = FindVisualizationDefaultSource();
        double duration;
        if (source is not null && source.IsActiveAt(PlayheadSeconds))
            duration = Math.Max(0.5, source.EndSeconds - PlayheadSeconds);
        else if (source is not null)
            duration = source.DurationSeconds;
        else
            duration = 5;

        var settings = new VisualizationSettings
        {
            Kind = kind,
            SourceClipId = source?.Id ?? string.Empty,
            ColorHex = DefaultVisualizationColor(kind)
        };
        var clip = new Clip
        {
            Name = VisualizationDisplayName(kind),
            Kind = ClipKind.Visualization,
            StartSeconds = PlayheadSeconds,
            DurationSeconds = duration,
            Visualization = settings
        };
        track.Clips.Add(clip);
        SelectedClip = clip;
        RequestVisualizationAnalysis(settings);
        NotifyTimelineChanged();
        StatusMessage = source is null
            ? $"可視化クリップを追加しました：{clip.Name}（音源が無いため、音声クリップを置いて音源を選んでください）"
            : $"可視化クリップを追加しました：{clip.Name}（音源：{source.Name}）";
    }

    /// <summary>可視化の種類の表示名。</summary>
    public static string VisualizationDisplayName(VisualizationKind kind) => kind switch
    {
        VisualizationKind.Spectrum => "スペクトル",
        VisualizationKind.Waveform => "波形",
        VisualizationKind.Lissajous => "リサジュー",
        _ => kind.ToString()
    };

    /// <summary>可視化の種類ごとの既定の描画色。</summary>
    private static string DefaultVisualizationColor(VisualizationKind kind) => kind switch
    {
        VisualizationKind.Spectrum => "#22D3EE",
        VisualizationKind.Waveform => "#4ADE80",
        VisualizationKind.Lissajous => "#FF2E88",
        _ => "#22D3EE"
    };

    /// <summary>可視化クリップ追加時の音源の既定値。再生ヘッド位置で有効な音声クリップを優先し、無ければ最初の音声つきクリップ。</summary>
    private Clip? FindVisualizationDefaultSource()
    {
        var active = FindVisualizationSourceClip();
        if (active is not null)
            return active;
        return Project.Tracks.SelectMany(t => t.Clips)
            .FirstOrDefault(c => c.Kind == ClipKind.Audio && !string.IsNullOrEmpty(c.SourcePath))
            ?? Project.Tracks.SelectMany(t => t.Clips)
                .FirstOrDefault(c => c.Kind == ClipKind.Video && !string.IsNullOrEmpty(c.SourcePath));
    }

    /// <summary>可視化の音源クリップの解析を（未解析なら）バックグラウンドで走らせる。</summary>
    internal void RequestVisualizationAnalysis(VisualizationSettings viz)
    {
        if (string.IsNullOrEmpty(viz.SourceClipId))
            return;
        var source = Project.Tracks
            .SelectMany(t => t.Clips)
            .FirstOrDefault(c => c.Id == viz.SourceClipId);
        if (source is not null)
            _ = EnsureAnalysisAsync(source);
    }
}
