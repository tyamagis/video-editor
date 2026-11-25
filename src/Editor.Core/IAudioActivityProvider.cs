// ダッキング用：音声が「鳴っている区間」の提供口（設計プラン 5章）。
// Core は Audio プロジェクトを参照しない。Audio 側が実装し、App / Render が
// 差し込む（IAudioPeakProvider と同じ形）。プレビューと書き出しが同じ提供口から
// 同じ区間を読むため、ダッキング包絡（AudioGainEnvelope）が一致する。

namespace Editor.Core;

/// <summary>
/// クリップ使用範囲のうち、しきい値以上の音が鳴っている区間の提供口。
/// Editor.Audio が実装する（無音検出と同じ 10ms RMS 包絡を使う）。
/// 区間は素材相対秒（イン点を 0）で返す。タイムラインへの写像は
/// AudioGainEnvelope.MapTriggerSegmentsToTimeline が Clip の写像で行う。
/// </summary>
public interface IAudioActivityProvider
{
    /// <summary>
    /// 指定範囲でしきい値（dBFS）以上の RMS が続く区間を返す。
    /// 測れなかった場合は空（ダッキングは掛からず、再生・書き出しは止めない）。
    /// 結果は提供側でキャッシュしてよい。
    /// </summary>
    Task<IReadOnlyList<AudioSegment>> GetActiveSegmentsAsync(
        string sourcePath, double inPointSeconds, double durationSeconds, double thresholdDb,
        CancellationToken ct = default);
}
