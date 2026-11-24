// タイムライン書き出しの音声ミックス計画（設計プラン 5章）。
// 書き出し範囲に入る音声つきクリップを集め、ffmpeg のフィルタ
// （atrim / adelay / amix、ノイズ除去は afftdn）で1本にミックスする計画を作る。
// フィルタグラフの文字列はここだけが持つ。FrameSequenceExporter は
// この計画を受け取り、ffmpeg の起動とフレームのパイプだけを行う。

using Editor.Core;
using Editor.Media;

namespace Editor.Render;

/// <summary>書き出し範囲の音声ミックス計画を作る。読めない素材は音声なしで進む。</summary>
internal sealed class AudioExportPlanner
{
    private readonly MediaProbe _probe;

    public AudioExportPlanner(MediaProbe probe)
    {
        _probe = probe;
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
            // ノイズ除去が有効なクリップは、書き出し時に afftdn で背景ノイズを減らす
            // （設計プラン 5章で FFmpeg のフィルタが第一候補。プレビューでは反映されない）。
            // atrim の前に掛けることで、ファイル全体からノイズ特性を取る。
            var denoise = clip.NoiseReductionEnabled
                ? $"afftdn=nr={NoiseReductionDb(clip.NoiseReductionStrength):F1}:nf=-50,"
                : string.Empty;
            // 書き出し範囲に入る部分だけ切り出し、範囲内の位置まで遅らせる。
            var filter = $"[{index}:a]{denoise}atrim=start={sourceStart:F3}:end={sourceEnd:F3}," +
                         $"asetpts=PTS-STARTPTS,adelay={delayMs}:all=1[{label}]";
            inputs.Add(new AudioInput(clip.SourcePath, filter, label));
            index++;
        }

        if (inputs.Count == 0)
            return (inputs, null);

        var mapLabel = inputs.Count == 1 ? inputs[0].Label : "aout";
        return (inputs, mapLabel);
    }

    /// <summary>ノイズ除去の強さ（0.0〜1.0）を afftdn の nr（dB、1〜30）に写す。</summary>
    private static double NoiseReductionDb(double strength)
        => 1.0 + Math.Clamp(strength, 0, 1) * 29.0;


}

/// <summary>音声入力1つ（ファイル・フィルタ・マップラベル）。</summary>
internal sealed record AudioInput(string Path, string Filter, string Label);
