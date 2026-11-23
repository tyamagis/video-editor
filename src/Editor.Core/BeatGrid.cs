// 拍グリッドとビートマップ（設計プラン 4章）。
// 方式A（BPM方式）はフェーズ1で実装。方式B（波形検出）と
// BPM自動検出はフェーズ2のため、ここではインターフェースと
// BPM方式からのビートマップ生成だけを用意する。

namespace Editor.Core;

/// <summary>ビートマップの由来。検出結果も最終的には同じ形で保持する。</summary>
public enum BeatMapSource
{
    ManualBpm,
    AutoDetected,
    WaveformDetected
}

/// <summary>曲中の拍時刻の列。BPM方式・波形検出のどちらで作ってもこの形になる。</summary>
public sealed class BeatMap
{
    public BeatMapSource Source { get; set; } = BeatMapSource.ManualBpm;
    public double Bpm { get; set; } = 120.0;
    public double OffsetSeconds { get; set; }
    public List<double> BeatTimes { get; set; } = new();

    /// <summary>BPMとオフセットから等間隔の拍時刻を算出する（方式A）。</summary>
    public static BeatMap FromBpm(double bpm, double offsetSeconds, double durationSeconds)
    {
        if (bpm <= 0)
            throw new ArgumentOutOfRangeException(nameof(bpm), "BPMは0より大きくなければなりません。");
        if (durationSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));

        var interval = 60.0 / bpm;
        var times = new List<double>();
        for (var t = offsetSeconds; t <= durationSeconds; t += interval)
            times.Add(t);

        return new BeatMap
        {
            Source = BeatMapSource.ManualBpm,
            Bpm = bpm,
            OffsetSeconds = offsetSeconds,
            BeatTimes = times
        };
    }
}

/// <summary>BPMから拍位置を計算する純粋関数群。単体テストの対象。</summary>
public static class BeatGridCalculator
{
    /// <summary>1拍の長さ（秒）。</summary>
    public static double BeatIntervalSeconds(double bpm)
    {
        if (bpm <= 0)
            throw new ArgumentOutOfRangeException(nameof(bpm), "BPMは0より大きくなければなりません。");
        return 60.0 / bpm;
    }

    /// <summary>指定区間の拍時刻を返す。</summary>
    public static List<double> GetBeatTimes(double bpm, double offsetSeconds, double durationSeconds)
        => BeatMap.FromBpm(bpm, offsetSeconds, durationSeconds).BeatTimes;

    /// <summary>時刻が属する拍の番号（0始まり）。オフセットより前は-1。</summary>
    public static int BeatIndexAt(double timeSeconds, double bpm, double offsetSeconds)
    {
        var interval = BeatIntervalSeconds(bpm);
        if (timeSeconds < offsetSeconds)
            return -1;
        return (int)Math.Floor((timeSeconds - offsetSeconds) / interval);
    }

    /// <summary>
    /// 最も近い拍時刻を返す。許容範囲（既定0.05秒）より遠ければ null。
    /// スナップ判定に使う。
    /// </summary>
    public static double? SnapToBeat(double timeSeconds, double bpm, double offsetSeconds, double toleranceSeconds = 0.05)
    {
        var interval = BeatIntervalSeconds(bpm);
        if (timeSeconds < offsetSeconds)
            return null;

        var beats = (timeSeconds - offsetSeconds) / interval;
        var nearest = Math.Round(beats) * interval + offsetSeconds;
        return Math.Abs(nearest - timeSeconds) <= toleranceSeconds ? nearest : null;
    }

    /// <summary>
    /// 検出した拍時刻の列の中で最も近いものを返す。許容範囲より遠ければ null。
    /// 検出ビートマップがあるときのスナップに使う（BPMグリッドではなく検出拍に合わせる）。
    /// </summary>
    public static double? SnapToBeatTimes(double timeSeconds, IReadOnlyList<double> beatTimes, double toleranceSeconds = 0.05)
    {
        double? best = null;
        var bestDist = double.MaxValue;
        foreach (var t in beatTimes)
        {
            var dist = Math.Abs(t - timeSeconds);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = t;
            }
        }
        return bestDist <= toleranceSeconds ? best : null;
    }

    /// <summary>小節の先頭かどうか（4拍を1小節とする）。小節線を濃く描く判定に使う。</summary>
    public static bool IsBarStart(int beatIndex) => beatIndex >= 0 && beatIndex % 4 == 0;
}
