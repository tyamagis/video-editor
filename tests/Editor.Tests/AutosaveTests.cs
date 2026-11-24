// 自動保存とアプリ設定の単体テスト（AutoSave / AppSettings / ProjectStore.Save の原子性）。
// ファイルの読み書きだけを対象とし、ViewModel やタイマーは扱わない。

using Editor.Core;
using Xunit;

namespace Editor.Tests;

/// <summary>自動保存（AutoSave）とアプリ設定（AppSettings）のテスト。</summary>
public sealed class AutosaveTests
{
    private static EditorProject SampleProject()
    {
        var project = new EditorProject { Name = "テスト" };
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

    // ---- 自動保存ファイルのパス ----

    [Fact]
    public void GetAutosavePath_InsertsAutosaveBeforeExtension()
    {
        using var temp = new TempDirectory();
        var projectPath = temp.File("MyProject.veproj");

        Assert.Equal(temp.File("MyProject.autosave.veproj"), AutoSave.GetAutosavePath(projectPath));
    }

    [Fact]
    public void IsAutosavePath_DetectsAutosaveFile()
    {
        Assert.True(AutoSave.IsAutosavePath("/tmp/MyProject.autosave.veproj"));
        Assert.False(AutoSave.IsAutosavePath("/tmp/MyProject.veproj"));
        // "autosave" という単語が名前に入っているだけでは自動保存ファイルではない。
        Assert.False(AutoSave.IsAutosavePath("/tmp/autosave-note.veproj"));
    }

    [Fact]
    public void GetProjectPathFromAutosave_RestoresOriginalPath()
    {
        using var temp = new TempDirectory();
        var autosavePath = temp.File("MyProject.autosave.veproj");

        Assert.Equal(temp.File("MyProject.veproj"), AutoSave.GetProjectPathFromAutosave(autosavePath));
    }

    // ---- 自動保存ファイルの新しさ ----

    [Fact]
    public void TryGetAutosaveInfo_NoAutosave_ReturnsFalse()
    {
        using var temp = new TempDirectory();
        var projectPath = temp.File("MyProject.veproj");
        File.WriteAllText(projectPath, "{}");

        Assert.False(AutoSave.TryGetAutosaveInfo(projectPath, out _, out _));
    }

    [Fact]
    public void TryGetAutosaveInfo_NewerAutosave_ReturnsTrueWithTime()
    {
        using var temp = new TempDirectory();
        var projectPath = temp.File("MyProject.veproj");
        File.WriteAllText(projectPath, "{}");
        File.SetLastWriteTimeUtc(projectPath, new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc));

        AutoSave.Save(projectPath, SampleProject());
        var autosavePath = AutoSave.GetAutosavePath(projectPath);
        var autosaveTime = new DateTime(2026, 11, 24, 1, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(autosavePath, autosaveTime);

        Assert.True(AutoSave.TryGetAutosaveInfo(projectPath, out var found, out var isNewer));
        Assert.Equal(autosaveTime, found);
        Assert.True(isNewer);
    }

    [Fact]
    public void TryGetAutosaveInfo_OlderAutosave_IsNewerIsFalse()
    {
        using var temp = new TempDirectory();
        var projectPath = temp.File("MyProject.veproj");
        File.WriteAllText(projectPath, "{}");
        File.SetLastWriteTimeUtc(projectPath, new DateTime(2026, 11, 24, 2, 0, 0, DateTimeKind.Utc));

        AutoSave.Save(projectPath, SampleProject());
        var autosavePath = AutoSave.GetAutosavePath(projectPath);
        File.SetLastWriteTimeUtc(autosavePath, new DateTime(2026, 11, 24, 1, 0, 0, DateTimeKind.Utc));

        Assert.True(AutoSave.TryGetAutosaveInfo(projectPath, out _, out var isNewer));
        Assert.False(isNewer);
    }

    [Fact]
    public void TryGetAutosaveInfo_ProjectFileMissing_IsNewerIsTrue()
    {
        // 本ファイルが無くて自動保存ファイルだけある場合も、復旧対象として扱う。
        using var temp = new TempDirectory();
        var projectPath = temp.File("MyProject.veproj");

        AutoSave.Save(projectPath, SampleProject());

        Assert.True(AutoSave.TryGetAutosaveInfo(projectPath, out _, out var isNewer));
        Assert.True(isNewer);
    }

    // ---- 保存・読み込み・削除 ----

    [Fact]
    public void Save_ThenLoad_RoundTrip()
    {
        using var temp = new TempDirectory();
        var projectPath = temp.File("MyProject.veproj");
        var project = SampleProject();

        AutoSave.Save(projectPath, project);
        var loaded = ProjectStore.Load(AutoSave.GetAutosavePath(projectPath));

        Assert.Equal("テスト", loaded.Name);
        Assert.Single(loaded.Tracks);
        Assert.Single(loaded.Tracks[0].Clips);
        Assert.Equal("クリップ1", loaded.Tracks[0].Clips[0].Name);
    }

    [Fact]
    public void Save_DoesNotTouchProjectFile()
    {
        using var temp = new TempDirectory();
        var projectPath = temp.File("MyProject.veproj");
        File.WriteAllText(projectPath, "元の内容");

        AutoSave.Save(projectPath, SampleProject());

        Assert.Equal("元の内容", File.ReadAllText(projectPath));
        Assert.True(File.Exists(AutoSave.GetAutosavePath(projectPath)));
    }

    [Fact]
    public void Save_OverwritesPreviousAutosave()
    {
        using var temp = new TempDirectory();
        var projectPath = temp.File("MyProject.veproj");

        AutoSave.Save(projectPath, SampleProject());
        var project2 = SampleProject();
        project2.Name = "更新後";
        AutoSave.Save(projectPath, project2);

        var loaded = ProjectStore.Load(AutoSave.GetAutosavePath(projectPath));
        Assert.Equal("更新後", loaded.Name);
    }

    [Fact]
    public void DeleteAutosave_RemovesFile_AndIgnoresMissing()
    {
        using var temp = new TempDirectory();
        var projectPath = temp.File("MyProject.veproj");

        AutoSave.Save(projectPath, SampleProject());
        Assert.True(File.Exists(AutoSave.GetAutosavePath(projectPath)));

        AutoSave.DeleteAutosave(projectPath);
        Assert.False(File.Exists(AutoSave.GetAutosavePath(projectPath)));

        // 無いものを消しても例外は出ない。
        AutoSave.DeleteAutosave(projectPath);
    }

    [Fact]
    public void Save_LeavesNoTempFiles()
    {
        using var temp = new TempDirectory();
        var projectPath = temp.File("MyProject.veproj");

        AutoSave.Save(projectPath, SampleProject());
        ProjectStore.Save(projectPath, SampleProject());

        // フォルダには本ファイルと自動保存ファイルだけが残る（一時ファイルは残らない）。
        var files = Directory.GetFiles(temp.Dir).Select(Path.GetFileName).ToArray();
        Assert.Equal(2, files.Length);
        Assert.Contains("MyProject.autosave.veproj", files);
        Assert.Contains("MyProject.veproj", files);
    }

    [Fact]
    public void ProjectStore_Save_RoundTrip()
    {
        using var temp = new TempDirectory();
        var projectPath = temp.File("MyProject.veproj");

        ProjectStore.Save(projectPath, SampleProject());
        var loaded = ProjectStore.Load(projectPath);

        Assert.Equal("テスト", loaded.Name);
        Assert.Single(loaded.Tracks[0].Clips);
    }

    // ---- アプリ設定 ----

    [Fact]
    public void AppSettings_Defaults()
    {
        var settings = new AppSettings();

        Assert.True(settings.AutosaveEnabled);
        Assert.Equal(5, settings.AutosaveIntervalMinutes);
        Assert.Equal(5, settings.ClampedIntervalMinutes);
    }

    [Fact]
    public void AppSettings_SaveLoad_RoundTrip()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.json");
        var settings = new AppSettings { AutosaveEnabled = false, AutosaveIntervalMinutes = 10 };

        settings.Save(path);
        var loaded = AppSettings.Load(path);

        Assert.False(loaded.AutosaveEnabled);
        Assert.Equal(10, loaded.AutosaveIntervalMinutes);
    }

    [Fact]
    public void AppSettings_MissingFile_ReturnsDefaults()
    {
        using var temp = new TempDirectory();

        var loaded = AppSettings.Load(temp.File("settings.json"));

        Assert.True(loaded.AutosaveEnabled);
        Assert.Equal(5, loaded.AutosaveIntervalMinutes);
    }

    [Fact]
    public void AppSettings_CorruptFile_ReturnsDefaults()
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.json");
        File.WriteAllText(path, "{壊れた設定");

        var loaded = AppSettings.Load(path);

        Assert.True(loaded.AutosaveEnabled);
        Assert.Equal(5, loaded.AutosaveIntervalMinutes);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(61, 60)]
    [InlineData(999, 60)]
    public void AppSettings_Interval_IsClampedTo1To60(int saved, int expected)
    {
        using var temp = new TempDirectory();
        var path = temp.File("settings.json");
        var settings = new AppSettings { AutosaveIntervalMinutes = saved };

        settings.Save(path);
        var loaded = AppSettings.Load(path);

        Assert.Equal(expected, loaded.AutosaveIntervalMinutes);
    }
}
