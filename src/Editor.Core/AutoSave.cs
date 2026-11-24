// 自動保存（タスク4）。
// 本ファイル（.veproj）を直接上書きしない。同じフォルダに
// 「プロジェクト名.autosave.veproj」という別名で保存する。
// 書き込みは一時ファイル経由の原子的な置換（AtomicFile）で行い、
// クラッシュしても本ファイル・自動保存ファイルのどちらも壊れないようにする。

using System.IO;

namespace Editor.Core;

/// <summary>自動保存ファイルの場所・有無・読み書き。</summary>
public static class AutoSave
{
    /// <summary>自動保存ファイルのパス。「MyProject.veproj」→「MyProject.autosave.veproj」。</summary>
    public static string GetAutosavePath(string projectPath)
    {
        var directory = Path.GetDirectoryName(projectPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(projectPath);
        var extension = Path.GetExtension(projectPath);
        return Path.Combine(directory, name + ".autosave" + extension);
    }

    /// <summary>指定パスが自動保存ファイルか（拡張子を除いた名前が ".autosave" で終わる）。</summary>
    public static bool IsAutosavePath(string path)
        => Path.GetFileNameWithoutExtension(path)
            .EndsWith(".autosave", StringComparison.OrdinalIgnoreCase);

    /// <summary>自動保存ファイルのパスから本ファイルのパスに戻す。「MyProject.autosave.veproj」→「MyProject.veproj」。</summary>
    public static string GetProjectPathFromAutosave(string autosavePath)
    {
        var directory = Path.GetDirectoryName(autosavePath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(autosavePath);
        var extension = Path.GetExtension(autosavePath);
        const string marker = ".autosave";
        if (name.EndsWith(marker, StringComparison.OrdinalIgnoreCase))
            name = name[..^marker.Length];
        return Path.Combine(directory, name + extension);
    }

    /// <summary>
    /// 自動保存ファイルの有無と、本ファイルより新しいかを調べる。
    /// 戻り値が true なら autosaveTimeUtc（UTC）に自動保存ファイルの更新日時が入る。
    /// 本ファイルが無くて自動保存ファイルだけある場合も「新しい」扱いにする
    /// （本ファイルが消えた・まだ無い場合の復旧のため）。
    /// </summary>
    public static bool TryGetAutosaveInfo(string projectPath, out DateTime autosaveTimeUtc, out bool isNewer)
    {
        autosaveTimeUtc = default;
        isNewer = false;
        var autosavePath = GetAutosavePath(projectPath);
        if (!File.Exists(autosavePath))
            return false;
        autosaveTimeUtc = File.GetLastWriteTimeUtc(autosavePath);
        isNewer = !File.Exists(projectPath) || autosaveTimeUtc > File.GetLastWriteTimeUtc(projectPath);
        return true;
    }

    /// <summary>プロジェクトを自動保存ファイルへ原子的に保存する。</summary>
    public static void Save(string projectPath, EditorProject project)
    {
        var json = ProjectStore.Serialize(project);
        AtomicFile.WriteAllText(GetAutosavePath(projectPath), json);
    }

    /// <summary>
    /// 自動保存ファイルを消す（明示保存の成功時・破棄時など）。
    /// 無くても失敗でも例外を出さない（自動保存ファイルの掃除で編集を止めないため）。
    /// </summary>
    public static void DeleteAutosave(string projectPath)
    {
        try
        {
            var autosavePath = GetAutosavePath(projectPath);
            if (File.Exists(autosavePath))
                File.Delete(autosavePath);
        }
        catch
        {
            // 消せなくても害はない（次回の明示保存・読み込みでまた試す）。
        }
    }
}
