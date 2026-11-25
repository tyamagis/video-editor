// マーカー機能のテスト（EditorProject.Markers と ProjectStore の往復）。
// 追加・ソート・削除・次／前ジャンプ、旧プロジェクト（Markers 無し）の読み込みを対象とする。

using Editor.Core;
using Xunit;

namespace Editor.Tests;

/// <summary>マーカー（EditorProject.Markers）のテスト。</summary>
public sealed class MarkerTests
{
    private static EditorProject SampleProject()
    {
        var project = new EditorProject { Name = "テスト", Version = 1 };
        var track = project.AddTrack(TrackKind.Video, "ビデオ 1");
        track.Clips.Add(new Clip
        {
            Name = "クリップ1",
            Kind = ClipKind.Video,
            StartSeconds = 0,
            DurationSeconds = 10
        });
        return project;
    }

    // ---- 追加・ソート ----

    /// <summary>追加した順番に関わらず、時刻順に保持されること。</summary>
    [Fact]
    public void AddMarker_KeepsSortedOrder()
    {
        var project = SampleProject();
        project.AddMarker(10, "C");
        project.AddMarker(5, "A");
        project.AddMarker(7, "B");

        Assert.Equal(new[] { 5.0, 7.0, 10.0 }, project.Markers.Select(m => m.TimeSeconds));
        Assert.Equal(new[] { "A", "B", "C" }, project.Markers.Select(m => m.Name));
    }

    /// <summary>同じ時刻のマーカーは、後から追加したものが後ろに入ること（安定）。</summary>
    [Fact]
    public void AddMarker_SameTime_AppendedAfterExisting()
    {
        var project = SampleProject();
        var first = project.AddMarker(5, "最初");
        var second = project.AddMarker(5, "次");

        Assert.Equal(new[] { first, second }, project.Markers);
    }

    /// <summary>負の時刻は 0 に丸められること。</summary>
    [Fact]
    public void AddMarker_NegativeTime_ClampedToZero()
    {
        var project = SampleProject();
        var marker = project.AddMarker(-3, "先頭");

        Assert.Equal(0, marker.TimeSeconds);
    }

    /// <summary>代入時は時刻順に並べ替えられ、null は空リストになること。</summary>
    [Fact]
    public void Markers_Setter_SortsAndCoalescesNull()
    {
        var project = SampleProject();
        project.Markers = new List<Marker>
        {
            new() { TimeSeconds = 9, Name = "C" },
            new() { TimeSeconds = 1, Name = "A" }
        };

        Assert.Equal(new[] { 1.0, 9.0 }, project.Markers.Select(m => m.TimeSeconds));

        project.Markers = null!;

        Assert.NotNull(project.Markers);
        Assert.Empty(project.Markers);
    }

    /// <summary>時刻を直接書き換えた後は SortMarkers で並べ替え直せること。</summary>
    [Fact]
    public void SortMarkers_RestoresOrderAfterDirectEdit()
    {
        var project = SampleProject();
        var a = project.AddMarker(2, "A");
        project.AddMarker(5, "B");
        a.TimeSeconds = 8; // 直接書き換え（順序が崩れる）。

        project.SortMarkers();

        Assert.Equal(new[] { 5.0, 8.0 }, project.Markers.Select(m => m.TimeSeconds));
    }

    // ---- 削除 ----

    /// <summary>指定したマーカーだけが削除されること。</summary>
    [Fact]
    public void RemoveMarker_RemovesOnlyThatMarker()
    {
        var project = SampleProject();
        var a = project.AddMarker(2, "A");
        var b = project.AddMarker(5, "B");

        Assert.True(project.RemoveMarker(a));
        Assert.Equal(new[] { b }, project.Markers);
        Assert.False(project.RemoveMarker(a)); // 2回目は削除されない。
    }

    // ---- 次／前ジャンプ ----

    /// <summary>次のマーカーは、指定時刻より後ろにある最初のもの。ちょうど同じ時刻のものは含めない。</summary>
    [Fact]
    public void NextMarkerAfter_ReturnsFirstMarkerAfterTime()
    {
        var project = SampleProject();
        project.AddMarker(2, "A");
        project.AddMarker(5, "B");
        project.AddMarker(8, "C");

        Assert.Equal("B", project.NextMarkerAfter(3)?.Name);
        Assert.Equal("C", project.NextMarkerAfter(5)?.Name); // ちょうど 5s の B は「次」ではないので、次は 8s の C。
        Assert.Null(project.NextMarkerAfter(8));
        Assert.Null(project.NextMarkerAfter(100));
    }

    /// <summary>前のマーカーは、指定時刻より前にある最後のもの。ちょうど同じ時刻のものは含めない。</summary>
    [Fact]
    public void PreviousMarkerBefore_ReturnsLastMarkerBeforeTime()
    {
        var project = SampleProject();
        project.AddMarker(2, "A");
        project.AddMarker(5, "B");
        project.AddMarker(8, "C");

        Assert.Equal("B", project.PreviousMarkerBefore(7)?.Name);
        Assert.Equal("A", project.PreviousMarkerBefore(5)?.Name); // ちょうど 5s の B は「前」ではない。
        Assert.Null(project.PreviousMarkerBefore(2));
        Assert.Null(project.PreviousMarkerBefore(0));
    }

    /// <summary>マーカーが無ければ次・前とも null になること。</summary>
    [Fact]
    public void NextAndPreviousMarker_WithNoMarkers_ReturnNull()
    {
        var project = SampleProject();

        Assert.Null(project.NextMarkerAfter(0));
        Assert.Null(project.PreviousMarkerBefore(10));
    }

    // ---- 保存・読み込み ----

    /// <summary>マーカーが保存・再読込で残ること（時刻・名前とも）。</summary>
    [Fact]
    public void Serialize_RoundTrip_PreservesMarkers()
    {
        var project = SampleProject();
        project.AddMarker(2.5, "イントロ");
        project.AddMarker(30, "サビ");

        var restored = ProjectStore.Deserialize(ProjectStore.Serialize(project));

        Assert.Equal(2, restored.Markers.Count);
        Assert.Equal(2.5, restored.Markers[0].TimeSeconds, 9);
        Assert.Equal("イントロ", restored.Markers[0].Name);
        Assert.Equal(30, restored.Markers[1].TimeSeconds, 9);
        Assert.Equal("サビ", restored.Markers[1].Name);
    }

    /// <summary>旧プロジェクト（Markers の項目が無い JSON）は空リストとして開けること。</summary>
    [Fact]
    public void Deserialize_OldProjectWithoutMarkers_LoadsAsEmptyList()
    {
        var project = SampleProject();
        var json = ProjectStore.Serialize(project);
        // 旧版で保存された形（Markers の行が無い JSON）を再現する。
        // Markers は最後に書かれるプロパティのため後ろにカンマが無い
        // （途中にある場合も想定して両方の形を消す）。
        var oldJson = json.Replace("\"Markers\": [],\n", string.Empty)
            .Replace(",\n  \"Markers\": []", string.Empty);
        Assert.DoesNotContain("Markers", oldJson); // 置換が効いていること（効かなければこのテストは無意味）。

        var restored = ProjectStore.Deserialize(oldJson);

        Assert.NotNull(restored.Markers);
        Assert.Empty(restored.Markers);
        Assert.Equal("テスト", restored.Name);
    }

    /// <summary>Markers が null の JSON でも空リストとして開けること。</summary>
    [Fact]
    public void Deserialize_ProjectWithNullMarkers_LoadsAsEmptyList()
    {
        var project = SampleProject();
        var json = ProjectStore.Serialize(project);
        var nullJson = json.Replace("\"Markers\": []", "\"Markers\": null");
        Assert.Contains("\"Markers\": null", nullJson);

        var restored = ProjectStore.Deserialize(nullJson);

        Assert.NotNull(restored.Markers);
        Assert.Empty(restored.Markers);
    }

    /// <summary>ファイルへの保存・読み込みの往復でマーカーが残ること。</summary>
    [Fact]
    public void Save_ThenLoad_RoundTrip_PreservesMarkers()
    {
        using var temp = new TempDirectory();
        var projectPath = temp.File("MyProject.veproj");
        var project = SampleProject();
        project.AddMarker(12.25, "ドロップ");

        ProjectStore.Save(projectPath, project);
        var loaded = ProjectStore.Load(projectPath);

        Assert.Single(loaded.Markers);
        Assert.Equal(12.25, loaded.Markers[0].TimeSeconds, 9);
        Assert.Equal("ドロップ", loaded.Markers[0].Name);
    }
}
