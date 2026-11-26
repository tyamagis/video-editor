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

public sealed partial class MainViewModel : INotifyPropertyChanged, IProjectSessionHost, IDetectionHost
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

    /// <summary>選択中クリップの音連動バインド行を作り直す（追加した順）。ついでに対象パラメータの一覧も作り直す。</summary>

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

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
