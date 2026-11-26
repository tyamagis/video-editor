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
    /// 呼び出し元のプロジェクトは変えない（Version の書き込みと LUT パスの相対化は
    /// シリアライズ用のコピー側で行う）。
    /// LUT ファイル（.cube）がプロジェクトファイルと同じフォルダ以下にある場合は、
    /// LutPath をプロジェクトファイルからの相対パスで保存する（プロジェクトフォルダごと
    /// 移動しても LUT を辿れるようにするため）。フォルダの外にある場合は絶対パスのまま。
    /// メモリ上のプロジェクト（と Serialize / Deserialize）は常に絶対パスで扱う。
    /// </summary>
    public static void Save(string path, EditorProject project)
    {
        // JSON 往復で深いコピーを作り、コピー側だけ LUT パスを相対化する
        // （WithVersion の浅いコピーではクリップ・エフェクトを共有してしまうため）。
        var copy = Deserialize(Serialize(project));
        RelativizeLutPaths(copy, ProjectDirectoryOf(path));
        var json = JsonSerializer.Serialize(
            copy.WithVersion(EditorProject.CurrentVersion), JsonDefaults.Indented);
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

    /// <summary>
    /// プロジェクトファイルを読む。LutPath が相対パスで保存されていたら、
    /// プロジェクトファイルのあるフォルダを基準に絶対パスへ解決する
    /// （メモリ上では常に絶対パスで扱う）。絶対パスで保存された旧プロジェクトは
    /// そのまま読める。解決した先にファイルが無くても読み込みは成功し、
    /// LUT は従来通り無効化される（描画・インスペクタ側の既存の扱い）。
    /// </summary>
    public static EditorProject Load(string path)
    {
        var project = LoadFromJson(File.ReadAllText(path));
        ResolveLutPaths(project, ProjectDirectoryOf(path));
        return project;
    }

    /// <summary>
    /// JSON 文字列からプロジェクトを復元する（テンプレート内のプロジェクト読み込み用）。
    /// Load と同じ版チェック・移行を行う。壊れた JSON は InvalidDataException になる。
    /// </summary>
    public static EditorProject LoadFromJson(string json)
    {
        int version;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("Version", out var versionElement)
                || !versionElement.TryGetInt32(out version))
            {
                version = 0; // Versionを持たない古い形式は v0 扱い
            }
        }
        catch (JsonException ex)
        {
            // 壊れた JSON は InvalidDataException に統一する（このメソッドの契約）。
            throw new InvalidDataException("プロジェクトファイルが壊れています。", ex);
        }

        if (version > EditorProject.CurrentVersion)
            throw new InvalidDataException(
                $"プロジェクトファイルの版 ({version}) がこのソフトの対応版 ({EditorProject.CurrentVersion}) より新しいです。");

        EditorProject project;
        try
        {
            project = JsonSerializer.Deserialize<EditorProject>(json, JsonDefaults.Indented)
                ?? throw new InvalidDataException("プロジェクトファイルを読み込めませんでした。");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("プロジェクトファイルが壊れています。", ex);
        }

        if (version < EditorProject.CurrentVersion)
            Migrate(project, version);

        project.Version = EditorProject.CurrentVersion;
        return project;
    }

    /// <summary>プロジェクトファイルのあるフォルダの絶対パス（保存・読み込みの相対基準）。</summary>
    private static string ProjectDirectoryOf(string projectPath)
        => Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? string.Empty;

    /// <summary>LUTエフェクトをすべて列挙する（トラック→クリップ→エフェクトの順）。</summary>
    private static IEnumerable<ClipEffect> LutEffects(EditorProject project)
        => project.Tracks
            .SelectMany(track => track.Clips)
            .SelectMany(clip => clip.Effects)
            .Where(effect => effect.Kind == EffectKind.Lut);

    /// <summary>保存用：プロジェクトフォルダ以下にある LUT のパスを相対パスに書き換える。</summary>
    private static void RelativizeLutPaths(EditorProject project, string projectDirectory)
    {
        foreach (var effect in LutEffects(project))
            effect.LutPath = ToProjectRelativePath(projectDirectory, effect.LutPath);
    }

    /// <summary>読み込み用：相対パスの LUT をプロジェクトフォルダ基準の絶対パスに解決する。</summary>
    private static void ResolveLutPaths(EditorProject project, string projectDirectory)
    {
        foreach (var effect in LutEffects(project))
            effect.LutPath = ToProjectAbsolutePath(projectDirectory, effect.LutPath);
    }

    /// <summary>
    /// LUT の絶対パスを、プロジェクトフォルダからの相対パスにする。
    /// 対象外（未指定・既に相対・フォルダの外・別ドライブ等で共通の基準が無い）は
    /// そのまま返す。相対化に失敗しても保存自体は止めない（絶対パスのまま保存する）。
    /// </summary>
    private static string ToProjectRelativePath(string projectDirectory, string lutPath)
    {
        if (string.IsNullOrWhiteSpace(lutPath) || !Path.IsPathFullyQualified(lutPath))
            return lutPath;
        string relative;
        try
        {
            relative = Path.GetRelativePath(projectDirectory, lutPath);
        }
        catch (Exception)
        {
            return lutPath;
        }
        // フォルダの外（".." で始まる）・共通の基準が無く絶対のまま返ってきた場合は相対化しない。
        if (Path.IsPathFullyQualified(relative))
            return lutPath;
        var firstSegment = relative
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar)[0];
        return firstSegment == ".." ? lutPath : relative;
    }

    /// <summary>
    /// LUT の相対パスを、プロジェクトフォルダを基準にした絶対パスに解決する。
    /// 未指定・既に絶対の場合はそのまま返す。ファイルの有無は見ない
    /// （無い場合の無効化は描画・インスペクタ側の既存の扱いに任せる）。
    /// </summary>
    private static string ToProjectAbsolutePath(string projectDirectory, string lutPath)
    {
        if (string.IsNullOrWhiteSpace(lutPath) || Path.IsPathFullyQualified(lutPath))
            return lutPath;
        return Path.GetFullPath(Path.Combine(projectDirectory, lutPath));
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
