// 評価エンジンの単体テスト：FFTと帯域エネルギー（FftAnalyzer）。
// Analyze（サンプル列 -> スペクトル）と BandEnergy（帯域エネルギーの正規化）の
// 純粋計算のみを対象とする。音声ファイルの読み取り（NAudio）には依存しない。

using Editor.Audio;
using Xunit;

namespace Editor.Tests;

/// <summary>FftAnalyzer（Analyze / BandEnergy）のテスト。</summary>
public sealed class FftBandEnergyTests
{
    private static SpectrumFrame Frame(double[] frequencies, double[] magnitudes)
        => new() { TimeSeconds = 0.0, Frequencies = frequencies, Magnitudes = magnitudes };

    // ---- BandEnergy ----

    [Fact]
    public void BandEnergy_HalfBand_ReturnsRatio()
    {
        // 10 ビン均等、20〜50Hz の 4 ビン -> 0.4
        var frame = Frame(
            new[] { 0.0, 10, 20, 30, 40, 50, 60, 70, 80, 90 },
            new[] { 1.0, 1, 1, 1, 1, 1, 1, 1, 1, 1 });

        Assert.Equal(0.4, FftAnalyzer.BandEnergy(frame, 20, 50), 9);
    }

    [Fact]
    public void BandEnergy_FullBand_ReturnsOne()
    {
        var frame = Frame(
            new[] { 0.0, 10, 20, 30 },
            new[] { 2.0, 3, 1, 4 });

        Assert.Equal(1.0, FftAnalyzer.BandEnergy(frame, 0, 100), 9);
    }

    [Fact]
    public void BandEnergy_EmptyBand_ReturnsZero()
    {
        var frame = Frame(
            new[] { 0.0, 10, 20, 30 },
            new[] { 2.0, 3, 1, 4 });

        Assert.Equal(0.0, FftAnalyzer.BandEnergy(frame, 100, 200), 9);
    }

    [Fact]
    public void BandEnergy_SilentFrame_ReturnsZero()
    {
        var frame = Frame(
            new[] { 0.0, 10, 20, 30 },
            new[] { 0.0, 0, 0, 0 });

        Assert.Equal(0.0, FftAnalyzer.BandEnergy(frame, 0, 100), 9);
    }

    [Fact]
    public void BandEnergy_BoundariesAreInclusive()
    {
        // 20Hz のビンだけにエネルギーがある。帯域 [20, 20] に含まれる。
        var frame = Frame(
            new[] { 0.0, 10, 20, 30 },
            new[] { 0.0, 0, 3, 0 });

        Assert.Equal(1.0, FftAnalyzer.BandEnergy(frame, 20, 20), 9);
        Assert.Equal(0.0, FftAnalyzer.BandEnergy(frame, 21, 30), 9);
    }

    // ---- Analyze ----

    [Fact]
    public void Analyze_FrameShape()
    {
        var analyzer = new FftAnalyzer();
        var samples = new float[2048];

        var frame = analyzer.Analyze(samples, 44100, 1.5);

        Assert.Equal(1.5, frame.TimeSeconds, 9);
        Assert.Equal(1024, frame.Frequencies.Length); // fftSize / 2
        Assert.Equal(1024, frame.Magnitudes.Length);
        Assert.Equal(0.0, frame.Frequencies[0], 9);
        Assert.Equal(44100.0 / 2048, frame.Frequencies[1], 9); // ビン幅
    }

    [Fact]
    public void Analyze_SineWave_PeakNear440Hz()
    {
        var analyzer = new FftAnalyzer();
        const int sampleRate = 44100;
        const int fftSize = 2048;
        var samples = new float[fftSize];
        for (var i = 0; i < fftSize; i++)
            samples[i] = (float)Math.Sin(2 * Math.PI * 440 * i / sampleRate);

        var frame = analyzer.Analyze(samples, sampleRate, 0.0, fftSize);

        var peakIndex = 0;
        for (var i = 1; i < frame.Magnitudes.Length; i++)
        {
            if (frame.Magnitudes[i] > frame.Magnitudes[peakIndex])
                peakIndex = i;
        }

        // ビン幅は約 21.5Hz なので、ピークは 440Hz から 1 ビン以内にあるはず。
        Assert.True(Math.Abs(frame.Frequencies[peakIndex] - 440) <= 44100.0 / fftSize,
            $"peak at {frame.Frequencies[peakIndex]}Hz, expected near 440Hz");
        Assert.True(frame.Magnitudes[peakIndex] > 0);
    }

    [Fact]
    public void Analyze_ConstantSignal_PeakAtDc()
    {
        var analyzer = new FftAnalyzer();
        var samples = new float[2048];
        Array.Fill(samples, 1.0f);

        var frame = analyzer.Analyze(samples, 44100, 0.0);

        var peakIndex = 0;
        for (var i = 1; i < frame.Magnitudes.Length; i++)
        {
            if (frame.Magnitudes[i] > frame.Magnitudes[peakIndex])
                peakIndex = i;
        }

        Assert.Equal(0, peakIndex);
    }

    [Fact]
    public void Analyze_Silence_AllMagnitudesZero()
    {
        var analyzer = new FftAnalyzer();
        var samples = new float[2048];

        var frame = analyzer.Analyze(samples, 44100, 0.0);

        Assert.All(frame.Magnitudes, m => Assert.Equal(0.0, m, 9));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    [InlineData(1000)] // 2 の累乗ではない
    [InlineData(2047)]
    public void Analyze_InvalidFftSize_Throws(int fftSize)
    {
        var analyzer = new FftAnalyzer();
        var samples = new float[2048];

        Assert.Throws<ArgumentException>(() => analyzer.Analyze(samples, 44100, 0.0, fftSize));
    }
}
