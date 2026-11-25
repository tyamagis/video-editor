// 音声のサンプルピークの提供口（設計プラン 5章）。
// Core は Audio プロジェクトを参照しない。Audio 側がこのインターフェースを
// 実装し、App / Render が差し込む（IAudioBandEnergyProvider と同じ形）。
// プレビューと書き出しが同じ提供口から同じ値を読むため、ゲインが一致する。

namespace Editor.Core;

/// <summary>サンプルピークの解析結果1件分。</summary>
public sealed record AudioPeakResult(
    /// <summary>クリップ使用範囲のサンプルピーク（線形振幅）。全チャンネルの最大値。測れなかった場合は 0。</summary>
    double PeakLinear,
    /// <summary>測れたか。測れなければ PeakLinear は 0 で、FailureReason に理由が入る。</summary>
    bool Available,
    /// <summary>測れなかった理由（測れた場合は null）。</summary>
    string? FailureReason = null);

/// <summary>
/// クリップの使用範囲のサンプルピークの提供口。Editor.Audio が実装する。
/// ピークはクリップのイン点〜イン点＋長さの範囲で測る（書き出し範囲ではなく
/// クリップの使用範囲で測るため、書き出し範囲を変えてもゲインは変わらない）。
/// </summary>
public interface IAudioPeakProvider
{
    /// <summary>指定範囲のサンプルピークを返す。結果は提供側でキャッシュしてよい。</summary>
    Task<AudioPeakResult> GetPeakAsync(
        string sourcePath, double inPointSeconds, double durationSeconds,
        CancellationToken ct = default);
}
