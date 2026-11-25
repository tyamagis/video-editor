// スナップ拡張・リップル編集・クリップのグループ化のテスト。
// いずれも TimelineEditing レベルの純粋ロジック（SnapCalculator / TimelineEditor / ClipGroups）と
// Clip.GroupId の JSON 往復・旧 JSON 互換を対象にする。

using Editor.Core;
using Xunit;

namespace Editor.Tests;

public sealed class SnapRippleGroupTests
{
    private static Clip MakeClip(string name, double start, double duration)
        => new() { Name = name, Kind = ClipKind.Video, StartSeconds = start, DurationSeconds = duration };

    /// <summary>トラック1（A:2-6, B:10-14）とトラック2（C:4-8）、マーカー 7.5、を持つプロジェクト。</summary>
    private static (EditorProject Project, Clip A, Clip B, Clip C) Sample()
    {
        var project = new EditorProject();
        var t1 = project.AddTrack(TrackKind.Video, "ビデオ 1");
        var t2 = project.AddTrack(TrackKind.Audio, "オーディオ 1");
        var a = MakeClip("A", 2, 4);
        var b = MakeClip("B", 10, 4);
        var c = MakeClip("C", 4, 4);
        c.Kind = ClipKind.Audio;
        t1.Clips.Add(a);
        t1.Clips.Add(b);
        t2.Clips.Add(c);
        project.AddMarker(7.5, "サビ");
        return (project, a, b, c);
    }

    // ---- スナップ候補 ----

    [Fact]
    public void CollectSnapTimes_IncludesZeroMarkersPlayheadAndClipEdges()
    {
        var (project, a, _, _) = Sample();
        var times = SnapCalculator.CollectSnapTimes(project, excludedClips: new[] { a }, playheadSeconds: 3.25);
        Assert.Equal(new[] { 0.0, 3.25, 4.0, 7.5, 8.0, 10.0, 14.0 }, times);
    }

    [Fact]
    public void CollectSnapTimes_ExcludesAllGivenClips_AndPlayheadWhenNull()
    {
        var (project, a, b, c) = Sample();
        var times = SnapCalculator.CollectSnapTimes(project, new[] { a, b, c });
        Assert.Equal(new[] { 0.0, 7.5 }, times); // 先頭とマーカーだけが残る。
    }

    [Fact]
    public void SnapToNearest_WithinTolerance_ReturnsNearest()
    {
        var candidates = new List<double> { 0, 4, 7.5, 10 };
        Assert.Equal(7.5, SnapCalculator.SnapToNearest(7.44, candidates, 0.1));
        Assert.Equal(4.0, SnapCalculator.SnapToNearest(4.09, candidates, 0.1));
    }

    [Fact]
    public void SnapToNearest_OutsideToleranceOrEmpty_ReturnsNull()
    {
        var candidates = new List<double> { 0, 4 };
        Assert.Null(SnapCalculator.SnapToNearest(2.0, candidates, 0.1));
        Assert.Null(SnapCalculator.SnapToNearest(0.0, Array.Empty<double>(), 0.1));
    }

    [Fact]
    public void SnapMoveStart_SnapsByStartEdge()
    {
        var candidates = new List<double> { 0, 10 };
        // 開始 10.05（長さ4）→ 開始端で 10 に吸着。
        Assert.Equal(10.0, SnapCalculator.SnapMoveStart(10.05, 4, candidates, 0.1), 9);
    }

    [Fact]
    public void SnapMoveStart_SnapsByEndEdgeWhenCloser()
    {
        var candidates = new List<double> { 0, 10 };
        // 開始 6.04・長さ4 → 終了 10.04 が終了端候補10に近いので開始は 6 になる。
        Assert.Equal(6.0, SnapCalculator.SnapMoveStart(6.04, 4, candidates, 0.1), 9);
    }

    [Fact]
    public void SnapMoveStart_NoCandidateInRange_KeepsRequestedAndClampsToZero()
    {
        var candidates = new List<double> { 10 };
        Assert.Equal(3.0, SnapCalculator.SnapMoveStart(3.0, 4, candidates, 0.1), 9);
        Assert.Equal(0.0, SnapCalculator.SnapMoveStart(-2.0, 4, candidates, 0.1), 9);
    }

    [Fact]
    public void WithNearbyBeatTimes_AddsOnlyBeatsNearTarget()
    {
        var candidates = new List<double> { 0, 10 };
        var beats = BeatGridCalculator.GetBeatTimes(120, 0, 20); // 0.5秒刻み
        var merged = SnapCalculator.WithNearbyBeatTimes(candidates, beats, nearTimeSeconds: 5.03, toleranceSeconds: 0.1);
        Assert.Contains(5.0, merged);
        Assert.DoesNotContain(3.0, merged);
        // 近くの拍へ吸着できる。
        Assert.Equal(5.0, SnapCalculator.SnapToNearest(5.03, merged, 0.1));
    }

    // ---- リップル削除 ----

    [Fact]
    public void RippleDelete_SameTrack_ShiftsOnlySameTrackFollowers()
    {
        var (project, a, b, c) = Sample();
        TimelineEditor.RippleDeleteClip(project, a, RippleScope.SameTrack);
        Assert.DoesNotContain(a, project.Tracks[0].Clips);
        Assert.Equal(6.0, b.StartSeconds, 9); // 10 - 4
        Assert.Equal(4.0, c.StartSeconds, 9); // 他トラックは動かない。
        Assert.Equal(7.5, project.Markers[0].TimeSeconds, 9); // マーカーは動かさない（設計規則）。
    }

    [Fact]
    public void RippleDelete_AllTracks_ShiftsFollowersOnAllTracks()
    {
        var (project, a, b, c) = Sample();
        var d = MakeClip("D", 9, 2);
        project.Tracks[1].Clips.Add(d);
        TimelineEditor.RippleDeleteClip(project, a, RippleScope.AllTracks);
        Assert.Equal(6.0, b.StartSeconds, 9);
        Assert.Equal(5.0, d.StartSeconds, 9); // 9 - 4（削除クリップ終了 6 以降に始まる）
        Assert.Equal(4.0, c.StartSeconds, 9); // 終了位置より前に始まるクリップは動かない。
    }

    [Fact]
    public void RippleDelete_OverlappingClipStartingInside_IsNotShifted()
    {
        var project = new EditorProject();
        var track = project.AddTrack(TrackKind.Video, "V");
        var a = MakeClip("A", 0, 10);
        var overlap = MakeClip("X", 5, 10); // A の内側から始まる。
        var after = MakeClip("B", 10, 5);
        track.Clips.Add(a);
        track.Clips.Add(overlap);
        track.Clips.Add(after);
        TimelineEditor.RippleDeleteClip(track, a);
        Assert.Equal(5.0, overlap.StartSeconds, 9);
        Assert.Equal(0.0, after.StartSeconds, 9); // 10 - 10
    }

    // ---- リップルトリミング・移動 ----

    [Fact]
    public void RippleTrimEnd_Shorten_PullsFollowers()
    {
        var (project, a, b, _) = Sample();
        TimelineEditor.RippleTrimClipEnd(project.Tracks[0], a, 4.0); // 6→4、delta -2
        Assert.Equal(4.0, a.EndSeconds, 9);
        Assert.Equal(8.0, b.StartSeconds, 9);
    }

    [Fact]
    public void RippleTrimEnd_ExtendingBeyondCurrentEnd_ThrowsAndChangesNothing()
    {
        // TrimClipEnd と同じ制約：素材の総長をモデルが知らないため、現在の終了より後ろへの
        // 延長はできない（ドラッグ操作では開始時点の状態へ戻してから適用するため、
        // 「短くした分を戻す」動きは UI 側で実質的に効く）。
        var project = new EditorProject();
        var track = project.AddTrack(TrackKind.Video, "V");
        var a = MakeClip("A", 0, 4);
        var b = MakeClip("B", 4, 4);
        track.Clips.Add(a);
        track.Clips.Add(b);
        Assert.Throws<ArgumentOutOfRangeException>(() => TimelineEditor.RippleTrimClipEnd(track, a, 5.0));
        Assert.Equal(4.0, a.EndSeconds, 9);
        Assert.Equal(4.0, b.StartSeconds, 9);
    }

    [Fact]
    public void RippleTrimEnd_PositiveDeltaFromRestoredOrigin_PushesFollowers()
    {
        // UIドラッグの流れの再現：原点（0-6、後続は6）から短縮状態（0-3、後続は3）を経て、
        // 原点へ戻してから 5 へ適用すると delta は +2 となり後続も 3→5 へ戻る方向に動く。
        var project = new EditorProject();
        var track = project.AddTrack(TrackKind.Video, "V");
        var a = MakeClip("A", 0, 3);
        var b = MakeClip("B", 3, 4);
        track.Clips.Add(a);
        track.Clips.Add(b);
        a.DurationSeconds = 6; // 原点復元に相当。
        b.StartSeconds = 6;
        TimelineEditor.RippleTrimClipEnd(track, a, 5.0); // 原点の終了 6 基準では無く現在 6 から -1。
        Assert.Equal(5.0, a.EndSeconds, 9);
        Assert.Equal(5.0, b.StartSeconds, 9);
    }

    [Fact]
    public void RippleTrimStart_Shorten_PullsFollowers()
    {
        var (project, a, b, _) = Sample();
        TimelineEditor.RippleTrimClipStart(project.Tracks[0], a, 3.0); // 先頭 2→3、長さ -1
        Assert.Equal(3.0, a.StartSeconds, 9);
        Assert.Equal(3.0, a.DurationSeconds, 9);
        Assert.Equal(9.0, b.StartSeconds, 9);
    }

    [Fact]
    public void RippleTrimEnd_AllTracks_FollowersOnOtherTracksMove()
    {
        var (project, a, _, c) = Sample();
        var d = MakeClip("D", 8, 2);
        project.Tracks[1].Clips.Add(d);
        TimelineEditor.RippleTrimClipEnd(project, project.Tracks[0], a, 4.0, RippleScope.AllTracks);
        Assert.Equal(6.0, d.StartSeconds, 9); // 8 - 2
        Assert.Equal(4.0, c.StartSeconds, 9); // 旧終了 6 より前に始まるので動かない。
    }

    [Fact]
    public void RippleMove_MovesClipAndFollowersTogether()
    {
        var (project, a, b, _) = Sample();
        var applied = TimelineEditor.RippleMoveClip(project.Tracks[0], a, 5.0);
        Assert.Equal(3.0, applied, 9);
        Assert.Equal(5.0, a.StartSeconds, 9);
        Assert.Equal(13.0, b.StartSeconds, 9);
    }

    [Fact]
    public void RippleMove_ClampsAtZero()
    {
        var (project, a, b, _) = Sample();
        var applied = TimelineEditor.RippleMoveClip(project.Tracks[0], a, -10.0);
        Assert.Equal(-2.0, applied, 9); // 開始 2 が 0 になるまでだけ。
        Assert.Equal(0.0, a.StartSeconds, 9);
        Assert.Equal(8.0, b.StartSeconds, 9);
    }

    // ---- グループ化 ----

    [Fact]
    public void GroupClips_AssignsSameIdAcrossTracks()
    {
        var (project, a, _, c) = Sample();
        var id = ClipGroups.GroupClips(project, new[] { a, c });
        Assert.Equal(id, a.GroupId);
        Assert.Equal(id, c.GroupId);
        Assert.Equal(2, ClipGroups.GetClips(project, id).Count);
        var bounds = ClipGroups.GetBounds(project, id);
        Assert.NotNull(bounds);
        Assert.Equal(2.0, bounds!.Value.StartSeconds, 9);
        Assert.Equal(8.0, bounds.Value.EndSeconds, 9);
    }

    [Fact]
    public void GroupClips_RequiresAtLeastTwoDistinctClips()
    {
        var (project, a, _, _) = Sample();
        Assert.Throws<ArgumentException>(() => ClipGroups.GroupClips(project, new[] { a }));
        Assert.Throws<ArgumentException>(() => ClipGroups.GroupClips(project, new[] { a, a }));
    }

    [Fact]
    public void GroupClips_RegroupingPartialOldGroup_CleansUpSingleton()
    {
        var (project, a, b, c) = Sample();
        var oldId = ClipGroups.GroupClips(project, new[] { a, b });
        var newId = ClipGroups.GroupClips(project, new[] { a, c }); // A を持っていく。
        Assert.Equal(newId, a.GroupId);
        Assert.Null(b.GroupId); // 1個だけ残った旧グループは自動解除。
        Assert.NotEqual(oldId, newId);
    }

    [Fact]
    public void Ungroup_ClearsAllMembers()
    {
        var (project, a, b, _) = Sample();
        var id = ClipGroups.GroupClips(project, new[] { a, b });
        Assert.Equal(2, ClipGroups.Ungroup(project, id));
        Assert.Null(a.GroupId);
        Assert.Null(b.GroupId);
    }

    [Fact]
    public void MoveGroup_KeepsRelativePositionsAcrossTracks()
    {
        var (project, a, _, c) = Sample();
        var id = ClipGroups.GroupClips(project, new[] { a, c });
        var applied = ClipGroups.MoveGroup(project, id, 3.0);
        Assert.Equal(3.0, applied, 9);
        Assert.Equal(5.0, a.StartSeconds, 9);
        Assert.Equal(7.0, c.StartSeconds, 9);
    }

    [Fact]
    public void MoveGroup_ClampsSoEarliestClipStaysAtOrAfterZero()
    {
        var (project, a, _, c) = Sample();
        var id = ClipGroups.GroupClips(project, new[] { a, c });
        var applied = ClipGroups.MoveGroup(project, id, -10.0);
        Assert.Equal(-2.0, applied, 9);
        Assert.Equal(0.0, a.StartSeconds, 9);
        Assert.Equal(2.0, c.StartSeconds, 9);
    }

    [Fact]
    public void TrimGroupEnd_TrimsOnlyClipsTouchingRightEdge()
    {
        var (project, a, _, c) = Sample();
        // A:2-6, C:4-8 → 右端は C の終了 8。
        var id = ClipGroups.GroupClips(project, new[] { a, c });
        ClipGroups.TrimGroupEnd(project, id, 7.0);
        Assert.Equal(7.0, c.EndSeconds, 9);
        Assert.Equal(6.0, a.EndSeconds, 9); // 端に接していない A は不変。
    }

    [Fact]
    public void TrimGroupStart_TrimsOnlyClipsTouchingLeftEdge()
    {
        var (project, a, _, c) = Sample();
        // 左端は A の開始 2。
        var id = ClipGroups.GroupClips(project, new[] { a, c });
        ClipGroups.TrimGroupStart(project, id, 3.0);
        Assert.Equal(3.0, a.StartSeconds, 9);
        Assert.Equal(3.0, a.DurationSeconds, 9);
        Assert.Equal(4.0, c.StartSeconds, 9); // C は不変。
    }

    [Fact]
    public void TrimGroupEnd_OutOfRangeForOneMember_ChangesNothing()
    {
        var project = new EditorProject();
        var track = project.AddTrack(TrackKind.Video, "V");
        var a = MakeClip("A", 0, 10);
        var b = MakeClip("B", 5, 5); // A と同じ右端 10 だが、開始が 5。
        track.Clips.Add(a);
        track.Clips.Add(b);
        var id = ClipGroups.GroupClips(project, new[] { a, b });
        // 4.0 は B の開始 5 より前なので B をトリミングできない → 全体が失敗し A も変わらない。
        Assert.Throws<ArgumentOutOfRangeException>(() => ClipGroups.TrimGroupEnd(project, id, 4.0));
        Assert.Equal(10.0, a.EndSeconds, 9);
        Assert.Equal(10.0, b.EndSeconds, 9);
    }

    [Fact]
    public void SplitClip_BothHalvesStayInGroup()
    {
        var (project, a, b, _) = Sample();
        var id = ClipGroups.GroupClips(project, new[] { a, b });
        var second = TimelineEditor.SplitClip(project.Tracks[0], a, 4.0);
        Assert.NotNull(second);
        Assert.Equal(id, a.GroupId);
        Assert.Equal(id, second!.GroupId);
        Assert.Equal(3, ClipGroups.GetClips(project, id).Count);
    }

    [Fact]
    public void CleanupSingletonGroups_AfterDelete_ClearsRemainingMember()
    {
        var (project, a, b, _) = Sample();
        ClipGroups.GroupClips(project, new[] { a, b });
        project.Tracks[0].Clips.Remove(a);
        Assert.Equal(1, ClipGroups.CleanupSingletonGroups(project));
        Assert.Null(b.GroupId);
    }

    [Fact]
    public void RippleDelete_GroupMemberOnSameTrack_ShiftsGroupedFollowerByDeletedDuration()
    {
        // リップルとグループの組み合わせ：同じトラックでグループの前半をリップル削除すると、
        // 後半（同じグループ）も後続として詰まる。相対関係はリップル規則が優先する（設計規則）。
        var project = new EditorProject();
        var track = project.AddTrack(TrackKind.Video, "V");
        var a = MakeClip("A", 0, 4);
        var b = MakeClip("B", 4, 4);
        track.Clips.Add(a);
        track.Clips.Add(b);
        ClipGroups.GroupClips(project, new[] { a, b });
        TimelineEditor.RippleDeleteClip(track, a);
        Assert.Equal(0.0, b.StartSeconds, 9);
        ClipGroups.CleanupSingletonGroups(project);
        Assert.Null(b.GroupId);
    }

    // ---- JSON ----

    [Fact]
    public void Json_RoundTrip_PreservesGroupId()
    {
        var (project, a, _, c) = Sample();
        var id = ClipGroups.GroupClips(project, new[] { a, c });
        var restored = ProjectStore.Deserialize(ProjectStore.Serialize(project));
        var restoredClips = restored.Tracks.SelectMany(t => t.Clips).ToList();
        Assert.Equal(2, restoredClips.Count(x => x.GroupId == id));
        Assert.Single(restoredClips, x => string.IsNullOrEmpty(x.GroupId));
    }

    [Fact]
    public void Json_OldProjectWithoutGroupId_LoadsAsUngrouped()
    {
        var (project, _, _, _) = Sample();
        var json = ProjectStore.Serialize(project);
        var oldJson = json.Replace("\"GroupId\": null,", string.Empty);
        Assert.DoesNotContain("GroupId", oldJson);
        var restored = ProjectStore.Deserialize(oldJson);
        Assert.All(restored.Tracks.SelectMany(t => t.Clips), clip => Assert.Null(clip.GroupId));
    }
}
