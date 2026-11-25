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
        if (clip.IsReversed)
        {
            // 逆再生：タイムラインの後半は素材の手前側になる。
            // 後半の素材範囲は [InPoint, InPoint+Duration-offset]（イン点は変わらない）。
            // 前半の素材範囲は [InPoint+Duration-offset, InPoint+Duration] になるので、
            // 前半のイン点を後ろへずらす（後半の長さ分だけ前半の末尾が前に来る）。
            second.InPointSeconds = clip.InPointSeconds;
            clip.InPointSeconds = clip.InPointSeconds + clip.DurationSeconds - offset;
        }
        else
        {
            second.InPointSeconds = clip.InPointSeconds + offset;
        }
        second.DurationSeconds = clip.EndSeconds - timeSeconds;

        // 分割で生まれる後半は元のクリップの途中からの続き。
        // 開始側トランジションは元の境界に付くものなので、後半には付けない。
        second.TransitionKind = TransitionKind.None;
        second.TransitionDurationSeconds = 1.0;

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
        if (!clip.IsReversed)
            // 順再生：先頭を削るとイン点が後ろへずれる。
            // 逆再生：先頭は素材の末尾側を再生しているので、削ってもイン点（素材範囲の先頭）は変わらない。
            clip.InPointSeconds += delta;
        clip.DurationSeconds -= delta;
        clip.StartSeconds = newStartSeconds;
    }

    /// <summary>クリップの末尾側をトリミングする。</summary>
    public static void TrimClipEnd(Clip clip, double newEndSeconds)
    {
        if (newEndSeconds <= clip.StartSeconds || newEndSeconds > clip.EndSeconds)
            throw new ArgumentOutOfRangeException(nameof(newEndSeconds), "トリミング位置がクリップ範囲外です。");

        if (clip.IsReversed)
            // 逆再生：末尾は素材の先頭側を再生しているので、削った分だけイン点が後ろへずれる。
            clip.InPointSeconds += clip.EndSeconds - newEndSeconds;
        clip.DurationSeconds = newEndSeconds - clip.StartSeconds;
    }

    /// <summary>クリップを移動する。0より前には出ない。</summary>
    public static void MoveClip(Clip clip, double newStartSeconds)
    {
        clip.StartSeconds = Math.Max(0, newStartSeconds);
    }

    /// <summary>
    /// クリップ内の無音区間（タイムライン時刻）を切り取り、後ろを詰める。
    /// 無音区間ごとに前後で分割し、無音部分を削除して後ろのクリップを前に詰める（リップル削除）。
    /// segments はタイムライン時刻で、昇順の想定。範囲外・空の区間は無視する。
    /// 後ろの区間から処理するため、前の区間の時刻はずれない。
    /// 戻り値は切り取った区間の数。
    /// </summary>
    public static int CutSilenceRanges(
        Track track, Clip clip, IReadOnlyList<(double StartSeconds, double EndSeconds)> segments)
    {
        if (!track.Clips.Contains(clip))
            throw new ArgumentException("指定されたクリップはこのトラックにありません。", nameof(clip));

        var removed = 0;
        foreach (var (start, end) in segments.OrderByDescending(s => s.StartSeconds))
        {
            // クリップ自体が先の処理で消えていたら（先頭からの無音区間の切り取り）、終わり。
            if (!track.Clips.Contains(clip))
                break;
            if (end <= clip.StartSeconds || start >= clip.EndSeconds || end - start <= 1e-6)
                continue;
            var cutStart = Math.Max(start, clip.StartSeconds);
            var cutEnd = Math.Min(end, clip.EndSeconds);
            if (cutEnd - cutStart <= 1e-6)
                continue;

            Clip silencePiece;
            if (cutEnd < clip.EndSeconds - 1e-9)
            {
                // 無音区間の終わりで分割する。後半は無音区間の後ろの残り。
                // （SplitClip は clip を左側に短くする。）
                SplitClip(track, clip, cutEnd);
            }
            if (cutStart > clip.StartSeconds + 1e-9)
            {
                // 無音区間の始まりで分割する。返された後半が無音部分。
                var middle = SplitClip(track, clip, cutStart);
                if (middle is null)
                    continue;
                silencePiece = middle;
            }
            else
            {
                // 無音区間がクリップ先頭から始まる。clip 自身が無音部分。
                silencePiece = clip;
            }

            RippleDeleteClip(track, silencePiece);
            removed++;
        }
        return removed;
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
