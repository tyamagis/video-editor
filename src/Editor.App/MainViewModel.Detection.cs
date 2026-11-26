// MainViewModel の分割ファイル（partial）。BPM・拍グリッド設定と、BPM検出／無音検出（DetectionService）への委譲窓口。

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
    /// <summary>BPM（手動設定）。拍グリッドの基準。既定120。</summary>
    public double Bpm
    {
        get => Project.Bpm;
        set
        {
            if (!double.IsFinite(value) || value <= 0)
                return; // "NaN" は <= 0 をすり抜けるため、有限でない値は無視する。
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
}
