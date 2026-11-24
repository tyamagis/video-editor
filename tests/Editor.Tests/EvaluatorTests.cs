// 評価エンジンの単体テスト：TimelineEvaluator（キーフレーム＋バインド＋トランジションの統合評価）。
// 音声は IAudioBandEnergyProvider のテスト用スタブで差し替え、実ファイル・NAudio・FFmpeg には依存しない。

using Editor.Core;
using Xunit;

namespace Editor.Tests;

/// <summary>TimelineEvaluator（EvaluateClip / EvaluateProject / GetEffectiveTransitionDuration）のテスト。</summary>
public sealed class EvaluatorTests
{
    /// <summary>固定の帯域エネルギーを返し、呼び出し引数を記録するテスト用スタブ。</summary>
    private sealed class FixedEnergyProvider : IAudioBandEnergyProvider
    {
        private readonly double _energy;

        public List<(string ClipId, double TimeSeconds, double LowHz, double HighHz)> Calls { get; } = new();

        public FixedEnergyProvider(double energy) => _energy = energy;

        public void Precompute(string clipId, string sourcePath, double frameRate)
        {
            // テストでは事前解析は不要。
        }

        public double GetBandEnergy(string clipId, double timeSeconds, double lowHz, double highHz)
        {
            Calls.Add((clipId, timeSeconds, lowHz, highHz));
            return _energy;
        }
    }

    private static Clip MakeClip(double startSeconds = 0, double durationSeconds = 10)
        => new() { StartSeconds = startSeconds, DurationSeconds = durationSeconds };

    /// <summary>
    /// 音源クリップ（audio）とバインド付きの対象クリップ（target）を持つプロジェクトを作る。
    /// target は "Scale" に Add 合成・Min 1.0・Max 1.12・20-80Hz のバインドを持つ。
    /// </summary>
    private static (EditorProject Project, Clip AudioClip, Clip TargetClip) CreateBindingSetup(
        double audioStartSeconds = 0, double audioInPointSeconds = 0, double audioDurationSeconds = 10)
    {
        var project = new EditorProject();
        var track = project.AddTrack(TrackKind.Video, "V1");

        var audio = new Clip
        {
            Id = "audio1",
            Kind = ClipKind.Audio,
            SourcePath = "C:\\music.mp3",
            StartSeconds = audioStartSeconds,
            InPointSeconds = audioInPointSeconds,
            DurationSeconds = audioDurationSeconds
        };
        var target = MakeClip();
        target.AudioBindings.Add(new AudioBinding
        {
            ParameterName = "Scale",
            SourceClipId = "audio1",
            LowHz = 20,
            HighHz = 80,
            MinValue = 1.0,
            MaxValue = 1.12,
            Combine = AudioBindCombine.Add,
            Enabled = true
        });

        track.Clips.Add(audio);
        track.Clips.Add(target);
        return (project, audio, target);
    }

    // ---- EvaluateClip：キーフレームのみ ----

    [Fact]
    public void EvaluateClip_NoKeyframes_ReturnsDefaults()
    {
        var state = TimelineEvaluator.EvaluateClip(MakeClip(), 5.0);

        Assert.Equal(1.0, state.Opacity, 9);
        Assert.Equal(1.0, state.Scale, 9);
        Assert.Equal(0.0, state.RotationDegrees, 9);
        Assert.Equal(0.0, state.PositionX, 9);
        Assert.Equal(0.0, state.PositionY, 9);
        Assert.Equal(0.0, state.Brightness, 9);
        Assert.Equal(1.0, state.Contrast, 9);
        Assert.Equal(BlendMode.Normal, state.BlendMode);
        Assert.Equal(5.0, state.TimeSeconds, 9);
    }

    [Fact]
    public void EvaluateClip_KeyframeOpacity_InterpolatesByLocalTime()
    {
        // クリップ先頭 10 秒、Opacity 0 -> 1（相対 0〜2 秒）。
        var clip = MakeClip(startSeconds: 10, durationSeconds: 5);
        clip.EnsureTrack("Opacity").Keyframes.AddRange(
            new[] { new Keyframe(0.0, 0.0), new Keyframe(2.0, 1.0) });

        Assert.Equal(0.0, TimelineEvaluator.EvaluateClip(clip, 10.0).Opacity, 9);
        Assert.Equal(0.5, TimelineEvaluator.EvaluateClip(clip, 11.0).Opacity, 9);
        Assert.Equal(1.0, TimelineEvaluator.EvaluateClip(clip, 12.0).Opacity, 9);
        Assert.Equal(0.0, TimelineEvaluator.EvaluateClip(clip, 9.0).Opacity, 9); // 開始より前は最初の値
    }

    [Fact]
    public void EvaluateClip_ScaleKeyframe()
    {
        var clip = MakeClip();
        clip.EnsureTrack("Scale").Keyframes.AddRange(
            new[] { new Keyframe(0.0, 1.0), new Keyframe(4.0, 2.0) });

        Assert.Equal(1.5, TimelineEvaluator.EvaluateClip(clip, 2.0).Scale, 9);
    }

    [Fact]
    public void EvaluateClip_TransitionOpacity_MultipliesOpacity()
    {
        var clip = MakeClip();

        Assert.Equal(0.5, TimelineEvaluator.EvaluateClip(clip, 1.0, 0.5).Opacity, 9);
        Assert.Equal(0.25, TimelineEvaluator.EvaluateClip(clip, 1.0, 0.25).Opacity, 9);
    }

    [Fact]
    public void EvaluateClip_OpacityIsClampedToZeroOne()
    {
        var clip = MakeClip();

        Assert.Equal(1.0, TimelineEvaluator.EvaluateClip(clip, 1.0, 2.0).Opacity, 9);
        Assert.Equal(0.0, TimelineEvaluator.EvaluateClip(clip, 1.0, -1.0).Opacity, 9);
    }

    // ---- EvaluateClip：エフェクトのキーフレーム ----

    [Fact]
    public void EvaluateClip_EffectWithoutKeyframes_KeepsFixedValues()
    {
        // キーフレームが無ければ旧プロジェクトと同じく固定値が使われる。
        var clip = MakeClip();
        clip.Effects.Add(new ClipEffect { Kind = EffectKind.Blur, Amount = 8 });
        clip.Effects.Add(new ClipEffect { Kind = EffectKind.Glow, Amount = 0.5, Radius = 16 });

        var state = TimelineEvaluator.EvaluateClip(clip, 3.0);

        Assert.Equal(2, state.Effects.Count);
        Assert.Equal(8.0, state.Effects[0].Amount, 9);
        Assert.Equal(0.5, state.Effects[1].Amount, 9);
        Assert.Equal(16.0, state.Effects[1].Radius, 9);
    }

    [Fact]
    public void EvaluateClip_EffectAmountKeyframe_InterpolatesByLocalTime()
    {
        // ブラー半径 0 -> 100（相対 0〜2 秒）。時刻はクリップ先頭からの相対秒で評価する。
        var clip = MakeClip(startSeconds: 10, durationSeconds: 5);
        clip.Effects.Add(new ClipEffect { Kind = EffectKind.Blur, Amount = 8 });
        clip.EnsureTrack("Effect:Blur.Amount").Keyframes.AddRange(
            new[] { new Keyframe(0.0, 0.0), new Keyframe(2.0, 100.0) });

        Assert.Equal(0.0, TimelineEvaluator.EvaluateClip(clip, 10.0).Effects[0].Amount, 9);
        Assert.Equal(50.0, TimelineEvaluator.EvaluateClip(clip, 11.0).Effects[0].Amount, 9);
        Assert.Equal(100.0, TimelineEvaluator.EvaluateClip(clip, 12.0).Effects[0].Amount, 9);
    }

    [Fact]
    public void EvaluateClip_EffectRadiusKeyframe_InterpolatesSeparately()
    {
        // グローの Amount は固定、Radius だけキーフレームで動かす。
        var clip = MakeClip();
        clip.Effects.Add(new ClipEffect { Kind = EffectKind.Glow, Amount = 0.5, Radius = 16 });
        clip.EnsureTrack("Effect:Glow.Radius").Keyframes.AddRange(
            new[] { new Keyframe(0.0, 16.0), new Keyframe(4.0, 100.0) });

        var state = TimelineEvaluator.EvaluateClip(clip, 2.0);

        Assert.Equal(0.5, state.Effects[0].Amount, 9);
        Assert.Equal(58.0, state.Effects[0].Radius, 9);
    }

    [Fact]
    public void EvaluateClip_EffectKeyframeValue_IsClampedToKindRange()
    {
        // キーフレーム値が種類の範囲を超えていても、評価時に丸める。
        var clip = MakeClip();
        clip.Effects.Add(new ClipEffect { Kind = EffectKind.Glow, Amount = 0.5 });
        clip.EnsureTrack("Effect:Glow.Amount").Keyframes.Add(new Keyframe(0.0, 5.0));

        Assert.Equal(1.0, TimelineEvaluator.EvaluateClip(clip, 0.0).Effects[0].Amount, 9);
    }

    [Fact]
    public void EvaluateClip_EffectKeyframe_DoesNotMutateOriginalEffect()
    {
        // 評価はコピーを作る。元の固定値は変わらない（次の評価や保存に影響しない）。
        var clip = MakeClip();
        var effect = new ClipEffect { Kind = EffectKind.Blur, Amount = 8 };
        clip.Effects.Add(effect);
        clip.EnsureTrack("Effect:Blur.Amount").Keyframes.Add(new Keyframe(0.0, 40.0));

        Assert.Equal(40.0, TimelineEvaluator.EvaluateClip(clip, 0.0).Effects[0].Amount, 9);
        Assert.Equal(8.0, effect.Amount, 9);
    }

    [Fact]
    public void EffectKeyframeParameterName_MatchesEvaluatorLookup()
    {
        Assert.Equal("Effect:Blur.Amount", ClipEffect.KeyframeParameterName(EffectKind.Blur, isAmount: true));
        Assert.Equal("Effect:Glow.Radius", ClipEffect.KeyframeParameterName(EffectKind.Glow, isAmount: false));
        Assert.Equal("Effect:Kaleidoscope.Radius", ClipEffect.KeyframeParameterName(EffectKind.Kaleidoscope, isAmount: false));
    }

    // ---- EvaluateClip：バインド ----

    [Fact]
    public void Binding_Add_AppliesBandEnergy()
    {
        var (project, _, target) = CreateBindingSetup();
        var audio = new FixedEnergyProvider(0.5); // 写像値は 1.06、Min からの変化分 +0.06

        var state = TimelineEvaluator.EvaluateClip(target, 5.0, 1.0, project, audio);

        Assert.Equal(1.06, state.Scale, 9);
    }

    [Fact]
    public void Binding_SilentEnergy_LeavesKeyframeValue()
    {
        var (project, _, target) = CreateBindingSetup();
        var audio = new FixedEnergyProvider(0.0);

        Assert.Equal(1.0, TimelineEvaluator.EvaluateClip(target, 5.0, 1.0, project, audio).Scale, 9);
    }

    [Fact]
    public void Binding_FullEnergy_ReachesMax()
    {
        var (project, _, target) = CreateBindingSetup();
        var audio = new FixedEnergyProvider(1.0);

        Assert.Equal(1.12, TimelineEvaluator.EvaluateClip(target, 5.0, 1.0, project, audio).Scale, 9);
    }

    [Fact]
    public void Binding_Disabled_IsIgnored()
    {
        var (project, _, target) = CreateBindingSetup();
        target.AudioBindings[0].Enabled = false;
        var audio = new FixedEnergyProvider(1.0);

        Assert.Equal(1.0, TimelineEvaluator.EvaluateClip(target, 5.0, 1.0, project, audio).Scale, 9);
        Assert.Empty(audio.Calls);
    }

    [Fact]
    public void Binding_WrongParameter_DoesNotAffectOtherParameters()
    {
        var (project, _, target) = CreateBindingSetup();
        // バインドの対象を Opacity に変える。Scale には影響しない。
        target.AudioBindings[0].ParameterName = "Opacity";
        target.AudioBindings[0].MinValue = 0.0;
        target.AudioBindings[0].MaxValue = 10.0;
        var audio = new FixedEnergyProvider(1.0);

        var state = TimelineEvaluator.EvaluateClip(target, 5.0, 1.0, project, audio);

        Assert.Equal(1.0, state.Scale, 9);
        Assert.Equal(1.0, state.Opacity, 9); // Opacity は 11.0 になるが 0〜1 に丸められる
    }

    [Fact]
    public void Binding_Multiply_ScalesKeyframeValue()
    {
        var (project, _, target) = CreateBindingSetup();
        target.EnsureTrack("Scale").Keyframes.Add(new Keyframe(0.0, 2.0));
        target.AudioBindings[0].Combine = AudioBindCombine.Multiply;
        var audio = new FixedEnergyProvider(1.0); // 写像値 1.12

        Assert.Equal(2.24, TimelineEvaluator.EvaluateClip(target, 5.0, 1.0, project, audio).Scale, 9);
    }

    [Fact]
    public void Binding_Override_ReplacesKeyframeValue()
    {
        var (project, _, target) = CreateBindingSetup();
        target.EnsureTrack("Scale").Keyframes.Add(new Keyframe(0.0, 2.0));
        target.AudioBindings[0].Combine = AudioBindCombine.Override;
        var audio = new FixedEnergyProvider(0.5); // 写像値 1.06

        Assert.Equal(1.06, TimelineEvaluator.EvaluateClip(target, 5.0, 1.0, project, audio).Scale, 9);
    }

    [Fact]
    public void Binding_MultipleBindings_Accumulate()
    {
        var (project, _, target) = CreateBindingSetup();
        // 2つ目の Add バインド：Min 0・Max 10、エネルギー 0.5 -> 写像値 5、変化分 +5。
        target.AudioBindings.Add(new AudioBinding
        {
            ParameterName = "Scale",
            SourceClipId = "audio1",
            LowHz = 20,
            HighHz = 80,
            MinValue = 0.0,
            MaxValue = 10.0,
            Combine = AudioBindCombine.Add,
            Enabled = true
        });
        var audio = new FixedEnergyProvider(0.5);

        // 1.0 + 0.06 + 5.0
        Assert.Equal(6.06, TimelineEvaluator.EvaluateClip(target, 5.0, 1.0, project, audio).Scale, 9);
    }

    [Fact]
    public void Binding_RotationParameter()
    {
        var (project, _, target) = CreateBindingSetup();
        target.AudioBindings[0].ParameterName = "Rotation";
        target.AudioBindings[0].MinValue = 0.0;
        target.AudioBindings[0].MaxValue = 90.0;
        var audio = new FixedEnergyProvider(0.5); // 写像値 45

        Assert.Equal(45.0, TimelineEvaluator.EvaluateClip(target, 5.0, 1.0, project, audio).RotationDegrees, 9);
    }

    [Fact]
    public void Binding_NoAudioProvider_IsIgnored()
    {
        var (project, _, target) = CreateBindingSetup();

        Assert.Equal(1.0, TimelineEvaluator.EvaluateClip(target, 5.0, 1.0, project, null).Scale, 9);
    }

    [Fact]
    public void Binding_NoProject_IsIgnored()
    {
        var (_, _, target) = CreateBindingSetup();
        var audio = new FixedEnergyProvider(1.0);

        Assert.Equal(1.0, TimelineEvaluator.EvaluateClip(target, 5.0, 1.0, null, audio).Scale, 9);
        Assert.Empty(audio.Calls);
    }

    [Fact]
    public void Binding_MissingSourceClip_IsIgnored()
    {
        var (project, _, target) = CreateBindingSetup();
        target.AudioBindings[0].SourceClipId = "not-exist";
        var audio = new FixedEnergyProvider(1.0);

        Assert.Equal(1.0, TimelineEvaluator.EvaluateClip(target, 5.0, 1.0, project, audio).Scale, 9);
        Assert.Empty(audio.Calls);
    }

    [Fact]
    public void Binding_SourceClipWithoutPath_IsIgnored()
    {
        var (project, audioClip, target) = CreateBindingSetup();
        audioClip.SourcePath = string.Empty;
        var audio = new FixedEnergyProvider(1.0);

        Assert.Equal(1.0, TimelineEvaluator.EvaluateClip(target, 5.0, 1.0, project, audio).Scale, 9);
        Assert.Empty(audio.Calls);
    }

    [Fact]
    public void Binding_SourceNotPlayingAtTime_IsIgnored()
    {
        // 音源クリップは [10, 12) の区間だけ流れる。
        var (project, _, target) = CreateBindingSetup(audioStartSeconds: 10, audioDurationSeconds: 2);
        var audio = new FixedEnergyProvider(0.5);

        Assert.Equal(1.0, TimelineEvaluator.EvaluateClip(target, 5.0, 1.0, project, audio).Scale, 9);
        Assert.Empty(audio.Calls);

        // 音源が流れている時刻では適用される。
        Assert.Equal(1.06, TimelineEvaluator.EvaluateClip(target, 11.0, 1.0, project, audio).Scale, 9);
        Assert.Single(audio.Calls);
    }

    [Fact]
    public void Binding_ConvertsTimelineTimeToSourceTime()
    {
        // 音源はタイムライン 5 秒から、素材内 2 秒の位置から流れる。
        // タイムライン時刻 7 秒 -> 素材内時刻 2 + (7 - 5) = 4 秒。
        var (project, _, target) = CreateBindingSetup(
            audioStartSeconds: 5, audioInPointSeconds: 2, audioDurationSeconds: 10);
        var audio = new FixedEnergyProvider(0.5);

        TimelineEvaluator.EvaluateClip(target, 7.0, 1.0, project, audio);

        var call = Assert.Single(audio.Calls);
        Assert.Equal("audio1", call.ClipId);
        Assert.Equal(4.0, call.TimeSeconds, 9);
        Assert.Equal(20.0, call.LowHz, 9);
        Assert.Equal(80.0, call.HighHz, 9);
    }

    // ---- EvaluateClip：エフェクト量へのバインド ----

    /// <summary>グロー付きの対象クリップを持つプロジェクトを作る。対象は "Effect:Glow.Amount" に Add 合成・Min 0.5・Max 1.0・20-80Hz のバインドを持つ。</summary>
    private static (EditorProject Project, Clip AudioClip, Clip TargetClip) CreateEffectBindingSetup()
    {
        var (project, audio, target) = CreateBindingSetup();
        target.Effects.Add(new ClipEffect { Kind = EffectKind.Glow, Amount = 0.5, Radius = 16 });
        target.AudioBindings.Add(new AudioBinding
        {
            ParameterName = "Effect:Glow.Amount",
            SourceClipId = "audio1",
            LowHz = 20,
            HighHz = 80,
            MinValue = 0.5,
            MaxValue = 1.0,
            Combine = AudioBindCombine.Add,
            Enabled = true
        });
        return (project, audio, target);
    }

    [Fact]
    public void Binding_EffectAmount_Add_PulsesWithBandEnergy()
    {
        var (project, _, target) = CreateEffectBindingSetup();
        var audio = new FixedEnergyProvider(0.5); // 写像値 0.75、Min からの変化分 +0.25

        Assert.Equal(0.75, TimelineEvaluator.EvaluateClip(target, 5.0, 1.0, project, audio).Effects[0].Amount, 9);
        Assert.Equal(1.0, TimelineEvaluator.EvaluateClip(target, 5.0, 1.0, project, new FixedEnergyProvider(1.0)).Effects[0].Amount, 9);
        Assert.Equal(0.5, TimelineEvaluator.EvaluateClip(target, 5.0, 1.0, project, new FixedEnergyProvider(0.0)).Effects[0].Amount, 9);
    }

    [Fact]
    public void Binding_EffectAmount_WithKeyframeBase_AddsDelta()
    {
        // キーフレームの基本値 0.2 に、バインドの変化分 +0.5（エネルギー 1.0）を足す。
        var (project, _, target) = CreateEffectBindingSetup();
        target.EnsureTrack("Effect:Glow.Amount").Keyframes.Add(new Keyframe(0.0, 0.2));
        var audio = new FixedEnergyProvider(1.0);

        Assert.Equal(0.7, TimelineEvaluator.EvaluateClip(target, 5.0, 1.0, project, audio).Effects[0].Amount, 9);
    }

    [Fact]
    public void Binding_EffectAmount_Override_ReplacesKeyframeValue()
    {
        var (project, _, target) = CreateEffectBindingSetup();
        target.EnsureTrack("Effect:Glow.Amount").Keyframes.Add(new Keyframe(0.0, 0.2));
        target.AudioBindings[1].Combine = AudioBindCombine.Override;
        var audio = new FixedEnergyProvider(0.5); // 写像値 0.75

        Assert.Equal(0.75, TimelineEvaluator.EvaluateClip(target, 5.0, 1.0, project, audio).Effects[0].Amount, 9);
    }

    [Fact]
    public void Binding_EffectAmount_Multiply_ScalesKeyframeValue()
    {
        var (project, _, target) = CreateEffectBindingSetup();
        target.EnsureTrack("Effect:Glow.Amount").Keyframes.Add(new Keyframe(0.0, 0.5));
        target.AudioBindings[1].Combine = AudioBindCombine.Multiply;
        target.AudioBindings[1].MinValue = 1.0;
        target.AudioBindings[1].MaxValue = 2.0;
        var audio = new FixedEnergyProvider(1.0); // 写像値 2.0

        Assert.Equal(1.0, TimelineEvaluator.EvaluateClip(target, 5.0, 1.0, project, audio).Effects[0].Amount, 9);
    }

    [Fact]
    public void Binding_EffectAmount_IsClampedToKindRange()
    {
        // Add で範囲を超えても（0.9 + 1.0 = 1.9）、Glow の上限 1.0 に丸める。
        var (project, _, target) = CreateEffectBindingSetup();
        target.EnsureTrack("Effect:Glow.Amount").Keyframes.Add(new Keyframe(0.0, 0.9));
        target.AudioBindings[1].MinValue = 0.0;
        target.AudioBindings[1].MaxValue = 1.0;
        var audio = new FixedEnergyProvider(1.0);

        Assert.Equal(1.0, TimelineEvaluator.EvaluateClip(target, 5.0, 1.0, project, audio).Effects[0].Amount, 9);

        // Override で下限を下回っても（写像値 -1.0）、0.0 に丸める。
        target.AudioBindings[1].Combine = AudioBindCombine.Override;
        target.AudioBindings[1].MinValue = -1.0;
        target.AudioBindings[1].MaxValue = 2.0;
        var silent = new FixedEnergyProvider(0.0);

        Assert.Equal(0.0, TimelineEvaluator.EvaluateClip(target, 5.0, 1.0, project, silent).Effects[0].Amount, 9);
    }

    [Fact]
    public void Binding_EffectRadius_Add_AppliesSeparately()
    {
        // Radius だけにバインドする。Amount は固定値のまま。
        var (project, _, target) = CreateEffectBindingSetup();
        target.AudioBindings[1].ParameterName = "Effect:Glow.Radius";
        target.AudioBindings[1].MinValue = 16.0;
        target.AudioBindings[1].MaxValue = 100.0;
        var audio = new FixedEnergyProvider(0.5); // 写像値 58、変化分 +42

        var state = TimelineEvaluator.EvaluateClip(target, 5.0, 1.0, project, audio);

        Assert.Equal(0.5, state.Effects[0].Amount, 9);
        Assert.Equal(58.0, state.Effects[0].Radius, 9);
    }

    [Fact]
    public void Binding_EffectKindNotAttached_IsIgnored()
    {
        // ブラーは付いていないので、"Effect:Blur.Amount" へのバインドは無視される。
        // 残る1回の解析呼び出しは、別に付いている Scale バインドのもの。
        var (project, _, target) = CreateEffectBindingSetup();
        target.AudioBindings[1].ParameterName = "Effect:Blur.Amount";
        var audio = new FixedEnergyProvider(1.0);

        var state = TimelineEvaluator.EvaluateClip(target, 5.0, 1.0, project, audio);

        Assert.Equal(0.5, state.Effects[0].Amount, 9);
        Assert.Single(audio.Calls);
    }

    [Fact]
    public void Binding_EffectBinding_Disabled_IsIgnored()
    {
        // 無効なエフェクト量バインドは無視される。残る1回の解析呼び出しは、別に付いている Scale バインドのもの。
        var (project, _, target) = CreateEffectBindingSetup();
        target.AudioBindings[1].Enabled = false;
        var audio = new FixedEnergyProvider(1.0);

        Assert.Equal(0.5, TimelineEvaluator.EvaluateClip(target, 5.0, 1.0, project, audio).Effects[0].Amount, 9);
        Assert.Single(audio.Calls);
    }

    [Fact]
    public void Binding_EffectBinding_DoesNotMutateOriginalEffect()
    {
        // 評価はコピーを作る。元の固定値は変わらない（次の評価や保存に影響しない）。
        var (project, _, target) = CreateEffectBindingSetup();
        var effect = target.Effects[0];
        var audio = new FixedEnergyProvider(1.0);

        Assert.Equal(1.0, TimelineEvaluator.EvaluateClip(target, 5.0, 1.0, project, audio).Effects[0].Amount, 9);
        Assert.Equal(0.5, effect.Amount, 9);
    }

    [Fact]
    public void Binding_EffectBinding_NoAudioProvider_IsIgnored()
    {
        var (project, _, target) = CreateEffectBindingSetup();

        Assert.Equal(0.5, TimelineEvaluator.EvaluateClip(target, 5.0, 1.0, project, null).Effects[0].Amount, 9);
    }

    [Fact]
    public void Binding_EffectParameter_SurvivesProjectRoundTrip()
    {
        // エフェクト量へのバインドは .veproj の JSON にそのまま残る（構造の変更なし）。
        var (project, _, target) = CreateEffectBindingSetup();
        var json = ProjectStore.Serialize(project);

        var restored = ProjectStore.Deserialize(json);
        var restoredTarget = restored.Tracks.SelectMany(t => t.Clips).First(c => c.Id == target.Id);

        var binding = Assert.Single(restoredTarget.AudioBindings, b => b.ParameterName == "Effect:Glow.Amount");
        Assert.Equal("audio1", binding.SourceClipId);
        Assert.Equal(20.0, binding.LowHz, 9);
        Assert.Equal(80.0, binding.HighHz, 9);
        Assert.Equal(0.5, binding.MinValue, 9);
        Assert.Equal(1.0, binding.MaxValue, 9);
        Assert.Equal(AudioBindCombine.Add, binding.Combine);
        Assert.True(binding.Enabled);
        Assert.Single(restoredTarget.Effects, e => e.Kind == EffectKind.Glow);
    }

    // ---- GetEffectiveTransitionDuration ----

    [Fact]
    public void GetEffectiveTransitionDuration_AdjacentClips_ReturnsDuration()
    {
        var prev = MakeClip(0, 2);
        var next = MakeClip(2, 2);
        next.TransitionDurationSeconds = 1.0;

        Assert.Equal(1.0, TimelineEvaluator.GetEffectiveTransitionDuration(prev, next), 9);
    }

    [Fact]
    public void GetEffectiveTransitionDuration_GapOrOverlap_ReturnsZero()
    {
        Assert.Equal(0.0, TimelineEvaluator.GetEffectiveTransitionDuration(MakeClip(0, 2), MakeClip(3, 2)), 9);
        Assert.Equal(0.0, TimelineEvaluator.GetEffectiveTransitionDuration(MakeClip(0, 3), MakeClip(2, 2)), 9);
    }

    [Fact]
    public void GetEffectiveTransitionDuration_ZeroOrNegativeDuration_ReturnsZero()
    {
        var prev = MakeClip(0, 2);
        var next = MakeClip(2, 2);
        next.TransitionDurationSeconds = 0.0;

        Assert.Equal(0.0, TimelineEvaluator.GetEffectiveTransitionDuration(prev, next), 9);

        next.TransitionDurationSeconds = -1.0;
        Assert.Equal(0.0, TimelineEvaluator.GetEffectiveTransitionDuration(prev, next), 9);
    }

    [Fact]
    public void GetEffectiveTransitionDuration_ClampedByShorterClip()
    {
        // 両クリップ 0.3 秒、指定 5 秒 -> 2 倍の 0.6 秒に丸められる。
        var prev = MakeClip(0, 0.3);
        var next = MakeClip(0.3, 0.3);
        next.TransitionDurationSeconds = 5.0;

        Assert.Equal(0.6, TimelineEvaluator.GetEffectiveTransitionDuration(prev, next), 9);

        // 片方だけ短い場合は短い方で決まる（0.4 秒 -> 0.8 秒）。
        prev = MakeClip(0, 0.4);
        next = MakeClip(0.4, 10);
        next.TransitionDurationSeconds = 5.0;

        Assert.Equal(0.8, TimelineEvaluator.GetEffectiveTransitionDuration(prev, next), 9);
    }

    // ---- EvaluateProject ----

    [Fact]
    public void EvaluateProject_SkipsInvisibleTracks()
    {
        var project = new EditorProject();
        var visible = project.AddTrack(TrackKind.Video, "V1");
        var hidden = project.AddTrack(TrackKind.Video, "V2");
        hidden.IsVisible = false;
        visible.Clips.Add(MakeClip(0, 10));
        hidden.Clips.Add(MakeClip(0, 10));

        var states = TimelineEvaluator.EvaluateProject(project, 5.0);

        Assert.Single(states);
    }

    [Fact]
    public void EvaluateProject_ExcludesInactiveClips()
    {
        var project = new EditorProject();
        var track = project.AddTrack(TrackKind.Video, "V1");
        track.Clips.Add(MakeClip(0, 2));
        track.Clips.Add(MakeClip(5, 2));

        Assert.Empty(TimelineEvaluator.EvaluateProject(project, 3.0));
        Assert.Single(TimelineEvaluator.EvaluateProject(project, 1.0));
        Assert.Single(TimelineEvaluator.EvaluateProject(project, 6.0));
    }

    [Fact]
    public void EvaluateProject_TracksOrderedBottomToTop()
    {
        var project = new EditorProject();
        var lower = project.AddTrack(TrackKind.Video, "V1");
        var upper = project.AddTrack(TrackKind.Video, "V2");
        var lowerClip = MakeClip(0, 10);
        var upperClip = MakeClip(0, 10);
        lower.Clips.Add(lowerClip);
        upper.Clips.Add(upperClip);

        var states = TimelineEvaluator.EvaluateProject(project, 5.0);

        Assert.Equal(2, states.Count);
        Assert.Same(lowerClip, states[0].Clip);
        Assert.Same(upperClip, states[1].Clip);
    }

    [Fact]
    public void EvaluateProject_Dissolve_FadesInNextClip()
    {
        // クリップA [0,2)、クリップB [2,4)・Dissolve 1.0 秒。フェード区間は [1.5, 2.5]。
        var project = new EditorProject();
        var track = project.AddTrack(TrackKind.Video, "V1");
        var clipA = MakeClip(0, 2);
        var clipB = MakeClip(2, 2);
        clipB.TransitionKind = TransitionKind.Dissolve;
        clipB.TransitionDurationSeconds = 1.0;
        track.Clips.Add(clipA);
        track.Clips.Add(clipB);

        // 境界のちょうど真ん中：B は 0.5 までフェードイン。A はそのまま 1.0。
        var atBoundary = TimelineEvaluator.EvaluateProject(project, 2.0);
        Assert.Equal(2, atBoundary.Count);
        Assert.Equal(1.0, atBoundary[0].Opacity, 9);
        Assert.Equal(0.5, atBoundary[1].Opacity, 9);

        // フェード区間の入口付近：B は 0.1 までフェードイン。
        var atFadeStart = TimelineEvaluator.EvaluateProject(project, 1.6);
        Assert.Equal(2, atFadeStart.Count);
        Assert.Equal(0.1, atFadeStart[1].Opacity, 9);

        // フェード区間の後：B は 1.0、A は区間外で含まれない。
        var afterFade = TimelineEvaluator.EvaluateProject(project, 2.6);
        var onlyB = Assert.Single(afterFade);
        Assert.Same(clipB, onlyB.Clip);
        Assert.Equal(1.0, onlyB.Opacity, 9);

        // フェード区間の前：B は含まれない。
        var beforeFade = TimelineEvaluator.EvaluateProject(project, 1.4);
        var onlyA = Assert.Single(beforeFade);
        Assert.Same(clipA, onlyA.Clip);
    }
}
