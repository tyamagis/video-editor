// App層入力検証監査（2026-11-26）の回帰テスト。
// Editor.App のテキスト欄は double プロパティへ直接バインドされるため、
// "NaN" / "Infinity" が WPF の変換を通って Core モデルへ流れ込む。
// Math.Clamp は NaN を素通しし、<= 0 等の範囲チェックも NaN には効かないため、
// Core 境界（Clamp* とプロパティセッター）で非有限値を既定値へ戻すことを担保する。

using Editor.Core;
using Xunit;

namespace Editor.Tests;

/// <summary>UIテキスト入力から流れ込む非有限値に対する Core 境界の回帰テスト。</summary>
public sealed class AppInputValidationAuditTests
{
    private static readonly double[] NonFinite = [double.NaN, double.PositiveInfinity, double.NegativeInfinity];

    // ---- Clamp* の NaN フォールバック ----

    [Fact]
    public void ClampTargetDb_NaN_ReturnsDefault()
        => Assert.Equal(AudioNormalizer.DefaultTargetDb, AudioNormalizer.ClampTargetDb(double.NaN));

    [Fact]
    public void ClampTargetDb_Finite_StillClamped()
    {
        Assert.Equal(-30.0, AudioNormalizer.ClampTargetDb(-100));
        Assert.Equal(0.0, AudioNormalizer.ClampTargetDb(5));
        Assert.Equal(-3.0, AudioNormalizer.ClampTargetDb(-3.0));
    }

    [Fact]
    public void VisualizationClamps_NaN_ReturnDefaults()
    {
        Assert.Equal(1.0, VisualizationSettings.ClampGain(double.NaN));
        Assert.Equal(2.0, VisualizationSettings.ClampLineWidth(double.NaN));
        Assert.Equal(0.1, VisualizationSettings.ClampWindowSeconds(double.NaN));
        Assert.Equal(0.25, VisualizationSettings.ClampTrailSeconds(double.NaN));
    }

    [Fact]
    public void VisualizationClamps_Infinity_StillClampedToRange()
    {
        Assert.Equal(4.0, VisualizationSettings.ClampGain(double.PositiveInfinity));
        Assert.Equal(0.5, VisualizationSettings.ClampLineWidth(double.NegativeInfinity));
    }

    [Fact]
    public void ClipEffectClamps_NaN_ReturnKindDefaults()
    {
        // Glow: DefaultAmount 0.5 / DefaultRadius 16。ToneCurve: DefaultThird 0。
        Assert.Equal(0.5, ClipEffect.ClampAmount(EffectKind.Glow, double.NaN));
        Assert.Equal(16.0, ClipEffect.ClampRadius(EffectKind.Glow, double.NaN));
        Assert.Equal(0.0, ClipEffect.ClampMidtones(EffectKind.ToneCurve, double.NaN));
    }

    [Fact]
    public void ClipEffectClamps_Finite_StillClamped()
    {
        Assert.Equal(1.0, ClipEffect.ClampAmount(EffectKind.Glow, 99));
        Assert.Equal(1.0, ClipEffect.ClampRadius(EffectKind.Glow, 0));
    }

    // ---- Clip の直接バインドプロパティ（開始・長さ・フォント等） ----

    [Fact]
    public void Clip_NonFiniteTimingAndFont_ReplacedByDefaults()
    {
        foreach (var bad in NonFinite)
        {
            var clip = new Clip
            {
                StartSeconds = bad,
                DurationSeconds = bad,
                InPointSeconds = bad,
                FontSize = bad,
                TransitionDurationSeconds = bad,
                Brightness = bad,
                Contrast = bad,
                NoiseReductionStrength = bad,
                NormalizeTargetDb = bad
            };
            Assert.Equal(0, clip.StartSeconds);
            Assert.Equal(0, clip.DurationSeconds);
            Assert.Equal(0, clip.InPointSeconds);
            Assert.Equal(64, clip.FontSize);
            Assert.Equal(1.0, clip.TransitionDurationSeconds);
            Assert.Equal(0, clip.Brightness);
            Assert.Equal(1.0, clip.Contrast);
            Assert.Equal(0.5, clip.NoiseReductionStrength);
            Assert.Equal(AudioNormalizer.DefaultTargetDb, clip.NormalizeTargetDb);
            Assert.True(double.IsFinite(clip.EndSeconds));
        }
    }

    [Fact]
    public void Clip_FiniteValues_PreservedExactly()
    {
        var clip = new Clip
        {
            StartSeconds = 2.5,
            DurationSeconds = 3.5,
            InPointSeconds = 1.25,
            FontSize = 48,
            TransitionDurationSeconds = -1.0, // 評価側で「無し」扱いの既存セマンティクスを保つ
            Brightness = 0.5,
            Contrast = 1.5,
            NoiseReductionStrength = 0.25,
            NormalizeTargetDb = -3.0
        };
        Assert.Equal(2.5, clip.StartSeconds);
        Assert.Equal(3.5, clip.DurationSeconds);
        Assert.Equal(1.25, clip.InPointSeconds);
        Assert.Equal(48, clip.FontSize);
        Assert.Equal(-1.0, clip.TransitionDurationSeconds);
        Assert.Equal(0.5, clip.Brightness);
        Assert.Equal(1.5, clip.Contrast);
        Assert.Equal(0.25, clip.NoiseReductionStrength);
        Assert.Equal(-3.0, clip.NormalizeTargetDb);
    }

    // ---- プロジェクト（BPM・書き出し範囲・拍オフセット） ----

    [Fact]
    public void Project_NonFiniteBpmAndExportRange_ReplacedByDefaults()
    {
        foreach (var bad in NonFinite)
        {
            var project = new EditorProject
            {
                Bpm = bad,
                ExportInSeconds = bad,
                ExportOutSeconds = bad,
                BeatOffsetSeconds = bad
            };
            Assert.Equal(120.0, project.Bpm);
            Assert.Equal(0, project.ExportInSeconds);
            Assert.Equal(0, project.ExportOutSeconds);
            Assert.Equal(0, project.BeatOffsetSeconds);
            // 拍グリッド計算が例外にならないこと（BPM NaN のときの実害）。
            Assert.True(BeatGridCalculator.BeatIntervalSeconds(project.Bpm) > 0);
        }
    }

    // ---- 可視化・バインド・キーフレーム・マーカー ----

    [Fact]
    public void VisualizationSettings_NonFinite_ReplacedByDefaults()
    {
        foreach (var bad in NonFinite)
        {
            var viz = new VisualizationSettings
            {
                LowHz = bad, HighHz = bad, Gain = bad,
                LineWidth = bad, WindowSeconds = bad, TrailSeconds = bad
            };
            Assert.Equal(20, viz.LowHz);
            Assert.Equal(16000, viz.HighHz);
            Assert.Equal(1.0, viz.Gain);
            Assert.Equal(2.0, viz.LineWidth);
            Assert.Equal(0.1, viz.WindowSeconds);
            Assert.Equal(0.25, viz.TrailSeconds);
        }
    }

    [Fact]
    public void AudioBinding_NonFinite_ReplacedByDefaults_AndMapValueStaysFinite()
    {
        foreach (var bad in NonFinite)
        {
            var binding = new AudioBinding
            {
                LowHz = bad, HighHz = bad, MinValue = bad,
                MaxValue = bad, Gain = bad, Threshold = bad
            };
            Assert.Equal(20, binding.LowHz);
            Assert.Equal(80, binding.HighHz);
            Assert.Equal(1.0, binding.MinValue);
            Assert.Equal(1.12, binding.MaxValue);
            Assert.Equal(1.0, binding.Gain);
            Assert.Equal(0.0, binding.Threshold);
            Assert.True(double.IsFinite(binding.MapValue(0.5)));
        }
    }

    [Fact]
    public void ClipEffect_NonFiniteParameters_ReplacedByZero()
    {
        foreach (var bad in NonFinite)
        {
            var effect = new ClipEffect { Amount = bad, Radius = bad, Midtones = bad };
            Assert.Equal(0, effect.Amount);
            Assert.Equal(0, effect.Radius);
            Assert.Equal(0, effect.Midtones);
        }
    }

    [Fact]
    public void Keyframe_NonFinite_ReplacedByZero()
    {
        foreach (var bad in NonFinite)
        {
            var keyframe = new Keyframe(bad, bad);
            Assert.Equal(0, keyframe.TimeSeconds);
            Assert.Equal(0, keyframe.Value);
        }
    }

    [Fact]
    public void Marker_NonFiniteTime_ReplacedByZero()
    {
        foreach (var bad in NonFinite)
        {
            var marker = new Marker { TimeSeconds = bad };
            Assert.Equal(0, marker.TimeSeconds);
        }
    }
}
