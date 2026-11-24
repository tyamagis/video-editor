// 評価エンジンの単体テスト：拍位置の算出（BeatGridCalculator / BeatMap）。
// BPMとオフセットから拍時刻を出す純粋計算のみを対象とする。

using Editor.Core;
using Xunit;

namespace Editor.Tests;

/// <summary>拍位置の算出（BeatGridCalculator / BeatMap.FromBpm）のテスト。</summary>
public sealed class BeatGridTests
{
    /// <summary>浮動小数点の誤差を許容して拍時刻の列を比較する。</summary>
    private static void AssertBeatTimesEqual(IReadOnlyList<double> expected, IReadOnlyList<double> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
            Assert.True(Math.Abs(expected[i] - actual[i]) <= 1e-9,
                $"beat[{i}]: expected {expected[i]}, actual {actual[i]}");
    }

    // ---- 拍間隔 ----

    [Fact]
    public void BeatInterval_120Bpm_IsHalfSecond()
    {
        Assert.Equal(0.5, BeatGridCalculator.BeatIntervalSeconds(120.0), 9);
    }

    [Fact]
    public void BeatInterval_60Bpm_IsOneSecond()
    {
        Assert.Equal(1.0, BeatGridCalculator.BeatIntervalSeconds(60.0), 9);
    }

    [Fact]
    public void BeatInterval_180Bpm_IsThirdOfSecond()
    {
        Assert.Equal(1.0 / 3.0, BeatGridCalculator.BeatIntervalSeconds(180.0), 9);
    }

    [Fact]
    public void BeatInterval_FractionalBpm()
    {
        Assert.Equal(60.0 / 127.5, BeatGridCalculator.BeatIntervalSeconds(127.5), 9);
        Assert.Equal(60.0 / 123.45, BeatGridCalculator.BeatIntervalSeconds(123.45), 9);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-120.0)]
    public void BeatInterval_ZeroOrNegativeBpm_Throws(double bpm)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BeatGridCalculator.BeatIntervalSeconds(bpm));
    }

    // ---- 拍時刻の列 ----

    [Fact]
    public void GetBeatTimes_120Bpm_TwoSeconds()
    {
        // 0, 0.5, 1.0, 1.5, 2.0（終端の拍も含む）
        AssertBeatTimesEqual(
            new[] { 0.0, 0.5, 1.0, 1.5, 2.0 },
            BeatGridCalculator.GetBeatTimes(120.0, 0.0, 2.0));
    }

    [Fact]
    public void GetBeatTimes_WithOffset_ShiftsAllBeats()
    {
        AssertBeatTimesEqual(
            new[] { 0.25, 0.75, 1.25, 1.75 },
            BeatGridCalculator.GetBeatTimes(120.0, 0.25, 2.0));
    }

    [Fact]
    public void GetBeatTimes_FractionalBpm_CountAndSpacing()
    {
        var interval = 60.0 / 123.45;
        var times = BeatGridCalculator.GetBeatTimes(123.45, 0.0, 2.0);

        Assert.Equal(5, times.Count); // 0, 0.486..., 0.972..., 1.458..., 1.944...
        for (var i = 0; i < times.Count; i++)
            Assert.True(Math.Abs(times[i] - i * interval) <= 1e-9,
                $"beat[{i}]: expected {i * interval}, actual {times[i]}");
    }

    [Fact]
    public void GetBeatTimes_ZeroDuration_ContainsOnlyOffsetBeat()
    {
        AssertBeatTimesEqual(new[] { 0.0 }, BeatGridCalculator.GetBeatTimes(120.0, 0.0, 0.0));
    }

    [Fact]
    public void GetBeatTimes_OffsetBeyondDuration_IsEmpty()
    {
        Assert.Empty(BeatGridCalculator.GetBeatTimes(120.0, 0.5, 0.0));
        Assert.Empty(BeatGridCalculator.GetBeatTimes(120.0, 3.0, 2.0));
    }

    [Fact]
    public void GetBeatTimes_NegativeDuration_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BeatGridCalculator.GetBeatTimes(120.0, 0.0, -1.0));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-120.0)]
    public void GetBeatTimes_ZeroOrNegativeBpm_Throws(double bpm)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BeatGridCalculator.GetBeatTimes(bpm, 0.0, 2.0));
    }

    [Fact]
    public void FromBpm_PreservesProperties()
    {
        var map = BeatMap.FromBpm(120.0, 0.25, 2.0);

        Assert.Equal(BeatMapSource.ManualBpm, map.Source);
        Assert.Equal(120.0, map.Bpm, 9);
        Assert.Equal(0.25, map.OffsetSeconds, 9);
        AssertBeatTimesEqual(new[] { 0.25, 0.75, 1.25, 1.75 }, map.BeatTimes);
    }

    // ---- 拍番号 ----

    [Fact]
    public void BeatIndexAt_Boundaries()
    {
        Assert.Equal(0, BeatGridCalculator.BeatIndexAt(0.0, 120.0, 0.0));
        Assert.Equal(0, BeatGridCalculator.BeatIndexAt(0.49, 120.0, 0.0));
        Assert.Equal(1, BeatGridCalculator.BeatIndexAt(0.5, 120.0, 0.0));
        Assert.Equal(3, BeatGridCalculator.BeatIndexAt(1.99, 120.0, 0.0));
        Assert.Equal(4, BeatGridCalculator.BeatIndexAt(2.0, 120.0, 0.0));
    }

    [Fact]
    public void BeatIndexAt_BeforeOffset_ReturnsMinusOne()
    {
        Assert.Equal(-1, BeatGridCalculator.BeatIndexAt(-0.1, 120.0, 0.0));
        Assert.Equal(-1, BeatGridCalculator.BeatIndexAt(0.1, 120.0, 0.25));
    }

    [Fact]
    public void BeatIndexAt_WithOffset()
    {
        Assert.Equal(0, BeatGridCalculator.BeatIndexAt(0.25, 120.0, 0.25));
        Assert.Equal(0, BeatGridCalculator.BeatIndexAt(0.74, 120.0, 0.25));
        Assert.Equal(1, BeatGridCalculator.BeatIndexAt(0.75, 120.0, 0.25));
    }

    // ---- 拍へのスナップ ----

    [Fact]
    public void SnapToBeat_WithinTolerance_ReturnsNearestBeat()
    {
        Assert.Equal(0.5, BeatGridCalculator.SnapToBeat(0.52, 120.0, 0.0));
        Assert.Equal(0.5, BeatGridCalculator.SnapToBeat(0.54, 120.0, 0.0));
        Assert.Equal(1.0, BeatGridCalculator.SnapToBeat(1.0, 120.0, 0.0));
    }

    [Fact]
    public void SnapToBeat_OutsideTolerance_ReturnsNull()
    {
        Assert.Null(BeatGridCalculator.SnapToBeat(0.56, 120.0, 0.0)); // 0.5 から 0.06 離れている
        Assert.Null(BeatGridCalculator.SnapToBeat(0.7, 120.0, 0.0));
        Assert.Null(BeatGridCalculator.SnapToBeat(-0.1, 120.0, 0.0)); // オフセットより前
    }

    [Fact]
    public void SnapToBeat_CustomTolerance()
    {
        Assert.Equal(0.5, BeatGridCalculator.SnapToBeat(0.58, 120.0, 0.0, 0.1));
        Assert.Null(BeatGridCalculator.SnapToBeat(0.58, 120.0, 0.0, 0.05));
    }

    [Fact]
    public void SnapToBeat_WithOffset()
    {
        Assert.Equal(0.75, BeatGridCalculator.SnapToBeat(0.77, 120.0, 0.25));
        Assert.Null(BeatGridCalculator.SnapToBeat(0.9, 120.0, 0.25));
    }

    [Fact]
    public void SnapToBeatTimes_NearestInList()
    {
        var beats = new[] { 0.0, 0.5, 1.0 };

        Assert.Equal(0.5, BeatGridCalculator.SnapToBeatTimes(0.48, beats));
        Assert.Equal(1.0, BeatGridCalculator.SnapToBeatTimes(0.97, beats));
        Assert.Null(BeatGridCalculator.SnapToBeatTimes(0.9, beats)); // 最も近い 1.0 まで 0.1
        Assert.Null(BeatGridCalculator.SnapToBeatTimes(0.5, Array.Empty<double>()));
    }

    // ---- 小節先頭 ----

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(8, true)]
    [InlineData(-1, false)]
    [InlineData(-4, false)]
    public void IsBarStart(int beatIndex, bool expected)
    {
        Assert.Equal(expected, BeatGridCalculator.IsBarStart(beatIndex));
    }
}
