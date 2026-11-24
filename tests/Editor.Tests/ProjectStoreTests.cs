// ProjectStore の Version 扱いのテスト（Save / Serialize の副作用なし・ラウンドトリップ）。
// Serialize は読み取り専用の操作であり、呼び出し元のプロジェクトの Version を変えてはならない。

using Editor.Core;
using Xunit;

namespace Editor.Tests;

/// <summary>ProjectStore.Save / Serialize の Version 扱いのテスト。</summary>
public sealed class ProjectStoreTests
{
    private static EditorProject SampleProject(int version)
    {
        var project = new EditorProject { Name = "テスト", Version = version };
        var track = project.AddTrack(TrackKind.Video, "ビデオ 1");
        track.Clips.Add(new Clip
        {
            Name = "クリップ1",
            Kind = ClipKind.Video,
            StartSeconds = 0,
            DurationSeconds = 5
        });
        return project;
    }

    [Fact]
    public void Serialize_DoesNotModifyProjectVersion()
    {
        var project = SampleProject(version: 0);

        var json = ProjectStore.Serialize(project);

        Assert.Equal(0, project.Version); // 呼び出し元のプロジェクトは変わらない。
        Assert.Contains("\"Version\": 1", json); // JSON には現行版が書かれる。
    }

    [Fact]
    public void Serialize_RoundTrip_RestoresCurrentVersion()
    {
        var project = SampleProject(version: 0);

        var json = ProjectStore.Serialize(project);
        var restored = ProjectStore.Deserialize(json);

        Assert.Equal(EditorProject.CurrentVersion, restored.Version);
        Assert.Equal("テスト", restored.Name);
        Assert.Single(restored.Tracks[0].Clips);
        Assert.Equal("クリップ1", restored.Tracks[0].Clips[0].Name);
        Assert.Equal(0, project.Version); // 元のプロジェクトも変わらない。
    }

    [Fact]
    public void Save_DoesNotModifyProjectVersion()
    {
        using var temp = new TempDirectory();
        var projectPath = temp.File("MyProject.veproj");
        var project = SampleProject(version: 0);

        ProjectStore.Save(projectPath, project);

        Assert.Equal(0, project.Version); // 保存後も呼び出し元のプロジェクトは変わらない。
    }

    [Fact]
    public void Save_ThenLoad_RoundTrip_RestoresCurrentVersion()
    {
        using var temp = new TempDirectory();
        var projectPath = temp.File("MyProject.veproj");
        var project = SampleProject(version: 0);

        ProjectStore.Save(projectPath, project);
        var loaded = ProjectStore.Load(projectPath);

        Assert.Equal(EditorProject.CurrentVersion, loaded.Version);
        Assert.Equal("テスト", loaded.Name);
        Assert.Single(loaded.Tracks[0].Clips);
        Assert.Equal(0, project.Version); // 元のプロジェクトも変わらない。
    }

    [Fact]
    public void Serialize_DoesNotModifyProject_WhenVersionIsAlreadyCurrent()
    {
        // 既に現行版でも、Serialize はオブジェクトを書き換えない（参照の共有がないことの確認）。
        var project = SampleProject(version: EditorProject.CurrentVersion);
        var jsonBefore = ProjectStore.Serialize(project);

        var jsonAgain = ProjectStore.Serialize(project);

        Assert.Equal(EditorProject.CurrentVersion, project.Version);
        Assert.Equal(jsonBefore, jsonAgain);
    }
}
