// BPM検出・無音カットの「検出→保留→検証→適用」状態機械。
// MainViewModel から 2026-11-24 に分離。振る舞い不変が最優先のため、
// ロジックは分離前の MainViewModel のものをそのまま移している。
// 適用時の検証（クリップ削除・トリミング・プロジェクト切替で適用を拒否する仕組み）は
// そのままここに残す。プロジェクト切替時は MainViewModel が Clear() を呼ぶ。
// SilenceDetector はコンストラクタ注入（AudioBeatAnalyzer と同じ扱い）。
// プロジェクト・選択クリップ・BPM へのアクセスと、Undo スナップショット・
// ステータス文言・画面更新は IDetectionHost 経由で MainViewModel に委譲する。

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Editor.Audio;
using Editor.Core;

namespace Editor.App;

/// <summary>DetectionService が MainViewModel に委譲する操作。</summary>
public interface IDetectionHost
{
    /// <summary>現在のプロジェクト（検出対象クリップの検索・適用時の検証に使う）。</summary>
    EditorProject Project { get; }

    /// <summary>選択中クリップ（検出対象の優先・無音カット対象）。適用で消えた場合は null にする。</summary>
    Clip? SelectedClip { get; set; }

    /// <summary>
    /// BPM（手動設定の setter と同じ。設定すると検出済みビートマップが消える）。
    /// 検出結果の適用時は、古いビートマップを消してから新しいものを入れるためこの順序が要る。
    /// </summary>
    double Bpm { get; set; }

    /// <summary>ステータスバーに出す文言を設定する。</summary>
    void SetStatusMessage(string message);

    /// <summary>適用の直前にアンドゥ用スナップショットを取る。</summary>
    void BeginEdit();

    /// <summary>適用後にプレビュー・タイムライン表示を更新する。</summary>
    void NotifyTimelineChanged();
}

/// <summary>
/// BPM検出と無音カットの状態機械。
/// 検出結果は「保留」し、検証を通った場合だけ「適用」でプロジェクトへ反映する。
/// </summary>
public sealed class DetectionService : INotifyPropertyChanged
{
    private readonly AudioBeatAnalyzer _beatAnalyzer;
    private readonly SilenceDetector _silenceDetector;
    private readonly IDetectionHost _host;

    /// <summary>BPM検出の実行中か（実行中は検出ボタンを無効化する）。</summary>
    private bool _isDetectingBpm;

    /// <summary>検出済みで未適用の結果（素材内時刻）。適用時にタイムライン時刻へ変換する。</summary>
    private BeatDetectionResult? _pendingDetection;

    /// <summary>保留中の検出結果の対象クリップ。プロジェクトから無くなれば適用できない。</summary>
    private Clip? _pendingDetectionClip;

    /// <summary>適用済みの検出ビートマップ（タイムライン時刻）。拍グリッド表示が使う。</summary>
    private BeatMap? _detectedBeatMap;

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

    /// <summary>保留中の無音検出時の逆再生フラグ。検出後に切り替えていたら適用しない。</summary>
    private bool _pendingSilenceReversed;

    /// <summary>保留中の無音検出時の再生速度。検出後に変えていたら適用しない（区間の写し方が変わるため）。</summary>
    private double _pendingSilenceSpeed = 1.0;

    /// <summary>無音検出のしきい値（dBFS）。既定 -40dB。</summary>
    private double _silenceThresholdDb = -40;

    /// <summary>無音区間とみなす最短の長さ（秒）。既定 0.5 秒。</summary>
    private double _silenceMinSeconds = 0.5;

    /// <summary>無音区間の前後に残す余白（秒）。既定 0.1 秒。</summary>
    private double _silenceMarginSeconds = 0.1;

    public event PropertyChangedEventHandler? PropertyChanged;

    public DetectionService(
        AudioBeatAnalyzer beatAnalyzer,
        SilenceDetector silenceDetector,
        IDetectionHost host)
    {
        _beatAnalyzer = beatAnalyzer;
        _silenceDetector = silenceDetector;
        _host = host;
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
        set
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
            _host.SetStatusMessage("BPMを検出するには、音声クリップまたは映像クリップをタイムラインに置いてください。");
            return;
        }

        IsDetectingBpm = true;
        _host.SetStatusMessage($"BPMを検出中…：{clip.Name}");
        try
        {
            var result = await _beatAnalyzer.AnalyzeAsync(clip.SourcePath).ConfigureAwait(true);
            if (!result.Detected)
            {
                SetPendingDetection(null, null);
                _host.SetStatusMessage($"BPMを検出できませんでした：{result.FailureReason}");
                return;
            }
            SetPendingDetection(result, clip);
            _host.SetStatusMessage($"BPMを検出しました：{result.Bpm:F1}（信頼度 {result.Confidence:P0}）。「適用」を押すとプロジェクトに反映されます。");
        }
        catch (OperationCanceledException)
        {
            _host.SetStatusMessage("BPMの検出をキャンセルしました。");
        }
        catch (Exception ex)
        {
            SetPendingDetection(null, null);
            _host.SetStatusMessage($"BPMの検出に失敗しました：{ex.Message}");
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
            _host.SetStatusMessage("適用する検出結果がありません。先に「BPMを検出」を押してください。");
            return;
        }
        // 検出後にクリップがプロジェクトから無くなっていたら適用しない。
        if (clip is null || !_host.Project.Tracks.SelectMany(t => t.Clips).Contains(clip))
        {
            SetPendingDetection(null, null);
            _host.SetStatusMessage("検出対象のクリップが無くなったため、適用できませんでした。もう一度検出してください。");
            return;
        }

        _host.BeginEdit();

        // 素材内時刻をタイムライン時刻に変換する（Clip.GetTimelineTimeSeconds。
        // 速度・逆再生を含む唯一の逆写像。逆再生なら素材の後ろの拍がタイムラインの
        // 手前に来て、速度が掛かると拍の間隔もタイムライン上で ÷速度 になる）。
        // 逆再生のオフセットは、素材で最後の拍（タイムラインでは最初の拍）を基準にする。
        var rawOffset = clip.IsReversed && result.BeatTimes.Count > 0
            ? clip.GetTimelineTimeSeconds(result.BeatTimes.Max())
            : clip.GetTimelineTimeSeconds(result.OffsetSeconds);
        // 拍オフセットは 0〜1周期の範囲に正規化する（負の場合は周期を足して合わせる）。
        // 0 で丸めるだけだと、イン点が大きいときに拍グリッドと検出拍がずれてしまう。
        var period = 60.0 / result.Bpm.Value;
        var offset = rawOffset - Math.Floor(rawOffset / period) * period;
        // 検出は素材全体に対して行われるため、使用範囲外の拍は写像前に捨てる
        // （GetTimelineTimeSeconds は範囲外を端に丸めるので、写像後に絞ると
        // 範囲外の拍がクリップの端に張り付いて残ってしまう）。
        var beatTimes = result.BeatTimes
            .Where(t => t >= clip.InPointSeconds - 1e-6
                        && t <= clip.InPointSeconds + clip.SourceDurationSeconds + 1e-6)
            .Select(t => clip.GetTimelineTimeSeconds(t))
            .Where(t => t >= clip.StartSeconds - 1e-6 && t <= clip.EndSeconds + 1e-6)
            .Select(t => Math.Clamp(t, clip.StartSeconds, clip.EndSeconds))
            .ToList();

        // Bpm セッターは検出済みビートマップを消すので、先に BPM・オフセットを設定する。
        _host.Bpm = result.Bpm.Value;
        _host.Project.BeatOffsetSeconds = offset;
        DetectedBeatMap = new BeatMap
        {
            Source = BeatMapSource.WaveformDetected,
            Bpm = result.Bpm.Value,
            OffsetSeconds = _host.Project.BeatOffsetSeconds,
            BeatTimes = beatTimes
        };
        SetPendingDetection(null, null);
        _host.NotifyTimelineChanged();
        _host.SetStatusMessage($"BPM {result.Bpm:F1} と拍 {beatTimes.Count}個を反映しました（{clip.Name}）。");
    }

    /// <summary>検出対象のクリップを選ぶ。選択中クリップを優先する。</summary>
    private Clip? FindDetectionTargetClip()
    {
        var selected = _host.SelectedClip;
        if (selected is not null
            && (selected.Kind == ClipKind.Audio || selected.Kind == ClipKind.Video)
            && !string.IsNullOrEmpty(selected.SourcePath))
            return selected;

        foreach (var track in _host.Project.Tracks)
            foreach (var clip in track.Clips)
                if (clip.Kind == ClipKind.Audio && !string.IsNullOrEmpty(clip.SourcePath))
                    return clip;

        foreach (var track in _host.Project.Tracks)
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

        var clip = _host.SelectedClip;
        if (clip is null || (clip.Kind != ClipKind.Audio && clip.Kind != ClipKind.Video)
            || string.IsNullOrEmpty(clip.SourcePath))
        {
            _host.SetStatusMessage("無音カットするには、音声クリップまたは映像クリップを選択してください。");
            return;
        }

        IsDetectingSilence = true;
        _host.SetStatusMessage($"無音区間を検出中…：{clip.Name}");
        try
        {
            var settings = new SilenceDetectionSettings
            {
                ThresholdDb = SilenceThresholdDb,
                MinSilenceSeconds = SilenceMinSeconds,
                MarginSeconds = SilenceMarginSeconds
            };
            var result = await _silenceDetector.AnalyzeAsync(
                clip.SourcePath, clip.InPointSeconds, clip.SourceDurationSeconds, settings)
                .ConfigureAwait(true);
            if (!result.Detected)
            {
                SetPendingSilence(null, null);
                _host.SetStatusMessage($"無音区間を検出できませんでした：{result.FailureReason}");
                return;
            }
            SetPendingSilence(result, clip);
            _host.SetStatusMessage(result.Segments.Count == 0
                ? "無音区間は見つかりませんでした。"
                : $"無音区間を検出しました：{result.Segments.Count} 個、合計 {result.TotalSilenceSeconds:F1} 秒。「適用」を押すと切り取ります。");
        }
        catch (OperationCanceledException)
        {
            _host.SetStatusMessage("無音区間の検出をキャンセルしました。");
        }
        catch (Exception ex)
        {
            SetPendingSilence(null, null);
            _host.SetStatusMessage($"無音区間の検出に失敗しました：{ex.Message}");
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
            _host.SetStatusMessage("適用する検出結果がありません。先に「検出」を押してください。");
            return;
        }
        // 検出後にクリップがプロジェクトから無くなっていたら適用しない。
        if (clip is null || !_host.Project.Tracks.SelectMany(t => t.Clips).Contains(clip))
        {
            SetPendingSilence(null, null);
            _host.SetStatusMessage("検出対象のクリップが無くなったため、適用できませんでした。もう一度検出してください。");
            return;
        }
        // 検出後にクリップをトリミングしていたら、検出区間とクリップの対応がずれるため適用しない。
        // 逆再生の切り替え・速度の変更も同じ（検出区間のタイムラインへの写し方が変わるため）。
        if (Math.Abs(clip.InPointSeconds - _pendingSilenceInPoint) > 1e-6
            || Math.Abs(clip.DurationSeconds - _pendingSilenceDuration) > 1e-6
            || clip.IsReversed != _pendingSilenceReversed
            || Math.Abs(clip.Speed - _pendingSilenceSpeed) > 1e-9)
        {
            SetPendingSilence(null, null);
            _host.SetStatusMessage("検出後にクリップが変更されたため、適用できませんでした。もう一度検出してください。");
            return;
        }
        if (result.Segments.Count == 0)
        {
            _host.SetStatusMessage("切る無音区間がありません。");
            return;
        }
        var track = _host.Project.Tracks.FirstOrDefault(t => t.Clips.Contains(clip));
        if (track is null)
        {
            SetPendingSilence(null, null);
            _host.SetStatusMessage("検出対象のクリップが無くなったため、適用できませんでした。");
            return;
        }

        // 検出した区間（イン点からの相対秒）をタイムライン時刻に変換する。
        // 変換は Clip.GetTimelineTimeSeconds（速度・逆再生を含む唯一の逆写像）に統一する。
        // 逆再生では区間の開始と終了が入れ替わるため、小さい方を開始にする。
        var segments = result.Segments
            .Select(s =>
            {
                var t1 = clip.GetTimelineTimeSeconds(clip.InPointSeconds + s.StartSeconds);
                var t2 = clip.GetTimelineTimeSeconds(clip.InPointSeconds + s.EndSeconds);
                return (StartSeconds: Math.Min(t1, t2), EndSeconds: Math.Max(t1, t2));
            })
            .ToList();

        _host.BeginEdit();
        var removed = TimelineEditor.CutSilenceRanges(track, clip, segments);
        // クリップ自体が消えた場合（全体が無音だった場合）は選択を外す。
        if (!_host.Project.Tracks.SelectMany(t => t.Clips).Contains(clip))
            _host.SelectedClip = null;
        SetPendingSilence(null, null);
        _host.NotifyTimelineChanged();
        _host.SetStatusMessage($"無音区間 {removed} 個を切り取りました（合計 {result.TotalSilenceSeconds:F1} 秒）。");
    }

    /// <summary>保留中の無音検出結果を差し替え、関連する表示を更新する。</summary>
    private void SetPendingSilence(SilenceDetectionResult? result, Clip? clip)
    {
        _pendingSilence = result;
        _pendingSilenceClip = clip;
        // 検出時のクリップの形を記録する（適用時に変わっていたら適用しない）。
        _pendingSilenceInPoint = clip?.InPointSeconds ?? 0;
        _pendingSilenceDuration = clip?.DurationSeconds ?? 0;
        _pendingSilenceReversed = clip?.IsReversed ?? false;
        _pendingSilenceSpeed = clip?.Speed ?? 1.0;
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
    public void Clear()
    {
        SetPendingDetection(null, null);
        SetPendingSilence(null, null);
        DetectedBeatMap = null;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
