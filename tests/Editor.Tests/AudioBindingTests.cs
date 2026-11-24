// 評価エンジンの単体テスト：周波数帯駆動バインド（AudioBinding）。
// MapValue（帯域エネルギー 0.0〜1.0 の写像）と CombineWith（合成方法）の純粋計算のみを対象とする。
// FFT・NAudio・実ファイルには依存しない。

using Editor.Core;
using Xunit;

namespace Editor.Tests;

/// <summary>周波数帯駆動バインド（AudioBinding.MapValue / CombineWith）のテスト。</summary>
public sealed class AudioBindingTests
{
    private static AudioBinding Binding(
        double minValue = 1.0, double maxValue = 1.12,
        double gain = 1.0, double threshold = 0.0, bool invert = false,
        AudioBindCombine combine = AudioBindCombine.Add)
        => new()
        {
            ParameterName = "Scale",
            MinValue = minValue,
            MaxValue = maxValue,
            Gain = gain,
            Threshold = threshold,
            Invert = invert,
            Combine = combine
        };

    // ---- MapValue：基本の写像 ----

    [Fact]
    public void MapValue_EnergyZero_ReturnsMinValue()
    {
        Assert.Equal(1.0, Binding().MapValue(0.0), 9);
    }

    [Fact]
    public void MapValue_EnergyOne_ReturnsMaxValue()
    {
        Assert.Equal(1.12, Binding().MapValue(1.0), 9);
    }

    [Fact]
    public void MapValue_EnergyHalf_ReturnsMidpoint()
    {
        Assert.Equal(1.06, Binding().MapValue(0.5), 9);
    }

    [Fact]
    public void MapValue_CustomRange_MapsLinearly()
    {
        var binding = Binding(minValue: 0.0, maxValue: 2.0);

        Assert.Equal(0.0, binding.MapValue(0.0), 9);
        Assert.Equal(0.5, binding.MapValue(0.25), 9);
        Assert.Equal(1.5, binding.MapValue(0.75), 9);
        Assert.Equal(2.0, binding.MapValue(1.0), 9);
    }

    [Fact]
    public void MapValue_EnergyOutsideRange_IsClamped()
    {
        var binding = Binding();

        Assert.Equal(1.0, binding.MapValue(-0.5), 9); // 0 未満は 0 と同じ
        Assert.Equal(1.12, binding.MapValue(1.5), 9); // 1 超過は 1 と同じ
    }

    [Fact]
    public void MapValue_InvertedRange_DecreasesWithEnergy()
    {
        // Min > Max の範囲指定でもそのまま線形写像される（音が大きいほど出力が小さい）。
        var binding = Binding(minValue: 2.0, maxValue: 1.0);

        Assert.Equal(2.0, binding.MapValue(0.0), 9);
        Assert.Equal(1.5, binding.MapValue(0.5), 9);
        Assert.Equal(1.0, binding.MapValue(1.0), 9);
    }

    // ---- MapValue：反転 ----

    [Fact]
    public void MapValue_Invert_ReversesMapping()
    {
        var binding = Binding(invert: true);

        Assert.Equal(1.12, binding.MapValue(0.0), 9); // 無音で最大
        Assert.Equal(1.0, binding.MapValue(1.0), 9); // 最大で最小
        Assert.Equal(1.09, binding.MapValue(0.25), 9); // 0.25 -> x=0.75
    }

    // ---- MapValue：ゲイン ----

    [Fact]
    public void MapValue_Gain_ScalesEnergy()
    {
        var binding = Binding(gain: 2.0);

        Assert.Equal(1.06, binding.MapValue(0.25), 9); // 0.25 * 2 = 0.5
        Assert.Equal(1.12, binding.MapValue(0.75), 9); // 0.75 * 2 = 1.5 -> 1 に丸め
        Assert.Equal(1.12, binding.MapValue(0.5), 9); // 0.5 * 2 = 1.0
    }

    [Fact]
    public void MapValue_GainZero_AlwaysReturnsMinValue()
    {
        var binding = Binding(gain: 0.0);

        Assert.Equal(1.0, binding.MapValue(1.0), 9);
        Assert.Equal(1.0, binding.MapValue(0.5), 9);
    }

    // ---- MapValue：しきい値 ----

    [Fact]
    public void MapValue_Threshold_BelowThresholdIsZero()
    {
        var binding = Binding(threshold: 0.5);

        Assert.Equal(1.0, binding.MapValue(0.25), 9); // しきい値未満は 0
        Assert.Equal(1.0, binding.MapValue(0.5), 9); // しきい値ちょうどは 0
        Assert.Equal(1.06, binding.MapValue(0.75), 9); // (0.75-0.5)/0.5 = 0.5
        Assert.Equal(1.12, binding.MapValue(1.0), 9); // (1.0-0.5)/0.5 = 1.0
    }

    [Fact]
    public void MapValue_ThresholdAndGain_Combine()
    {
        // しきい値で正規化してからゲインを掛ける：0.4 -> (0.4-0.2)/0.8=0.25 -> *2=0.5
        var binding = Binding(threshold: 0.2, gain: 2.0);

        Assert.Equal(1.06, binding.MapValue(0.4), 9);
        Assert.Equal(1.12, binding.MapValue(0.6), 9); // (0.6-0.2)/0.8=0.5 -> *2=1.0
        Assert.Equal(1.0, binding.MapValue(0.1), 9); // しきい値未満
    }

    // ---- CombineWith：合成方法 ----

    [Fact]
    public void CombineWith_Add_AddsDeltaFromMin()
    {
        var binding = Binding(combine: AudioBindCombine.Add);

        // 無音時（出力 = Min）はキーフレーム値のまま。
        Assert.Equal(1.0, binding.CombineWith(1.0, 1.0), 9);
        // 出力が Min より 0.06 大きい分だけ加算。
        Assert.Equal(1.06, binding.CombineWith(1.0, 1.06), 9);
        Assert.Equal(0.92, binding.CombineWith(0.8, 1.12), 9);
    }

    [Fact]
    public void CombineWith_Add_WithInvertedRange_SubtractsDelta()
    {
        // Min > Max の範囲では出力が Min を下回る分だけキーフレーム値から引かれる。
        var binding = Binding(minValue: 1.12, maxValue: 1.0, combine: AudioBindCombine.Add);

        Assert.Equal(0.88, binding.CombineWith(1.0, 1.0), 9); // 1.0 + (1.0 - 1.12)
    }

    [Fact]
    public void CombineWith_Multiply_MultipliesByMappedValue()
    {
        var binding = Binding(combine: AudioBindCombine.Multiply);

        Assert.Equal(2.12, binding.CombineWith(2.0, 1.06), 9);
        Assert.Equal(2.0, binding.CombineWith(2.0, 1.0), 9); // 出力 1.0 なら変化なし
        Assert.Equal(0.56, binding.CombineWith(0.5, 1.12), 9);
    }

    [Fact]
    public void CombineWith_Override_ReplacesKeyframeValue()
    {
        var binding = Binding(combine: AudioBindCombine.Override);

        Assert.Equal(1.06, binding.CombineWith(2.0, 1.06), 9);
        Assert.Equal(1.0, binding.CombineWith(2.0, 1.0), 9);
        Assert.Equal(1.12, binding.CombineWith(0.0, 1.12), 9);
    }
}
