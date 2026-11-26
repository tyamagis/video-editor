// MainViewModel の分割ファイル（partial）。書き出し範囲の設定と、タイムライン／単一クリップの書き出し呼び出し。

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

    /// <summary>タイムライン全体を MP4 に書き出す。バインドの音源解析が終わるまで待ってから描き始める。</summary>
    public async Task ExportTimelineAsync(
        string outputPath, ExportSettings settings,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        await EnsureBindingAnalysesAsync().ConfigureAwait(true);
        await _timelineExporter.ExportAsync(Project, outputPath, settings, progress, ct).ConfigureAwait(true);
    }

    /// <summary>単一クリップの区間書き出し（最初の経路の確認用）。逆再生クリップは逆向きに、速度変更クリップは速度を変えて書き出し、音声フェードもクリップの設定通り適用する（ダッキングはプロジェクト内のトリガー解析が要るため対象外。ExportService 側のコメント参照）。素材側の長さは SourceDurationSeconds。</summary>
    public Task ExportSingleClipAsync(
        Clip clip, string outputPath, ExportSettings settings,
        IProgress<double>? progress = null, CancellationToken ct = default)
        => _exportService.ExportSingleClipAsync(
            clip.SourcePath, clip.InPointSeconds, clip.SourceDurationSeconds,
            outputPath, settings, progress, ct, clip.IsReversed, clip.Speed,
            clip.AudioFadeInSeconds, clip.AudioFadeOutSeconds);
}
