// MainViewModel の分割ファイル（partial）。再生ヘッド・再生状態と、プレビュー音声再生の制御。

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
}
