// テスト監査（2026-11-25）で追加：機械生成テキスト（ffprobe 出力・ffmpeg 進捗）の
// 解析が CurrentCulture で壊れないことの確認。
// MediaProbe の duration / フレームレート解析は修正前、カンマ小数のロケールで
// "2.500000" を 2500000 秒と誤読した（実バグの回帰テスト）。

using System.Globalization;
using Editor.Media;
using Xunit;

namespace Editor.Tests;

/// <summary>ffprobe / ffmpeg 出力のカルチャ非依存解析のテスト。</summary>
public sealed class TestAuditCultureTests
{
    /// <summary>指定カルチャで処理を実行し、元のカルチャに戻す。</summary>
    private static T WithCulture<T>(string cultureName, Func<T> action)
    {
        var prevCulture = Thread.CurrentThread.CurrentCulture;
        var prevUiCulture = Thread.CurrentThread.CurrentUICulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);
            return action();
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = prevCulture;
            Thread.CurrentThread.CurrentUICulture = prevUiCulture;
        }
    }

    [Fact]
    public void ParseDurationSeconds_UnderCommaDecimalCulture_ReadsDotDecimal()
    {
        var seconds = WithCulture("de-DE", () => MediaProbe.ParseDurationSeconds("2.500000"));
        Assert.Equal(2.5, seconds);
    }

    [Fact]
    public void ParseDurationSeconds_InvalidInput_ReturnsNull()
    {
        Assert.Null(MediaProbe.ParseDurationSeconds(null));
        Assert.Null(MediaProbe.ParseDurationSeconds(""));
        Assert.Null(MediaProbe.ParseDurationSeconds("abc"));
    }

    [Fact]
    public void ParseFrameRate_Fraction_UnderCommaDecimalCulture()
    {
        var fps = WithCulture("de-DE", () => MediaProbe.ParseFrameRate("30000/1001"));
        Assert.Equal(29.97, fps, 2);
    }

    [Fact]
    public void ParseFrameRate_Decimal_UnderCommaDecimalCulture()
    {
        // 修正前は de-DE で "29.97" が 2997 と誤読された。
        var fps = WithCulture("de-DE", () => MediaProbe.ParseFrameRate("29.97"));
        Assert.Equal(29.97, fps, 2);
    }

    [Theory]
    [InlineData(null, 0.0)]
    [InlineData("", 0.0)]
    [InlineData("0/0", 0.0)]
    [InlineData("25/0", 0.0)]
    [InlineData("abc", 0.0)]
    [InlineData("25", 25.0)]
    [InlineData("50/2", 25.0)]
    public void ParseFrameRate_EdgeCases(string? value, double expected)
        => Assert.Equal(expected, MediaProbe.ParseFrameRate(value), 6);

    [Fact]
    public void FfmpegProgressTime_UnderCommaDecimalCulture_Parses()
    {
        // ffmpeg の進捗 "time=00:00:02.50" は TimeSpan 解析のため元々カルチャ安全。
        // その前提が崩れていないことを固定する。
        var seconds = WithCulture("de-DE",
            () => FfmpegProcess.TryParseProgressTime("frame=  100 fps= 25 time=00:00:02.50 bitrate=1000kbits/s"));
        Assert.Equal(2.5, seconds);
    }

    [Fact]
    public void FfmpegProgressTime_NoTimeToken_ReturnsNull()
    {
        Assert.Null(FfmpegProcess.TryParseProgressTime("frame=  100 fps= 25 bitrate=1000kbits/s"));
    }
}
