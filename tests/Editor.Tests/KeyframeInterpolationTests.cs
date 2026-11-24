// 評価エンジンの単体テスト：キーフレーム補間（KeyframeTrack.Evaluate）。
// UI・FFmpeg・NAudio・実ファイルには依存しない純粋な計算ロジックのみを対象とする。

using Editor.Core;
using Xunit;

namespace Editor.Tests;

/// <summary>キーフレーム補間（KeyframeTrack.Evaluate）のテスト。</summary>
public sealed class KeyframeInterpolationTests
{
    private static KeyframeTrack Track(params Keyframe[] keyframes)
    {
        var track = new KeyframeTrack { ParameterName = "Scale" };
        foreach (var keyframe in keyframes)
            track.AddKeyframe(keyframe);
        return track;
    }

    [Fact]
    public void Evaluate_EmptyTrack_ReturnsDefaultValue()
    {
        var track = Track();

        Assert.Equal(3.14, track.Evaluate(1.0, 3.14), 9);
        Assert.Equal(0.0, track.Evaluate(-5.0, 0.0), 9);
    }

    [Fact]
    public void Evaluate_SingleKeyframe_ReturnsItsValueAtAnyTime()
    {
        var track = Track(new Keyframe(5.0, 7.0));

        Assert.Equal(7.0, track.Evaluate(0.0, 0.0), 9); // 最初より前
        Assert.Equal(7.0, track.Evaluate(5.0, 0.0), 9); // ちょうどその時刻
        Assert.Equal(7.0, track.Evaluate(100.0, 0.0), 9); // 最後より後
    }

    [Fact]
    public void Evaluate_Linear_MidpointIsAverage()
    {
        var track = Track(new Keyframe(0.0, 0.0), new Keyframe(2.0, 10.0));

        Assert.Equal(5.0, track.Evaluate(1.0, 0.0), 9);
    }

    [Fact]
    public void Evaluate_Linear_QuarterPoint()
    {
        var track = Track(new Keyframe(0.0, 0.0), new Keyframe(2.0, 10.0));

        Assert.Equal(2.5, track.Evaluate(0.5, 0.0), 9);
        Assert.Equal(7.5, track.Evaluate(1.5, 0.0), 9);
    }

    [Fact]
    public void Evaluate_BeforeFirstKeyframe_ReturnsFirstValue()
    {
        var track = Track(new Keyframe(1.0, 3.0), new Keyframe(3.0, 9.0));

        Assert.Equal(3.0, track.Evaluate(0.0, 0.0), 9);
        Assert.Equal(3.0, track.Evaluate(-10.0, 0.0), 9);
    }

    [Fact]
    public void Evaluate_AfterLastKeyframe_ReturnsLastValue()
    {
        var track = Track(new Keyframe(1.0, 3.0), new Keyframe(3.0, 9.0));

        Assert.Equal(9.0, track.Evaluate(3.0, 0.0), 9);
        Assert.Equal(9.0, track.Evaluate(100.0, 0.0), 9);
    }

    [Fact]
    public void Evaluate_ExactlyAtKeyframe_ReturnsKeyframeValue()
    {
        var track = Track(new Keyframe(0.0, 0.0), new Keyframe(1.0, 10.0), new Keyframe(2.0, 20.0));

        Assert.Equal(0.0, track.Evaluate(0.0, 0.0), 9);
        Assert.Equal(10.0, track.Evaluate(1.0, 0.0), 9);
        Assert.Equal(20.0, track.Evaluate(2.0, 0.0), 9);
    }

    [Fact]
    public void Evaluate_Hold_ReturnsStartValueUntilNextKeyframe()
    {
        // Hold は区間開始側の値を保ち、次のキーフレーム時刻で跳ぶ。
        var track = Track(
            new Keyframe(0.0, 0.0, Interpolation.Hold),
            new Keyframe(2.0, 10.0));

        Assert.Equal(0.0, track.Evaluate(0.0, 0.0), 9);
        Assert.Equal(0.0, track.Evaluate(1.0, 0.0), 9);
        Assert.Equal(0.0, track.Evaluate(1.999, 0.0), 9);
        Assert.Equal(10.0, track.Evaluate(2.0, 0.0), 9);
    }

    [Fact]
    public void Evaluate_EachSegmentUsesItsOwnInterpolation()
    {
        // 第1区間は Hold、第2区間は Linear。区間の補間方法は開始側キーフレームが決める。
        var track = Track(
            new Keyframe(0.0, 0.0, Interpolation.Hold),
            new Keyframe(1.0, 10.0, Interpolation.Linear),
            new Keyframe(3.0, 20.0));

        Assert.Equal(0.0, track.Evaluate(0.5, 0.0), 9); // 第1区間：Hold
        Assert.Equal(15.0, track.Evaluate(2.0, 0.0), 9); // 第2区間：Linear の中間
    }

    [Fact]
    public void Evaluate_EaseInOut_AppliesSmoothstep()
    {
        // smoothstep: t=0.25 -> 0.15625、t=0.5 -> 0.5、t=0.75 -> 0.84375
        var track = Track(
            new Keyframe(0.0, 0.0, Interpolation.EaseInOut),
            new Keyframe(2.0, 10.0));

        Assert.Equal(1.5625, track.Evaluate(0.5, 0.0), 9);
        Assert.Equal(5.0, track.Evaluate(1.0, 0.0), 9);
        Assert.Equal(8.4375, track.Evaluate(1.5, 0.0), 9);
    }

    [Fact]
    public void Evaluate_AddKeyframeOutOfOrder_StillEvaluatesInTimeOrder()
    {
        // 追加は挿入位置を維持する（AddKeyframe）ため、順不同で追加しても評価は時刻順になる。
        // 数値は従来（Evaluate 内で毎回ソートしていた頃）と同じ。
        var track = Track(new Keyframe(2.0, 10.0), new Keyframe(0.0, 0.0));

        Assert.Equal(5.0, track.Evaluate(1.0, 0.0), 9);
        Assert.Equal(0.0, track.Evaluate(-1.0, 0.0), 9);
        Assert.Equal(10.0, track.Evaluate(3.0, 0.0), 9);
    }

    [Fact]
    public void Keyframes_Setter_SortsUnsortedList()
    {
        // 代入（JSON読み込み・分割処理など）では並べ替えで正規化する。
        var track = new KeyframeTrack { ParameterName = "Scale" };
        track.Keyframes = new List<Keyframe>
        {
            new(2.0, 10.0),
            new(0.0, 0.0),
            new(1.0, 5.0)
        };

        Assert.Collection(track.Keyframes,
            k => Assert.Equal(0.0, k.TimeSeconds, 9),
            k => Assert.Equal(1.0, k.TimeSeconds, 9),
            k => Assert.Equal(2.0, k.TimeSeconds, 9));
        Assert.Equal(2.5, track.Evaluate(0.5, 0.0), 9);
        Assert.Equal(7.5, track.Evaluate(1.5, 0.0), 9);
    }

    [Fact]
    public void AddKeyframe_InsertsAfterEqualTime_StableOrder()
    {
        // 同じ時刻があれば後ろに入れる（従来の安定ソートと同じ順序）。
        var track = Track(new Keyframe(1.0, 10.0), new Keyframe(1.0, 20.0));

        Assert.Equal(1.0, track.Keyframes[0].TimeSeconds, 9);
        Assert.Equal(10.0, track.Keyframes[0].Value, 9);
        Assert.Equal(20.0, track.Keyframes[1].Value, 9);
        Assert.Equal(10.0, track.Evaluate(1.0, 0.0), 9);
    }

    [Fact]
    public void RemoveKeyframe_KeepsRemainingOrder()
    {
        var track = Track(
            new Keyframe(0.0, 0.0),
            new Keyframe(1.0, 10.0),
            new Keyframe(3.0, 20.0));
        var middle = track.Keyframes[1];

        Assert.True(track.RemoveKeyframe(middle));

        Assert.Equal(2, track.Keyframes.Count);
        Assert.Equal(0.0, track.Keyframes[0].TimeSeconds, 9);
        Assert.Equal(3.0, track.Keyframes[1].TimeSeconds, 9);
        Assert.Equal(10.0, track.Evaluate(1.5, 0.0), 9);
    }

    [Fact]
    public void SortKeyframes_FixesOrderAfterDirectTimeEdit()
    {
        // キーフレームの時刻を直接書き換えた後は SortKeyframes で並べ替え直す
        // （インスペクタの時刻編集がこの経路を使う）。
        var track = Track(
            new Keyframe(0.0, 0.0),
            new Keyframe(1.0, 10.0),
            new Keyframe(3.0, 20.0));
        track.Keyframes[0].TimeSeconds = 2.0;

        track.SortKeyframes();

        Assert.Collection(track.Keyframes,
            k => Assert.Equal(1.0, k.TimeSeconds, 9),
            k => Assert.Equal(2.0, k.TimeSeconds, 9),
            k => Assert.Equal(3.0, k.TimeSeconds, 9));
        Assert.Equal(5.0, track.Evaluate(1.5, 0.0), 9);
    }

    [Fact]
    public void Evaluate_ThreeKeyframes_SelectsCorrectSegment()
    {
        var track = Track(
            new Keyframe(0.0, 0.0),
            new Keyframe(1.0, 10.0),
            new Keyframe(3.0, 20.0));

        Assert.Equal(5.0, track.Evaluate(0.5, 0.0), 9); // 第1区間
        Assert.Equal(15.0, track.Evaluate(2.0, 0.0), 9); // 第2区間の中間
        Assert.Equal(17.5, track.Evaluate(2.5, 0.0), 9); // 第2区間の 3/4
    }

    [Fact]
    public void Evaluate_NegativeValues_InterpolateCorrectly()
    {
        var track = Track(new Keyframe(0.0, -10.0), new Keyframe(2.0, 10.0));

        Assert.Equal(0.0, track.Evaluate(1.0, 0.0), 9);
        Assert.Equal(-5.0, track.Evaluate(0.5, 0.0), 9);
    }

    [Fact]
    public void EvaluateParameter_UnknownParameter_ReturnsDefaultValue()
    {
        var clip = new Clip { StartSeconds = 0, DurationSeconds = 5 };
        clip.EnsureTrack("Scale").AddKeyframe(new Keyframe(0.0, 2.0));

        // "Scale" のトラックはあっても "Rotation" は無いので既定値になる。
        Assert.Equal(0.0, clip.EvaluateParameter("Rotation", 1.0, 0.0), 9);
        Assert.Equal(9.9, clip.EvaluateParameter("Opacity", 1.0, 9.9), 9);
    }

    [Fact]
    public void EvaluateParameter_NoKeyframeTrack_ReturnsDefaultValue()
    {
        var clip = new Clip { StartSeconds = 0, DurationSeconds = 5 };

        Assert.Equal(1.0, clip.EvaluateParameter("Scale", 1.0, 1.0), 9);
    }
}
