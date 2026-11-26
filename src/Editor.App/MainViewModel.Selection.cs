// MainViewModel の分割ファイル（partial）。選択中クリップ／素材の状態と、選択に連動するインスペクタ表示フラグ。

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
    public MediaItem? SelectedMediaItem
    {
        get => _selectedMediaItem;
        set { _selectedMediaItem = value; OnPropertyChanged(); }
    }

    public Clip? SelectedClip
    {
        get => _selectedClip;
        set
        {
            _selectedClip = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedClip));
            OnPropertyChanged(nameof(IsSelectedClipGrouped));
            OnPropertyChanged(nameof(SelectedClipBrightness));
            OnPropertyChanged(nameof(SelectedClipContrast));
            OnPropertyChanged(nameof(SelectedClipTransitionKind));
            OnPropertyChanged(nameof(SelectedClipTransitionDurationSeconds));
            OnPropertyChanged(nameof(TransitionHint));
            OnPropertyChanged(nameof(SelectedClipBlendMode));
            OnPropertyChanged(nameof(IsAudioClipSelected));
            OnPropertyChanged(nameof(IsVideoClipSelected));
            OnPropertyChanged(nameof(HasVisualEffects));
            OnPropertyChanged(nameof(CanHaveAudioBindings));
            OnPropertyChanged(nameof(HasAudioClip));
            OnPropertyChanged(nameof(CanSilenceCut));
            OnPropertyChanged(nameof(SelectedClipNoiseReductionEnabled));
            OnPropertyChanged(nameof(SelectedClipNoiseReductionStrength));
            OnPropertyChanged(nameof(SelectedClipNormalizeAudioEnabled));
            OnPropertyChanged(nameof(SelectedClipNormalizeTargetDb));
            OnPropertyChanged(nameof(NormalizeHint));
            OnPropertyChanged(nameof(SelectedClipAudioFadeInSeconds));
            OnPropertyChanged(nameof(SelectedClipAudioFadeOutSeconds));
            OnPropertyChanged(nameof(SelectedClipDuckingRole));
            OnPropertyChanged(nameof(SelectedClipDuckingAmountDb));
            OnPropertyChanged(nameof(SelectedClipDuckingAttackSeconds));
            OnPropertyChanged(nameof(SelectedClipDuckingReleaseSeconds));
            OnPropertyChanged(nameof(SelectedClipDuckingThresholdDb));
            OnPropertyChanged(nameof(IsDuckingTargetSelected));
            OnPropertyChanged(nameof(SelectedClipIsReversed));
            OnPropertyChanged(nameof(SelectedClipSpeed));
            OnPropertyChanged(nameof(CanAddKeyframeAtPlayhead));
            OnPropertyChanged(nameof(AudioBindingHint));
            OnPropertyChanged(nameof(IsVisualizationClipSelected));
            OnPropertyChanged(nameof(VisualizationHint));
            OnPropertyChanged(nameof(SelectedVisualizationKind));
            OnPropertyChanged(nameof(SelectedVisualizationSourceClipId));
            OnPropertyChanged(nameof(SelectedVisualizationColorHex));
            OnPropertyChanged(nameof(SelectedVisualizationLowHz));
            OnPropertyChanged(nameof(SelectedVisualizationHighHz));
            OnPropertyChanged(nameof(SelectedVisualizationBandCount));
            OnPropertyChanged(nameof(SelectedVisualizationGain));
            OnPropertyChanged(nameof(SelectedVisualizationLogarithmic));
            OnPropertyChanged(nameof(SelectedVisualizationLineWidth));
            OnPropertyChanged(nameof(SelectedVisualizationWindowSeconds));
            OnPropertyChanged(nameof(SelectedVisualizationTrailSeconds));
            OnPropertyChanged(nameof(IsTextClipSelected));
            OnPropertyChanged(nameof(SelectedTextAnimationInKind));
            OnPropertyChanged(nameof(SelectedTextAnimationInDirection));
            OnPropertyChanged(nameof(SelectedTextAnimationInDurationSeconds));
            OnPropertyChanged(nameof(SelectedTextAnimationInEasing));
            OnPropertyChanged(nameof(SelectedTextAnimationInFade));
            OnPropertyChanged(nameof(SelectedTextAnimationInHighlightColorHex));
            OnPropertyChanged(nameof(ShowInDirection));
            OnPropertyChanged(nameof(ShowInFade));
            OnPropertyChanged(nameof(ShowInHighlightColor));
            OnPropertyChanged(nameof(SelectedTextAnimationOutKind));
            OnPropertyChanged(nameof(SelectedTextAnimationOutDirection));
            OnPropertyChanged(nameof(SelectedTextAnimationOutDurationSeconds));
            OnPropertyChanged(nameof(SelectedTextAnimationOutEasing));
            OnPropertyChanged(nameof(SelectedTextAnimationOutFade));
            OnPropertyChanged(nameof(ShowOutDirection));
            OnPropertyChanged(nameof(ShowOutFade));
            OnPropertyChanged(nameof(ShowSpectrumSettings));
            OnPropertyChanged(nameof(ShowWaveformSettings));
            OnPropertyChanged(nameof(ShowLissajousSettings));
            RefreshKeyframeRows();
            RefreshEffectRows();
            RefreshAudioBindingRows();
            _ = RefreshNormalizeHintAsync();
        }
    }

    /// <summary>クリップが選択されているか。インスペクタの編集欄の有効化に使う。</summary>
    public bool HasSelectedClip => SelectedClip is not null;

    /// <summary>選択中クリップが音声クリップか（映像エフェクトは適用されない）。</summary>
    public bool IsAudioClipSelected => SelectedClip?.Kind == ClipKind.Audio;

    /// <summary>選択中クリップが映像クリップか（無音カットで映像も一緒に切れる旨の表示に使う）。</summary>
    public bool IsVideoClipSelected => SelectedClip?.Kind == ClipKind.Video;

    /// <summary>映像エフェクト欄を有効にするか（音声クリップと可視化クリップでは無効。可視化クリップの描画は専用の設定欄で調整する）。</summary>
    public bool HasVisualEffects => SelectedClip is not null
        && SelectedClip.Kind != ClipKind.Audio
        && SelectedClip.Kind != ClipKind.Visualization;

    /// <summary>音連動バインド欄を有効にするか（音声クリップ自体には付けられない。可視化クリップには付けられる）。</summary>
    public bool CanHaveAudioBindings => SelectedClip is not null && SelectedClip.Kind != ClipKind.Audio;

    /// <summary>選択中クリップが可視化クリップか（可視化設定欄の表示に使う）。</summary>
    public bool IsVisualizationClipSelected => SelectedClip?.Kind == ClipKind.Visualization;

    /// <summary>スペクトル用の設定欄を表示するか（可視化クリップで種類がスペクトルのとき）。</summary>
    public bool ShowSpectrumSettings => SelectedClip?.Visualization?.Kind == VisualizationKind.Spectrum;

    /// <summary>波形用の設定欄を表示するか（可視化クリップで種類が波形のとき）。</summary>
    public bool ShowWaveformSettings => SelectedClip?.Visualization?.Kind == VisualizationKind.Waveform;

    /// <summary>リサジュー用の設定欄を表示するか（可視化クリップで種類がリサジューのとき）。</summary>
    public bool ShowLissajousSettings => SelectedClip?.Visualization?.Kind == VisualizationKind.Lissajous;
}
