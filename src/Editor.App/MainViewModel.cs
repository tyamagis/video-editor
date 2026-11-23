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

/// <summary>素材プールに表示する1素材。</summary>
public sealed class MediaItem
{
    public required string SourcePath { get; init; }
    public required MediaInfo Info { get; init; }
    public string Name => Path.GetFileName(SourcePath);
    public string Display => $"{Name}  ({Info.Duration:mm\\:ss}, {Info.Width}x{Info.Height})";
}

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
            _owner.NotifyTimelineChanged();
        }
    }

    /// <summary>値。不透明度だけ 0〜1 に丸める（描画側でも丸めている）。</summary>
    public double Value
    {
        get => Keyframe.Value;
        set
        {
            var clamped = _parameter == "Opacity" ? Math.Clamp(value, 0, 1) : value;
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
            OnPropertyChanged(nameof(ShowKeyColor));
            _owner.NotifyTimelineChanged();
        }
    }

    /// <summary>主パラメータ。種類ごとに意味と範囲が違う（ClipEffect の説明を参照）。</summary>
    public double Amount
    {
        get => Effect.Amount;
        set
        {
            var clamped = ClampAmount(Effect.Kind, value);
            if (Math.Abs(Effect.Amount - clamped) < 1e-9)
                return;
            Effect.Amount = clamped;
            OnPropertyChanged();
            _owner.NotifyTimelineChanged();
        }
    }

    /// <summary>副パラメータ（グロー：ぼかし半径、ワープ：波長、クロマキー：なめらかさ）。</summary>
    public double Radius
    {
        get => Effect.Radius;
        set
        {
            var clamped = ClampRadius(Effect.Kind, value);
            if (Math.Abs(Effect.Radius - clamped) < 1e-9)
                return;
            Effect.Radius = clamped;
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
    public string AmountLabel => Effect.Kind switch
    {
        EffectKind.Blur => "半径",
        EffectKind.Glow => "強さ",
        EffectKind.RgbShift => "ずれ",
        EffectKind.Slice => "ずれ幅",
        EffectKind.Warp => "振幅",
        EffectKind.ChromaKey => "しきい値",
        _ => "量"
    };

    /// <summary>副パラメータ欄の見出し（種類ごと）。</summary>
    public string RadiusLabel => Effect.Kind switch
    {
        EffectKind.Glow => "ぼかし半径",
        EffectKind.Warp => "波長",
        EffectKind.ChromaKey => "なめらかさ",
        _ => string.Empty
    };

    /// <summary>副パラメータ欄を表示するか（グロー・ワープ・クロマキーだけ使う）。</summary>
    public bool ShowRadius => Effect.Kind is EffectKind.Glow or EffectKind.Warp or EffectKind.ChromaKey;

    /// <summary>キー色欄を表示するか（クロマキーだけ使う）。</summary>
    public bool ShowKeyColor => Effect.Kind == EffectKind.ChromaKey;

    private static double ClampAmount(EffectKind kind, double value) => kind switch
    {
        EffectKind.Blur => Math.Clamp(value, 0, 100),
        EffectKind.Glow => Math.Clamp(value, 0, 1),
        EffectKind.RgbShift => Math.Clamp(value, 0, 50),
        EffectKind.Slice => Math.Clamp(value, 0, 100),
        EffectKind.Warp => Math.Clamp(value, 0, 100),
        EffectKind.ChromaKey => Math.Clamp(value, 0, 1),
        _ => value
    };

    private static double ClampRadius(EffectKind kind, double value) => kind switch
    {
        EffectKind.Glow => Math.Clamp(value, 1, 100),
        EffectKind.Warp => Math.Clamp(value, 20, 1000),
        EffectKind.ChromaKey => Math.Clamp(value, 0, 1),
        _ => value
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

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

    /// <summary>動かすパラメータ。離散的な変更なので、代入前にアンドゥ用スナップショットを取る。</summary>
    public string ParameterName
    {
        get => Binding.ParameterName;
        set
        {
            if (Binding.ParameterName == value)
                return;
            _owner.BeginEdit();
            Binding.ParameterName = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ParameterLabel));
            _owner.NotifyTimelineChanged();
        }
    }

    /// <summary>パラメータの表示名。</summary>
    public string ParameterLabel => ParameterName switch
    {
        "Opacity" => "不透明度",
        "Scale" => "拡大率",
        "Rotation" => "回転",
        "PositionX" => "位置 X",
        "PositionY" => "位置 Y",
        _ => ParameterName
    };

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

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly MediaProbe _mediaProbe;
    private readonly ThumbnailService _thumbnailService;
    private readonly ExportService _exportService;
    private readonly WaveformReader _waveformReader;
    private readonly PreviewRenderer _previewRenderer;
    private readonly ITimelineExporter _timelineExporter;
    private readonly FfmpegSettings _ffmpegSettings;
    private readonly AudioBeatAnalyzer _beatAnalyzer;
    private readonly AudioBandEnergyProvider _bandProvider;
    private readonly SilenceDetector _silenceDetector = new();
    private readonly Dispatcher _uiDispatcher;

    private readonly DispatcherTimer _playTimer;
    private readonly AudioPreviewPlayer _audioPlayer = new();
    private EditorProject _project = new();
    private Clip? _selectedClip;
    private MediaItem? _selectedMediaItem;
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

    /// <summary>無音検出の実行中か（実行中は検出ボタンを無効化する）。</summary>
    private bool _isDetectingSilence;

    /// <summary>検出済みで未適用の無音区間（クリップのイン点からの相対秒）。</summary>
    private SilenceDetectionResult? _pendingSilence;

    /// <summary>保留中の無音検出の対象クリップ。プロジェクトから無くなれば適用できない。</summary>
    private Clip? _pendingSilenceClip;

    /// <summary>保留中の無音検出時のクリップのイン点（秒）。検出後にトリミングされていたら適用しない。</summary>
    private double _pendingSilenceInPoint;

    /// <summary>保留中の無音検出時のクリップの長さ（秒）。検出後に長さが変わっていたら適用しない。</summary>
    private double _pendingSilenceDuration;

    /// <summary>無音検出のしきい値（dBFS）。既定 -40dB。</summary>
    private double _silenceThresholdDb = -40;

    /// <summary>無音区間とみなす最短の長さ（秒）。既定 0.5 秒。</summary>
    private double _silenceMinSeconds = 0.5;

    /// <summary>無音区間の前後に残す余白（秒）。既定 0.1 秒。</summary>
    private double _silenceMarginSeconds = 0.1;

    /// <summary>BPM検出の実行中か（実行中は検出ボタンを無効化する）。</summary>
    private bool _isDetectingBpm;

    /// <summary>検出済みで未適用の結果（素材内時刻）。適用時にタイムライン時刻へ変換する。</summary>
    private BeatDetectionResult? _pendingDetection;

    /// <summary>保留中の検出結果の対象クリップ。プロジェクトから無くなれば適用できない。</summary>
    private Clip? _pendingDetectionClip;

    /// <summary>適用済みの検出ビートマップ（タイムライン時刻）。拍グリッド表示が使う。</summary>
    private BeatMap? _detectedBeatMap;

    /// <summary>アンドゥ／リドゥの上限（スナップショット件数）。</summary>
    private const int MaxUndoSteps = 50;

    /// <summary>アンドゥ用スナップショット（プロジェクト全体のJSON）。末尾が最新の変更前状態。</summary>
    private readonly List<string> _undoStack = new();

    /// <summary>リドゥ用スナップショット（プロジェクト全体のJSON）。末尾が最新の変更前状態。</summary>
    private readonly List<string> _redoStack = new();

    /// <summary>波形データのキャッシュ（素材パス → 波形）。表示切替で消さない。</summary>
    public Dictionary<string, WaveformData> Waveforms { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>プレビューを描き直す合図。MainWindow が購読する。</summary>
    public event EventHandler? PreviewInvalidated;

    public MainViewModel(
        MediaProbe mediaProbe,
        ThumbnailService thumbnailService,
        ExportService exportService,
        WaveformReader waveformReader,
        PreviewRenderer previewRenderer,
        ITimelineExporter timelineExporter,
        FfmpegSettings ffmpegSettings,
        AudioBeatAnalyzer beatAnalyzer,
        AudioBandEnergyProvider bandProvider)
    {
        _mediaProbe = mediaProbe;
        _thumbnailService = thumbnailService;
        _exportService = exportService;
        _waveformReader = waveformReader;
        _previewRenderer = previewRenderer;
        _timelineExporter = timelineExporter;
        _ffmpegSettings = ffmpegSettings;
        _beatAnalyzer = beatAnalyzer;
        _bandProvider = bandProvider;
        _uiDispatcher = Dispatcher.CurrentDispatcher;

        _playTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0 / 30) };
        _playTimer.Tick += (_, _) => AdvancePlayhead();

        PlayPauseCommand = new RelayCommand(_ => IsPlaying = !IsPlaying);
        SplitCommand = new RelayCommand(_ => SplitAtPlayhead());
        DeleteClipCommand = new RelayCommand(_ => DeleteSelectedClip(), _ => SelectedClip is not null);
        AddTextClipCommand = new RelayCommand(_ => AddTextClip("歌詞テロップ"));
    }

    public EditorProject Project
    {
        get => _project;
        private set
        {
            // プロジェクトが変われば検出結果は合わなくなるので消す。
            ClearDetectionState();
            lock (_analysisTasks)
            {
                _analysisTasks.Clear(); // 解析タスクも新しいプロジェクトのクリップIdで取り直す。
            }
            _project = value;
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
            OnPropertyChanged(nameof(SelectedClipBrightness));
            OnPropertyChanged(nameof(SelectedClipContrast));
            OnPropertyChanged(nameof(SelectedClipTransitionKind));
            OnPropertyChanged(nameof(SelectedClipTransitionDurationSeconds));
            OnPropertyChanged(nameof(TransitionHint));
            OnPropertyChanged(nameof(SelectedClipBlendMode));
            OnPropertyChanged(nameof(IsAudioClipSelected));
            OnPropertyChanged(nameof(IsVideoClipSelected));
            OnPropertyChanged(nameof(HasVisualEffects));
            OnPropertyChanged(nameof(HasAudioClip));
            OnPropertyChanged(nameof(CanSilenceCut));
            OnPropertyChanged(nameof(SelectedClipNoiseReductionEnabled));
            OnPropertyChanged(nameof(SelectedClipNoiseReductionStrength));
            OnPropertyChanged(nameof(CanAddKeyframeAtPlayhead));
            OnPropertyChanged(nameof(AudioBindingHint));
            RefreshKeyframeRows();
            RefreshEffectRows();
            RefreshAudioBindingRows();
        }
    }

    /// <summary>クリップが選択されているか。インスペクタの編集欄の有効化に使う。</summary>
    public bool HasSelectedClip => SelectedClip is not null;

    /// <summary>選択中クリップが音声クリップか（映像エフェクトは適用されない）。</summary>
    public bool IsAudioClipSelected => SelectedClip?.Kind == ClipKind.Audio;

    /// <summary>選択中クリップが映像クリップか（無音カットで映像も一緒に切れる旨の表示に使う）。</summary>
    public bool IsVideoClipSelected => SelectedClip?.Kind == ClipKind.Video;

    /// <summary>映像エフェクト欄を有効にするか（音声クリップでは無効）。</summary>
    public bool HasVisualEffects => SelectedClip is not null && SelectedClip.Kind != ClipKind.Audio;

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

    /// <summary>キーフレーム編集で選んでいるパラメータ（"Opacity" など）。</summary>
    public string KeyframeParameter
    {
        get => _keyframeParameter;
        set
        {
            if (_keyframeParameter == value)
                return;
            _keyframeParameter = value;
            OnPropertyChanged();
            RefreshKeyframeRows();
        }
    }

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
    public bool IsDetectingBpm
    {
        get => _isDetectingBpm;
        private set
        {
            _isDetectingBpm = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanDetectBpm));
        }
    }

    /// <summary>BPM検出ボタンを押せるか（検出中は押せない）。</summary>
    public bool CanDetectBpm => !IsDetectingBpm;

    /// <summary>未適用の検出結果があるか（「適用」ボタンの有効化に使う）。</summary>
    public bool HasPendingDetection => _pendingDetection is not null;

    /// <summary>検出結果の表示（例：検出：120.3 BPM（信頼度 87%）・拍 128個）。未検出時は空。</summary>
    public string DetectedBpmText
    {
        get
        {
            if (_pendingDetection?.Bpm is not { } bpm)
                return string.Empty;
            return $"検出：{bpm:F1} BPM（信頼度 {_pendingDetection.Confidence:P0}）・拍 {_pendingDetection.BeatTimes.Count}個";
        }
    }

    /// <summary>
    /// 適用済みの検出ビートマップ（タイムライン時刻）。拍グリッド表示は、
    /// これがあるときは BPM からの算出ではなくこの拍時刻を描く。
    /// </summary>
    public BeatMap? DetectedBeatMap
    {
        get => _detectedBeatMap;
        private set
        {
            if (_detectedBeatMap == value)
                return;
            _detectedBeatMap = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// BPMを自動検出する。対象は選択中の音声クリップ（または音声つき映像クリップ）、
    /// 無ければプロジェクト内の最初の音声クリップ、次に最初の映像クリップ。
    /// 結果は保留し、「適用」でプロジェクトへ反映する。検出中は UI を固めない。
    /// </summary>
    public async Task DetectBpmAsync()
    {
        if (IsDetectingBpm)
            return;

        var clip = FindDetectionTargetClip();
        if (clip is null)
        {
            StatusMessage = "BPMを検出するには、音声クリップまたは映像クリップをタイムラインに置いてください。";
            return;
        }

        IsDetectingBpm = true;
        StatusMessage = $"BPMを検出中…：{clip.Name}";
        try
        {
            var result = await _beatAnalyzer.AnalyzeAsync(clip.SourcePath).ConfigureAwait(true);
            if (!result.Detected)
            {
                SetPendingDetection(null, null);
                StatusMessage = $"BPMを検出できませんでした：{result.FailureReason}";
                return;
            }
            SetPendingDetection(result, clip);
            StatusMessage = $"BPMを検出しました：{result.Bpm:F1}（信頼度 {result.Confidence:P0}）。「適用」を押すとプロジェクトに反映されます。";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "BPMの検出をキャンセルしました。";
        }
        catch (Exception ex)
        {
            SetPendingDetection(null, null);
            StatusMessage = $"BPMの検出に失敗しました：{ex.Message}";
        }
        finally
        {
            IsDetectingBpm = false;
        }
    }

    /// <summary>
    /// 保留中の検出結果をプロジェクトへ反映する。BPM と拍オフセットを設定し、
    /// 検出した拍時刻をビートマップとして保持する（アンドゥ対象）。
    /// </summary>
    public void ApplyDetectedBpm()
    {
        var result = _pendingDetection;
        var clip = _pendingDetectionClip;
        if (result is null || result.Bpm is null)
        {
            StatusMessage = "適用する検出結果がありません。先に「BPMを検出」を押してください。";
            return;
        }
        // 検出後にクリップがプロジェクトから無くなっていたら適用しない。
        if (clip is null || !Project.Tracks.SelectMany(t => t.Clips).Contains(clip))
        {
            SetPendingDetection(null, null);
            StatusMessage = "検出対象のクリップが無くなったため、適用できませんでした。もう一度検出してください。";
            return;
        }

        BeginEdit();

        // 素材内時刻をタイムライン時刻に変換する。
        var shift = clip.StartSeconds - clip.InPointSeconds;
        var rawOffset = clip.StartSeconds + (result.OffsetSeconds - clip.InPointSeconds);
        // 拍オフセットは 0〜1周期の範囲に正規化する（負の場合は周期を足して合わせる）。
        // 0 で丸めるだけだと、イン点が大きいときに拍グリッドと検出拍がずれてしまう。
        var period = 60.0 / result.Bpm.Value;
        var offset = rawOffset - Math.Floor(rawOffset / period) * period;
        var beatTimes = result.BeatTimes
            .Select(t => t + shift)
            .Where(t => t >= clip.StartSeconds - 1e-6 && t <= clip.EndSeconds + 1e-6)
            .Select(t => Math.Clamp(t, clip.StartSeconds, clip.EndSeconds))
            .ToList();

        // Bpm セッターは検出済みビートマップを消すので、先に BPM・オフセットを設定する。
        Bpm = result.Bpm.Value;
        Project.BeatOffsetSeconds = offset;
        DetectedBeatMap = new BeatMap
        {
            Source = BeatMapSource.WaveformDetected,
            Bpm = result.Bpm.Value,
            OffsetSeconds = Project.BeatOffsetSeconds,
            BeatTimes = beatTimes
        };
        SetPendingDetection(null, null);
        NotifyTimelineChanged();
        StatusMessage = $"BPM {result.Bpm:F1} と拍 {beatTimes.Count}個を反映しました（{clip.Name}）。";
    }

    /// <summary>検出対象のクリップを選ぶ。選択中クリップを優先する。</summary>
    private Clip? FindDetectionTargetClip()
    {
        var selected = SelectedClip;
        if (selected is not null
            && (selected.Kind == ClipKind.Audio || selected.Kind == ClipKind.Video)
            && !string.IsNullOrEmpty(selected.SourcePath))
            return selected;

        foreach (var track in Project.Tracks)
            foreach (var clip in track.Clips)
                if (clip.Kind == ClipKind.Audio && !string.IsNullOrEmpty(clip.SourcePath))
                    return clip;

        foreach (var track in Project.Tracks)
            foreach (var clip in track.Clips)
                if (clip.Kind == ClipKind.Video && !string.IsNullOrEmpty(clip.SourcePath))
                    return clip;

        return null;
    }

    /// <summary>無音検出の実行中か。</summary>
    public bool IsDetectingSilence
    {
        get => _isDetectingSilence;
        private set
        {
            _isDetectingSilence = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanDetectSilence));
            OnPropertyChanged(nameof(CanSilenceCut));
        }
    }

    /// <summary>無音検出ボタンを押せるか（検出中は押せない）。</summary>
    public bool CanDetectSilence => !IsDetectingSilence;

    /// <summary>無音とみなすしきい値（dBFS、-90〜-10）。検出パラメータで、プロジェクトには保存しない。</summary>
    public double SilenceThresholdDb
    {
        get => _silenceThresholdDb;
        set
        {
            var clamped = Math.Clamp(value, -90, -10);
            if (Math.Abs(_silenceThresholdDb - clamped) < 1e-9)
                return;
            _silenceThresholdDb = clamped;
            OnPropertyChanged();
        }
    }

    /// <summary>無音区間とみなす最短の長さ（秒、0.1〜10）。</summary>
    public double SilenceMinSeconds
    {
        get => _silenceMinSeconds;
        set
        {
            var clamped = Math.Clamp(value, 0.1, 10);
            if (Math.Abs(_silenceMinSeconds - clamped) < 1e-9)
                return;
            _silenceMinSeconds = clamped;
            OnPropertyChanged();
        }
    }

    /// <summary>無音区間の前後に残す余白（秒、0〜1）。</summary>
    public double SilenceMarginSeconds
    {
        get => _silenceMarginSeconds;
        set
        {
            var clamped = Math.Clamp(value, 0, 1);
            if (Math.Abs(_silenceMarginSeconds - clamped) < 1e-9)
                return;
            _silenceMarginSeconds = clamped;
            OnPropertyChanged();
        }
    }

    /// <summary>未適用の無音検出結果があるか（「適用」ボタンの有効化に使う）。</summary>
    public bool HasPendingSilence => _pendingSilence is not null;

    /// <summary>無音検出結果の表示（例：無音区間 12 個、合計 34.5 秒）。未検出時は空。</summary>
    public string SilenceDetectionText
    {
        get
        {
            if (_pendingSilence is null)
                return string.Empty;
            if (_pendingSilence.Segments.Count == 0)
                return "無音区間は見つかりませんでした。";
            var longest = _pendingSilence.Segments.Max(s => s.DurationSeconds);
            return $"無音区間 {_pendingSilence.Segments.Count} 個、合計 {_pendingSilence.TotalSilenceSeconds:F1} 秒（最長 {longest:F1} 秒）";
        }
    }

    /// <summary>
    /// 選択中クリップの無音区間を検出する。対象は音声クリップ、または音声つき映像クリップ
    /// （映像クリップの場合は映像も一緒に切れる）。検出は非同期で、結果は保留し、
    /// 「適用」でプロジェクトへ反映する。検出中は UI を固めない。
    /// </summary>
    public async Task DetectSilenceAsync()
    {
        if (IsDetectingSilence)
            return;

        var clip = SelectedClip;
        if (clip is null || (clip.Kind != ClipKind.Audio && clip.Kind != ClipKind.Video)
            || string.IsNullOrEmpty(clip.SourcePath))
        {
            StatusMessage = "無音カットするには、音声クリップまたは映像クリップを選択してください。";
            return;
        }

        IsDetectingSilence = true;
        StatusMessage = $"無音区間を検出中…：{clip.Name}";
        try
        {
            var settings = new SilenceDetectionSettings
            {
                ThresholdDb = SilenceThresholdDb,
                MinSilenceSeconds = SilenceMinSeconds,
                MarginSeconds = SilenceMarginSeconds
            };
            var result = await _silenceDetector.AnalyzeAsync(
                clip.SourcePath, clip.InPointSeconds, clip.DurationSeconds, settings)
                .ConfigureAwait(true);
            if (!result.Detected)
            {
                SetPendingSilence(null, null);
                StatusMessage = $"無音区間を検出できませんでした：{result.FailureReason}";
                return;
            }
            SetPendingSilence(result, clip);
            StatusMessage = result.Segments.Count == 0
                ? "無音区間は見つかりませんでした。"
                : $"無音区間を検出しました：{result.Segments.Count} 個、合計 {result.TotalSilenceSeconds:F1} 秒。「適用」を押すと切り取ります。";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "無音区間の検出をキャンセルしました。";
        }
        catch (Exception ex)
        {
            SetPendingSilence(null, null);
            StatusMessage = $"無音区間の検出に失敗しました：{ex.Message}";
        }
        finally
        {
            IsDetectingSilence = false;
        }
    }

    /// <summary>
    /// 保留中の無音検出結果をプロジェクトへ反映する。無音区間の前後でクリップを分割し、
    /// 無音部分を削除して、同じトラックの後ろのクリップを前に詰める。
    /// 分割・削除・詰めを1回の操作として1つのアンドゥ・スナップショットにする。
    /// 映像クリップの場合は映像も一緒に切れる。
    /// </summary>
    public void ApplySilenceCut()
    {
        var result = _pendingSilence;
        var clip = _pendingSilenceClip;
        if (result is null)
        {
            StatusMessage = "適用する検出結果がありません。先に「検出」を押してください。";
            return;
        }
        // 検出後にクリップがプロジェクトから無くなっていたら適用しない。
        if (clip is null || !Project.Tracks.SelectMany(t => t.Clips).Contains(clip))
        {
            SetPendingSilence(null, null);
            StatusMessage = "検出対象のクリップが無くなったため、適用できませんでした。もう一度検出してください。";
            return;
        }
        // 検出後にクリップをトリミングしていたら、検出区間とクリップの対応がずれるため適用しない。
        if (Math.Abs(clip.InPointSeconds - _pendingSilenceInPoint) > 1e-6
            || Math.Abs(clip.DurationSeconds - _pendingSilenceDuration) > 1e-6)
        {
            SetPendingSilence(null, null);
            StatusMessage = "検出後にクリップが変更されたため、適用できませんでした。もう一度検出してください。";
            return;
        }
        if (result.Segments.Count == 0)
        {
            StatusMessage = "切る無音区間がありません。";
            return;
        }
        var track = Project.Tracks.FirstOrDefault(t => t.Clips.Contains(clip));
        if (track is null)
        {
            SetPendingSilence(null, null);
            StatusMessage = "検出対象のクリップが無くなったため、適用できませんでした。";
            return;
        }

        // 検出した区間（イン点からの相対秒）をタイムライン時刻に変換する。
        var segments = result.Segments
            .Select(s => (StartSeconds: clip.StartSeconds + s.StartSeconds,
                          EndSeconds: clip.StartSeconds + s.EndSeconds))
            .ToList();

        BeginEdit();
        var removed = TimelineEditor.CutSilenceRanges(track, clip, segments);
        // クリップ自体が消えた場合（全体が無音だった場合）は選択を外す。
        if (!Project.Tracks.SelectMany(t => t.Clips).Contains(clip))
            SelectedClip = null;
        SetPendingSilence(null, null);
        NotifyTimelineChanged();
        StatusMessage = $"無音区間 {removed} 個を切り取りました（合計 {result.TotalSilenceSeconds:F1} 秒）。";
    }

    /// <summary>保留中の無音検出結果を差し替え、関連する表示を更新する。</summary>
    private void SetPendingSilence(SilenceDetectionResult? result, Clip? clip)
    {
        _pendingSilence = result;
        _pendingSilenceClip = clip;
        // 検出時のクリップの形を記録する（適用時に変わっていたら適用しない）。
        _pendingSilenceInPoint = clip?.InPointSeconds ?? 0;
        _pendingSilenceDuration = clip?.DurationSeconds ?? 0;
        OnPropertyChanged(nameof(HasPendingSilence));
        OnPropertyChanged(nameof(SilenceDetectionText));
    }

    /// <summary>保留中の検出結果を差し替え、関連する表示を更新する。</summary>
    private void SetPendingDetection(BeatDetectionResult? result, Clip? clip)
    {
        _pendingDetection = result;
        _pendingDetectionClip = clip;
        OnPropertyChanged(nameof(HasPendingDetection));
        OnPropertyChanged(nameof(DetectedBpmText));
    }

    /// <summary>保留中の検出結果と適用済みビートマップを消す（プロジェクト切替・読込時など）。</summary>
    private void ClearDetectionState()
    {
        SetPendingDetection(null, null);
        SetPendingSilence(null, null);
        DetectedBeatMap = null;
    }

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
            var waveform = await Task.Run(() => _waveformReader.Read(path)).ConfigureAwait(true);
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
        SelectedClip = null;
        NotifyTimelineChanged();
    }

    /// <summary>選択中クリップ・選択中パラメータのキーフレーム行を作り直す（時刻順）。</summary>
    public void RefreshKeyframeRows()
    {
        KeyframeRows.Clear();
        var clip = SelectedClip;
        if (clip is null)
            return;
        var track = clip.KeyframeTracks.FirstOrDefault(t => t.ParameterName == KeyframeParameter);
        if (track is null)
            return;
        foreach (var keyframe in track.Keyframes.OrderBy(k => k.TimeSeconds))
            KeyframeRows.Add(new KeyframeRow(this, clip, KeyframeParameter, keyframe));
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
        var value = clip.EvaluateParameter(KeyframeParameter, local, defaultValue);
        var track = clip.EnsureTrack(KeyframeParameter);
        track.Keyframes.Add(new Keyframe(local, value));
        track.Keyframes.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
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
        track.Keyframes.Remove(row.Keyframe);
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
        if (clip is null || clip.Kind == ClipKind.Audio)
            return;
        if (clip.Effects.Any(e => e.Kind == kind))
        {
            StatusMessage = "そのエフェクトは既に付いています。";
            return;
        }

        BeginEdit();
        clip.Effects.Add(CreateEffect(kind));
        RefreshEffectRows();
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
        NotifyTimelineChanged();
    }

    /// <summary>種類ごとの既定パラメータでエフェクトを作る。</summary>
    private static ClipEffect CreateEffect(EffectKind kind) => kind switch
    {
        EffectKind.Blur => new ClipEffect { Kind = kind, Amount = 8 },
        EffectKind.Glow => new ClipEffect { Kind = kind, Amount = 0.5, Radius = 16 },
        EffectKind.RgbShift => new ClipEffect { Kind = kind, Amount = 6 },
        EffectKind.Slice => new ClipEffect { Kind = kind, Amount = 24 },
        EffectKind.Warp => new ClipEffect { Kind = kind, Amount = 20, Radius = 240 },
        EffectKind.ChromaKey => new ClipEffect { Kind = kind, Amount = 0.30, Radius = 0.15, KeyColorHex = "#00FF00" },
        _ => new ClipEffect { Kind = kind }
    };

    /// <summary>エフェクト種類の表示名。</summary>
    private static string EffectDisplayName(EffectKind kind) => kind switch
    {
        EffectKind.Blur => "ブラー",
        EffectKind.Glow => "グロー",
        EffectKind.RgbShift => "RGBシフト",
        EffectKind.Slice => "スライス",
        EffectKind.Warp => "ワープ（波）",
        EffectKind.ChromaKey => "クロマキー",
        _ => kind.ToString()
    };

    /// <summary>選択中クリップの音連動バインド行を作り直す（追加した順）。</summary>
    public void RefreshAudioBindingRows()
    {
        AudioBindingRows.Clear();
        var clip = SelectedClip;
        if (clip is null)
            return;
        foreach (var binding in clip.AudioBindings)
            AudioBindingRows.Add(new AudioBindingRow(this, binding));
    }

    /// <summary>選択中クリップの音連動バインド行。</summary>
    public ObservableCollection<AudioBindingRow> AudioBindingRows { get; } = new();

    /// <summary>「音連動バインドを追加」で選んでいるパラメータ（既定は拡大率）。</summary>
    public string NewAudioBindingParameter
    {
        get => _newAudioBindingParameter;
        set
        {
            if (_newAudioBindingParameter == value)
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
                return "音の帯域に合わせてパラメータを動かせます（例：20-80Hz で拡大率）。";
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
        var (combine, min, max) = DefaultBindingRange(parameter);
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

    /// <summary>パラメータごとのバインド既定値（合成方法・出力範囲）。拡大率は設計プランの例（100%〜112%）に合わせる。</summary>
    private static (AudioBindCombine Combine, double Min, double Max) DefaultBindingRange(string parameter) => parameter switch
    {
        "Opacity" => (AudioBindCombine.Override, 0.0, 1.0),
        "Scale" => (AudioBindCombine.Add, 1.0, 1.12),
        "Rotation" => (AudioBindCombine.Add, -10.0, 10.0),
        "PositionX" => (AudioBindCombine.Add, -100.0, 100.0),
        "PositionY" => (AudioBindCombine.Add, -100.0, 100.0),
        _ => (AudioBindCombine.Add, 0.0, 1.0)
    };

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
    /// バインドの音源になっている全クリップの音声解析が終わるまで待つ（書き出し前など）。
    /// 解析に失敗しても例外は出さない（そのバインドは無音扱いになる）。
    /// </summary>
    public async Task EnsureBindingAnalysesAsync()
    {
        var sources = Project.Tracks.SelectMany(t => t.Clips)
            .SelectMany(c => c.AudioBindings)
            .Where(b => b.Enabled && !string.IsNullOrEmpty(b.SourceClipId))
            .Select(b => b.SourceClipId)
            .Distinct()
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
                // 解析失敗はバインドが無音扱いになるだけ。StatusMessage で通知済み。
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
        var sourceTime = clip.InPointSeconds + (PlayheadSeconds - clip.StartSeconds);
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
        var sourceTime = clip.InPointSeconds + (PlayheadSeconds - clip.StartSeconds);
        return _bandProvider.GetStereoWindow(clip.Id, sourceTime, LissajousWindowSeconds);
    }

    /// <summary>元に戻せるか（アンドゥスタックが空でないか）。メニューの有効化に使う。</summary>
    public bool CanUndo => _undoStack.Count > 0;

    /// <summary>やり直せるか（リドゥスタックが空でないか）。メニューの有効化に使う。</summary>
    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>
    /// 変更操作の直前に呼ぶ。変更前のプロジェクト全体を JSON でスナップショットし、
    /// アンドゥスタックに積む。新しい編集なのでリドゥスタックは空になる。
    /// ドラッグのような連続操作では、操作の開始時に1回だけ呼ぶこと。
    /// </summary>
    public void BeginEdit()
    {
        _undoStack.Add(ProjectStore.Serialize(Project));
        if (_undoStack.Count > MaxUndoSteps)
            _undoStack.RemoveAt(0); // 古い方から捨てる。
        _redoStack.Clear();
        RaiseUndoStateChanged();
    }

    /// <summary>1つ前の状態に戻す。スタックが空なら何もしない。</summary>
    public void Undo()
    {
        if (_undoStack.Count == 0)
            return;
        _redoStack.Add(ProjectStore.Serialize(Project));
        var json = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        RestoreSnapshot(json);
        StatusMessage = "元に戻しました。";
    }

    /// <summary>元に戻した操作をやり直す。スタックが空なら何もしない。</summary>
    public void Redo()
    {
        if (_redoStack.Count == 0)
            return;
        _undoStack.Add(ProjectStore.Serialize(Project));
        var json = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        RestoreSnapshot(json);
        StatusMessage = "やり直しました。";
    }

    /// <summary>プロジェクトの読み込み時に両スタックを空にする。</summary>
    public void ClearUndoHistory()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        RaiseUndoStateChanged();
    }

    /// <summary>スナップショットからプロジェクトを復元し、画面を更新する。</summary>
    private void RestoreSnapshot(string json)
    {
        IsPlaying = false; // 再生中なら止める（復元したプロジェクトと音声がずれるため）。
        Project = ProjectStore.Deserialize(json);
        SelectedClip = null; // 復元前のクリップ参照は古くなるので選択を外す。
        // 再生ヘッドが復元後の長さを超えていたら末尾に丸める。
        PlayheadSeconds = Math.Min(PlayheadSeconds, Project.DurationSeconds);
        RaiseUndoStateChanged();
    }

    private void RaiseUndoStateChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    public void SaveProject(string path)
    {
        ProjectStore.Save(path, Project);
        StatusMessage = $"保存しました：{path}";
    }

    public void LoadProject(string path)
    {
        IsPlaying = false;
        ClearUndoHistory();
        Project = ProjectStore.Load(path);
        SelectedClip = null;
        PlayheadSeconds = 0;
        StatusMessage = $"開きました：{path}";
        // バインドの音源解析を裏で走らせておく（終わればプレビューに反映される）。
        _ = EnsureBindingAnalysesAsync();
    }

    /// <summary>タイムライン全体を MP4 に書き出す。バインドの音源解析が終わるまで待ってから描き始める。</summary>
    public async Task ExportTimelineAsync(
        string outputPath, ExportSettings settings,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        await EnsureBindingAnalysesAsync().ConfigureAwait(true);
        await _timelineExporter.ExportAsync(Project, outputPath, settings, progress, ct).ConfigureAwait(true);
    }

    /// <summary>単一クリップの区間書き出し（最初の経路の確認用）。</summary>
    public Task ExportSingleClipAsync(
        Clip clip, string outputPath, ExportSettings settings,
        IProgress<double>? progress = null, CancellationToken ct = default)
        => _exportService.ExportSingleClipAsync(
            clip.SourcePath, clip.InPointSeconds, clip.DurationSeconds,
            outputPath, settings, progress, ct);

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

    /// <summary>再生ヘッド位置でアクティブな音声つきクリップの音を全て鳴らす。無ければ止める。</summary>
    public void StartAudioAtPlayhead()
    {
        var sources = Project.Tracks
            .SelectMany(t => t.Clips)
            .Where(c => c.IsActiveAt(PlayheadSeconds) && !string.IsNullOrEmpty(c.SourcePath))
            .Select(c => (Clip: c, Media: MediaItems.FirstOrDefault(m => m.SourcePath == c.SourcePath)))
            .Where(x => x.Media is not null && x.Media.Info.HasAudio)
            .Select(x => (x.Media!.SourcePath, x.Clip.InPointSeconds + (PlayheadSeconds - x.Clip.StartSeconds)))
            .ToList();

        if (sources.Count == 0)
        {
            _audioPlayer.Stop();
            return;
        }

        try
        {
            _audioPlayer.Play(sources);
        }
        catch (Exception ex)
        {
            StatusMessage = $"音声の再生に失敗しました：{ex.Message}";
        }
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
