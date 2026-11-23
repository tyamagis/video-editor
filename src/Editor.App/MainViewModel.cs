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

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly MediaProbe _mediaProbe;
    private readonly ThumbnailService _thumbnailService;
    private readonly ExportService _exportService;
    private readonly WaveformReader _waveformReader;
    private readonly PreviewRenderer _previewRenderer;
    private readonly ITimelineExporter _timelineExporter;
    private readonly FfmpegSettings _ffmpegSettings;

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
        FfmpegSettings ffmpegSettings)
    {
        _mediaProbe = mediaProbe;
        _thumbnailService = thumbnailService;
        _exportService = exportService;
        _waveformReader = waveformReader;
        _previewRenderer = previewRenderer;
        _timelineExporter = timelineExporter;
        _ffmpegSettings = ffmpegSettings;

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
        set { _selectedClip = value; OnPropertyChanged(); }
    }

    public double PlayheadSeconds
    {
        get => _playheadSeconds;
        set
        {
            _playheadSeconds = Math.Clamp(value, 0, Math.Max(0, Project.DurationSeconds));
            OnPropertyChanged();
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
            NotifyTimelineChanged();
        }
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

    /// <summary>再生ヘッド位置で全トラックのクリップを分割する。</summary>
    public void SplitAtPlayhead()
    {
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
        foreach (var track in Project.Tracks)
        {
            if (track.Clips.Remove(SelectedClip))
                break;
        }
        SelectedClip = null;
        NotifyTimelineChanged();
    }

    public void SaveProject(string path)
    {
        ProjectStore.Save(path, Project);
        StatusMessage = $"保存しました：{path}";
    }

    public void LoadProject(string path)
    {
        IsPlaying = false;
        Project = ProjectStore.Load(path);
        SelectedClip = null;
        PlayheadSeconds = 0;
        StatusMessage = $"開きました：{path}";
    }

    /// <summary>タイムライン全体を MP4 に書き出す（映像のみ。音声ミックスは次のステップ）。</summary>
    public Task ExportTimelineAsync(
        string outputPath, ExportSettings settings,
        IProgress<double>? progress = null, CancellationToken ct = default)
        => _timelineExporter.ExportAsync(Project, outputPath, settings, progress, ct);

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
        OnPropertyChanged(nameof(Project));
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
