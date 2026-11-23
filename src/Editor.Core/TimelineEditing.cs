// タイムライン編集操作（設計プラン 2章・7章：カット、トリミング、分割、移動、リップル削除）。
// 操作はドメインモデルだけを触る。Undo/Redoはこれらの操作を
// コマンドとして記録する形で後段に組み込む（フェーズ1では操作自体を用意）。

namespace Editor.Core;

/// <summary>トラックに対する編集操作の集まり。</summary>
public static class TimelineEditor
{
    /// <summary>
    /// クリップを再生ヘッド位置で分割する。分割後の後半クリップを返す。
    /// 分割位置がクリップ範囲外の場合は null を返す。
    /// キーフレームは分割位置で分けられ、後半は相対時刻が振り直される。
    /// </summary>
    public static Clip? SplitClip(Track track, Clip clip, double timeSeconds)
    {
        if (!track.Clips.Contains(clip))
            throw new ArgumentException("指定されたクリップはこのトラックにありません。", nameof(clip));
        if (timeSeconds <= clip.StartSeconds || timeSeconds >= clip.EndSeconds)
            return null;

        var offset = timeSeconds - clip.StartSeconds;
        var second = clip.Clone();
        second.StartSeconds = timeSeconds;
        second.InPointSeconds = clip.InPointSeconds + offset;
        second.DurationSeconds = clip.EndSeconds - timeSeconds;

        // 後半クリップのキーフレームは分割位置を0として振り直す。
        foreach (var kt in second.KeyframeTracks)
        {
            kt.Keyframes = kt.Keyframes
                .Where(k => k.TimeSeconds > offset)
                .Select(k => new Keyframe(k.TimeSeconds - offset, k.Value, k.Interpolation))
                .ToList();
        }

        // 前半クリップは分割位置より後のキーフレームを捨てる。
        foreach (var kt in clip.KeyframeTracks)
            kt.Keyframes = kt.Keyframes.Where(k => k.TimeSeconds <= offset).ToList();

        clip.DurationSeconds = offset;
        track.Clips.Insert(track.Clips.IndexOf(clip) + 1, second);
        return second;
    }

    /// <summary>クリップの先頭側をトリミングする。イン点が後ろへずれる。</summary>
    public static void TrimClipStart(Clip clip, double newStartSeconds)
    {
        if (newStartSeconds < clip.StartSeconds || newStartSeconds >= clip.EndSeconds)
            throw new ArgumentOutOfRangeException(nameof(newStartSeconds), "トリミング位置がクリップ範囲外です。");

        var delta = newStartSeconds - clip.StartSeconds;
        clip.InPointSeconds += delta;
        clip.DurationSeconds -= delta;
        clip.StartSeconds = newStartSeconds;
    }

    /// <summary>クリップの末尾側をトリミングする。</summary>
    public static void TrimClipEnd(Clip clip, double newEndSeconds)
    {
        if (newEndSeconds <= clip.StartSeconds || newEndSeconds > clip.EndSeconds)
            throw new ArgumentOutOfRangeException(nameof(newEndSeconds), "トリミング位置がクリップ範囲外です。");

        clip.DurationSeconds = newEndSeconds - clip.StartSeconds;
    }

    /// <summary>クリップを移動する。0より前には出ない。</summary>
    public static void MoveClip(Clip clip, double newStartSeconds)
    {
        clip.StartSeconds = Math.Max(0, newStartSeconds);
    }

    /// <summary>
    /// クリップを削除し、同じトラック上で後ろにあるクリップを前に詰める（リップル削除）。
    /// </summary>
    public static void RippleDeleteClip(Track track, Clip clip)
    {
        if (!track.Clips.Remove(clip))
            return;

        foreach (var other in track.Clips.Where(c => c.StartSeconds >= clip.EndSeconds))
            other.StartSeconds -= clip.DurationSeconds;
    }
}
