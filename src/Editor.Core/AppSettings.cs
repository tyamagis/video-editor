// アプリ設定の保存・読み込み（タスク4：自動保存の設定）。
// %APPDATA%\VideoEditor\settings.json に JSON で保存する。
// プロジェクトファイルには含めない（プロジェクトを開く・作るたびに同じ設定で動くため）。
// ファイルが無い・壊れている場合は既定値で動き、設定を変えたときに上書きして直す。

using System.IO;
using System.Text.Json;

namespace Editor.Core;

/// <summary>アプリ全体の設定（今は自動保存まわりだけ）。</summary>
public sealed class AppSettings
{
    /// <summary>設定ファイルの既定の場所（%APPDATA%\VideoEditor\settings.json）。</summary>
    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VideoEditor", "settings.json");

    /// <summary>自動保存する（既定はオン）。</summary>
    public bool AutosaveEnabled { get; set; } = true;

    /// <summary>自動保存の間隔（分、1〜60。既定は5分）。</summary>
    public int AutosaveIntervalMinutes { get; set; } = 5;

    /// <summary>間隔を 1〜60 分の範囲に丸めた値。</summary>
    public int ClampedIntervalMinutes => Math.Clamp(AutosaveIntervalMinutes, 1, 60);

    /// <summary>設定を読み込む。ファイルが無い・壊れている・読めない場合は既定値を返す。</summary>
    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultFilePath;
        try
        {
            if (!File.Exists(path))
                return new AppSettings();
            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Indented);
            if (settings is null)
                return new AppSettings();
            // 古い設定ファイルに範囲外の値が入っていても、ここで丸める。
            settings.AutosaveIntervalMinutes = settings.ClampedIntervalMinutes;
            return settings;
        }
        catch
        {
            // 壊れた設定ファイルで起動できなくなるより、既定値で動く方を選ぶ。
            return new AppSettings();
        }
    }

    /// <summary>設定を保存する（失敗したら例外を投げる。呼び出し側で握りつぶしてよい）。</summary>
    public void Save(string? path = null)
    {
        path ??= DefaultFilePath;
        AutosaveIntervalMinutes = ClampedIntervalMinutes;
        var json = JsonSerializer.Serialize(this, JsonDefaults.Indented);
        AtomicFile.WriteAllText(path, json);
    }
}
