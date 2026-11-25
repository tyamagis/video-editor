// タイムライン書き出しの音声ミックス計画（設計プラン 5章）。
// 書き出し範囲に入る音声つきクリップを集め、ffmpeg のフィルタ
// （atrim / adelay / amix、ノイズ除去は afftdn）で1本にミックスする計画を作る。
// フィルタグラフの文字列はここだけが持つ。FrameSequenceExporter は
// この計画を受け取り、ffmpeg の起動とフレームのパイプだけを行う。

using Editor.Core;

namespace Editor.Render;

/// <summary>書き出し範囲の音声ミックス計画を作る。読めない素材は音声なしで進む。</summary>
internal sealed class AudioExportPlanner
{
    private readonly IMediaProbe _probe;
    private readonly IAudioPeakProvider? _peakProvider;

    public AudioExportPlanner(IMediaProbe probe, IAudioPeakProvider? peakProvider = null)
    {
        _probe = probe;
        _peakProvider = peakProvider;
    }

    /// <summary>書き出し範囲に入る音声つきクリップの入力計画を作る。音声が無ければ MapLabel は null。</summary>
    public async Task<(List<AudioInput> Inputs, string? MapLabel)> BuildAudioPlanAsync(
        EditorProject project, double startSeconds, double endSeconds, CancellationToken ct)
    {
        var inputs = new List<AudioInput>();

        var clips = project.Tracks
            .SelectMany(t => t.Clips)
            .Where(c => !string.IsNullOrEmpty(c.SourcePath)
                        && c.EndSeconds > startSeconds && c.StartSeconds < endSeconds)
            .OrderBy(c => c.StartSeconds)
            .ToList();

        var index = 1; // 0 は映像（標準入力）なので音声は 1 から。
        foreach (var clip in clips)
        {
            ct.ThrowIfCancellationRequested();
            MediaInfo? info;
            try
            {
                info = await _probe.ProbeAsync(clip.SourcePath, ct).ConfigureAwait(false);
            }
            catch
            {
                continue; // 読めない素材は音声なしで進む。
            }
            if (info is null || !info.HasAudio)
                continue;

            var overlapStart = Math.Max(clip.StartSeconds, startSeconds);
            var overlapEnd = Math.Min(clip.EndSeconds, endSeconds);
            if (overlapEnd <= overlapStart)
                continue;

            var sourceStart = clip.InPointSeconds + (overlapStart - clip.StartSeconds);
            var sourceEnd = clip.InPointSeconds + (overlapEnd - clip.StartSeconds);
            var delayMs = (long)Math.Round((overlapStart - startSeconds) * 1000);
            var label = $"a{index}";
            // ノーマライズが有効なクリップは、クリップの使用範囲のピークから
            // ゲインを計算する（プレビューと同じ計算・同じ提供口。ピークは
            // 書き出し範囲ではなくクリップの使用範囲で測るため、書き出し範囲を
            // 変えてもゲインは変わらない）。ピークが測れなければ等倍で書き出す。
            var normalizeGain = 1.0;
            if (clip.NormalizeAudioEnabled && _peakProvider is not null)
            {
                try
                {
                    var peak = await _peakProvider
                        .GetPeakAsync(clip.SourcePath, clip.InPointSeconds, clip.DurationSeconds, ct)
                        .ConfigureAwait(false);
                    if (peak.Available)
                        normalizeGain = AudioNormalizer.ComputeGain(peak.PeakLinear, clip.NormalizeTargetDb);
                }
                catch
                {
                    // ピーク解析の失敗で書き出しを止めない（元の音量で書き出す）。
                }
            }
            // フィルタ（ノイズ除去・ノーマライズ・切り出し・逆再生・遅延）は BuildClipFilter が作る。
            var filter = BuildClipFilter(index, clip, sourceStart, sourceEnd, delayMs, label, normalizeGain);
            inputs.Add(new AudioInput(clip.SourcePath, filter, label));
            index++;
        }

        if (inputs.Count == 0)
            return (inputs, null);

        var mapLabel = inputs.Count == 1 ? inputs[0].Label : "aout";
        return (inputs, mapLabel);
    }

    /// <summary>
    /// 音声クリップ1つのフィルタを作る。書き出し範囲に入る部分だけ切り出し、
    /// 逆再生なら切り出した区間を逆向きにしてから、範囲内の位置まで遅らせる。
    /// areverse は atrim の後に掛ける（切り出した区間だけを逆向きにするため。
    /// atrim の前に掛けると切り出し位置の対応が逆転してしまう）。
    /// areverse の後に asetpts で PTS を振り直す（areverse が PTS をどう扱うかに
    /// 依存せず、遅延の基準をそろえるため）。
    /// normalizeGain はノーマライズの線形倍率（1.0 が等倍）。volume フィルタは
    /// atrim の前に掛ける（ゲインは線形のため、atrim の前後で結果は同じ）。
    /// </summary>
    internal static string BuildClipFilter(
        int index, Clip clip, double sourceStart, double sourceEnd, long delayMs, string label,
        double normalizeGain = 1.0)
    {
        // ノイズ除去が有効なクリップは afftdn で背景ノイズを減らす
        // （設計プラン 5章で FFmpeg のフィルタが第一候補。プレビューでは反映されない）。
        // atrim の前に掛けることで、ファイル全体からノイズ特性を取る。
        var denoise = clip.NoiseReductionEnabled
            ? $"afftdn=nr={NoiseReductionDb(clip.NoiseReductionStrength):F1}:nf=-50,"
            : string.Empty;
        var normalize = VolumeFilter(normalizeGain);
        var reverse = clip.IsReversed ? "areverse," : string.Empty;
        return $"[{index}:a]{denoise}{normalize}atrim=start={sourceStart:F3}:end={sourceEnd:F3}," +
               $"{reverse}asetpts=PTS-STARTPTS,adelay={delayMs}:all=1[{label}]";
    }

    /// <summary>ノイズ除去の強さ（0.0〜1.0）を afftdn の nr（dB、1〜30）に写す。</summary>
    private static double NoiseReductionDb(double strength)
        => 1.0 + Math.Clamp(strength, 0, 1) * 29.0;

    /// <summary>ノーマライズのゲイン（線形倍率）を ffmpeg の volume フィルタにする。等倍（1.0）のときは空文字（フィルタを付けない）。</summary>
    internal static string VolumeFilter(double gain)
        => Math.Abs(gain - 1.0) <= 1e-6 ? string.Empty : $"volume={gain:F4},";


}

/// <summary>音声入力1つ（ファイル・フィルタ・マップラベル）。</summary>
internal sealed record AudioInput(string Path, string Filter, string Label);
