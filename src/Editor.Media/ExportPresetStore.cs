// 書き出しプリセットの保存・読み込み（AppSettings と同じ流儀）。
// %APPDATA%\VideoEditor\export-presets.json に JSON で保存する。プロジェクトファイルには含めない。
// ファイルが無い・壊れている・中身が不正な場合は既定プリセットで動き、保存時に上書きして直す。
// 名前の重複：ユーザー作成プリセットと同名で保存すると上書きする（ダイアログ側で確認を取る）。
// 既定プリセットと同名での保存はできない（BuiltInNameConflict を返す）。
// 既定プリセットは削除できない（TryDeletePreset は false を返す）。

using System.IO;
using System.Text.Json;
using Editor.Core;

namespace Editor.Media;

/// <summary>プリセット保存の結果。</summary>
public enum SavePresetResult
{
    /// <summary>新規保存した。</summary>
    Saved,
    /// <summary>同名のユーザー作成プリセットを上書きした。</summary>
    Overwritten,
    /// <summary>既定プリセットと同じ名前のため保存できない。</summary>
    BuiltInNameConflict,
    /// <summary>名前が空、または設定値が不正のため保存できない。</summary>
    Invalid
}

/// <summary>書き出しプリセットの一覧と、最後に選択したプリセット名の保存・読み込み。</summary>
public sealed class ExportPresetStore
{
    /// <summary>プリセットファイルの既定の場所（%APPDATA%\VideoEditor\export-presets.json）。</summary>
    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VideoEditor", "export-presets.json");

    /// <summary>既定プリセット。初回起動時やファイルが壊れている場合に使う。</summary>
    public static IReadOnlyList<ExportPreset> DefaultPresets { get; } =
    [
        new ExportPreset("1080p 30fps", ExportSettings.Hd30, IsBuiltIn: true),
        new ExportPreset("1080p 60fps", ExportSettings.Hd60, IsBuiltIn: true),
        new ExportPreset("4K 30fps", ExportSettings.Uhd30, IsBuiltIn: true),
        // SNS 向け：軽い 720p、CRF を少し上げてファイルサイズを抑え、音声は 128 kbps。
        new ExportPreset("SNS向け 720p", new ExportSettings(1280, 720, 30, Crf: 22, AudioBitrateKbps: 128), IsBuiltIn: true)
    ];

    private string? _path;

    /// <summary>プリセット一覧（既定＋ユーザー作成）。</summary>
    public List<ExportPreset> Presets { get; set; } = DefaultPresets.ToList();

    /// <summary>最後に選択したプリセットの名前（次回ダイアログを開いたときに復元する）。</summary>
    public string? LastSelectedPresetName { get; set; }

    /// <summary>名前でプリセットを探す（大文字・小文字を区別しない）。見つからなければ null。</summary>
    public ExportPreset? FindPreset(string name)
        => Presets.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// プリセットを保存する。同名のユーザー作成プリセットがあれば上書きし Overwritten を返す。
    /// 既定プリセットと同名の場合は BuiltInNameConflict、名前や設定値が不正な場合は Invalid を返す。
    /// 保存はメモリ上のみで、ファイルへ書き出すには Save() を呼ぶこと。
    /// </summary>
    public SavePresetResult SavePreset(ExportPreset preset)
    {
        try
        {
            preset.Validate();
        }
        catch
        {
            return SavePresetResult.Invalid;
        }

        var existing = FindPreset(preset.Name);
        if (existing is not null)
        {
            if (existing.IsBuiltIn)
                return SavePresetResult.BuiltInNameConflict;
            // 上書きしても IsBuiltIn は付かない（ユーザー作成のまま）。
            Presets[Presets.IndexOf(existing)] = preset with { IsBuiltIn = false };
            return SavePresetResult.Overwritten;
        }

        Presets.Add(preset with { IsBuiltIn = false });
        return SavePresetResult.Saved;
    }

    /// <summary>
    /// プリセットを削除する。ユーザー作成のものだけ削除でき、削除できたら true を返す。
    /// 既定プリセットや存在しない名前は削除できず false を返す。
    /// 削除はメモリ上のみで、ファイルへ書き出すには Save() を呼ぶこと。
    /// </summary>
    public bool TryDeletePreset(string name)
    {
        var existing = FindPreset(name);
        if (existing is null || existing.IsBuiltIn)
            return false;
        if (string.Equals(LastSelectedPresetName, existing.Name, StringComparison.OrdinalIgnoreCase))
            LastSelectedPresetName = null;
        return Presets.Remove(existing);
    }

    /// <summary>
    /// プリセットを読み込む。ファイルが無い・壊れている・読めない場合は既定プリセットを返す。
    /// 不正なエントリは読み飛ばし、欠けている既定プリセットは補う（既定が常に使えるようにする）。
    /// </summary>
    public static ExportPresetStore Load(string? path = null)
    {
        path ??= DefaultFilePath;
        var store = new ExportPresetStore { _path = path };
        try
        {
            if (!File.Exists(path))
                return store;
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<ExportPresetStore>(json, JsonDefaults.Indented);
            if (loaded is null || loaded.Presets.Count == 0)
                return store;
            // 不正なエントリ（名前が空・設定値が範囲外など）は読み飛ばす。
            var valid = new List<ExportPreset>();
            foreach (var preset in loaded.Presets)
            {
                try
                {
                    preset.Validate();
                    valid.Add(preset);
                }
                catch
                {
                    // 壊れたエントリは無視し、残りで動く。
                }
            }
            if (valid.Count == 0)
                return store;
            store.Presets = valid;
            store.LastSelectedPresetName = loaded.LastSelectedPresetName;
        }
        catch
        {
            // 壊れたプリセットファイルで起動できなくなるより、既定値で動く方を選ぶ。
            return new ExportPresetStore { _path = path };
        }

        // 既定プリセットがファイルから欠けていたら補う（手で消された場合も復旧する）。
        // ユーザー作成プリセットは IsBuiltIn が立っていても既定としては扱わない。
        foreach (var defaults in DefaultPresets)
        {
            var existing = store.Presets.FirstOrDefault(p =>
                string.Equals(p.Name, defaults.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                store.Presets.Add(defaults);
            }
            else if (!existing.IsBuiltIn && IsDefaultName(existing.Name))
            {
                // 既定と同名のユーザー作成プリセットがファイルに残っていたら、既定で置き換える。
                store.Presets[store.Presets.IndexOf(existing)] = defaults;
            }
        }
        return store;
    }

    private static bool IsDefaultName(string name)
        => DefaultPresets.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>プリセットを保存する（失敗したら例外を投げる。呼び出し側で握りつぶしてよい）。</summary>
    public void Save(string? path = null)
    {
        path ??= _path ?? DefaultFilePath;
        _path = path;
        var json = JsonSerializer.Serialize(this, JsonDefaults.Indented);
        AtomicFile.WriteAllText(path, json);
    }
}
