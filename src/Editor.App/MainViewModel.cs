// メイン画面の ViewModel（フェーズ1）。
// 素材の読み込み、タイムラインへの配置、カット・分割・削除、
// 再生ヘッドの進行、プロジェクト保存、書き出しの呼び出しを担う。
// ファイルダイアログは MainWindow 側に置き、ここはパスを受け取るだけにする。

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

public sealed class MainViewModel : INotifyPropertyChanged, IProjectSessionHost, IDetectionHost
{
    private readonly MediaProbe _mediaProbe;
    private readonly ExportService _exportService;
    private readonly WaveformCache _waveformCache;
    private readonly PreviewRenderer _previewRenderer;
    private readonly ITimelineExporter _timelineExporter;
    private readonly FfmpegSettings _ffmpegSettings;
    private readonly AudioBandEnergyProvider _bandProvider;
    private readonly IAudioPeakProvider _peakProvider;
    private readonly IAudioActivityProvider _activityProvider;
    private readonly Dispatcher _uiDispatcher;

    private readonly DispatcherTimer _playTimer;
    private readonly AudioPreviewPlayer _audioPlayer = new();
    private EditorProject _project = new();
    private Clip? _selectedClip;
    private MediaItem? _selectedMediaItem;

    /// <summary>複数選択中のクリップ（Ctrl+クリックで切り替え。グループ化の対象）。主選択は SelectedClip が担う。</summary>
    private readonly List<Clip> _multiSelectedClips = new();

    /// <summary>ノーマライズの状態についての説明文（測定したピークと適用されるゲインなど）。</summary>
    private string _normalizeHint = string.Empty;

    /// <summary>音声再生の開始世代。ピーク解析を待っている間に再生が止められたり
    /// 鳴らし直されたりしたら、古い世代の再生は捨てる。</summary>
    private int _audioPlayGeneration;
    private double _playheadSeconds;
    private bool _isPlaying;
    private bool _showWaveform = true;
    private PreviewResolution _previewResolution = PreviewResolution.Full;
    private string _statusMessage = "素材を読み込んでください。";
    private string _keyframeParameter = "Opacity";
    private EffectKind _newEffectKind = EffectKind.Blur;

    /// <summary>「音連動バインドを追加」で選んでいるパラメータ（既定は拡大率）。</summary>
    private string _newAudioBindingParameter = "Scale";

    /// <summary>スペクトル表示をプレビューに重ねるか。</summary>
    private bool _showSpectrum;

    /// <summary>リサジュー表示をプレビューに重ねるか。</summary>
    private bool _showLissajous;

    /// <summary>音声解析の実行中タスク（クリップId → タスク）。同じクリップの重複した解析を1回にまとめる。</summary>
    private readonly Dictionary<string, Task> _analysisTasks = new();

    /// <summary>Undo/Redo・保存／読込・自動保存のセッション状態。コンストラクタで作る。</summary>
    private readonly ProjectSession _session;

    /// <summary>BPM検出・無音カットの「検出→保留→検証→適用」状態機械。コンストラクタで作る。</summary>
    private readonly DetectionService _detection;

    /// <summary>波形データのキャッシュ（素材パス → 波形）。表示切替で消さない。</summary>
    public Dictionary<string, WaveformData> Waveforms { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>プレビューを描き直す合図。MainWindow が購読する。</summary>
    public event EventHandler? PreviewInvalidated;

    public MainViewModel(
        MediaProbe mediaProbe,
        ExportService exportService,
        WaveformCache waveformCache,
        PreviewRenderer previewRenderer,
        ITimelineExporter timelineExporter,
        FfmpegSettings ffmpegSettings,
        AudioBeatAnalyzer beatAnalyzer,
        AudioBandEnergyProvider bandProvider,
        SilenceDetector silenceDetector,
        IAudioPeakProvider? peakProvider = null,
        IAudioActivityProvider? activityProvider = null)
    {
        _mediaProbe = mediaProbe;
        _exportService = exportService;
        _waveformCache = waveformCache;
        _previewRenderer = previewRenderer;
        _timelineExporter = timelineExporter;
        _ffmpegSettings = ffmpegSettings;
        _bandProvider = bandProvider;
        _peakProvider = peakProvider ?? new PeakAnalyzer();
        _activityProvider = activityProvider ?? new AudioActivityAnalyzer();
        _uiDispatcher = Dispatcher.CurrentDispatcher;

        _playTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0 / 30) };
        _playTimer.Tick += (_, _) => AdvancePlayhead();

        // セッション状態（Undo/Redo・保存／読込・自動保存）と検出状態機械は
        // 専用クラスへ分離。ホスト（この ViewModel）に委譲する操作は
        // インターフェース経由で渡す。どちらも UI スレッドで作る
        // （ProjectSession は自動保存タイマーを作るため）。
        _session = new ProjectSession(this);
        _session.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);
        _detection = new DetectionService(beatAnalyzer, silenceDetector, this);
        _detection.PropertyChanged += (_, e) =>
        {
            OnPropertyChanged(e.PropertyName);
            if (e.PropertyName == nameof(DetectionService.IsDetectingSilence))
                OnPropertyChanged(nameof(CanSilenceCut));
        };

        PlayPauseCommand = new RelayCommand(_ => IsPlaying = !IsPlaying);
        SplitCommand = new RelayCommand(_ => SplitAtPlayhead());
        DeleteClipCommand = new RelayCommand(_ => DeleteSelectedClip(), _ => SelectedClip is not null);
        RippleDeleteClipCommand = new RelayCommand(_ => RippleDeleteSelectedClip(), _ => SelectedClip is not null);
        GroupClipsCommand = new RelayCommand(_ => GroupSelectedClips());
        UngroupClipCommand = new RelayCommand(_ => UngroupSelectedClip(), _ => SelectedClip is not null && ClipGroups.IsGrouped(SelectedClip));
        AddTextClipCommand = new RelayCommand(_ => AddTextClip("歌詞テロップ"));

        RefreshKeyframeParameterOptions();
    }

    public EditorProject Project
    {
        get => _project;
        private set
        {
            // プロジェクトが変われば検出結果は合わなくなるので消す。
            _detection.Clear();
            lock (_analysisTasks)
            {
                _analysisTasks.Clear(); // 解析タスクも新しいプロジェクトのクリップIdで取り直す。
            }
            _project = value;
            _multiSelectedClips.Clear(); // 複数選択は旧プロジェクトのクリップ参照なので破棄する。
            OnPropertyChanged(nameof(MultiSelectedClips));
            RefreshMarkerRows(); // プロジェクトが変わればマーカー一覧も作り直す（選択は引き継げなければ外れる）。
            OnPropertyChanged();
            OnPropertyChanged(nameof(Bpm));
            OnPropertyChanged(nameof(GridMode));
            OnPropertyChanged(nameof(ExportInSeconds));
            OnPropertyChanged(nameof(ExportOutSeconds));
            OnPropertyChanged(nameof(ExportRangeLabel));
            NotifyTimelineChanged();
        }
    }

    public ObservableCollection<MediaItem> MediaItems { get; } = new();

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

    /// <summary>音声つきクリップ（音声または映像で、素材パスがある）が選択されているか。無音カット・ノイズ除去欄の有効化に使う。</summary>
    public bool HasAudioClip => SelectedClip is not null
        && (SelectedClip.Kind == ClipKind.Audio || SelectedClip.Kind == ClipKind.Video)
        && !string.IsNullOrEmpty(SelectedClip.SourcePath);

    /// <summary>無音カットを使えるか（音声つきクリップを選択中で、検出中でない）。</summary>
    public bool CanSilenceCut => HasAudioClip && !IsDetectingSilence;

    /// <summary>選択中クリップのノイズ除去の有効・無効（書き出し時に適用）。離散的な変更なので、代入前にアンドゥ用スナップショットを取る。</summary>
    public bool SelectedClipNoiseReductionEnabled
    {
        get => SelectedClip?.NoiseReductionEnabled ?? false;
        set
        {
            if (SelectedClip is null || SelectedClip.NoiseReductionEnabled == value)
                return;
            BeginEdit();
            SelectedClip.NoiseReductionEnabled = value;
            OnPropertyChanged();
        }
    }

    /// <summary>選択中クリップのノイズ除去の強さ（0.0〜1.0）。クリップ未選択時は 0.5。</summary>
    public double SelectedClipNoiseReductionStrength
    {
        get => SelectedClip?.NoiseReductionStrength ?? 0.5;
        set
        {
            if (SelectedClip is null)
                return;
            SelectedClip.NoiseReductionStrength = Math.Clamp(value, 0.0, 1.0);
            OnPropertyChanged();
        }
    }

    // ---- 音声ノーマライズ（インスペクタ） ----

    /// <summary>選択中クリップの音声ノーマライズの有効・無効（プレビュー・書き出し共通）。離散的な変更なので、代入前にアンドゥ用スナップショットを取る。有効にしたらピーク解析を裏で走らせる（再生時に待たずに済むよう先読みする）。</summary>
    public bool SelectedClipNormalizeAudioEnabled
    {
        get => SelectedClip?.NormalizeAudioEnabled ?? false;
        set
        {
            if (SelectedClip is null || SelectedClip.NormalizeAudioEnabled == value)
                return;
            BeginEdit();
            SelectedClip.NormalizeAudioEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NormalizeHint));
            _ = RefreshNormalizeHintAsync();
        }
    }

    /// <summary>選択中クリップのノーマライズの目標ピークレベル（dBFS、-30〜0）。クリップ未選択時は -1。ピークは目標に依存しないため、変えても再解析は要らない（キャッシュ済みのピークからゲインを計算し直すだけ）。</summary>
    public double SelectedClipNormalizeTargetDb
    {
        get => SelectedClip?.NormalizeTargetDb ?? AudioNormalizer.DefaultTargetDb;
        set
        {
            if (SelectedClip is null)
                return;
            SelectedClip.NormalizeTargetDb = AudioNormalizer.ClampTargetDb(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(NormalizeHint));
            _ = RefreshNormalizeHintAsync();
        }
    }

    /// <summary>ノーマライズの状態についての説明文（測定したピークと適用されるゲインなど）。選択クリップが変わる・設定が変わると裏で更新する。</summary>
    public string NormalizeHint
    {
        get => _normalizeHint;
        private set
        {
            if (_normalizeHint == value)
                return;
            _normalizeHint = value;
            OnPropertyChanged();
        }
    }

    /// <summary>選択中クリップのノーマライズ情報を裏で測り、NormalizeHint を更新する。選択が変わった場合は古い結果を捨てる。ピーク解析はキャッシュされるため、2回目以降はすぐ終わる。</summary>
    private async Task RefreshNormalizeHintAsync()
    {
        var clip = SelectedClip;
        if (clip is null || !clip.NormalizeAudioEnabled || string.IsNullOrEmpty(clip.SourcePath))
        {
            NormalizeHint = string.Empty;
            return;
        }
        var clipId = clip.Id;
        var targetDb = AudioNormalizer.ClampTargetDb(clip.NormalizeTargetDb);
        NormalizeHint = "ピークを解析中…";
        AudioPeakResult peak;
        try
        {
            peak = await _peakProvider
                .GetPeakAsync(clip.SourcePath, clip.InPointSeconds, clip.SourceDurationSeconds)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (SelectedClip?.Id == clipId)
                NormalizeHint = $"ピークを測れませんでした：{ex.Message}（元の音量で再生・書き出しします）";
            return;
        }
        if (SelectedClip?.Id != clipId)
            return; // 選択が変わったので古い結果は捨てる。
        if (!peak.Available)
        {
            NormalizeHint = $"ピークを測れませんでした：{peak.FailureReason}（元の音量で再生・書き出しします）";
            return;
        }
        var gainDb = AudioNormalizer.ComputeGainDb(peak.PeakLinear, targetDb);
        var peakDb = AudioNormalizer.PeakToDb(peak.PeakLinear);
        var peakText = double.IsNegativeInfinity(peakDb) ? "無音" : $"{peakDb:F1} dBFS";
        var sign = gainDb >= 0 ? "+" : string.Empty;
        // 上限（+40 dB）で止まった場合は、目標に届かない旨を明示する。
        var capped = !double.IsNegativeInfinity(peakDb)
            && targetDb - peakDb > AudioNormalizer.MaxGainDb + 1e-9;
        NormalizeHint = capped
            ? $"ピーク {peakText} → ゲイン {sign}{gainDb:F1} dB（上限 +40 dB で止めています。目標 {targetDb:F1} dBFS には届きません）"
            : $"ピーク {peakText} → ゲイン {sign}{gainDb:F1} dB（目標 {targetDb:F1} dBFS）";
    }

    // ---- 音声フェード／ダッキング（インスペクタ） ----

    /// <summary>選択中クリップの音声フェードインの長さ（タイムライン秒、0〜60）。連続変更は欄の GotFocus でスナップショットを取る流儀（ノーマライズ目標と同じ）。</summary>
    public double SelectedClipAudioFadeInSeconds
    {
        get => SelectedClip?.AudioFadeInSeconds ?? 0;
        set
        {
            if (SelectedClip is null)
                return;
            SelectedClip.AudioFadeInSeconds = AudioGainEnvelope.ClampFadeSeconds(value);
            OnPropertyChanged();
        }
    }

    /// <summary>選択中クリップの音声フェードアウトの長さ（タイムライン秒、0〜60）。</summary>
    public double SelectedClipAudioFadeOutSeconds
    {
        get => SelectedClip?.AudioFadeOutSeconds ?? 0;
        set
        {
            if (SelectedClip is null)
                return;
            SelectedClip.AudioFadeOutSeconds = AudioGainEnvelope.ClampFadeSeconds(value);
            OnPropertyChanged();
        }
    }

    /// <summary>選択中クリップのダッキングの役割（なし／対象／トリガー）。離散的な変更なので、代入前にアンドゥ用スナップショットを取る。</summary>
    public DuckingRole SelectedClipDuckingRole
    {
        get => SelectedClip?.DuckingRole ?? DuckingRole.None;
        set
        {
            if (SelectedClip is null || SelectedClip.DuckingRole == value)
                return;
            BeginEdit();
            SelectedClip.DuckingRole = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDuckingTargetSelected));
        }
    }

    /// <summary>選択中クリップがダッキング対象か（対象用パラメータ欄の表示に使う）。</summary>
    public bool IsDuckingTargetSelected => SelectedClip?.DuckingRole == DuckingRole.Target;

    /// <summary>ダッキングで下げる量（dB、0〜30）。対象クリップだけが使う。</summary>
    public double SelectedClipDuckingAmountDb
    {
        get => SelectedClip?.DuckingAmountDb ?? AudioGainEnvelope.DefaultDuckAmountDb;
        set
        {
            if (SelectedClip is null)
                return;
            SelectedClip.DuckingAmountDb = AudioGainEnvelope.ClampDuckAmountDb(value);
            OnPropertyChanged();
        }
    }

    /// <summary>ダッキングのアタック（秒、0.005〜2）。対象クリップだけが使う。</summary>
    public double SelectedClipDuckingAttackSeconds
    {
        get => SelectedClip?.DuckingAttackSeconds ?? AudioGainEnvelope.DefaultAttackSeconds;
        set
        {
            if (SelectedClip is null)
                return;
            SelectedClip.DuckingAttackSeconds = AudioGainEnvelope.ClampAttackSeconds(value);
            OnPropertyChanged();
        }
    }

    /// <summary>ダッキングのリリース（秒、0.005〜5）。対象クリップだけが使う。</summary>
    public double SelectedClipDuckingReleaseSeconds
    {
        get => SelectedClip?.DuckingReleaseSeconds ?? AudioGainEnvelope.DefaultReleaseSeconds;
        set
        {
            if (SelectedClip is null)
                return;
            SelectedClip.DuckingReleaseSeconds = AudioGainEnvelope.ClampReleaseSeconds(value);
            OnPropertyChanged();
        }
    }

    /// <summary>トリガー検出のしきい値（dBFS、-90〜0）。対象クリップだけが使う。変えるとトリガー区間の再解析が要るが、解析は再生・書き出し時にキャッシュキー（しきい値を含む）で取り直される。</summary>
    public double SelectedClipDuckingThresholdDb
    {
        get => SelectedClip?.DuckingThresholdDb ?? AudioGainEnvelope.DefaultThresholdDb;
        set
        {
            if (SelectedClip is null)
                return;
            SelectedClip.DuckingThresholdDb = AudioGainEnvelope.ClampThresholdDb(value);
            OnPropertyChanged();
        }
    }

    /// <summary>選択中クリップの逆再生（映像・音声とも逆向きに再生する）。離散的な変更なので、代入前にアンドゥ用スナップショットを取る。切り替えるとプレビューが描き直される。</summary>
    public bool SelectedClipIsReversed
    {
        get => SelectedClip?.IsReversed ?? false;
        set
        {
            if (SelectedClip is null || SelectedClip.IsReversed == value)
                return;
            BeginEdit();
            SelectedClip.IsReversed = value;
            OnPropertyChanged();
            NotifyTimelineChanged();
        }
    }

    /// <summary>
    /// 選択中クリップの再生速度（倍率、0.1〜4.0。1.0 が等速）。
    /// 変更は TimelineEditor.ChangeSpeed が行う（素材の使用範囲は変えず、
    /// タイムライン上の長さ = 使用範囲 ÷ 速度 に合わせる）。
    /// スライダーの連続変更ではアンドゥ用スナップショットは欄の GotFocus
    /// （EditableField_GotFocus）が操作の開始時に1回だけ取る流儀のため、
    /// ここでは BeginEdit しない（ノーマライズの目標ピークと同じ扱い）。
    /// 長さが変わるので NotifyTimelineChanged でタイムライン・プレビューを更新する。
    /// </summary>
    public double SelectedClipSpeed
    {
        get => SelectedClip?.Speed ?? 1.0;
        set
        {
            if (SelectedClip is null)
                return;
            var clamped = Clip.ClampSpeed(value);
            if (Math.Abs(SelectedClip.Speed - clamped) < 1e-12)
                return;
            TimelineEditor.ChangeSpeed(SelectedClip, clamped);
            OnPropertyChanged();
            NotifyTimelineChanged();
        }
    }

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

    /// <summary>選択中クリップのブレンドモード。未選択時は通常。</summary>
    public BlendMode SelectedClipBlendMode
    {
        get => SelectedClip?.BlendMode ?? BlendMode.Normal;
        set
        {
            if (SelectedClip is null || SelectedClip.BlendMode == value)
                return;
            SelectedClip.BlendMode = value;
            OnPropertyChanged();
            NotifyTimelineChanged();
        }
    }

    /// <summary>選択中クリップの明るさ（-1.0〜1.0）。クリップ未選択時は 0。</summary>
    public double SelectedClipBrightness
    {
        get => SelectedClip?.Brightness ?? 0;
        set
        {
            if (SelectedClip is null)
                return;
            SelectedClip.Brightness = Math.Clamp(value, -1.0, 1.0);
            OnPropertyChanged();
            NotifyTimelineChanged();
        }
    }

    /// <summary>選択中クリップのコントラスト（0.0〜2.0）。クリップ未選択時は 1.0。</summary>
    public double SelectedClipContrast
    {
        get => SelectedClip?.Contrast ?? 1.0;
        set
        {
            if (SelectedClip is null)
                return;
            SelectedClip.Contrast = Math.Clamp(value, 0.0, 2.0);
            OnPropertyChanged();
            NotifyTimelineChanged();
        }
    }

    /// <summary>選択中クリップの開始側トランジション種別。未選択時は「なし」。</summary>
    public TransitionKind SelectedClipTransitionKind
    {
        get => SelectedClip?.TransitionKind ?? TransitionKind.None;
        set
        {
            if (SelectedClip is null || SelectedClip.TransitionKind == value)
                return;
            SelectedClip.TransitionKind = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TransitionHint));
            NotifyTimelineChanged();
        }
    }

    /// <summary>選択中クリップの開始側トランジションの長さ（秒）。0.05〜60 に丸める。</summary>
    public double SelectedClipTransitionDurationSeconds
    {
        get => SelectedClip?.TransitionDurationSeconds ?? 1.0;
        set
        {
            if (SelectedClip is null)
                return;
            var clamped = Math.Clamp(value, 0.05, 60.0);
            if (Math.Abs(SelectedClip.TransitionDurationSeconds - clamped) < 1e-9)
                return;
            SelectedClip.TransitionDurationSeconds = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TransitionHint));
            NotifyTimelineChanged();
        }
    }

    /// <summary>選択中クリップのトランジションについての説明文。</summary>
    public string TransitionHint
    {
        get
        {
            var clip = SelectedClip;
            if (clip is null)
                return string.Empty;
            if (clip.Kind == ClipKind.Audio)
                return "音声クリップのトランジション（音量のクロスフェード）は未対応です。";
            if (clip.TransitionKind == TransitionKind.None)
                return "開始側の境界にトランジションはありません。";
            var prev = FindPreviousAdjacentClip(clip);
            if (prev is null)
                return "前のクリップと隣り合っていないため、適用されません。";
            return $"前のクリップ「{prev.Name}」との境界で {clip.TransitionDurationSeconds:F1} 秒のディゾルブ。";
        }
    }

    /// <summary>同じトラック上で、指定クリップの直前に隣り合っているクリップ。無ければ null。</summary>
    private Clip? FindPreviousAdjacentClip(Clip clip)
    {
        foreach (var track in Project.Tracks)
        {
            var clips = track.Clips.OrderBy(c => c.StartSeconds).ToList();
            var index = clips.IndexOf(clip);
            if (index > 0)
            {
                var prev = clips[index - 1];
                if (Math.Abs(prev.EndSeconds - clip.StartSeconds) < 1e-6)
                    return prev;
            }
        }
        return null;
    }

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

    public double PlayheadSeconds
    {
        get => _playheadSeconds;
        set
        {
            _playheadSeconds = Math.Clamp(value, 0, Math.Max(0, Project.DurationSeconds));
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanAddKeyframeAtPlayhead));
            PreviewInvalidated?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            _isPlaying = value;
            if (value)
            {
                _playTimer.Start();
                StartAudioAtPlayhead();
                // バインドの音源解析を裏で走らせる（終わるまでバインドは無音扱い）。
                _ = EnsureBindingAnalysesAsync();
            }
            else
            {
                _playTimer.Stop();
                _audioPlayer.Stop();
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(PlayPauseLabel));
        }
    }

    public string PlayPauseLabel => IsPlaying ? "停止" : "再生";

    /// <summary>BPM（手動設定）。拍グリッドの基準。既定120。</summary>
    public double Bpm
    {
        get => Project.Bpm;
        set
        {
            if (value <= 0)
                return;
            Project.Bpm = value;
            OnPropertyChanged();
            // 手動で BPM を変えたら、検出済みビートマップは合わなくなるので消す。
            if (DetectedBeatMap is not null)
                DetectedBeatMap = null;
            NotifyTimelineChanged();
        }
    }

    /// <summary>BPM検出の実行中か。</summary>
    public bool IsDetectingBpm => _detection.IsDetectingBpm;

    /// <summary>BPM検出ボタンを押せるか（検出中は押せない）。</summary>
    public bool CanDetectBpm => _detection.CanDetectBpm;

    /// <summary>未適用の検出結果があるか（「適用」ボタンの有効化に使う）。</summary>
    public bool HasPendingDetection => _detection.HasPendingDetection;

    /// <summary>検出結果の表示（例：検出：120.3 BPM（信頼度 87%）・拍 128個）。未検出時は空。</summary>
    public string DetectedBpmText => _detection.DetectedBpmText;

    /// <summary>
    /// 適用済みの検出ビートマップ（タイムライン時刻）。拍グリッド表示は、
    /// これがあるときは BPM からの算出ではなくこの拍時刻を描く。
    /// </summary>
    public BeatMap? DetectedBeatMap
    {
        get => _detection.DetectedBeatMap;
        private set => _detection.DetectedBeatMap = value;
    }

    /// <summary>BPMを自動検出する。結果は保留し、「適用」でプロジェクトへ反映する。</summary>
    public Task DetectBpmAsync() => _detection.DetectBpmAsync();

    /// <summary>保留中の検出結果をプロジェクトへ反映する（アンドゥ対象）。</summary>
    public void ApplyDetectedBpm() => _detection.ApplyDetectedBpm();

    /// <summary>無音検出の実行中か。</summary>
    public bool IsDetectingSilence => _detection.IsDetectingSilence;

    /// <summary>無音検出ボタンを押せるか（検出中は押せない）。</summary>
    public bool CanDetectSilence => _detection.CanDetectSilence;

    /// <summary>無音とみなすしきい値（dBFS、-90〜-10）。検出パラメータで、プロジェクトには保存しない。</summary>
    public double SilenceThresholdDb
    {
        get => _detection.SilenceThresholdDb;
        set => _detection.SilenceThresholdDb = value;
    }

    /// <summary>無音区間とみなす最短の長さ（秒、0.1〜10）。</summary>
    public double SilenceMinSeconds
    {
        get => _detection.SilenceMinSeconds;
        set => _detection.SilenceMinSeconds = value;
    }

    /// <summary>無音区間の前後に残す余白（秒、0〜1）。</summary>
    public double SilenceMarginSeconds
    {
        get => _detection.SilenceMarginSeconds;
        set => _detection.SilenceMarginSeconds = value;
    }

    /// <summary>未適用の無音検出結果があるか（「適用」ボタンの有効化に使う）。</summary>
    public bool HasPendingSilence => _detection.HasPendingSilence;

    /// <summary>無音検出結果の表示（例：無音区間 12 個、合計 34.5 秒）。未検出時は空。</summary>
    public string SilenceDetectionText => _detection.SilenceDetectionText;

    /// <summary>選択中クリップの無音区間を検出する。結果は保留し、「適用」でプロジェクトへ反映する。</summary>
    public Task DetectSilenceAsync() => _detection.DetectSilenceAsync();

    /// <summary>保留中の無音検出結果をプロジェクトへ反映する（アンドゥ対象）。</summary>
    public void ApplySilenceCut() => _detection.ApplySilenceCut();

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

    /// <summary>書き出し範囲の開始（秒）。</summary>
    public double ExportInSeconds
    {
        get => Project.ExportInSeconds;
        set
        {
            Project.ExportInSeconds = Math.Max(0, value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ExportRangeLabel));
            NotifyTimelineChanged();
        }
    }

    /// <summary>書き出し範囲の終了（秒）。0 は末尾まで。</summary>
    public double ExportOutSeconds
    {
        get => Project.ExportOutSeconds;
        set
        {
            Project.ExportOutSeconds = Math.Max(0, value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ExportRangeLabel));
            NotifyTimelineChanged();
        }
    }

    /// <summary>書き出し範囲の表示（例：書き出し範囲： 2.0s 〜 10.0s）。</summary>
    public string ExportRangeLabel
    {
        get
        {
            var start = Project.EffectiveExportInSeconds;
            var end = Project.EffectiveExportOutSeconds;
            var whole = start <= 0 && end >= Project.DurationSeconds;
            return whole
                ? "書き出し範囲：全体"
                : $"書き出し範囲： {start:F1}s 〜 {end:F1}s";
        }
    }

    /// <summary>書き出し範囲の開始を再生ヘッド位置にする。</summary>
    public void SetExportInAtPlayhead()
    {
        ExportInSeconds = PlayheadSeconds;
        StatusMessage = $"書き出し範囲の開始： {PlayheadSeconds:F1}s";
    }

    /// <summary>書き出し範囲の終了を再生ヘッド位置にする。</summary>
    public void SetExportOutAtPlayhead()
    {
        ExportOutSeconds = PlayheadSeconds;
        StatusMessage = $"書き出し範囲の終了： {PlayheadSeconds:F1}s";
    }

    /// <summary>書き出し範囲を全体に戻す。</summary>
    public void ClearExportRange()
    {
        Project.ExportInSeconds = 0;
        Project.ExportOutSeconds = 0;
        OnPropertyChanged(nameof(ExportInSeconds));
        OnPropertyChanged(nameof(ExportOutSeconds));
        OnPropertyChanged(nameof(ExportRangeLabel));
        NotifyTimelineChanged();
        StatusMessage = "書き出し範囲を全体に戻しました。";
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    public ICommand PlayPauseCommand { get; }
    public ICommand SplitCommand { get; }
    public ICommand DeleteClipCommand { get; }
    public ICommand RippleDeleteClipCommand { get; }
    public ICommand GroupClipsCommand { get; }
    public ICommand UngroupClipCommand { get; }
    public ICommand AddTextClipCommand { get; }

    public FfmpegSettings FfmpegSettings => _ffmpegSettings;

    public async Task<bool> CheckFfmpegAsync()
        => await _ffmpegSettings.IsAvailableAsync().ConfigureAwait(true);

    /// <summary>素材を読み込み、プールに追加する。音声つき素材は波形も作る。</summary>
    public async Task AddMediaAsync(string path)
    {
        var info = await _mediaProbe.ProbeAsync(path).ConfigureAwait(true);
        var item = new MediaItem { SourcePath = path, Info = info };
        MediaItems.Add(item);

        if (info.HasAudio)
        {
            // 波形もキャッシュ対象。プロジェクトのキャッシュフォルダが決まっていれば
            // ディスクキャッシュを使い、無ければ読み直す（2回目の読み込みが速くなる）。
            var waveform = await Task.Run(() => _waveformCache.GetOrRead(path)).ConfigureAwait(true);
            Waveforms[path] = waveform;
        }

        StatusMessage = $"読み込みました：{item.Name}";
    }

    /// <summary>選択中の素材をタイムラインに配置する。開始時刻を省略すると末尾に置く。</summary>
    public void AddMediaToTimeline(MediaItem item) => AddMediaToTimeline(item, null);

    public void AddMediaToTimeline(MediaItem item, double? startSeconds)
    {
        BeginEdit();
        var kind = item.Info.HasVideo ? TrackKind.Video : TrackKind.Audio;
        var track = Project.Tracks.FirstOrDefault(t => t.Kind == kind)
            ?? Project.AddTrack(kind, kind == TrackKind.Video ? "ビデオ 1" : "オーディオ 1");

        var duration = item.Info.Duration.TotalSeconds;
        if (duration <= 0)
            duration = 5; // 長さが取れなかった素材は5秒で置く（幅0で見えなくなるのを防ぐ）

        var clip = new Clip
        {
            Name = item.Name,
            Kind = item.Info.HasVideo ? ClipKind.Video : ClipKind.Audio,
            SourcePath = item.SourcePath,
            StartSeconds = startSeconds ?? track.Clips.Select(c => c.EndSeconds).DefaultIfEmpty(0).Max(),
            DurationSeconds = duration,
            InPointSeconds = 0
        };
        track.Clips.Add(clip);
        SelectedClip = clip;
        NotifyTimelineChanged();
        StatusMessage = $"タイムラインに追加しました：{item.Name}";
    }

    /// <summary>テキストクリップを再生ヘッド位置に追加する。</summary>
    public void AddTextClip(string text)
    {
        BeginEdit();
        var track = Project.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Text)
            ?? Project.AddTrack(TrackKind.Text, "テキスト 1");

        var clip = new Clip
        {
            Name = text,
            Kind = ClipKind.Text,
            Text = text,
            StartSeconds = PlayheadSeconds,
            DurationSeconds = 5
        };
        track.Clips.Add(clip);
        SelectedClip = clip;
        NotifyTimelineChanged();
    }

    /// <summary>
    /// 歌詞ファイル（1行1フレーズ）を読み、テキストクリップとして一括で配置する。
    /// 読み込みに失敗した場合は LyricsImportException（何も配置しない）。
    /// 配置は1回の操作としてアンドゥできる（BeginEdit を一度だけ呼ぶ）。
    /// 追加するクリップは手動のテキスト追加と同じ既定値（フォント・サイズ・色）になる。
    /// </summary>
    /// <returns>配置したクリップの数。</returns>
    public int ImportLyrics(string path, LyricsImportOptions options)
    {
        // 読み込み・検証を先に済ませる。失敗したときはスナップショットを積まない。
        var phrases = LyricsFileReader.ReadPhrases(path, options);
        var plan = LyricsImport.PlanClips(phrases, options);

        BeginEdit();
        var added = LyricsImport.ImportInto(Project, plan);
        SelectedClip = added.FirstOrDefault();
        NotifyTimelineChanged();
        StatusMessage = $"歌詞を {added.Count} 行流し込みました：{Path.GetFileName(path)}";
        return added.Count;
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

    /// <summary>再生ヘッド位置で全トラックのクリップを分割する。対象が無ければ何もしない。</summary>
    public void SplitAtPlayhead()
    {
        // クリップ先頭ちょうどの位置は IsActiveAt には入るが SplitClip は
        // 分割しない（null）ため、厳密に「内側」のクリップで判定する。
        // そうしないと分割されないのにアンドゥが1件積まれてしまう。
        var targets = Project.Tracks
            .SelectMany(t => t.Clips)
            .Where(c => c.StartSeconds < PlayheadSeconds && PlayheadSeconds < c.EndSeconds)
            .ToList();
        if (targets.Count == 0)
            return;

        BeginEdit();
        var splitAny = false;
        foreach (var track in Project.Tracks)
        {
            foreach (var clip in track.Clips.Where(c => c.IsActiveAt(PlayheadSeconds)).ToList())
            {
                if (TimelineEditor.SplitClip(track, clip, PlayheadSeconds) is not null)
                    splitAny = true;
            }
        }
        if (splitAny)
            NotifyTimelineChanged();
    }

    public void DeleteSelectedClip()
    {
        if (SelectedClip is null)
            return;
        BeginEdit();
        foreach (var track in Project.Tracks)
        {
            if (track.Clips.Remove(SelectedClip))
                break;
        }
        _multiSelectedClips.Remove(SelectedClip);
        OnPropertyChanged(nameof(MultiSelectedClips));
        // グループの残りが1個になったら自動解除する（1個だけのグループは意味を持たない）。
        ClipGroups.CleanupSingletonGroups(Project);
        SelectedClip = null;
        NotifyTimelineChanged();
    }

    // ---- 複数選択・グループ化・リップル削除 ----

    /// <summary>複数選択中のクリップ（主選択 SelectedClip とは別に保持する。グループ化の対象）。</summary>
    public IReadOnlyList<Clip> MultiSelectedClips => _multiSelectedClips;

    /// <summary>選択中クリップがグループに属しているか（グループ解除メニューの有効化・表示用）。</summary>
    public bool IsSelectedClipGrouped => SelectedClip is not null && ClipGroups.IsGrouped(SelectedClip);

    /// <summary>Ctrl+クリックで複数選択を切り替える。切り替えたクリップを主選択にもする。</summary>
    public void ToggleClipSelection(Clip clip)
    {
        if (_multiSelectedClips.Contains(clip))
            _multiSelectedClips.Remove(clip);
        else
            _multiSelectedClips.Add(clip);
        SelectedClip = clip;
        OnPropertyChanged(nameof(MultiSelectedClips));
        NotifyTimelineChanged();
    }

    /// <summary>
    /// グループ化（Ctrl+G）。対象は複数選択＋主選択の和集合。
    /// 2個未満なら何もしない（その旨をステータスに出す）。1回の操作としてアンドゥできる。
    /// グループ化後は複数選択を解く（グループ自体が選択単位になるため）。
    /// </summary>
    public void GroupSelectedClips()
    {
        var targets = _multiSelectedClips
            .Concat(SelectedClip is null ? Array.Empty<Clip>() : new[] { SelectedClip })
            .Distinct()
            .Where(c => Project.Tracks.Any(t => t.Clips.Contains(c)))
            .ToList();
        if (targets.Count < 2)
        {
            StatusMessage = "グループ化には2個以上のクリップが必要です。Ctrl+クリックで複数選択してください。";
            return;
        }
        BeginEdit();
        ClipGroups.GroupClips(Project, targets);
        _multiSelectedClips.Clear();
        OnPropertyChanged(nameof(MultiSelectedClips));
        OnPropertyChanged(nameof(IsSelectedClipGrouped));
        NotifyTimelineChanged();
        StatusMessage = $"{targets.Count} 個のクリップをグループ化しました。ドラッグでまとめて移動・トリミングできます。";
    }

    /// <summary>グループ解除（Ctrl+Shift+G）。選択中クリップのグループ全体を解除する。</summary>
    public void UngroupSelectedClip()
    {
        if (SelectedClip is null || !ClipGroups.IsGrouped(SelectedClip))
            return;
        BeginEdit();
        var count = ClipGroups.Ungroup(Project, SelectedClip.GroupId);
        OnPropertyChanged(nameof(IsSelectedClipGrouped));
        NotifyTimelineChanged();
        StatusMessage = $"グループを解除しました（{count} 個のクリップ）。";
    }

    /// <summary>
    /// リップル削除（Shift+Del：同トラックのみ／allTracks: 全トラックの後続も詰まる）。
    /// 通常削除（Del）と区別する操作。選択中クリップがグループに属する場合は
    /// グループ全員をそれぞれのトラックでリップル削除する（グループ単位の削除）。
    /// マーカーは動かさない（ClipGroups / TimelineEditor の設計規則）。
    /// </summary>
    public void RippleDeleteSelectedClip(bool allTracks = false)
    {
        if (SelectedClip is null)
            return;
        var scope = allTracks ? RippleScope.AllTracks : RippleScope.SameTrack;
        BeginEdit();
        if (ClipGroups.IsGrouped(SelectedClip))
        {
            // グループ全員を消す。各トラックで開始の遅い順に処理すると、同じトラックの
            // グループ内後続が先に消え、詰め量の二重計算を避けられる。
            foreach (var member in ClipGroups.GetClips(Project, SelectedClip.GroupId).OrderByDescending(c => c.StartSeconds).ToList())
                TimelineEditor.RippleDeleteClip(Project, member, scope);
            StatusMessage = "グループをリップル削除しました（後続クリップが前に詰まります）。";
        }
        else
        {
            TimelineEditor.RippleDeleteClip(Project, SelectedClip, scope);
            StatusMessage = allTracks
                ? "リップル削除しました（全トラックの後続が前に詰まります）。"
                : "リップル削除しました（同じトラックの後続が前に詰まります）。";
        }
        _multiSelectedClips.RemoveAll(c => !Project.Tracks.Any(t => t.Clips.Contains(c)));
        OnPropertyChanged(nameof(MultiSelectedClips));
        ClipGroups.CleanupSingletonGroups(Project);
        SelectedClip = null;
        NotifyTimelineChanged();
    }

    // ---- マーカー（MV作業で曲の区切り・サビ・ドロップに印を付ける） ----

    /// <summary>マーカー一覧の表示・編集用行（時刻順）。</summary>
    public ObservableCollection<MarkerRow> MarkerRows { get; } = new();

    private MarkerRow? _selectedMarker;

    /// <summary>選択中のマーカー行（ルーラーでクリックしたもの・一覧で選んだもの）。null は未選択。</summary>
    public MarkerRow? SelectedMarker
    {
        get => _selectedMarker;
        set { _selectedMarker = value; OnPropertyChanged(); }
    }

    /// <summary>マーカー行を作り直す（時刻順）。選択中のマーカーが残っていれば選択を引き継ぐ。</summary>
    public void RefreshMarkerRows()
    {
        var selected = _selectedMarker?.Marker;
        MarkerRows.Clear();
        foreach (var marker in Project.Markers)
            MarkerRows.Add(new MarkerRow(this, marker));
        SelectedMarker = selected is null
            ? null
            : MarkerRows.FirstOrDefault(r => r.Marker == selected);
    }

    /// <summary>再生ヘッド位置にマーカーを追加する。名前は「マーカー N」（未使用の最小の番号）。</summary>
    public void AddMarkerAtPlayhead()
    {
        BeginEdit();
        var marker = Project.AddMarker(PlayheadSeconds, NextDefaultMarkerName());
        RefreshMarkerRows();
        SelectedMarker = MarkerRows.FirstOrDefault(r => r.Marker == marker);
        NotifyTimelineChanged();
        StatusMessage = $"マーカーを追加しました：{marker.Name}（{marker.TimeSeconds:F2}s）";
    }

    /// <summary>未使用の最小の番号で「マーカー N」を作る（削除で空いた番号は再利用する）。</summary>
    private string NextDefaultMarkerName()
    {
        var n = 1;
        while (Project.Markers.Any(m => m.Name == $"マーカー {n}"))
            n++;
        return $"マーカー {n}";
    }

    /// <summary>マーカーを削除する。</summary>
    public void DeleteMarker(MarkerRow row)
    {
        if (!Project.Markers.Contains(row.Marker))
            return;
        BeginEdit();
        var name = row.Marker.Name;
        Project.RemoveMarker(row.Marker);
        RefreshMarkerRows();
        NotifyTimelineChanged();
        StatusMessage = $"マーカーを削除しました：{name}";
    }

    /// <summary>選択中のマーカーを削除する。未選択なら何もしない。</summary>
    public void DeleteSelectedMarker()
    {
        if (SelectedMarker is not null)
            DeleteMarker(SelectedMarker);
    }

    /// <summary>指定マーカーの行を選択する（ルーラーでクリックしたとき）。行が無ければ何もしない。</summary>
    public void SelectMarker(Marker marker)
    {
        var row = MarkerRows.FirstOrDefault(r => r.Marker == marker);
        if (row is not null)
            SelectedMarker = row;
    }

    /// <summary>再生ヘッドを次のマーカーへ移動する。次のマーカーが無ければ何もしない。</summary>
    public void JumpToNextMarker()
    {
        var next = Project.NextMarkerAfter(PlayheadSeconds);
        if (next is null)
        {
            StatusMessage = "次のマーカーはありません。";
            return;
        }
        PlayheadSeconds = next.TimeSeconds;
        SelectMarker(next);
        StatusMessage = $"マーカー「{next.Name}」へ移動しました（{PlayheadSeconds:F2}s）。";
    }

    /// <summary>再生ヘッドを前のマーカーへ移動する。前のマーカーが無ければ何もしない。</summary>
    public void JumpToPreviousMarker()
    {
        var prev = Project.PreviousMarkerBefore(PlayheadSeconds);
        if (prev is null)
        {
            StatusMessage = "前のマーカーはありません。";
            return;
        }
        PlayheadSeconds = prev.TimeSeconds;
        SelectMarker(prev);
        StatusMessage = $"マーカー「{prev.Name}」へ移動しました（{PlayheadSeconds:F2}s）。";
    }

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

    /// <summary>選択中クリップの音連動バインド行を作り直す（追加した順）。ついでに対象パラメータの一覧も作り直す。</summary>
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

    /// <summary>元に戻せるか（アンドゥスタックが空でないか）。メニューの有効化に使う。</summary>
    public bool CanUndo => _session.CanUndo;

    /// <summary>やり直せるか（リドゥスタックが空でないか）。メニューの有効化に使う。</summary>
    public bool CanRedo => _session.CanRedo;

    /// <summary>
    /// 変更操作の直前に呼ぶ。変更前のプロジェクト全体を JSON でスナップショットし、
    /// アンドゥスタックに積む。新しい編集なのでリドゥスタックは空になる。
    /// ドラッグのような連続操作では、操作の開始時に1回だけ呼ぶこと。
    /// </summary>
    public void BeginEdit() => _session.BeginEdit();

    /// <summary>1つ前の状態に戻す。スタックが空なら何もしない。</summary>
    public void Undo() => _session.Undo();

    /// <summary>元に戻した操作をやり直す。スタックが空なら何もしない。</summary>
    public void Redo() => _session.Redo();

    /// <summary>プロジェクトの読み込み時に両スタックを空にする。</summary>
    public void ClearUndoHistory() => _session.ClearUndoHistory();

    /// <summary>プロジェクトをファイルへ保存する（名前を付けて保存にも使う）。</summary>
    public void SaveProject(string path) => _session.SaveProject(path);

    /// <summary>プロジェクトファイルを読み込む。自動保存ファイルがあれば復旧待ちにする。</summary>
    public void LoadProject(string path) => _session.LoadProject(path);

    // ---- プロジェクトテンプレート ----

    /// <summary>現在のプロジェクトをテンプレートとして保存する（同名があれば上書き。保存先は既定のテンプレートフォルダ）。</summary>
    public SaveTemplateResult SaveAsTemplate(string name) => TemplateStore.Save(name, Project);

    /// <summary>同名（大文字・小文字を区別しない）のテンプレートがあるか（上書き確認に使う）。</summary>
    public bool TemplateExists(string name) => TemplateStore.TemplateExists(name);

    /// <summary>テンプレートの一覧を取得する（名前順。壊れたファイルも「読み込めません」として含む）。</summary>
    public IReadOnlyList<TemplateInfo> ListTemplates() => TemplateStore.List();

    /// <summary>
    /// テンプレートから新しいプロジェクトを作る（未保存扱い）。
    /// 参照メディアが見つからないクリップは配置だけ残し、見つからなかったファイルの一覧を結果に含める。
    /// テンプレートファイルが壊れている場合は例外を投げる（現在のプロジェクトは変わらない）。
    /// </summary>
    public TemplateCreateResult NewProjectFromTemplate(string path)
    {
        var result = TemplateStore.CreateProject(path);
        _session.NewProjectFromTemplate(result.Project);
        return result;
    }

    /// <summary>本ファイルより新しい自動保存ファイルの内容でプロジェクトを復旧する。</summary>
    public void RecoverFromAutosave() => _session.RecoverFromAutosave();

    /// <summary>自動保存ファイルを破棄する（復旧しないと決めたとき）。</summary>
    public void DiscardAutosave() => _session.DiscardAutosave();

    // ---- 自動保存 ----

    /// <summary>アプリ設定（自動保存の ON/OFF と間隔）。%APPDATA% の settings.json に保存される。</summary>
    public AppSettings Settings => _session.Settings;

    /// <summary>現在開いているプロジェクトファイルのパス。未保存（新規）の場合は null（自動保存はしない）。</summary>
    public string? CurrentProjectPath => _session.CurrentProjectPath;

    /// <summary>プロジェクトファイルのパスが決まっているか（保存済み・開いたプロジェクトか）。</summary>
    public bool HasProjectPath => _session.HasProjectPath;

    /// <summary>自動保存する（既定はオン）。変えると設定ファイルに保存される（保存に失敗しても切り替え自体は有効）。</summary>
    public bool AutosaveEnabled
    {
        get => _session.AutosaveEnabled;
        set => _session.AutosaveEnabled = value;
    }

    /// <summary>自動保存の間隔（分、1〜60）。</summary>
    public int AutosaveIntervalMinutes => _session.AutosaveIntervalMinutes;

    /// <summary>自動保存の間隔を変える（1〜60分に丸める）。変えると設定ファイルに保存され、タイマーの間隔も変わる（保存に失敗しても変更自体は有効）。</summary>
    public void SetAutosaveIntervalMinutes(int minutes) => _session.SetAutosaveIntervalMinutes(minutes);

    /// <summary>自動保存の状態表示（ステータスバーに出す）。</summary>
    public string AutosaveStatus => _session.AutosaveStatus;

    /// <summary>本ファイルより新しい自動保存ファイルがあるか（「自動保存から復旧」メニューの有効化に使う）。</summary>
    public bool HasRecoveryAutosave => _session.HasRecoveryAutosave;

    /// <summary>自動保存ファイルについての説明文（例：更新日時と復旧方法）。無ければ空。</summary>
    public string AutosaveInfo => _session.AutosaveInfo;

    /// <summary>
    /// 今のプロジェクトを自動保存ファイルへ保存する（タイマー・終了時に呼ぶ）。
    /// 本ファイルは触らない。内容が前回の自動保存（または明示保存）と同じなら書き直さない。
    /// 復旧待ち・未保存・自動保存オフのときは何もしない。失敗しても例外は出さない。
    /// </summary>
    public void SaveAutosaveNow() => _session.SaveAutosaveNow();

    // ---- IProjectSessionHost / IDetectionHost の実装（セッション・検出からの委譲先） ----

    void IProjectSessionHost.SetProject(EditorProject project) => Project = project;

    void IProjectSessionHost.SetStatusMessage(string message) => StatusMessage = message;

    void IDetectionHost.SetStatusMessage(string message) => StatusMessage = message;

    void IProjectSessionHost.OnProjectPathChanged() => UpdateProjectCacheDirectories();

    /// <summary>
    /// 解析キャッシュの保存先を今のプロジェクトに合わせる。
    /// プロジェクトファイルの隣の「プロジェクト名.cache」フォルダ。
    /// 未保存の場合は null（メモリのみ・永続化しない）。
    /// </summary>
    private void UpdateProjectCacheDirectories()
    {
        var cacheDirectory = _session.CurrentProjectPath is null
            ? null
            : AudioAnalysisCache.GetCacheDirectory(_session.CurrentProjectPath);
        _bandProvider.CacheDirectory = cacheDirectory;
        _waveformCache.CacheDirectory = cacheDirectory;
    }

    /// <summary>タイムライン全体を MP4 に書き出す。バインドの音源解析が終わるまで待ってから描き始める。</summary>
    public async Task ExportTimelineAsync(
        string outputPath, ExportSettings settings,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        await EnsureBindingAnalysesAsync().ConfigureAwait(true);
        await _timelineExporter.ExportAsync(Project, outputPath, settings, progress, ct).ConfigureAwait(true);
    }

    /// <summary>単一クリップの区間書き出し（最初の経路の確認用）。逆再生クリップは逆向きに、速度変更クリップは速度を変えて書き出す（素材側の長さは SourceDurationSeconds）。</summary>
    public Task ExportSingleClipAsync(
        Clip clip, string outputPath, ExportSettings settings,
        IProgress<double>? progress = null, CancellationToken ct = default)
        => _exportService.ExportSingleClipAsync(
            clip.SourcePath, clip.InPointSeconds, clip.SourceDurationSeconds,
            outputPath, settings, progress, ct, clip.IsReversed, clip.Speed);

    public Task<SKBitmap> RenderPreviewAsync(
        double timeSeconds, (int Width, int Height)? maxDisplaySize = null,
        CancellationToken ct = default)
        => _previewRenderer.RenderPreviewAsync(Project, timeSeconds, PreviewResolution, maxDisplaySize, ct);

    public WaveformData? GetWaveform(string sourcePath)
        => Waveforms.TryGetValue(sourcePath, out var data) ? data : null;

    public void NotifyTimelineChanged()
    {
        // プロジェクトが変わったのでプレビューのキャッシュも捨てる。
        // 捨てないと、再生ヘッドが同じ時刻のままでは古いフレームが返ってしまう。
        _previewRenderer.Invalidate();
        OnPropertyChanged(nameof(Project));
        OnPropertyChanged(nameof(TransitionHint));
        OnPropertyChanged(nameof(AudioBindingHint));
        OnPropertyChanged(nameof(VisualizationHint));
        OnPropertyChanged(nameof(AudioSourceClips));
        PreviewInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private void AdvancePlayhead()
    {
        var next = PlayheadSeconds + 1.0 / Project.FrameRate;
        if (next >= Project.DurationSeconds && Project.DurationSeconds > 0)
        {
            PlayheadSeconds = Project.DurationSeconds;
            IsPlaying = false;
            return;
        }
        PlayheadSeconds = next;
    }

    /// <summary>再生ヘッド位置でアクティブな音声つきクリップの音を全て鳴らす。無ければ止める。逆再生クリップは逆向きに鳴らす。速度変更クリップはリサンプリングで速度を変えて鳴らす（ピッチも変わる。書き出しは atempo でピッチ保持のため、プレビューと書き出しでピッチだけが違う）。ノーマライズが有効なクリップはピークから計算したゲインを掛ける（書き出しと同じ計算・同じ提供口）。ピーク解析が終わるまで再生開始が少し遅れることがある（2回目以降はキャッシュ済み）。解析に失敗したクリップは元の音量で鳴らす。</summary>
    public async void StartAudioAtPlayhead()
    {
        var generation = ++_audioPlayGeneration;
        var actives = Project.Tracks
            .SelectMany(t => t.Clips)
            .Where(c => c.IsActiveAt(PlayheadSeconds) && !string.IsNullOrEmpty(c.SourcePath))
            .Select(c => (Clip: c, Media: MediaItems.FirstOrDefault(m => m.SourcePath == c.SourcePath)))
            .Where(x => x.Media is not null && x.Media.Info.HasAudio)
            .ToList();

        if (actives.Count == 0)
        {
            _audioPlayer.Stop();
            return;
        }

        // ノーマライズのゲインを全クリップ分まとめて測る（並列）。測れなければ等倍。
        var gains = await Task.WhenAll(actives.Select(async x =>
        {
            if (!x.Clip.NormalizeAudioEnabled)
                return 1.0;
            try
            {
                var peak = await _peakProvider.GetPeakAsync(
                    x.Clip.SourcePath, x.Clip.InPointSeconds, x.Clip.SourceDurationSeconds)
                    .ConfigureAwait(false);
                return peak.Available
                    ? AudioNormalizer.ComputeGain(peak.PeakLinear, x.Clip.NormalizeTargetDb)
                    : 1.0;
            }
            catch
            {
                return 1.0; // 解析に失敗しても再生は止めない（元の音量で鳴らす）。
            }
        })).ConfigureAwait(true);

        // フェード／ダッキング包絡も全クリップ分まとめて作る（書き出しと同じ
        // Core 計算・同じトリガー区間提供口）。ダッキング対象はトリガーの
        // 鳴っている区間を解析する。測れなければフェードだけが効く。
        var envelopes = await Task.WhenAll(actives.Select(async x =>
        {
            var needsDucking = x.Clip.DuckingRole == DuckingRole.Target;
            if (!needsDucking && x.Clip.AudioFadeInSeconds <= 0 && x.Clip.AudioFadeOutSeconds <= 0)
                return null;
            var triggerIntervals = needsDucking
                ? await CollectPreviewTriggerIntervalsAsync(x.Clip).ConfigureAwait(false)
                : (IReadOnlyList<AudioSegment>)Array.Empty<AudioSegment>();
            return AudioGainEnvelope.HasEnvelope(x.Clip, triggerIntervals)
                ? AudioGainEnvelope.BuildBreakpoints(x.Clip, triggerIntervals)
                : null;
        })).ConfigureAwait(true);

        // 解析を待っている間に停止されたり鳴らし直されたりしたら、古い再生は捨てる。
        if (!IsPlaying || generation != _audioPlayGeneration)
            return;

        var sources = actives.Select((x, i) => new AudioPreviewSource(
            x.Media!.SourcePath,
            x.Clip.GetSourceTimeSeconds(PlayheadSeconds),
            x.Clip.IsReversed,
            x.Clip.InPointSeconds,
            x.Clip.InPointSeconds + x.Clip.SourceDurationSeconds,
            gains[i],
            x.Clip.Speed,
            envelopes[i],
            PlayheadSeconds - x.Clip.StartSeconds)).ToList();

        try
        {
            _audioPlayer.Play(sources);
        }
        catch (Exception ex)
        {
            StatusMessage = $"音声の再生に失敗しました：{ex.Message}";
        }
    }

    /// <summary>
    /// プレビュー用：対象クリップに効くトリガー区間（タイムライン絶対秒）を集める。
    /// 書き出し（AudioExportPlanner）と同じ提供口・同じ Core 収集関数を使う。
    /// 解析に失敗したトリガーは区間なしとして扱い、再生は止めない。
    /// </summary>
    private async Task<List<AudioSegment>> CollectPreviewTriggerIntervalsAsync(Clip target)
    {
        var segmentsByTrigger = new Dictionary<string, IReadOnlyList<AudioSegment>>();
        foreach (var trigger in Project.Tracks.SelectMany(t => t.Clips)
                     .Where(c => c.DuckingRole == DuckingRole.Trigger && c.Id != target.Id
                                 && !string.IsNullOrEmpty(c.SourcePath)))
        {
            try
            {
                segmentsByTrigger[trigger.Id] = await _activityProvider
                    .GetActiveSegmentsAsync(trigger.SourcePath, trigger.InPointSeconds,
                        trigger.SourceDurationSeconds, target.DuckingThresholdDb)
                    .ConfigureAwait(false);
            }
            catch
            {
                segmentsByTrigger[trigger.Id] = Array.Empty<AudioSegment>();
            }
        }
        return AudioGainEnvelope.CollectTriggerIntervals(
            Project, target,
            trigger => segmentsByTrigger.TryGetValue(trigger.Id, out var s) ? s : Array.Empty<AudioSegment>());
    }

    /// <summary>再生中なら音声を再生ヘッド位置から鳴らし直す（シーク後に呼ぶ）。</summary>
    public void RestartAudioIfPlaying()
    {
        if (IsPlaying)
            StartAudioAtPlayhead();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
