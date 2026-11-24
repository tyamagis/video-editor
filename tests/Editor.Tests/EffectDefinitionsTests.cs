// 追加エフェクト9種の定義・評価テスト：全種類が EffectDefinitions に載っていること、
// 既定値が範囲内であること、キーフレーム・丸めが新種にも効くこと。
// （描画本体のテストは NewEffectsRenderTests.cs。）

using Editor.Core;
using Xunit;

namespace Editor.Tests;

/// <summary>EffectDefinitions・ClipEffect・TimelineEvaluator の新エフェクト9種まわりのテスト。</summary>
public sealed class EffectDefinitionsTests
{
    /// <summary>追加された9種（末尾。既存17種の順序・数値は変えない）。</summary>
    private static readonly EffectKind[] NewKinds =
    {
        EffectKind.ChromaticAberration,
        EffectKind.Fisheye,
        EffectKind.VhsGlitch,
        EffectKind.DropShadow,
        EffectKind.FilmGrain,
        EffectKind.ZoomBlur,
        EffectKind.MotionBlur,
        EffectKind.LuminanceKey,
        EffectKind.ToneCurve
    };

    /// <summary>全 EffectKind（既存17種＋新9種＝26種）に定義があること。</summary>
    [Fact]
    public void AllKinds_HaveDefinitions()
    {
        var kinds = Enum.GetValues<EffectKind>();
        Assert.Equal(26, kinds.Length);
        foreach (var kind in kinds)
        {
            var definition = EffectDefinitions.Get(kind);
            Assert.NotNull(definition);
            Assert.Equal(kind, definition.Kind);
            Assert.False(string.IsNullOrEmpty(definition.DisplayName));
            Assert.False(string.IsNullOrEmpty(definition.AmountLabel));
        }
    }

    /// <summary>新9種の既定値が Amount / Radius の範囲内であること。</summary>
    [Fact]
    public void NewKinds_DefaultsAreWithinRange()
    {
        foreach (var kind in NewKinds)
        {
            var definition = EffectDefinitions.Get(kind)!;
            Assert.InRange(definition.DefaultAmount, definition.AmountMin, definition.AmountMax);
            if (definition.UsesRadius)
            {
                Assert.InRange(definition.DefaultRadius, definition.RadiusMin!.Value, definition.RadiusMax!.Value);
                Assert.False(string.IsNullOrEmpty(definition.RadiusLabel));
            }
        }
    }

    /// <summary>新9種の Amount / Radius の丸めが定義の範囲に収まること。</summary>
    [Theory]
    [InlineData(EffectKind.ChromaticAberration, 100, 20)]
    [InlineData(EffectKind.Fisheye, 2, 1)]
    [InlineData(EffectKind.VhsGlitch, 2, 1)]
    [InlineData(EffectKind.DropShadow, 2, 1)]
    [InlineData(EffectKind.FilmGrain, 2, 1)]
    [InlineData(EffectKind.ZoomBlur, 2, 1)]
    [InlineData(EffectKind.MotionBlur, 100, 64)]
    [InlineData(EffectKind.LuminanceKey, 2, 1)]
    [InlineData(EffectKind.ToneCurve, 2, 1)]
    public void NewKinds_ClampAmount(EffectKind kind, double input, double expected)
    {
        Assert.Equal(expected, ClipEffect.ClampAmount(kind, input));
        var definition = EffectDefinitions.Get(kind)!;
        Assert.Equal(definition.AmountMin, ClipEffect.ClampAmount(kind, definition.AmountMin - 10));
    }

    /// <summary>新9種の Radius の丸め（使う種類は範囲内、使わない種類はそのまま）。</summary>
    [Theory]
    [InlineData(EffectKind.ChromaticAberration, 10, 0.5)]
    [InlineData(EffectKind.Fisheye, 10, 2)]
    [InlineData(EffectKind.VhsGlitch, 100, 8)]
    [InlineData(EffectKind.DropShadow, 100, 50)]
    [InlineData(EffectKind.FilmGrain, 100, 8)]
    [InlineData(EffectKind.ZoomBlur, 100, 16)]
    [InlineData(EffectKind.MotionBlur, 400, 360)]
    [InlineData(EffectKind.LuminanceKey, 10, 1)]
    [InlineData(EffectKind.ToneCurve, 10, 1)]
    public void NewKinds_ClampRadius(EffectKind kind, double input, double expected)
    {
        Assert.Equal(expected, ClipEffect.ClampRadius(kind, input));
    }

    /// <summary>新9種のキーフレームパラメータ名（"Effect:{種類}.Amount/Radius"）が評価で効くこと。</summary>
    [Theory]
    [InlineData(EffectKind.ChromaticAberration, true, 4, 12)]
    [InlineData(EffectKind.Fisheye, true, 0.5, 0.9)]
    [InlineData(EffectKind.VhsGlitch, true, 0.5, 0.8)]
    [InlineData(EffectKind.DropShadow, true, 0.5, 0.9)]
    [InlineData(EffectKind.DropShadow, false, 12, 30)]
    [InlineData(EffectKind.FilmGrain, true, 0.15, 0.6)]
    [InlineData(EffectKind.ZoomBlur, false, 8, 12)]
    [InlineData(EffectKind.MotionBlur, true, 16, 40)]
    [InlineData(EffectKind.LuminanceKey, true, 0.5, 0.7)]
    [InlineData(EffectKind.ToneCurve, true, 0, 0.5)]
    public void NewKinds_KeyframeEvaluation(EffectKind kind, bool isAmount, double fixedValue, double keyedValue)
    {
        var parameter = ClipEffect.KeyframeParameterName(kind, isAmount);
        var clip = new Clip { StartSeconds = 0, DurationSeconds = 10 };
        var effect = new ClipEffect { Kind = kind, Amount = fixedValue, Radius = fixedValue };
        clip.Effects.Add(effect);
        clip.KeyframeTracks.Add(new KeyframeTrack
        {
            ParameterName = parameter,
            Keyframes =
            {
                new Keyframe { TimeSeconds = 0, Value = fixedValue },
                new Keyframe { TimeSeconds = 2, Value = keyedValue }
            }
        });

        var atStart = TimelineEvaluator.EvaluateClip(clip, 0);
        var atEnd = TimelineEvaluator.EvaluateClip(clip, 2);
        var evaluated = isAmount ? atEnd.Effects[0].Amount : atEnd.Effects[0].Radius;
        var initial = isAmount ? atStart.Effects[0].Amount : atStart.Effects[0].Radius;
        Assert.Equal(fixedValue, initial);
        Assert.Equal(keyedValue, evaluated);
    }

    /// <summary>新種のエフェクト量が音連動バインドの対象になること（タスク1の動的一覧＋評価は種類に依存しない）。</summary>
    [Fact]
    public void NewKinds_AudioBindingApplies()
    {
        // 音源クリップ（audio）と、色収差 Amount にバインドを持つ対象クリップ（target）。
        var audio = new Clip
        {
            Id = "audio1",
            Kind = ClipKind.Audio,
            StartSeconds = 0,
            DurationSeconds = 10,
            SourcePath = "audio.mp3"
        };
        var target = new Clip { StartSeconds = 0, DurationSeconds = 10 };
        target.Effects.Add(new ClipEffect { Kind = EffectKind.ChromaticAberration, Amount = 4 });
        target.AudioBindings.Add(new AudioBinding
        {
            ParameterName = ClipEffect.KeyframeParameterName(EffectKind.ChromaticAberration, isAmount: true),
            SourceClipId = "audio1",
            LowHz = 20,
            HighHz = 80,
            MinValue = 0,
            MaxValue = 8,
            Combine = AudioBindCombine.Add
        });
        var project = new EditorProject();
        project.Tracks.Add(new Track { Clips = { audio, target } });

        // 帯域エネルギー 1.0（最大）なら、キーフレーム値 4 に (8 - 0) が加算されて 12 になる。
        var evaluated = TimelineEvaluator.EvaluateClip(target, 1, 1.0, project, new FixedEnergyProvider(1.0));
        Assert.Equal(12, evaluated.Effects[0].Amount);
        // 無音（エネルギー 0）ならキーフレーム値のまま。
        var silent = TimelineEvaluator.EvaluateClip(target, 1, 1.0, project, new FixedEnergyProvider(0.0));
        Assert.Equal(4, silent.Effects[0].Amount);
    }

    /// <summary>固定の帯域エネルギーを返すテスト用スタブ。</summary>
    private sealed class FixedEnergyProvider : IAudioBandEnergyProvider
    {
        private readonly double _energy;

        public FixedEnergyProvider(double energy) => _energy = energy;

        public void Precompute(string clipId, string sourcePath, double frameRate)
        {
        }

        public double GetBandEnergy(string clipId, double timeSeconds, double lowHz, double highHz)
            => _energy;
    }

    /// <summary>既存17種の数値（.veproj 互換）が変わっていないこと。</summary>
    [Fact]
    public void ExistingKinds_ValuesUnchanged()
    {
        Assert.Equal(0, (int)EffectKind.Blur);
        Assert.Equal(1, (int)EffectKind.Glow);
        Assert.Equal(2, (int)EffectKind.RgbShift);
        Assert.Equal(3, (int)EffectKind.Slice);
        Assert.Equal(4, (int)EffectKind.Warp);
        Assert.Equal(5, (int)EffectKind.ChromaKey);
        Assert.Equal(6, (int)EffectKind.Mosaic);
        Assert.Equal(7, (int)EffectKind.Vignette);
        Assert.Equal(8, (int)EffectKind.Echo);
        Assert.Equal(9, (int)EffectKind.Invert);
        Assert.Equal(10, (int)EffectKind.HueShift);
        Assert.Equal(11, (int)EffectKind.Saturation);
        Assert.Equal(12, (int)EffectKind.Monochrome);
        Assert.Equal(13, (int)EffectKind.Sepia);
        Assert.Equal(14, (int)EffectKind.Posterize);
        Assert.Equal(15, (int)EffectKind.Kaleidoscope);
        Assert.Equal(16, (int)EffectKind.Noise);
        Assert.Equal(17, (int)EffectKind.ChromaticAberration);
        Assert.Equal(25, (int)EffectKind.ToneCurve);
    }
}
