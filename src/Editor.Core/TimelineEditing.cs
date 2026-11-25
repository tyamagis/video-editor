// タイムライン編集操作（設計プラン 2章・7章：カット、トリミング、分割、移動、リップル削除）。
// 操作はドメインモデルだけを触る。Undo/Redoはこれらの操作を
// コマンドとして記録する形で後段に組み込む（フェーズ1では操作自体を用意）。

namespace Editor.Core;

/// <summary>
/// リップル編集の対象範囲。
/// SameTrack（既定）：削除・長さ変更の影響は同じトラック内だけに閉じる（従来の RippleDeleteClip の意味論）。
/// AllTracks：他のトラックでも、基準位置より後ろのクリップが同じ量だけ詰まる／広がる。
/// どちらの範囲でもマーカーは動かさない（マーカーは曲の構造＝絶対時刻への印であり、
/// クリップの詰めに連動させるとサビ・ドロップ位置の印が壊れるため。意図した規則）。
/// </summary>
public enum RippleScope
{
    SameTrack,
    AllTracks
}

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
        // 分割位置の素材側オフセット（タイムラインの経過時間 × 速度）。
        var sourceOffset = offset * clip.Speed;
        var second = clip.Clone();
        second.StartSeconds = timeSeconds;
        if (clip.IsReversed)
        {
            // 逆再生：タイムラインの後半は素材の手前側になる。
            // 後半の素材範囲は [InPoint, InPoint+使用範囲-sourceOffset]（イン点は変わらない）。
            // 前半の素材範囲は [InPoint+使用範囲-sourceOffset, InPoint+使用範囲] になるので、
            // 前半のイン点を後ろへずらす（後半の素材の長さ分だけ前半の末尾が前に来る）。
            second.InPointSeconds = clip.InPointSeconds;
            clip.InPointSeconds = clip.InPointSeconds + clip.SourceDurationSeconds - sourceOffset;
        }
        else
        {
            second.InPointSeconds = clip.InPointSeconds + sourceOffset;
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
            // 順再生：先頭を削るとイン点が後ろへずれる（削ったタイムライン時間 × 速度だけ素材が進む）。
            // 逆再生：先頭は素材の末尾側を再生しているので、削ってもイン点（素材範囲の先頭）は変わらない。
            clip.InPointSeconds += delta * clip.Speed;
        clip.DurationSeconds -= delta;
        clip.StartSeconds = newStartSeconds;
    }

    /// <summary>クリップの末尾側をトリミングする。</summary>
    public static void TrimClipEnd(Clip clip, double newEndSeconds)
    {
        if (newEndSeconds <= clip.StartSeconds || newEndSeconds > clip.EndSeconds)
            throw new ArgumentOutOfRangeException(nameof(newEndSeconds), "トリミング位置がクリップ範囲外です。");

        if (clip.IsReversed)
            // 逆再生：末尾は素材の先頭側を再生しているので、削った分だけイン点が後ろへずれる
            // （削ったタイムライン時間 × 速度だけ素材範囲の先頭が進む）。
            clip.InPointSeconds += (clip.EndSeconds - newEndSeconds) * clip.Speed;
        clip.DurationSeconds = newEndSeconds - clip.StartSeconds;
    }

    /// <summary>
    /// クリップの再生速度を変える。素材の使用範囲（イン点とその長さ）は変えず、
    /// タイムライン上の長さを「使用範囲 ÷ 新しい速度」に合わせる（開始位置は固定）。
    /// そのため、速度を変えてから元に戻すと長さも元に戻る。
    /// 速度は Clip.ClampSpeed で [0.1, 4.0] に丸める。
    /// </summary>
    public static void ChangeSpeed(Clip clip, double newSpeed)
    {
        var clamped = Clip.ClampSpeed(newSpeed);
        if (Math.Abs(clamped - clip.Speed) < 1e-12)
            return;
        var sourceDuration = clip.SourceDurationSeconds;
        clip.Speed = clamped;
        clip.DurationSeconds = sourceDuration / clamped;
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
    /// 「後ろ」の判定は従来どおり、削除クリップの終了位置ちょうど以降に開始するクリップ。
    /// 削除クリップと重なって始まるクリップは動かさない（重なりは利用者が意図して置いたものとみなす）。
    /// </summary>
    public static void RippleDeleteClip(Track track, Clip clip)
    {
        if (!track.Clips.Remove(clip))
            return;

        foreach (var other in track.Clips.Where(c => c.StartSeconds >= clip.EndSeconds))
            other.StartSeconds -= clip.DurationSeconds;
    }

    /// <summary>
    /// プロジェクト単位のリップル削除。scope が AllTracks のときは、他のトラックでも
    /// 削除クリップの終了位置以降に始まるクリップが同じ長さだけ前に詰まる。
    /// クリップがどのトラックにも無ければ何もしない。
    /// </summary>
    public static void RippleDeleteClip(EditorProject project, Clip clip, RippleScope scope)
    {
        var track = project.Tracks.FirstOrDefault(t => t.Clips.Contains(clip));
        if (track is null)
            return;
        if (scope == RippleScope.SameTrack)
        {
            RippleDeleteClip(track, clip);
            return;
        }

        var end = clip.EndSeconds;
        var duration = clip.DurationSeconds;
        track.Clips.Remove(clip);
        foreach (var t in project.Tracks)
        {
            foreach (var other in t.Clips.Where(c => c.StartSeconds >= end))
                other.StartSeconds -= duration;
        }
    }

    /// <summary>後続クリップ（基準位置以降に始まるもの）を delta 秒だけずらす。delta が負なら前に詰まる。0未満にはしない。</summary>
    private static void ShiftFollowers(Track track, Clip except, double fromEndSeconds, double deltaSeconds)
    {
        if (Math.Abs(deltaSeconds) < 1e-12)
            return;
        foreach (var other in track.Clips)
        {
            if (ReferenceEquals(other, except))
                continue;
            if (other.StartSeconds >= fromEndSeconds - 1e-9)
                other.StartSeconds = Math.Max(0, other.StartSeconds + deltaSeconds);
        }
    }

    /// <summary>プロジェクト内の全トラックで後続クリップをずらす（AllTracks 用）。</summary>
    private static void ShiftFollowers(EditorProject project, Clip except, double fromEndSeconds, double deltaSeconds)
    {
        foreach (var track in project.Tracks)
            ShiftFollowers(track, except, fromEndSeconds, deltaSeconds);
    }

    /// <summary>
    /// リップル末尾トリミング。末尾を動かした量（delta）だけ、同じトラックの後続クリップが追随する。
    /// 短くすれば後続は前に詰まる。延長の可否は TrimClipEnd と同じ（現在の終了より後ろへは伸ばせない。
    /// ドラッグ操作では開始時点の状態へ戻してから適用するため、「短くした分を戻す」動きは実質的に効く）。
    /// </summary>
    public static void RippleTrimClipEnd(Track track, Clip clip, double newEndSeconds)
    {
        var oldEnd = clip.EndSeconds;
        TrimClipEnd(clip, newEndSeconds);
        ShiftFollowers(track, clip, oldEnd, newEndSeconds - oldEnd);
    }

    /// <summary>プロジェクト単位のリップル末尾トリミング。AllTracks では他トラックの後続も追随する。</summary>
    public static void RippleTrimClipEnd(EditorProject project, Track track, Clip clip, double newEndSeconds, RippleScope scope)
    {
        if (scope == RippleScope.SameTrack)
        {
            RippleTrimClipEnd(track, clip, newEndSeconds);
            return;
        }
        var oldEnd = clip.EndSeconds;
        TrimClipEnd(clip, newEndSeconds);
        ShiftFollowers(project, clip, oldEnd, newEndSeconds - oldEnd);
    }

    /// <summary>
    /// リップル先頭トリミング。先頭を後ろへ削ると長さが縮んだ分だけ後続が前に詰まり、
    /// 先頭を前へ伸ばす（TrimClipStart の範囲内では起きない）場合も同じ規則で追随する。
    /// </summary>
    public static void RippleTrimClipStart(Track track, Clip clip, double newStartSeconds)
    {
        var oldEnd = clip.EndSeconds;
        var oldStart = clip.StartSeconds;
        TrimClipStart(clip, newStartSeconds);
        ShiftFollowers(track, clip, oldEnd, -(newStartSeconds - oldStart));
    }

    /// <summary>プロジェクト単位のリップル先頭トリミング。AllTracks では他トラックの後続も追随する。</summary>
    public static void RippleTrimClipStart(EditorProject project, Track track, Clip clip, double newStartSeconds, RippleScope scope)
    {
        if (scope == RippleScope.SameTrack)
        {
            RippleTrimClipStart(track, clip, newStartSeconds);
            return;
        }
        var oldEnd = clip.EndSeconds;
        var oldStart = clip.StartSeconds;
        TrimClipStart(clip, newStartSeconds);
        ShiftFollowers(project, clip, oldEnd, -(newStartSeconds - oldStart));
    }

    /// <summary>
    /// リップル移動。クリップと、その終了位置以降に始まる後続クリップをまとめて同じ量だけ動かす。
    /// クリップが0より前に出ないよう、delta はクリップの開始位置で頭打ちにする。
    /// 実際に適用した delta を返す。
    /// </summary>
    public static double RippleMoveClip(Track track, Clip clip, double newStartSeconds)
    {
        var delta = Math.Max(0, newStartSeconds) - clip.StartSeconds;
        if (Math.Abs(delta) < 1e-12)
            return 0;
        var oldEnd = clip.EndSeconds;
        clip.StartSeconds += delta;
        ShiftFollowers(track, clip, oldEnd, delta);
        return delta;
    }
}
