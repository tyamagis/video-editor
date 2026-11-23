// 評価エンジン（設計プラン 5章）。
// 時刻 t における各クリップの確定値を計算する。
// プレビュー描画と書き出しは同じ評価結果を使うため、
// ここが両者の表示一致の要になる。

namespace Editor.Core;

/// <summary>ある時刻におけるクリップ1つの確定状態。</summary>
public sealed class EvaluatedClipState
{
    public required Clip Clip { get; init; }
    public double Opacity { get; init; } = 1.0;
    public double Scale { get; init; } = 1.0;
    public double RotationDegrees { get; init; }
    public double PositionX { get; init; }
    public double PositionY { get; init; }
    public double Brightness { get; init; }
    public double Contrast { get; init; } = 1.0;
}

/// <summary>キーフレームと固定プロパティから時刻 t の状態を計算する。</summary>
public static class TimelineEvaluator
{
    /// <summary>クリップ1つの、時刻 t における状態を計算する。</summary>
    public static EvaluatedClipState EvaluateClip(Clip clip, double timeSeconds)
    {
        var local = timeSeconds - clip.StartSeconds;
        return new EvaluatedClipState
        {
            Clip = clip,
            Opacity = clip.EvaluateParameter("Opacity", local, 1.0),
            Scale = clip.EvaluateParameter("Scale", local, 1.0),
            RotationDegrees = clip.EvaluateParameter("Rotation", local, 0.0),
            PositionX = clip.EvaluateParameter("PositionX", local, 0.0),
            PositionY = clip.EvaluateParameter("PositionY", local, 0.0),
            Brightness = clip.Brightness,
            Contrast = clip.Contrast
        };
    }

    /// <summary>時刻 t で有効な全クリップの状態を返す。下のトラックから順に並ぶ。</summary>
    public static List<EvaluatedClipState> EvaluateProject(EditorProject project, double timeSeconds)
    {
        var result = new List<EvaluatedClipState>();
        foreach (var track in project.Tracks)
        {
            if (!track.IsVisible)
                continue;
            foreach (var clip in track.Clips.Where(c => c.IsActiveAt(timeSeconds)))
                result.Add(EvaluateClip(clip, timeSeconds));
        }
        return result;
    }
}
