// 音の可視化（スペクトル・波形・リサジュー）への拡張口（設計プラン 4章）。
// Core は Audio プロジェクトを参照しない。
// Audio 側がこのインターフェースを実装し、App / Render が差し込む。

namespace Editor.Core;

/// <summary>
/// 可視化用の音声解析データの提供口。Editor.Audio が実装する。
/// 帯域エネルギー（バインド）と同じ事前解析キャッシュを読むため、
/// 可視化エフェクトを増やしても解析は重複しない。
/// </summary>
public interface IAudioVisualizationProvider
{
    /// <summary>指定クリップの音声が事前に解析済みか。</summary>
    bool IsAnalyzed(string clipId);

    /// <summary>
    /// 指定時刻のスペクトルを帯域に分けて返す（各値 0.0〜1.0）。
    /// logarithmic が true なら対数間隔、false なら線形間隔の帯域境界を使う。
    /// </summary>
    double[] GetSpectrumBands(
        string clipId, double timeSeconds, int bandCount,
        double lowHz, double highHz, bool logarithmic);

    /// <summary>
    /// 指定時刻の直前 windowSeconds 分のステレオ波形を返す（波形・リサジュー用）。
    /// 解析されていない・窓に標本が無ければ空の配列を返す。
    /// </summary>
    (float[] Left, float[] Right) GetStereoWindow(
        string clipId, double timeSeconds, double windowSeconds);
}
