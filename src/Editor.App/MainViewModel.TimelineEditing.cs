// MainViewModel の分割ファイル（partial）。タイムライン編集（分割・削除・複数選択・グループ化・リップル削除）。

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using Editor.Audio;
using Editor.Core;
using Editor.Media;
using Editor.Render;
using SkiaSharp;

namespace Editor.App;

public sealed partial class MainViewModel
{
    /// <summary>再生ヘッド位置で全トラックのクリップを分割する。対象が無ければ何もしない。</summary>
    public void SplitAtPlayhead()
    {
        // クリップ先頭ちょうどの位置は IsActiveAt には入るが SplitClip は
        // 分割しない（null）ため、厳密に「内側」のクリップで判定する。
        // そうしないと分割されないのにアンドゥが1件積まれてしまう。
        var targets = Project.Tracks
            .SelectMany(t => t.Clips)
            .Where(c => c.StartSeconds < PlayheadSeconds && PlayheadSeconds < c.EndSeconds)
            .ToList();
        if (targets.Count == 0)
            return;

        BeginEdit();
        var splitAny = false;
        foreach (var track in Project.Tracks)
        {
            foreach (var clip in track.Clips.Where(c => c.IsActiveAt(PlayheadSeconds)).ToList())
            {
                if (TimelineEditor.SplitClip(track, clip, PlayheadSeconds) is not null)
                    splitAny = true;
            }
        }
        if (splitAny)
            NotifyTimelineChanged();
    }

    public void DeleteSelectedClip()
    {
        if (SelectedClip is null)
            return;
        BeginEdit();
        foreach (var track in Project.Tracks)
        {
            if (track.Clips.Remove(SelectedClip))
                break;
        }
        _multiSelectedClips.Remove(SelectedClip);
        OnPropertyChanged(nameof(MultiSelectedClips));
        // グループの残りが1個になったら自動解除する（1個だけのグループは意味を持たない）。
        ClipGroups.CleanupSingletonGroups(Project);
        SelectedClip = null;
        NotifyTimelineChanged();
    }

    // ---- 複数選択・グループ化・リップル削除 ----

    /// <summary>複数選択中のクリップ（主選択 SelectedClip とは別に保持する。グループ化の対象）。</summary>
    public IReadOnlyList<Clip> MultiSelectedClips => _multiSelectedClips;

    /// <summary>選択中クリップがグループに属しているか（グループ解除メニューの有効化・表示用）。</summary>
    public bool IsSelectedClipGrouped => SelectedClip is not null && ClipGroups.IsGrouped(SelectedClip);

    /// <summary>Ctrl+クリックで複数選択を切り替える。切り替えたクリップを主選択にもする。</summary>
    public void ToggleClipSelection(Clip clip)
    {
        if (_multiSelectedClips.Contains(clip))
            _multiSelectedClips.Remove(clip);
        else
            _multiSelectedClips.Add(clip);
        SelectedClip = clip;
        OnPropertyChanged(nameof(MultiSelectedClips));
        NotifyTimelineChanged();
    }

    /// <summary>
    /// グループ化（Ctrl+G）。対象は複数選択＋主選択の和集合。
    /// 2個未満なら何もしない（その旨をステータスに出す）。1回の操作としてアンドゥできる。
    /// グループ化後は複数選択を解く（グループ自体が選択単位になるため）。
    /// </summary>
    public void GroupSelectedClips()
    {
        var targets = _multiSelectedClips
            .Concat(SelectedClip is null ? Array.Empty<Clip>() : new[] { SelectedClip })
            .Distinct()
            .Where(c => Project.Tracks.Any(t => t.Clips.Contains(c)))
            .ToList();
        if (targets.Count < 2)
        {
            StatusMessage = "グループ化には2個以上のクリップが必要です。Ctrl+クリックで複数選択してください。";
            return;
        }
        BeginEdit();
        ClipGroups.GroupClips(Project, targets);
        _multiSelectedClips.Clear();
        OnPropertyChanged(nameof(MultiSelectedClips));
        OnPropertyChanged(nameof(IsSelectedClipGrouped));
        NotifyTimelineChanged();
        StatusMessage = $"{targets.Count} 個のクリップをグループ化しました。ドラッグでまとめて移動・トリミングできます。";
    }

    /// <summary>グループ解除（Ctrl+Shift+G）。選択中クリップのグループ全体を解除する。</summary>
    public void UngroupSelectedClip()
    {
        if (SelectedClip is null || !ClipGroups.IsGrouped(SelectedClip))
            return;
        BeginEdit();
        var count = ClipGroups.Ungroup(Project, SelectedClip.GroupId);
        OnPropertyChanged(nameof(IsSelectedClipGrouped));
        NotifyTimelineChanged();
        StatusMessage = $"グループを解除しました（{count} 個のクリップ）。";
    }

    /// <summary>
    /// リップル削除（Shift+Del：同トラックのみ／allTracks: 全トラックの後続も詰まる）。
    /// 通常削除（Del）と区別する操作。選択中クリップがグループに属する場合は
    /// グループ全員をそれぞれのトラックでリップル削除する（グループ単位の削除）。
    /// マーカーは動かさない（ClipGroups / TimelineEditor の設計規則）。
    /// </summary>
    public void RippleDeleteSelectedClip(bool allTracks = false)
    {
        if (SelectedClip is null)
            return;
        var scope = allTracks ? RippleScope.AllTracks : RippleScope.SameTrack;
        BeginEdit();
        if (ClipGroups.IsGrouped(SelectedClip))
        {
            // グループ全員を消す。各トラックで開始の遅い順に処理すると、同じトラックの
            // グループ内後続が先に消え、詰め量の二重計算を避けられる。
            foreach (var member in ClipGroups.GetClips(Project, SelectedClip.GroupId).OrderByDescending(c => c.StartSeconds).ToList())
                TimelineEditor.RippleDeleteClip(Project, member, scope);
            StatusMessage = "グループをリップル削除しました（後続クリップが前に詰まります）。";
        }
        else
        {
            TimelineEditor.RippleDeleteClip(Project, SelectedClip, scope);
            StatusMessage = allTracks
                ? "リップル削除しました（全トラックの後続が前に詰まります）。"
                : "リップル削除しました（同じトラックの後続が前に詰まります）。";
        }
        _multiSelectedClips.RemoveAll(c => !Project.Tracks.Any(t => t.Clips.Contains(c)));
        OnPropertyChanged(nameof(MultiSelectedClips));
        ClipGroups.CleanupSingletonGroups(Project);
        SelectedClip = null;
        NotifyTimelineChanged();
    }
}
