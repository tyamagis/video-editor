// フェーズ2の音連動への拡張口（設計プラン 4章）。
// Core は Audio プロジェクトを参照しない。
// Audio 側がこのインターフェースを実装し、App / Render が差し込む。

namespace Editor.Core;

/// <summary>
/// 周波数帯エネルギーの提供口。Editor.Audio が実装する。
/// フェーズ1では未使用。フェーズ2のバインドがここから値を読む。
/// </summary>
public interface IAudioBandEnergyProvider
{
    /// <summary>指定クリップの音声を事前に解析し、帯域エネルギーを引ける状態にする。</summary>
    void Precompute(string clipId, string sourcePath, double frameRate);

    /// <summary>
    /// 時刻 timeSeconds における指定周波数帯（lowHz〜highHz）のエネルギーを返す。
    /// 0.0（無音）〜1.0（最大）に正規化される。
    /// </summary>
    double GetBandEnergy(string clipId, double timeSeconds, double lowHz, double highHz);
}
