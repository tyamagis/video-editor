// TextAnimation（テキストアニメーションのモデル・評価ロジック）の単体テスト。
// 評価規則は TextAnimation.Evaluate の XML コメントを参照。

using Editor.Core;
using Xunit;

namespace Editor.Tests;

/// <summary>TextAnimation / TextAnimationState / Clip・Evaluator との統合のテスト。</summary>
public sealed class TextAnimationTests
{
    private static TextAnimation FadeIn(double duration = 1, TextAnimationEasing easing = TextAnimationEasing.Linear)
        => new() { Kind = TextAnimationKind.Fade, DurationSeconds = duration, Easing = easing };

    private static TextAnimation FadeOut(double duration = 1, TextAnimationEasing easing = TextAnimationEasing.Linear)
        => new() { Kind = TextAnimationKind.Fade, DurationSeconds = duration, Easing = easing };

    // --- イージング ---

    [Fact]
    public void Apply_Linear_ReturnsInput()
    {
        Assert.Equal(0.25, TextAnimation.Apply(TextAnimationEasing.Linear, 0.25), 9);
        Assert.Equal(0.75, TextAnimation.Apply(TextAnimationEasing.Linear, 0.75), 9);
    }

    [Fact]
    public void Apply_EaseIn_IsTSquared()
    {
        Assert.Equal(0.25, TextAnimation.Apply(TextAnimationEasing.EaseIn, 0.5), 9);
        Assert.Equal(0, TextAnimation.Apply(TextAnimationEasing.EaseIn, 0), 9);
        Assert.Equal(1, TextAnimation.Apply(TextAnimationEasing.EaseIn, 1), 9);
    }

    [Fact]
    public void Apply_EaseOut_IsOneMinusOneMinusTSquared()
    {
        Assert.Equal(0.75, TextAnimation.Apply(TextAnimationEasing.EaseOut, 0.5), 9);
        Assert.Equal(0, TextAnimation.Apply(TextAnimationEasing.EaseOut, 0), 9);
        Assert.Equal(1, TextAnimation.Apply(TextAnimationEasing.EaseOut, 1), 9);
    }

    [Fact]
    public void Apply_EaseInOut_IsSmoothstep()
    {
        Assert.Equal(0.5, TextAnimation.Apply(TextAnimationEasing.EaseInOut, 0.5), 9);
        Assert.Equal(0.15625, TextAnimation.Apply(TextAnimationEasing.EaseInOut, 0.25), 9);
        Assert.Equal(0.84375, TextAnimation.Apply(TextAnimationEasing.EaseInOut, 0.75), 9);
    }

    [Fact]
    public void Apply_ClampsToZeroOne()
    {
        Assert.Equal(0, TextAnimation.Apply(TextAnimationEasing.Linear, -2), 9);
        Assert.Equal(1, TextAnimation.Apply(TextAnimationEasing.Linear, 2), 9);
        Assert.Equal(0, TextAnimation.Apply(TextAnimationEasing.EaseIn, double.NaN), 9);
    }

    // --- Identity ---

    [Fact]
    public void Identity_HasExpectedValues()
    {
        var identity = TextAnimationState.Identity;
        Assert.Equal(1, identity.Opacity);
        Assert.Equal(0, identity.OffsetXFraction);
        Assert.Equal(0, identity.OffsetYFraction);
        Assert.Equal(1, identity.Scale);
        Assert.Equal(-1, identity.VisibleCharCount);
        Assert.Equal(0, identity.HighlightedCharCount);
        Assert.True(identity.IsIdentity);
    }

    [Fact]
    public void IsIdentity_FalseWhenAnyFieldDiffers()
    {
        Assert.False((TextAnimationState.Identity with { Opacity = 0.5 }).IsIdentity);
        Assert.False((TextAnimationState.Identity with { VisibleCharCount = 3 }).IsIdentity);
        Assert.False((TextAnimationState.Identity with { Scale = 0.5 }).IsIdentity);
    }

    // --- フェード ---

    [Fact]
    public void FadeIn_Progress()
    {
        var anim = FadeIn();

        var start = TextAnimation.Evaluate(anim, null, 0, 10, 5);
        Assert.Equal(0, start.Opacity);
        Assert.True(start.IsIdentity is false);

        var mid = TextAnimation.Evaluate(anim, null, 0.5, 10, 5);
        Assert.Equal(0.5, mid.Opacity, 9);

        // Duration 経過後は恒等。
        var end = TextAnimation.Evaluate(anim, null, 1, 10, 5);
        Assert.True(end.IsIdentity);
        var after = TextAnimation.Evaluate(anim, null, 5, 10, 5);
        Assert.True(after.IsIdentity);
    }

    [Fact]
    public void FadeOut_Progress()
    {
        var anim = FadeOut();

        // 残り 1 秒（クリップ 10 秒・local 9 秒）で開始。
        var start = TextAnimation.Evaluate(null, anim, 9, 10, 5);
        Assert.Equal(1, start.Opacity, 9);

        var mid = TextAnimation.Evaluate(null, anim, 9.5, 10, 5);
        Assert.Equal(0.5, mid.Opacity, 9);

        var end = TextAnimation.Evaluate(null, anim, 10, 10, 5);
        Assert.Equal(0, end.Opacity, 9);
    }

    [Fact]
    public void FadeOut_NotAppliedWhilePlentyRemains()
    {
        var state = TextAnimation.Evaluate(null, FadeOut(), 0, 10, 5);
        Assert.True(state.IsIdentity);
    }

    [Fact]
    public void Fade_UsesEasing()
    {
        // EaseOut：p=0.5 → 0.75。
        var eased = TextAnimation.Evaluate(FadeIn(easing: TextAnimationEasing.EaseOut), null, 0.5, 10, 5);
        Assert.Equal(0.75, eased.Opacity, 9);
        // EaseIn：p=0.5 → 0.25。
        var easedIn = TextAnimation.Evaluate(FadeIn(easing: TextAnimationEasing.EaseIn), null, 0.5, 10, 5);
        Assert.Equal(0.25, easedIn.Opacity, 9);
    }

    [Fact]
    public void NoneAnimation_IsIdentity()
    {
        var state = TextAnimation.Evaluate(new TextAnimation { Kind = TextAnimationKind.None }, null, 0, 10, 5);
        Assert.True(state.IsIdentity);
        var nulls = TextAnimation.Evaluate(null, null, 0.5, 10, 5);
        Assert.True(nulls.IsIdentity);
    }

    // --- スライド ---

    [Theory]
    [InlineData(TextAnimationDirection.Left, -0.25, 0)]
    [InlineData(TextAnimationDirection.Right, 0.25, 0)]
    [InlineData(TextAnimationDirection.Up, 0, -0.25)]
    [InlineData(TextAnimationDirection.Down, 0, 0.25)]
    public void SlideIn_StartsOffsetTowardDirection(TextAnimationDirection direction, double expectedX, double expectedY)
    {
        var anim = new TextAnimation
        {
            Kind = TextAnimationKind.Slide,
            Direction = direction,
            DurationSeconds = 1,
            Easing = TextAnimationEasing.Linear
        };

        var state = TextAnimation.Evaluate(anim, null, 0, 10, 5);
        Assert.Equal(expectedX, state.OffsetXFraction, 9);
        Assert.Equal(expectedY, state.OffsetYFraction, 9);
        // Fade=true（既定）なら同時にフェードする。
        Assert.Equal(0, state.Opacity, 9);
    }

    [Fact]
    public void SlideIn_ReachesFinalPosition()
    {
        var anim = new TextAnimation
        {
            Kind = TextAnimationKind.Slide,
            Direction = TextAnimationDirection.Left,
            DurationSeconds = 1,
            Easing = TextAnimationEasing.Linear
        };

        var mid = TextAnimation.Evaluate(anim, null, 0.5, 10, 5);
        Assert.Equal(-0.125, mid.OffsetXFraction, 9);
        Assert.Equal(0.5, mid.Opacity, 9);

        var end = TextAnimation.Evaluate(anim, null, 1, 10, 5);
        Assert.True(end.IsIdentity);
    }

    [Fact]
    public void SlideOut_MovesTowardDirection()
    {
        var anim = new TextAnimation
        {
            Kind = TextAnimationKind.Slide,
            Direction = TextAnimationDirection.Left,
            DurationSeconds = 1,
            Easing = TextAnimationEasing.Linear
        };

        var mid = TextAnimation.Evaluate(null, anim, 9.5, 10, 5);
        Assert.Equal(-0.125, mid.OffsetXFraction, 9);
        Assert.Equal(0.5, mid.Opacity, 9);

        var end = TextAnimation.Evaluate(null, anim, 10, 10, 5);
        Assert.Equal(-0.25, end.OffsetXFraction, 9);
        Assert.Equal(0, end.Opacity, 9);
    }

    [Fact]
    public void Slide_FadeFalse_KeepsOpacityOne()
    {
        var anim = new TextAnimation
        {
            Kind = TextAnimationKind.Slide,
            Direction = TextAnimationDirection.Left,
            DurationSeconds = 1,
            Easing = TextAnimationEasing.Linear,
            Fade = false
        };

        var state = TextAnimation.Evaluate(anim, null, 0, 10, 5);
        Assert.Equal(1, state.Opacity, 9);
        Assert.Equal(-0.25, state.OffsetXFraction, 9);
    }

    // --- スケール ---

    [Fact]
    public void ScaleIn_GoesHalfToOne()
    {
        var anim = new TextAnimation
        {
            Kind = TextAnimationKind.Scale,
            DurationSeconds = 1,
            Easing = TextAnimationEasing.Linear
        };

        Assert.Equal(0.5, TextAnimation.Evaluate(anim, null, 0, 10, 5).Scale, 9);
        Assert.Equal(0.75, TextAnimation.Evaluate(anim, null, 0.5, 10, 5).Scale, 9);
        Assert.True(TextAnimation.Evaluate(anim, null, 1, 10, 5).IsIdentity);
    }

    [Fact]
    public void ScaleOut_GoesOneToHalf()
    {
        var anim = new TextAnimation
        {
            Kind = TextAnimationKind.Scale,
            DurationSeconds = 1,
            Easing = TextAnimationEasing.Linear,
            Fade = false
        };

        Assert.Equal(1, TextAnimation.Evaluate(null, anim, 9, 10, 5).Scale, 9);
        Assert.Equal(0.75, TextAnimation.Evaluate(null, anim, 9.5, 10, 5).Scale, 9);
        Assert.Equal(0.5, TextAnimation.Evaluate(null, anim, 10, 10, 5).Scale, 9);
        Assert.Equal(1, TextAnimation.Evaluate(null, anim, 10, 10, 5).Opacity, 9);
    }

    // --- タイプライター ---

    [Fact]
    public void TypewriterIn_StepsByCharCount()
    {
        var anim = new TextAnimation
        {
            Kind = TextAnimationKind.Typewriter,
            DurationSeconds = 1,
            Easing = TextAnimationEasing.EaseIn // イージングは無視される。
        };

        Assert.Equal(0, TextAnimation.Evaluate(anim, null, 0, 10, 10).VisibleCharCount);
        Assert.Equal(5, TextAnimation.Evaluate(anim, null, 0.5, 10, 10).VisibleCharCount);
        Assert.Equal(9, TextAnimation.Evaluate(anim, null, 0.99, 10, 10).VisibleCharCount);
        // Duration 経過後は全文字（-1）。
        Assert.Equal(-1, TextAnimation.Evaluate(anim, null, 1, 10, 10).VisibleCharCount);
        Assert.True(TextAnimation.Evaluate(anim, null, 1, 10, 10).IsIdentity);
    }

    [Fact]
    public void TypewriterOut_ErasesFromEnd()
    {
        var anim = new TextAnimation { Kind = TextAnimationKind.Typewriter, DurationSeconds = 1 };

        Assert.Equal(10, TextAnimation.Evaluate(null, anim, 9, 10, 10).VisibleCharCount);
        Assert.Equal(5, TextAnimation.Evaluate(null, anim, 9.5, 10, 10).VisibleCharCount);
        Assert.Equal(0, TextAnimation.Evaluate(null, anim, 10, 10, 10).VisibleCharCount);
    }

    // --- カラオケ ---

    [Fact]
    public void KaraokeIn_HighlightsLinearly()
    {
        var anim = new TextAnimation
        {
            Kind = TextAnimationKind.Karaoke,
            DurationSeconds = 1,
            Easing = TextAnimationEasing.EaseIn // イージングは無視される。
        };

        Assert.Equal(0, TextAnimation.Evaluate(anim, null, 0, 10, 10).HighlightedCharCount);
        Assert.Equal(5, TextAnimation.Evaluate(anim, null, 0.5, 10, 10).HighlightedCharCount);
        // Duration 経過後も全文字ハイライトのまま。
        var after = TextAnimation.Evaluate(anim, null, 5, 10, 10);
        Assert.Equal(10, after.HighlightedCharCount);
        Assert.False(after.IsIdentity);
    }

    [Fact]
    public void KaraokeOut_IsIgnored()
    {
        var anim = new TextAnimation { Kind = TextAnimationKind.Karaoke, DurationSeconds = 1 };
        var state = TextAnimation.Evaluate(null, anim, 9.5, 10, 10);
        Assert.True(state.IsIdentity);
        Assert.Equal(0, state.HighlightedCharCount);
    }

    // --- イン＋アウトの合成 ---

    [Fact]
    public void InAndOutOverlap_OpacityMultiplies()
    {
        // クリップ 1 秒、イン・アウトともフェード 1 秒。local 0.25 では両方が 0.25 / 0.75。
        var state = TextAnimation.Evaluate(FadeIn(), FadeOut(), 0.25, 1, 5);
        Assert.Equal(0.25 * 0.75, state.Opacity, 9);
    }

    [Fact]
    public void InAndOutOverlap_OffsetsAddAndScalesMultiply()
    {
        var slideIn = new TextAnimation
        {
            Kind = TextAnimationKind.Slide,
            Direction = TextAnimationDirection.Left,
            DurationSeconds = 1,
            Easing = TextAnimationEasing.Linear,
            Fade = false
        };
        var slideOut = new TextAnimation
        {
            Kind = TextAnimationKind.Slide,
            Direction = TextAnimationDirection.Right,
            DurationSeconds = 1,
            Easing = TextAnimationEasing.Linear,
            Fade = false
        };

        // local 0.5：イン -0.125、アウト +0.125 → 合計 0。
        var slide = TextAnimation.Evaluate(slideIn, slideOut, 0.5, 1, 5);
        Assert.Equal(0, slide.OffsetXFraction, 9);

        var scaleIn = new TextAnimation { Kind = TextAnimationKind.Scale, DurationSeconds = 1, Easing = TextAnimationEasing.Linear, Fade = false };
        var scaleOut = new TextAnimation { Kind = TextAnimationKind.Scale, DurationSeconds = 1, Easing = TextAnimationEasing.Linear, Fade = false };
        var scale = TextAnimation.Evaluate(scaleIn, scaleOut, 0.5, 1, 5);
        Assert.Equal(0.75 * 0.75, scale.Scale, 9);
    }

    [Fact]
    public void InAndOutOverlap_VisibleCharCountTakesMin()
    {
        var typeIn = new TextAnimation { Kind = TextAnimationKind.Typewriter, DurationSeconds = 1 };
        var typeOut = new TextAnimation { Kind = TextAnimationKind.Typewriter, DurationSeconds = 1 };

        // local 0.25：イン floor(2.5)=2、アウト ceil(7.5)=8 → 小さい方の 2。
        var state = TextAnimation.Evaluate(typeIn, typeOut, 0.25, 1, 10);
        Assert.Equal(2, state.VisibleCharCount);
    }

    [Fact]
    public void InAndOutOverlap_HighlightedCharCountTakesMax()
    {
        var karaokeIn = new TextAnimation { Kind = TextAnimationKind.Karaoke, DurationSeconds = 1 };
        var fadeOut = FadeOut();

        // カラオケのイン（5 文字）とフェードのアウト（ハイライト 0）→ 大きい方の 5。
        var state = TextAnimation.Evaluate(karaokeIn, fadeOut, 0.5, 1, 10);
        Assert.Equal(5, state.HighlightedCharCount);
        Assert.Equal(0.5, state.Opacity, 9);
    }

    // --- Duration のクランプ・異常値 ---

    [Fact]
    public void Duration_ClampedToMinAndMax()
    {
        // 0.01 秒 → 0.05 秒に丸める。local 0.025 は進行度 0.5。
        var tiny = new TextAnimation { Kind = TextAnimationKind.Fade, DurationSeconds = 0.01, Easing = TextAnimationEasing.Linear };
        Assert.Equal(0.5, TextAnimation.Evaluate(tiny, null, 0.025, 10, 5).Opacity, 9);

        // 100 秒 → 60 秒に丸める。local 30 は進行度 0.5。
        var huge = new TextAnimation { Kind = TextAnimationKind.Fade, DurationSeconds = 100, Easing = TextAnimationEasing.Linear };
        Assert.Equal(0.5, TextAnimation.Evaluate(huge, null, 30, 10, 5).Opacity, 9);

        // 0・負・NaN もクラッシュせず既定の 0.5 秒扱い（NaN）または下限（0・負）になる。
        var zero = new TextAnimation { Kind = TextAnimationKind.Fade, DurationSeconds = 0, Easing = TextAnimationEasing.Linear };
        Assert.Equal(0.5, TextAnimation.Evaluate(zero, null, 0.025, 10, 5).Opacity, 9);
        var nan = new TextAnimation { Kind = TextAnimationKind.Fade, DurationSeconds = double.NaN, Easing = TextAnimationEasing.Linear };
        Assert.Equal(0.5, TextAnimation.Evaluate(nan, null, 0.25, 10, 5).Opacity, 9);
    }

    [Fact]
    public void AbnormalValues_DoNotThrow()
    {
        // local が負、clipDuration が 0 以下、charCount が負、NaN でもクラッシュしない。
        var anim = FadeIn();
        var a = TextAnimation.Evaluate(anim, FadeOut(), -5, 10, 5);
        Assert.Equal(0, a.Opacity, 9); // local は 0 に丸める（フェードイン開始時）。
        var b = TextAnimation.Evaluate(anim, FadeOut(), 0, 0, 5);
        Assert.Equal(0, b.Opacity, 9); // clipDuration 0：アウトは即 p=1 → 不透明度 0。
        var c = TextAnimation.Evaluate(anim, null, 0.5, 10, -3);
        Assert.Equal(0.5, c.Opacity, 9);
        var d = TextAnimation.Evaluate(anim, null, double.NaN, double.NaN, 5);
        Assert.Equal(0, d.Opacity, 9);
        var e = TextAnimation.Evaluate(anim, null, double.PositiveInfinity, 10, 5);
        Assert.True(e.IsIdentity); // 無限大の local は Duration 経過後として恒等（アウト無しの場合）。
    }

    // --- Clone ---

    [Fact]
    public void TextAnimation_Clone_IsIndependent()
    {
        var original = new TextAnimation
        {
            Kind = TextAnimationKind.Slide,
            Direction = TextAnimationDirection.Up,
            DurationSeconds = 1.2,
            Easing = TextAnimationEasing.EaseIn,
            Fade = false,
            HighlightColorHex = "#00FF00"
        };

        var clone = original.Clone();
        clone.Kind = TextAnimationKind.Fade;
        clone.HighlightColorHex = "#FF0000";

        Assert.Equal(TextAnimationKind.Slide, original.Kind);
        Assert.Equal("#00FF00", original.HighlightColorHex);
        Assert.Equal(TextAnimationDirection.Up, clone.Direction);
        Assert.Equal(1.2, clone.DurationSeconds, 9);
    }

    [Fact]
    public void Clip_Clone_DeepCopiesAnimations()
    {
        var clip = new Clip
        {
            Kind = ClipKind.Text,
            Text = "テスト",
            AnimationIn = new TextAnimation { Kind = TextAnimationKind.Fade, DurationSeconds = 1 },
            AnimationOut = new TextAnimation { Kind = TextAnimationKind.Slide, Direction = TextAnimationDirection.Down }
        };

        var clone = clip.Clone();
        clone.AnimationIn.Kind = TextAnimationKind.Scale;
        clone.AnimationOut.Direction = TextAnimationDirection.Left;

        Assert.Equal(TextAnimationKind.Fade, clip.AnimationIn.Kind);
        Assert.Equal(TextAnimationDirection.Down, clip.AnimationOut.Direction);
        Assert.NotSame(clip.AnimationIn, clone.AnimationIn);
        Assert.NotSame(clip.AnimationOut, clone.AnimationOut);
    }

    // --- 保存・読込 ---

    [Fact]
    public void ProjectStore_RoundTrip_RestoresAnimations()
    {
        var project = new EditorProject { Name = "テスト" };
        var track = project.AddTrack(TrackKind.Text, "テキスト 1");
        track.Clips.Add(new Clip
        {
            Name = "テキスト",
            Kind = ClipKind.Text,
            Text = "こんにちは",
            StartSeconds = 0,
            DurationSeconds = 5,
            AnimationIn = new TextAnimation
            {
                Kind = TextAnimationKind.Slide,
                Direction = TextAnimationDirection.Right,
                DurationSeconds = 0.8,
                Easing = TextAnimationEasing.EaseInOut,
                Fade = false,
                HighlightColorHex = "#00FF00"
            },
            AnimationOut = new TextAnimation
            {
                Kind = TextAnimationKind.Karaoke,
                DurationSeconds = 2,
                HighlightColorHex = "#FF00FF"
            }
        });

        var restored = ProjectStore.Deserialize(ProjectStore.Serialize(project));
        var clip = restored.Tracks[0].Clips[0];

        Assert.Equal(TextAnimationKind.Slide, clip.AnimationIn.Kind);
        Assert.Equal(TextAnimationDirection.Right, clip.AnimationIn.Direction);
        Assert.Equal(0.8, clip.AnimationIn.DurationSeconds, 9);
        Assert.Equal(TextAnimationEasing.EaseInOut, clip.AnimationIn.Easing);
        Assert.False(clip.AnimationIn.Fade);
        Assert.Equal("#00FF00", clip.AnimationIn.HighlightColorHex);
        Assert.Equal(TextAnimationKind.Karaoke, clip.AnimationOut.Kind);
        Assert.Equal(2, clip.AnimationOut.DurationSeconds, 9);
        Assert.Equal("#FF00FF", clip.AnimationOut.HighlightColorHex);
    }

    [Fact]
    public void ProjectStore_OldProjectWithoutAnimations_LoadsAsNone()
    {
        // AnimationIn / AnimationOut の項目が無い旧版 JSON。
        const string oldJson = """
            {
              "Version": 1,
              "Name": "旧プロジェクト",
              "Width": 1920,
              "Height": 1080,
              "FrameRate": 30,
              "Bpm": 120,
              "Tracks": [
                {
                  "Name": "テキスト 1",
                  "Kind": 2,
                  "Clips": [
                    {
                      "Name": "テキスト",
                      "Kind": 3,
                      "StartSeconds": 0,
                      "DurationSeconds": 5,
                      "Text": "こんにちは"
                    }
                  ]
                }
              ]
            }
            """;

        var project = ProjectStore.LoadFromJson(oldJson);
        var clip = project.Tracks[0].Clips[0];

        Assert.NotNull(clip.AnimationIn);
        Assert.NotNull(clip.AnimationOut);
        Assert.Equal(TextAnimationKind.None, clip.AnimationIn.Kind);
        Assert.Equal(TextAnimationKind.None, clip.AnimationOut.Kind);
    }

    // --- Evaluator との統合 ---

    [Fact]
    public void EvaluateClip_TextClip_EvaluatesAnimation()
    {
        var clip = new Clip
        {
            Kind = ClipKind.Text,
            Text = "abc",
            StartSeconds = 2,
            DurationSeconds = 10,
            AnimationIn = FadeIn(duration: 1, easing: TextAnimationEasing.EaseOut)
        };

        // local 0.5、EaseOut で p=0.75 → 不透明度 0.75。
        var state = TimelineEvaluator.EvaluateClip(clip, 2.5);
        Assert.Equal(0.75, state.TextAnimation.Opacity, 9);
        Assert.False(state.TextAnimation.IsIdentity);
    }

    [Fact]
    public void EvaluateClip_TextClip_CountsCharsAsTextElements()
    {
        // "a😀b" は UTF-16 では 4 単位だがテキスト要素は 3。
        var clip = new Clip
        {
            Kind = ClipKind.Text,
            Text = "a\U0001F600b",
            StartSeconds = 0,
            DurationSeconds = 10,
            AnimationIn = new TextAnimation { Kind = TextAnimationKind.Typewriter, DurationSeconds = 1 }
        };

        // local 0.5：floor(0.5 * 3) = 1 文字。
        var state = TimelineEvaluator.EvaluateClip(clip, 0.5);
        Assert.Equal(1, state.TextAnimation.VisibleCharCount);
    }

    [Fact]
    public void EvaluateClip_NonTextClip_StaysIdentity()
    {
        var clip = new Clip
        {
            Kind = ClipKind.Video,
            StartSeconds = 0,
            DurationSeconds = 10,
            AnimationIn = FadeIn()
        };

        var state = TimelineEvaluator.EvaluateClip(clip, 0.5);
        Assert.True(state.TextAnimation.IsIdentity);
    }
}
