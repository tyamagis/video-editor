// プロジェクトテンプレートの単体テスト（TemplateStore の保存・読込・一覧・削除、
// メディア参照が切れている場合の扱い、壊れたテンプレートファイル時のフォールバック）。
// ファイルの読み書きだけを対象とし、ダイアログ（WPF）は扱わない。

using Editor.Core;
using Xunit;

namespace Editor.Tests;

/// <summary>プロジェクトテンプレートの保存・読込・一覧・削除のテスト。</summary>
public sealed class TemplateTests
{
    private static EditorProject SampleProject()
    {
        var project = new EditorProject
        {
            Name = "MVひな形",
            Width = 1920,
            Height = 1080,
            FrameRate = 30,
            Bpm = 128.0
        };
        var video = project.AddTrack(TrackKind.Video, "ビデオ 1");
        video.Clips.Add(new Clip
        {
            Name = "本編",
            Kind = ClipKind.Video,
            SourcePath = @"C:\素材\本編.mp4",
            StartSeconds = 0,
            DurationSeconds = 10,
            Effects = { new ClipEffect { Kind = EffectKind.Glow, Amount = 0.5, Radius = 10 } }
        });
        var text = project.AddTrack(TrackKind.Text, "テキスト 1");
        text.Clips.Add(new Clip
        {
            Name = "タイトル",
            Kind = ClipKind.Text,
            Text = "タイトル",
            StartSeconds = 0,
            DurationSeconds = 5
        });
        project.AddMarker(4.0, "サビ");
        return project;
    }

    // ---- 保存・読込 ----

    [Fact]
    public void SaveAndLoad_RoundTripsProjectSettings()
    {
        using var temp = new TempDirectory();
        var project = SampleProject();

        Assert.Equal(SaveTemplateResult.Saved, TemplateStore.Save("MV用", project, temp.Dir));

        var loaded = TemplateStore.Load(Path.Combine(temp.Dir, "MV用.vetemplate"));
        Assert.Equal("MVひな形", loaded.Name);
        Assert.Equal(128.0, loaded.Bpm);
        Assert.Equal(1920, loaded.Width);
        Assert.Equal(2, loaded.Tracks.Count);
        Assert.Equal("本編", loaded.Tracks[0].Clips[0].Name);
        Assert.Equal(@"C:\素材\本編.mp4", loaded.Tracks[0].Clips[0].SourcePath);
        Assert.Single(loaded.Tracks[0].Clips[0].Effects);
        Assert.Equal(EffectKind.Glow, loaded.Tracks[0].Clips[0].Effects[0].Kind);
        Assert.Equal("タイトル", loaded.Tracks[1].Clips[0].Text);
        Assert.Single(loaded.Markers);
        Assert.Equal("サビ", loaded.Markers[0].Name);
    }

    [Fact]
    public void Save_DoesNotModifySourceProject()
    {
        using var temp = new TempDirectory();
        var project = SampleProject();
        project.Version = 0;

        TemplateStore.Save("MV用", project, temp.Dir);

        Assert.Equal(0, project.Version); // 保存後も呼び出し元のプロジェクトは変わらない。
    }

    [Fact]
    public void List_EmptyDirectory_ReturnsEmpty()
    {
        using var temp = new TempDirectory();
        Assert.Empty(TemplateStore.List(temp.Dir));
    }

    [Fact]
    public void List_MissingDirectory_ReturnsEmpty()
    {
        using var temp = new TempDirectory();
        Assert.Empty(TemplateStore.List(temp.File("存在しないフォルダ")));
    }

    [Fact]
    public void Save_List_ShowsNameAndCreatedDate()
    {
        using var temp = new TempDirectory();
        TemplateStore.Save("MV用", SampleProject(), temp.Dir);

        var list = TemplateStore.List(temp.Dir);

        Assert.Single(list);
        Assert.Equal("MV用", list[0].Name);
        Assert.True(list[0].IsValid);
        Assert.Null(list[0].ErrorMessage);
        // 作成日時は今（UTC）に近い。
        Assert.True(DateTime.UtcNow - list[0].CreatedAtUtc < TimeSpan.FromMinutes(1));
    }

    // ---- 名前の重複・不正 ----

    [Fact]
    public void Save_DuplicateName_OverwritesInPlace()
    {
        using var temp = new TempDirectory();
        var first = SampleProject();
        var second = SampleProject();
        second.Bpm = 100.0;

        Assert.Equal(SaveTemplateResult.Saved, TemplateStore.Save("MV用", first, temp.Dir));
        Assert.Equal(SaveTemplateResult.Overwritten, TemplateStore.Save("MV用", second, temp.Dir));

        // ファイルは1件のまま、内容だけ置き換わる。
        Assert.Single(TemplateStore.List(temp.Dir));
        Assert.Single(Directory.GetFiles(temp.Dir, "*.vetemplate"));
        Assert.Equal(100.0, TemplateStore.Load(Path.Combine(temp.Dir, "MV用.vetemplate")).Bpm);
    }

    [Fact]
    public void Save_DuplicateName_CaseInsensitive_Overwrites()
    {
        using var temp = new TempDirectory();
        Assert.Equal(SaveTemplateResult.Saved, TemplateStore.Save("MV用", SampleProject(), temp.Dir));
        Assert.Equal(SaveTemplateResult.Overwritten, TemplateStore.Save("mv用", SampleProject(), temp.Dir));
        Assert.Single(Directory.GetFiles(temp.Dir, "*.vetemplate"));
    }

    [Fact]
    public void Save_InvalidName_ReturnsInvalidNameAndCreatesNothing()
    {
        using var temp = new TempDirectory();
        Assert.Equal(SaveTemplateResult.InvalidName, TemplateStore.Save("", SampleProject(), temp.Dir));
        Assert.Equal(SaveTemplateResult.InvalidName, TemplateStore.Save("   ", SampleProject(), temp.Dir));
        Assert.Equal(SaveTemplateResult.InvalidName, TemplateStore.Save(null!, SampleProject(), temp.Dir));
        Assert.Equal(SaveTemplateResult.InvalidName, TemplateStore.Save("a/b", SampleProject(), temp.Dir));
        Assert.Equal(SaveTemplateResult.InvalidName, TemplateStore.Save(".", SampleProject(), temp.Dir));
        Assert.Equal(SaveTemplateResult.InvalidName, TemplateStore.Save(new string('あ', 101), SampleProject(), temp.Dir));
        Assert.Empty(Directory.GetFiles(temp.Dir, "*.vetemplate"));
    }

    [Fact]
    public void TemplateExists_FindsSavedTemplateCaseInsensitively()
    {
        using var temp = new TempDirectory();
        Assert.False(TemplateStore.TemplateExists("MV用", temp.Dir));
        TemplateStore.Save("MV用", SampleProject(), temp.Dir);
        Assert.True(TemplateStore.TemplateExists("MV用", temp.Dir));
        Assert.True(TemplateStore.TemplateExists("mv用", temp.Dir));
    }

    // ---- 削除 ----

    [Fact]
    public void Delete_RemovesTemplateFromList()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Dir, "MV用.vetemplate");
        TemplateStore.Save("MV用", SampleProject(), temp.Dir);
        Assert.Single(TemplateStore.List(temp.Dir));

        Assert.True(TemplateStore.Delete(path));

        Assert.Empty(TemplateStore.List(temp.Dir));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Delete_Nonexistent_ReturnsFalse()
    {
        using var temp = new TempDirectory();
        Assert.False(TemplateStore.Delete(temp.File("無い.vetemplate")));
    }

    // ---- メディア参照が切れている場合 ----

    [Fact]
    public void CreateProject_MissingMedia_ReportsPathsAndKeepsClips()
    {
        using var temp = new TempDirectory();
        var project = SampleProject();
        // 「本編」は存在しないパスを指す。テキストクリップは素材を持たない。
        TemplateStore.Save("MV用", project, temp.Dir);

        var result = TemplateStore.CreateProject(Path.Combine(temp.Dir, "MV用.vetemplate"));

        Assert.Single(result.MissingMediaPaths);
        Assert.Equal(@"C:\素材\本編.mp4", result.MissingMediaPaths[0]);
        Assert.Single(result.MissingClipNames);
        Assert.Equal("本編", result.MissingClipNames[0]);
        // 該当クリップは除かず、配置だけ残る（位置・長さ・エフェクトを保持）。
        var clip = result.Project.Tracks[0].Clips[0];
        Assert.Equal("本編", clip.Name);
        Assert.Equal(0, clip.StartSeconds);
        Assert.Equal(10, clip.DurationSeconds);
        Assert.Single(clip.Effects);
        // テキストクリップは影響を受けない。
        Assert.Single(result.Project.Tracks[1].Clips);
    }

    [Fact]
    public void CreateProject_ExistingMedia_NoMissing()
    {
        using var temp = new TempDirectory();
        var mediaPath = temp.File("本編.mp4");
        File.WriteAllText(mediaPath, "dummy");
        var project = SampleProject();
        project.Tracks[0].Clips[0].SourcePath = mediaPath;
        TemplateStore.Save("MV用", project, temp.Dir);

        var result = TemplateStore.CreateProject(Path.Combine(temp.Dir, "MV用.vetemplate"));

        Assert.Empty(result.MissingMediaPaths);
        Assert.Empty(result.MissingClipNames);
    }

    [Fact]
    public void CreateProject_SameMissingFileTwice_PathsAreDeduplicated()
    {
        using var temp = new TempDirectory();
        var project = SampleProject();
        project.Tracks[0].Clips.Add(new Clip
        {
            Name = "本編2",
            Kind = ClipKind.Video,
            SourcePath = @"C:\素材\本編.mp4",
            StartSeconds = 10,
            DurationSeconds = 5
        });
        TemplateStore.Save("MV用", project, temp.Dir);

        var result = TemplateStore.CreateProject(Path.Combine(temp.Dir, "MV用.vetemplate"));

        Assert.Single(result.MissingMediaPaths);
        Assert.Equal(2, result.MissingClipNames.Count); // クリップ名はクリップごとに残る。
    }

    // ---- 壊れたテンプレートファイル ----

    [Fact]
    public void List_BrokenFile_IsMarkedInvalidAndDoesNotThrow()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Dir, "壊れた.vetemplate"), "{ これは壊れた JSON");
        TemplateStore.Save("MV用", SampleProject(), temp.Dir);

        var list = TemplateStore.List(temp.Dir);

        // 壊れたファイルも一覧に残り（削除できる）、有効な方はそのまま使える。
        Assert.Equal(2, list.Count);
        var broken = list.First(t => t.Name == "壊れた");
        Assert.False(broken.IsValid);
        Assert.NotNull(broken.ErrorMessage);
        Assert.True(TemplateStore.List(temp.Dir).First(t => t.Name == "MV用").IsValid);
    }

    [Fact]
    public void Load_BrokenFile_ThrowsInvalidDataException()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Dir, "壊れた.vetemplate");
        File.WriteAllText(path, "{ これは壊れた JSON");

        Assert.Throws<InvalidDataException>(() => TemplateStore.Load(path));
    }

    [Fact]
    public void Load_FileWithoutProject_ThrowsInvalidDataException()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Dir, "空.vetemplate");
        File.WriteAllText(path, "{\"Version\": 1, \"Name\": \"空\"}");

        Assert.Throws<InvalidDataException>(() => TemplateStore.Load(path));
    }

    [Fact]
    public void Load_FutureTemplateVersion_ThrowsInvalidDataException()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Dir, "未来.vetemplate");
        var json = "{\"Version\": 999, \"Name\": \"未来\", \"CreatedAtUtc\": \"2026-11-24T00:00:00Z\", \"Project\": "
            + ProjectStore.Serialize(SampleProject()) + "}";
        File.WriteAllText(path, json);

        var ex = Assert.Throws<InvalidDataException>(() => TemplateStore.Load(path));
        Assert.Contains("999", ex.Message);
    }

    [Fact]
    public void Load_MissingFile_ThrowsInvalidDataException()
    {
        using var temp = new TempDirectory();
        Assert.Throws<InvalidDataException>(() => TemplateStore.Load(temp.File("無い.vetemplate")));
    }

    [Fact]
    public void Delete_BrokenFile_RemovesItFromList()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Dir, "壊れた.vetemplate");
        File.WriteAllText(path, "{ これは壊れた JSON");
        Assert.Single(TemplateStore.List(temp.Dir));

        Assert.True(TemplateStore.Delete(path));

        Assert.Empty(TemplateStore.List(temp.Dir));
    }
}
