// MainViewModel の分割ファイル（partial）。プレビュー表示設定（グリッド・波形・解像度）、スペクトル／リサジューのオーバーレイ、プレビューフレーム描画。

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
    public GridMode GridMode
    {
        get => Project.GridMode;
        set
        {
            Project.GridMode = value;
            OnPropertyChanged();
            NotifyTimelineChanged();
        }
    }

    /// <summary>波形表示の表示・非表示（既定は表示）。隠しても解析データは保持する。</summary>
    public bool ShowWaveform
    {
        get => _showWaveform;
        set { _showWaveform = value; OnPropertyChanged(); NotifyTimelineChanged(); }
    }

    /// <summary>プレビュー解像度（フル／1/2／1/4）。書き出し品質には影響しない。</summary>
    public PreviewResolution PreviewResolution
    {
        get => _previewResolution;
        set { _previewResolution = value; OnPropertyChanged(); PreviewInvalidated?.Invoke(this, EventArgs.Empty); }
    }

    /// <summary>スペクトル表示をプレビューに重ねるか。</summary>
    public bool ShowSpectrum
    {
        get => _showSpectrum;
        set
        {
            if (_showSpectrum == value)
                return;
            _showSpectrum = value;
            OnPropertyChanged();
            PreviewInvalidated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>リサジュー表示をプレビューに重ねるか。</summary>
    public bool ShowLissajous
    {
        get => _showLissajous;
        set
        {
            if (_showLissajous == value)
                return;
            _showLissajous = value;
            OnPropertyChanged();
            PreviewInvalidated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>スペクトルの帯域数（表示用）。</summary>
    public const int SpectrumBandCount = 32;

    /// <summary>スペクトル表示の対象帯域（Hz）。</summary>
    public const double SpectrumLowHz = 20;
    public const double SpectrumHighHz = 16000;

    /// <summary>リサジュー表示に使う直前の波形の長さ（秒）。</summary>
    public const double LissajousWindowSeconds = 0.08;

    /// <summary>
    /// 再生ヘッド位置で有効な音声つきクリップ（スペクトル・リサジューの音源）。
    /// 音声クリップを優先し、無ければ映像クリップ。
    /// </summary>
    private Clip? FindVisualizationSourceClip()
    {
        var active = Project.Tracks.SelectMany(t => t.Clips)
            .Where(c => c.IsActiveAt(PlayheadSeconds) && !string.IsNullOrEmpty(c.SourcePath))
            .ToList();
        return active.FirstOrDefault(c => c.Kind == ClipKind.Audio)
            ?? active.FirstOrDefault(c => c.Kind == ClipKind.Video);
    }

    /// <summary>
    /// 再生ヘッド位置のスペクトル（帯域ごとの強さ 0.0〜1.0）。解析がまだなら
    /// バックグラウンドで解析を走らせ、今回は null を返す。
    /// </summary>
    public double[]? GetSpectrumBands()
    {
        var clip = FindVisualizationSourceClip();
        if (clip is null)
            return null;
        if (!_bandProvider.IsAnalyzed(clip.Id))
        {
            _ = EnsureAnalysisAsync(clip);
            return null;
        }
        var sourceTime = clip.GetSourceTimeSeconds(PlayheadSeconds);
        return _bandProvider.GetSpectrumBands(clip.Id, sourceTime, SpectrumBandCount, SpectrumLowHz, SpectrumHighHz);
    }

    /// <summary>
    /// 再生ヘッド位置の直前のステレオ波形（リサジュー用）。解析がまだなら
    /// バックグラウンドで解析を走らせ、今回は null を返す。
    /// </summary>
    public (float[] Left, float[] Right)? GetStereoWindow()
    {
        var clip = FindVisualizationSourceClip();
        if (clip is null)
            return null;
        if (!_bandProvider.IsAnalyzed(clip.Id))
        {
            _ = EnsureAnalysisAsync(clip);
            return null;
        }
        var sourceTime = clip.GetSourceTimeSeconds(PlayheadSeconds);
        return _bandProvider.GetStereoWindow(clip.Id, sourceTime, LissajousWindowSeconds);
    }

    public Task<SKBitmap> RenderPreviewAsync(
        double timeSeconds, (int Width, int Height)? maxDisplaySize = null,
        CancellationToken ct = default)
        => _previewRenderer.RenderPreviewAsync(Project, timeSeconds, PreviewResolution, maxDisplaySize, ct);

    public WaveformData? GetWaveform(string sourcePath)
        => Waveforms.TryGetValue(sourcePath, out var data) ? data : null;
}
