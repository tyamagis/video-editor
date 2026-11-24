// プロジェクトファイルの保存・読み込み（設計プラン 5章・8章）。
// JSON形式。素材は含めずパス参照のみ。Version番号を持ち、
// 古い版は読み込み時に移行する（現時点では v1 のみ）。

using System.IO;
using System.Text.Json;

namespace Editor.Core;

/// <summary>プロジェクトのJSON保存・読み込み。</summary>
public static class ProjectStore
{
    /// <summary>
    /// プロジェクトを保存する。一時ファイル経由の原子的な置換で書くため、
    /// 保存途中でクラッシュしても本ファイルが半端な内容になることはない。
    /// 呼び出し元のプロジェクトは変えない（Version の書き込みはシリアライズ用のコピー側で行う）。
    /// </summary>
    public static void Save(string path, EditorProject project)
    {
        var json = JsonSerializer.Serialize(
            project.WithVersion(EditorProject.CurrentVersion), JsonDefaults.Indented);
        AtomicFile.WriteAllText(path, json);
    }

    /// <summary>
    /// プロジェクトを JSON 文字列にする（アンドゥ用スナップショット）。
    /// 読み取り専用の操作であり、呼び出し元のプロジェクトは変えない
    /// （Version の書き込みはシリアライズ用のコピー側で行う）。
    /// </summary>
    public static string Serialize(EditorProject project)
        => JsonSerializer.Serialize(
            project.WithVersion(EditorProject.CurrentVersion), JsonDefaults.Indented);

    /// <summary>JSON 文字列からプロジェクトを復元する（アンドゥ用）。同じ版で作られたものだけを扱う。</summary>
    public static EditorProject Deserialize(string json)
        => JsonSerializer.Deserialize<EditorProject>(json, JsonDefaults.Indented)
            ?? throw new InvalidDataException("プロジェクトを復元できませんでした。");

    public static EditorProject Load(string path)
        => LoadFromJson(File.ReadAllText(path));

    /// <summary>
    /// JSON 文字列からプロジェクトを復元する（テンプレート内のプロジェクト読み込み用）。
    /// Load と同じ版チェック・移行を行う。壊れた JSON は InvalidDataException になる。
    /// </summary>
    public static EditorProject LoadFromJson(string json)
    {
        int version;
        using (var doc = JsonDocument.Parse(json))
        {
            if (!doc.RootElement.TryGetProperty("Version", out var versionElement)
                || !versionElement.TryGetInt32(out version))
            {
                version = 0; // Versionを持たない古い形式は v0 扱い
            }
        }

        if (version > EditorProject.CurrentVersion)
            throw new InvalidDataException(
                $"プロジェクトファイルの版 ({version}) がこのソフトの対応版 ({EditorProject.CurrentVersion}) より新しいです。");

        var project = JsonSerializer.Deserialize<EditorProject>(json, JsonDefaults.Indented)
            ?? throw new InvalidDataException("プロジェクトファイルを読み込めませんでした。");

        if (version < EditorProject.CurrentVersion)
            Migrate(project, version);

        project.Version = EditorProject.CurrentVersion;
        return project;
    }

    /// <summary>
    /// 古い版から現行版への移行。v0 → v1 では既定値の補完だけを行う。
    /// 将来の版追加時はここに case を足す。
    /// </summary>
    private static void Migrate(EditorProject project, int fromVersion)
    {
        switch (fromVersion)
        {
            case 0:
                if (project.Bpm <= 0)
                    project.Bpm = 120.0;
                if (project.FrameRate <= 0)
                    project.FrameRate = 30;
                if (project.Width <= 0)
                    project.Width = 1920;
                if (project.Height <= 0)
                    project.Height = 1080;
                break;
            default:
                throw new InvalidDataException($"未対応のプロジェクト版です: {fromVersion}");
        }
    }
}
