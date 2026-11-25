// クリップのグループ化（複数クリップを1単位として移動・トリミングする）。
//
// 設計（既存モデル・Undo/JSON の流儀に合わせた規則）：
// - 表現：Clip.GroupId（string?）。同じ Id を持つクリップ同士が1グループ。
//   グループはトラックをまたげる（映像＋音声＋テキストを一緒に動かす用途）。
//   グループ自体のエンティティは作らない（プロジェクトに Groups リストは持たない）。
//   そのため Undo（プロジェクト全体の JSON スナップショット）と JSON 保存は
//   追加の配線なしでそのまま効く。旧プロジェクトは GroupId 無し＝全クリップ無グループで開ける。
// - 分割：SplitClip の Clone が GroupId をコピーするので、分割した両片は同じグループに残る。
// - 削除：通常削除・リップル削除は対象クリップだけを消す。残りが1個になったグループは
//   意味を持たないため、CleanupSingletonGroups で自動的に解除する（呼び出し側が削除後に呼ぶ。
//   このファイルの GroupClips / Ungroup は内部で整合を保つ）。
// - グループ化のやり直し：既にグループに属すクリップを含めて GroupClips すると、
//   選ばれたクリップは新しいグループへ移る。元のグループの残りが1個になれば自動解除する
//   （部分的に持っていかれたグループを「1個だけのグループ」として残さない）。
// - 移動：グループ全員の相対位置を保ったまま同じ delta を適用する。全員のうち
//   最も早い開始位置が0未満にならないよう delta を頭打ちにし、適用した delta を返す。
// - トリミング：グループの外接範囲（バウンディングボックス）の端に接しているクリップだけを
//   トリミングする（左端に接するクリップの先頭、右端に接するクリップの末尾）。
//   端に接していないクリップは変えない（グループ内側のクリップの長さは保つ）。
//   検証は全員分を先に行い、1つでも範囲外なら例外を投げて何も変えない。

namespace Editor.Core;

/// <summary>クリップのグループ操作。</summary>
public static class ClipGroups
{
    /// <summary>端への接触判定に使う許容誤差（秒）。</summary>
    public const double EdgeToleranceSeconds = 1e-6;

    /// <summary>クリップがグループに属しているか。</summary>
    public static bool IsGrouped(Clip clip) => !string.IsNullOrEmpty(clip.GroupId);

    /// <summary>指定グループのクリップを全トラックから集める（開始時刻順）。groupId が空なら空。</summary>
    public static IReadOnlyList<Clip> GetClips(EditorProject project, string? groupId)
    {
        if (string.IsNullOrEmpty(groupId))
            return Array.Empty<Clip>();
        return project.Tracks.SelectMany(t => t.Clips)
            .Where(c => c.GroupId == groupId)
            .OrderBy(c => c.StartSeconds)
            .ToList();
    }

    /// <summary>グループの外接範囲（最小の開始・最大の終了）。グループが空なら null。</summary>
    public static (double StartSeconds, double EndSeconds)? GetBounds(EditorProject project, string? groupId)
    {
        var clips = GetClips(project, groupId);
        if (clips.Count == 0)
            return null;
        return (clips.Min(c => c.StartSeconds), clips.Max(c => c.EndSeconds));
    }

    /// <summary>
    /// 複数クリップをグループ化し、新しいグループ Id を返す。
    /// 2個未満・重複・プロジェクトに無いクリップがあれば例外。
    /// 既存グループから部分的に移ってきた場合、残り1個になった旧グループは自動解除する。
    /// </summary>
    public static string GroupClips(EditorProject project, IEnumerable<Clip> clips)
    {
        var targets = clips.Distinct().ToList();
        if (targets.Count < 2)
            throw new ArgumentException("グループ化には2個以上のクリップが必要です。", nameof(clips));
        var all = project.Tracks.SelectMany(t => t.Clips).ToHashSet();
        if (targets.Any(c => !all.Contains(c)))
            throw new ArgumentException("プロジェクトに無いクリップが含まれています。", nameof(clips));

        var groupId = Guid.NewGuid().ToString("N");
        foreach (var clip in targets)
            clip.GroupId = groupId;
        CleanupSingletonGroups(project);
        return groupId;
    }

    /// <summary>グループを解除する（全員の GroupId を消す）。解除した個数を返す。</summary>
    public static int Ungroup(EditorProject project, string? groupId)
    {
        var clips = GetClips(project, groupId);
        foreach (var clip in clips)
            clip.GroupId = null;
        return clips.Count;
    }

    /// <summary>
    /// 残り1個になったグループを自動解除する（削除・再グループ化の後の整合用）。
    /// 解除したクリップの個数を返す。
    /// </summary>
    public static int CleanupSingletonGroups(EditorProject project)
    {
        var counts = project.Tracks.SelectMany(t => t.Clips)
            .Where(IsGrouped)
            .GroupBy(c => c.GroupId!)
            .Where(g => g.Count() == 1)
            .Select(g => g.Single())
            .ToList();
        foreach (var clip in counts)
            clip.GroupId = null;
        return counts.Count;
    }

    /// <summary>
    /// グループ全体を delta 秒だけ移動する。相対位置は保つ。
    /// 全員の最小開始位置が0未満にならないよう delta を頭打ちにし、適用した delta を返す。
    /// </summary>
    public static double MoveGroup(EditorProject project, string? groupId, double deltaSeconds)
    {
        var clips = GetClips(project, groupId);
        if (clips.Count == 0)
            return 0;
        var applied = Math.Max(deltaSeconds, -clips.Min(c => c.StartSeconds));
        if (Math.Abs(applied) < 1e-12)
            return 0;
        foreach (var clip in clips)
            clip.StartSeconds += applied;
        return applied;
    }

    /// <summary>
    /// グループの左端をトリミングする。左端（グループ最小開始）に接するクリップだけ
    /// TrimClipStart を適用する。全員分の検証を先に行い、範囲外なら何も変えず例外を投げる。
    /// </summary>
    public static void TrimGroupStart(EditorProject project, string? groupId, double newStartSeconds)
    {
        var clips = GetClips(project, groupId);
        if (clips.Count == 0)
            return;
        var edge = clips.Min(c => c.StartSeconds);
        var targets = clips.Where(c => Math.Abs(c.StartSeconds - edge) <= EdgeToleranceSeconds).ToList();
        foreach (var clip in targets)
        {
            if (newStartSeconds < clip.StartSeconds || newStartSeconds >= clip.EndSeconds)
                throw new ArgumentOutOfRangeException(nameof(newStartSeconds), "グループの先頭トリミング位置がクリップ範囲外です。");
        }
        foreach (var clip in targets)
            TimelineEditor.TrimClipStart(clip, newStartSeconds);
    }

    /// <summary>
    /// グループの右端をトリミングする。右端（グループ最大終了）に接するクリップだけ
    /// TrimClipEnd を適用する。全員分の検証を先に行い、範囲外なら何も変えず例外を投げる。
    /// </summary>
    public static void TrimGroupEnd(EditorProject project, string? groupId, double newEndSeconds)
    {
        var clips = GetClips(project, groupId);
        if (clips.Count == 0)
            return;
        var edge = clips.Max(c => c.EndSeconds);
        var targets = clips.Where(c => Math.Abs(c.EndSeconds - edge) <= EdgeToleranceSeconds).ToList();
        foreach (var clip in targets)
        {
            if (newEndSeconds <= clip.StartSeconds || newEndSeconds > clip.EndSeconds)
                throw new ArgumentOutOfRangeException(nameof(newEndSeconds), "グループの末尾トリミング位置がクリップ範囲外です。");
        }
        foreach (var clip in targets)
            TimelineEditor.TrimClipEnd(clip, newEndSeconds);
    }
}
