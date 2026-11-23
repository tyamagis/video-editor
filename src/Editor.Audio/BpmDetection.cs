// BPM・ビート検出の口（設計プラン 4章）。
// 自動検出と波形検出はフェーズ2のため、ここではインターフェースと
// 未実装スタブだけを用意する。フェーズ1ではBPM手動設定＋拍グリッドを使う。

namespace Editor.Audio;

/// <summary>音声からBPMを自動検出する口（一定BPMの曲向け）。フェーズ2で実装。</summary>
public interface IBpmDetector
{
    Task<double?> DetectBpmAsync(string audioPath, CancellationToken ct = default);
}

/// <summary>音声波形から拍時刻を直接検出する口。フェーズ2で実装。</summary>
public interface IBeatDetector
{
    Task<IReadOnlyList<double>> DetectBeatsAsync(string audioPath, CancellationToken ct = default);
}

/// <summary>BPM自動検出（フェーズ2）。現時点では未実装。</summary>
public sealed class AutoBpmDetector : IBpmDetector
{
    public Task<double?> DetectBpmAsync(string audioPath, CancellationToken ct = default)
        => throw new NotImplementedException("BPM自動検出はフェーズ2で実装予定です。フェーズ1では手動設定を使ってください。");
}

/// <summary>波形からのビート検出（フェーズ2）。現時点では未実装。</summary>
public sealed class WaveformBeatDetector : IBeatDetector
{
    public Task<IReadOnlyList<double>> DetectBeatsAsync(string audioPath, CancellationToken ct = default)
        => throw new NotImplementedException("波形からのビート検出はフェーズ2で実装予定です。");
}
