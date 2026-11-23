// BPM・ビート検出の口（設計プラン 4章・フェーズ2）。
// 実検出は AudioBeatAnalyzer が行い、ここはその口として委譲する。
// BPM自動検出と波形検出は同じ解析を共有する（1回の解析で両方出る）。

namespace Editor.Audio;

/// <summary>音声からBPMを自動検出する口（一定BPMの曲向け）。</summary>
public interface IBpmDetector
{
    Task<double?> DetectBpmAsync(string audioPath, CancellationToken ct = default);
}

/// <summary>音声波形から拍時刻を直接検出する口（素材内時刻の列を返す）。</summary>
public interface IBeatDetector
{
    Task<IReadOnlyList<double>> DetectBeatsAsync(string audioPath, CancellationToken ct = default);
}

/// <summary>BPM自動検出。AudioBeatAnalyzer に委譲する。検出できなければ null。</summary>
public sealed class AutoBpmDetector : IBpmDetector
{
    private readonly AudioBeatAnalyzer _analyzer;

    public AutoBpmDetector(AudioBeatAnalyzer analyzer) => _analyzer = analyzer;

    public async Task<double?> DetectBpmAsync(string audioPath, CancellationToken ct = default)
    {
        var result = await _analyzer.AnalyzeAsync(audioPath, ct).ConfigureAwait(false);
        return result.Detected ? result.Bpm : null;
    }
}

/// <summary>波形からのビート検出。AudioBeatAnalyzer に委譲する。検出できなければ空の列。</summary>
public sealed class WaveformBeatDetector : IBeatDetector
{
    private readonly AudioBeatAnalyzer _analyzer;

    public WaveformBeatDetector(AudioBeatAnalyzer analyzer) => _analyzer = analyzer;

    public async Task<IReadOnlyList<double>> DetectBeatsAsync(string audioPath, CancellationToken ct = default)
    {
        var result = await _analyzer.AnalyzeAsync(audioPath, ct).ConfigureAwait(false);
        return result.Detected ? result.BeatTimes : Array.Empty<double>();
    }
}
