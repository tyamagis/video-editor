// 書き出しプリセットの単体テスト（ExportPreset / ExportPresetStore / ExportSettings の検証）。
// ファイルの読み書きだけを対象とし、ダイアログ（WPF）や ffmpeg の実行は扱わない。

using Editor.Core;
using Editor.Media;
using Xunit;

namespace Editor.Tests;

/// <summary>書き出しプリセットの保存・読込・削除・フォールバックのテスト。</summary>
public sealed class ExportPresetTests
{
    // ---- 既定プリセット ----

    [Fact]
    public void DefaultPresets_ContainsFourPresets()
    {
        Assert.Equal(4, ExportPresetStore.DefaultPresets.Count);
        Assert.All(ExportPresetStore.DefaultPresets, p => Assert.True(p.IsBuiltIn));
    }

    [Fact]
    public void DefaultPresets_HasExpectedValues()
    {
        var presets = ExportPresetStore.DefaultPresets.ToDictionary(p => p.Name);
        Assert.Equal(new ExportSettings(1920, 1080, 30), presets["1080p 30fps"].Settings);
        Assert.Equal(new ExportSettings(1920, 1080, 60), presets["1080p 60fps"].Settings);
        Assert.Equal(new ExportSettings(3840, 2160, 30), presets["4K 30fps"].Settings);
        // SNS 向けは軽い 720p、CRF 22、音声 128 kbps。
        Assert.Equal(
            new ExportSettings(1280, 720, 30, Crf: 22, AudioBitrateKbps: 128),
            presets["SNS向け 720p"].Settings);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        using var temp = new TempDirectory();
        var store = ExportPresetStore.Load(temp.File("export-presets.json"));
        Assert.Equal(ExportPresetStore.DefaultPresets.Count, store.Presets.Count);
        Assert.Equal("1080p 30fps", store.Presets[0].Name);
        Assert.Null(store.LastSelectedPresetName);
    }

    // ---- 保存・読込 ----

    [Fact]
    public void SaveAndLoad_RoundTripsUserPresetAndLastSelected()
    {
        using var temp = new TempDirectory();
        var path = temp.File("export-presets.json");
        var store = ExportPresetStore.Load(path);
        Assert.Equal(SavePresetResult.Saved,
            store.SavePreset(new ExportPreset("自作 720p60", new ExportSettings(1280, 720, 60))));
        store.LastSelectedPresetName = "自作 720p60";
        store.Save(path);

        var loaded = ExportPresetStore.Load(path);
        Assert.Equal(5, loaded.Presets.Count);
        var custom = loaded.FindPreset("自作 720p60");
        Assert.NotNull(custom);
        Assert.False(custom.IsBuiltIn);
        Assert.Equal(new ExportSettings(1280, 720, 60), custom.Settings);
        Assert.Equal("自作 720p60", loaded.LastSelectedPresetName);
    }

    [Fact]
    public void SavePreset_DuplicateUserPreset_OverwritesInPlace()
    {
        using var temp = new TempDirectory();
        var store = ExportPresetStore.Load(temp.File("export-presets.json"));
        Assert.Equal(SavePresetResult.Saved,
            store.SavePreset(new ExportPreset("自作", new ExportSettings(1280, 720, 30))));
        Assert.Equal(SavePresetResult.Overwritten,
            store.SavePreset(new ExportPreset("自作", new ExportSettings(1920, 1080, 60))));
        // 件数は増えず、値だけ置き換わる。
        Assert.Single(store.Presets, p => p.Name == "自作");
        Assert.Equal(new ExportSettings(1920, 1080, 60), store.FindPreset("自作")!.Settings);
    }

    [Fact]
    public void SavePreset_DuplicateName_IsCaseInsensitive()
    {
        using var temp = new TempDirectory();
        var store = ExportPresetStore.Load(temp.File("export-presets.json"));
        store.SavePreset(new ExportPreset("MyPreset", new ExportSettings(1280, 720, 30)));
        Assert.Equal(SavePresetResult.Overwritten,
            store.SavePreset(new ExportPreset("mypreset", new ExportSettings(1920, 1080, 30))));
        Assert.Single(store.Presets, p => string.Equals(p.Name, "MyPreset", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SavePreset_BuiltInName_ReturnsConflictAndKeepsBuiltIn()
    {
        using var temp = new TempDirectory();
        var store = ExportPresetStore.Load(temp.File("export-presets.json"));
        Assert.Equal(SavePresetResult.BuiltInNameConflict,
            store.SavePreset(new ExportPreset("1080p 30fps", new ExportSettings(640, 360, 30))));
        Assert.Equal(new ExportSettings(1920, 1080, 30),
            store.FindPreset("1080p 30fps")!.Settings);
    }

    [Fact]
    public void SavePreset_EmptyNameOrInvalidSettings_ReturnsInvalid()
    {
        using var temp = new TempDirectory();
        var store = ExportPresetStore.Load(temp.File("export-presets.json"));
        Assert.Equal(SavePresetResult.Invalid,
            store.SavePreset(new ExportPreset("   ", new ExportSettings(1920, 1080, 30))));
        Assert.Equal(SavePresetResult.Invalid,
            store.SavePreset(new ExportPreset("自作", new ExportSettings(0, 1080, 30))));
        Assert.Equal(4, store.Presets.Count);
    }

    // ---- 削除 ----

    [Fact]
    public void TryDeletePreset_UserPreset_RemovesIt()
    {
        using var temp = new TempDirectory();
        var path = temp.File("export-presets.json");
        var store = ExportPresetStore.Load(path);
        store.SavePreset(new ExportPreset("自作", new ExportSettings(1280, 720, 30)));
        store.LastSelectedPresetName = "自作";
        Assert.True(store.TryDeletePreset("自作"));
        Assert.Null(store.FindPreset("自作"));
        // 削除したプリセットが「最後に選択したもの」なら、それも消える。
        Assert.Null(store.LastSelectedPresetName);
        store.Save(path);
        Assert.Null(ExportPresetStore.Load(path).FindPreset("自作"));
    }

    [Fact]
    public void TryDeletePreset_BuiltInOrMissing_ReturnsFalse()
    {
        using var temp = new TempDirectory();
        var store = ExportPresetStore.Load(temp.File("export-presets.json"));
        Assert.False(store.TryDeletePreset("1080p 30fps"));
        Assert.False(store.TryDeletePreset("存在しないプリセット"));
        Assert.Equal(4, store.Presets.Count);
    }

    // ---- 壊れたファイルのフォールバック ----

    [Fact]
    public void Load_CorruptJson_ReturnsDefaults()
    {
        using var temp = new TempDirectory();
        var path = temp.File("export-presets.json");
        File.WriteAllText(path, "{ これは壊れた JSON");
        var store = ExportPresetStore.Load(path);
        Assert.Equal(4, store.Presets.Count);
        Assert.Equal("1080p 30fps", store.Presets[0].Name);
    }

    [Fact]
    public void Load_EmptyJsonObject_ReturnsDefaults()
    {
        using var temp = new TempDirectory();
        var path = temp.File("export-presets.json");
        File.WriteAllText(path, "{}");
        var store = ExportPresetStore.Load(path);
        Assert.Equal(4, store.Presets.Count);
    }

    [Fact]
    public void Load_InvalidEntries_AreSkippedAndMissingDefaultsAreRestored()
    {
        using var temp = new TempDirectory();
        var path = temp.File("export-presets.json");
        // 有効なユーザー作成プリセット1件、不正なエントリ3件、既定プリセットは1件だけ。
        File.WriteAllText(path, """
            {
              "Presets": [
                { "Name": "自作", "Settings": { "Width": 1280, "Height": 720, "FrameRate": 30, "Crf": 20, "Preset": "medium", "AudioBitrateKbps": 192 }, "IsBuiltIn": false },
                { "Name": "", "Settings": { "Width": 1920, "Height": 1080, "FrameRate": 30, "Crf": 20, "Preset": "medium", "AudioBitrateKbps": 192 }, "IsBuiltIn": false },
                { "Name": "幅ゼロ", "Settings": { "Width": 0, "Height": 1080, "FrameRate": 30, "Crf": 20, "Preset": "medium", "AudioBitrateKbps": 192 }, "IsBuiltIn": false },
                { "Name": "未知プリセット", "Settings": { "Width": 1920, "Height": 1080, "FrameRate": 30, "Crf": 20, "Preset": "unknown", "AudioBitrateKbps": 192 }, "IsBuiltIn": false },
                { "Name": "1080p 30fps", "Settings": { "Width": 1920, "Height": 1080, "FrameRate": 30, "Crf": 20, "Preset": "medium", "AudioBitrateKbps": 192 }, "IsBuiltIn": true }
              ],
              "LastSelectedPresetName": "自作"
            }
            """);
        var store = ExportPresetStore.Load(path);
        // 有効な「自作」＋既定4種（欠けていた3種は補われる）。
        Assert.Equal(5, store.Presets.Count);
        Assert.NotNull(store.FindPreset("自作"));
        Assert.Null(store.FindPreset("幅ゼロ"));
        Assert.Null(store.FindPreset("未知プリセット"));
        Assert.Equal("自作", store.LastSelectedPresetName);
    }

    [Fact]
    public void Save_ThenCorrupt_StillLoadsDefaults()
    {
        using var temp = new TempDirectory();
        var path = temp.File("export-presets.json");
        var store = ExportPresetStore.Load(path);
        store.SavePreset(new ExportPreset("自作", new ExportSettings(1280, 720, 30)));
        store.Save(path);
        File.WriteAllText(path, "not json at all");
        var loaded = ExportPresetStore.Load(path);
        Assert.Equal(4, loaded.Presets.Count);
        Assert.Null(loaded.FindPreset("自作"));
    }

    // ---- ExportSettings の検証（プリセット経由で不正値が入らないことの土台） ----

    [Theory]
    [InlineData(15)]
    [InlineData(513)]
    public void ExportSettings_Validate_RejectsOutOfRangeAudioBitrate(int bitrate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ExportSettings(1920, 1080, 30, AudioBitrateKbps: bitrate).Validate());
    }

    [Fact]
    public void ExportSettings_Validate_RejectsUnknownEncoderPreset()
    {
        Assert.Throws<ArgumentException>(
            () => new ExportSettings(1920, 1080, 30, Preset: "ultrafast-plus").Validate());
    }

    [Fact]
    public void ExportSettings_Validate_AcceptsAllEncoderPresets()
    {
        foreach (var preset in ExportSettings.EncoderPresets)
            new ExportSettings(1920, 1080, 30, Preset: preset).Validate();
    }

    [Fact]
    public void ExportPreset_Validate_RejectsEmptyName()
    {
        Assert.Throws<ArgumentException>(
            () => new ExportPreset("", ExportSettings.Hd30).Validate());
    }
}
