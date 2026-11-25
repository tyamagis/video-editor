// 音声ノーマライズのゲイン計算（設計プラン 5章）。
// ピーク正規化（サンプルピーク基準。true peak ではない）。
// プレビュー（NAudio の AudioFileReader.Volume）と書き出し
// （ffmpeg の volume フィルタ）の両方が、この計算だけを読む。
// 計算が1か所にあるため、同じクリップ・同じ目標では必ず同じゲインになる。

namespace Editor.Core;

/// <summary>ピーク正規化のゲイン計算。状態を持たない純粋関数。</summary>
public static class AudioNormalizer
{
    /// <summary>
    /// 既定の目標ピークレベル（dBFS）。-1 dBFS は、放送・配信の慣例
    /// （-1 dBTP 相当の安全マージン）に倣い、クリッピングを避けつつ
    /// ほぼフルスケールまで持ち上げる値。サンプルピーク基準のため、
    /// コーデック変換でのインターサンプルピークの伸び（通常 1dB 未満）
    /// にも 1dB の余裕がある。
    /// </summary>
    public const double DefaultTargetDb = -1.0;

    /// <summary>目標レベルとして指定できる下限（dBFS）。</summary>
    public const double MinTargetDb = -30.0;

    /// <summary>目標レベルとして指定できる上限（dBFS）。0 dBFS（フルスケール）まで。</summary>
    public const double MaxTargetDb = 0.0;

    /// <summary>
    /// ゲインの上限（+40 dB = 100倍）。これ以上持ち上げるとノイズフロアや
    /// 量子化ノイズまで大きく増幅されるため、持ち上げはここで止める。
    /// 非常に小さい音のクリップは目標に届かない場合がある（その旨は UI の説明に出す）。
    /// </summary>
    public const double MaxGainDb = 40.0;

    /// <summary>ゲインの上限（線形倍率。MaxGainDb と同じ）。</summary>
    public const double MaxGainLinear = 100.0;

    /// <summary>目標レベルを -30〜0 dBFS の範囲に丸める。</summary>
    public static double ClampTargetDb(double value) => Math.Clamp(value, MinTargetDb, MaxTargetDb);

    /// <summary>サンプルピーク（線形振幅、0〜）を dBFS に変換する。無音（0以下）は -∞。</summary>
    public static double PeakToDb(double peakLinear)
        => peakLinear <= 0 ? double.NegativeInfinity : 20 * Math.Log10(peakLinear);

    /// <summary>
    /// ピーク（線形振幅）と目標レベル（dBFS）からゲイン（dB）を計算する。
    /// gainDb = 目標 - ピーク(dBFS)。無音（ピーク 0）のときは持ち上げようが
    /// ないため 0 dB。上限は MaxGainDb（+40 dB）。絞る側（負のゲイン）に下限は無い。
    /// </summary>
    public static double ComputeGainDb(double peakLinear, double targetDb)
    {
        if (peakLinear <= 0)
            return 0;
        var gainDb = ClampTargetDb(targetDb) - PeakToDb(peakLinear);
        return Math.Min(gainDb, MaxGainDb);
    }

    /// <summary>ピーク（線形振幅）と目標レベル（dBFS）からゲイン（線形倍率）を計算する。無音のときは 1.0。</summary>
    public static double ComputeGain(double peakLinear, double targetDb)
        => Math.Pow(10, ComputeGainDb(peakLinear, targetDb) / 20.0);
}
