// プロジェクトテンプレートの保存・読み込み・一覧・削除（ProjectStore / AppSettings と同じ流儀）。
// テンプレートは「プロジェクト構成のひな形」：トラック構成・クリップ配置・テキスト・
// エフェクト・BPM・解像度・マーカーなどのプロジェクト設定を丸ごと含む。
// メディアファイルそのものは含めない。メディアへの参照（ファイルパス）はそのまま残す。
// テンプレートから作るときに参照先のファイルが無ければ、そのクリップは配置だけ残し
// （位置・長さ・エフェクトは保持する）、見つからなかったファイルの一覧を結果に含めて
// 呼び出し側で警告を出す。バインドや可視化の音源参照（クリップId）は、クリップを
// 残すことで壊れない。
// 保存場所は %APPDATA%\VideoEditor\Templates\、拡張子は .vetemplate。
// テンプレートファイルが壊れている場合は一覧では「読み込めません」と表示し、
// 読み込み時は InvalidDataException を投げる（通常プロジェクトには影響しない）。

using System.IO;
using System.Text.Json;

namespace Editor.Core;

/// <summary>テンプレート保存の結果。</summary>
public enum SaveTemplateResult
{
    /// <summary>新規保存した。</summary>
    Saved,
    /// <summary>同名のテンプレートを上書きした。</summary>
    Overwritten,
    /// <summary>名前が不正のため保存できない。</summary>
    InvalidName
}

/// <summary>テンプレート一覧の1件。壊れたファイルも IsValid=false で含める（削除できるようにする）。</summary>
public sealed record TemplateInfo(
    string Name,
    string Path,
    DateTime CreatedAtUtc,
    bool IsValid,
    string? ErrorMessage)
{
    /// <summary>一覧表示用の文字列（名前＋作成日時。壊れているものはその旨を付ける）。</summary>
    public string DisplayText => IsValid
        ? $"{Name}（{CreatedAtUtc.ToLocalTime():yyyy/MM/dd HH:mm}）"
        : $"{Name}（読み込めません）";
}

/// <summary>テンプレートからプロジェクトを作った結果。見つからなかったメディアがあれば一覧に含める。</summary>
public sealed record TemplateCreateResult(
    EditorProject Project,
    IReadOnlyList<string> MissingMediaPaths,
    IReadOnlyList<string> MissingClipNames);

/// <summary>テンプレートファイル（.vetemplate）の内容。内側のプロジェクトは ProjectStore と同じ JSON。</summary>
public sealed class ProjectTemplateFile
{
    /// <summary>テンプレートファイル形式の版。</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public EditorProject Project { get; set; } = new();
}

/// <summary>プロジェクトテンプレートの保存・読み込み・一覧・削除。</summary>
public static class TemplateStore
{
    /// <summary>テンプレートファイルの拡張子。</summary>
    public const string Extension = ".vetemplate";

    /// <summary>テンプレートの既定の保存場所（%APPDATA%\VideoEditor\Templates）。</summary>
    public static string DefaultTemplatesDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VideoEditor", "Templates");

    /// <summary>
    /// テンプレート名を検証する。不正な理由を返し、正しければ null を返す。
    /// 空・100文字超・ファイル名に使えない文字（/ など）・"." / ".." は不正。
    /// </summary>
    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "テンプレート名を入力してください。";
        var trimmed = name.Trim();
        if (trimmed.Length > 100)
            return "テンプレート名は100文字以内にしてください。";
        if (trimmed is "." or "..")
            return "テンプレート名に「.」や「..」は使えません。";
        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "テンプレート名にファイル名として使えない文字が含まれています。";
        return null;
    }

    /// <summary>同名（大文字・小文字を区別しない）のテンプレートがあるか。</summary>
    public static bool TemplateExists(string name, string? directory = null)
        => FindByName(directory ?? DefaultTemplatesDirectory, name) is not null;

    /// <summary>
    /// 現在のプロジェクトをテンプレートとして保存する。同名のテンプレートがあれば
    /// そのファイルを上書きし Overwritten を返す。名前が不正なら InvalidName を返す
    /// （ファイルは作らない）。保存に失敗した場合は例外を投げる。
    /// 呼び出し元のプロジェクトは変えない（Version の書き込みはシリアライズ用のコピー側で行う）。
    /// </summary>
    public static SaveTemplateResult Save(string name, EditorProject project, string? directory = null)
    {
        if (ValidateName(name) is not null)
            return SaveTemplateResult.InvalidName;
        var trimmed = name.Trim();
        directory ??= DefaultTemplatesDirectory;

        var existing = FindByName(directory, trimmed);
        var path = existing ?? Path.Combine(directory, SanitizeFileName(trimmed) + Extension);

        var file = new ProjectTemplateFile
        {
            Version = ProjectTemplateFile.CurrentVersion,
            Name = trimmed,
            CreatedAtUtc = DateTime.UtcNow,
            Project = project.WithVersion(EditorProject.CurrentVersion)
        };
        var json = JsonSerializer.Serialize(file, JsonDefaults.Indented);
        AtomicFile.WriteAllText(path, json);
        return existing is null ? SaveTemplateResult.Saved : SaveTemplateResult.Overwritten;
    }

    /// <summary>
    /// テンプレートの一覧を取得する（名前順）。フォルダが無ければ空。
    /// 壊れたファイルも IsValid=false で含める（読み飛ばすと削除できなくなるため）。
    /// 一覧の取得自体は壊れたファイルがあっても例外を投げない。
    /// </summary>
    public static IReadOnlyList<TemplateInfo> List(string? directory = null)
    {
        directory ??= DefaultTemplatesDirectory;
        var result = new List<TemplateInfo>();
        if (!Directory.Exists(directory))
            return result;

        foreach (var path in Directory.EnumerateFiles(directory, "*" + Extension))
        {
            // 名前・作成日時はヘッダから読む（壊れたファイルでも、読める部分は表示に使う）。
            string displayName;
            DateTime createdAtUtc;
            try
            {
                (displayName, createdAtUtc) = ReadHeader(path);
            }
            catch
            {
                displayName = Path.GetFileNameWithoutExtension(path);
                try
                {
                    createdAtUtc = File.GetLastWriteTimeUtc(path);
                }
                catch
                {
                    createdAtUtc = DateTime.UtcNow;
                }
            }
            try
            {
                // 中身のプロジェクトまで読んでみて、壊れていたら無効扱いにする
                // （一覧では有効に見えて、作るときに初めて失敗するのを防ぐ）。
                _ = Load(path);
                result.Add(new TemplateInfo(displayName, path, createdAtUtc, IsValid: true, ErrorMessage: null));
            }
            catch (Exception ex)
            {
                result.Add(new TemplateInfo(displayName, path, createdAtUtc, IsValid: false, ErrorMessage: ex.Message));
            }
        }
        return result.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// テンプレートファイルを読み込み、プロジェクトを復元する。
    /// ファイルが無い・壊れている・版が新しい場合は InvalidDataException を投げる。
    /// </summary>
    public static EditorProject Load(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"テンプレートファイルを読み込めませんでした：{ex.Message}", ex);
        }
        return LoadFromJson(json);
    }

    /// <summary>
    /// テンプレートの JSON 文字列からプロジェクトを復元する（Load と同じ版チェック・移行）。
    /// 壊れた内容は InvalidDataException になる。
    /// </summary>
    public static EditorProject LoadFromJson(string json)
    {
        string projectJson;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("Version", out var versionElement)
                && versionElement.TryGetInt32(out var version)
                && version > ProjectTemplateFile.CurrentVersion)
            {
                throw new InvalidDataException(
                    $"テンプレートファイルの版 ({version}) がこのソフトの対応版 ({ProjectTemplateFile.CurrentVersion}) より新しいです。");
            }
            if (!root.TryGetProperty("Project", out var projectElement))
                throw new InvalidDataException("テンプレートファイルが壊れています（プロジェクト情報がありません）。");
            projectJson = projectElement.GetRawText();
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"テンプレートファイルが壊れています：{ex.Message}", ex);
        }

        try
        {
            return ProjectStore.LoadFromJson(projectJson);
        }
        catch (Exception ex)
        {
            // 内側の JSON が壊れている場合の JsonException も含め、
            // テンプレートの読み込み失敗は InvalidDataException に統一する
            // （呼び出し側が「壊れたテンプレート」として扱えるようにする）。
            throw new InvalidDataException($"テンプレート内のプロジェクトを読み込めませんでした：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// テンプレートからプロジェクトを作る。メディア参照が切れているクリップは
    /// 配置だけ残し（除かない）、見つからなかったファイルとクリップ名を結果に含める。
    /// テキスト・可視化クリップは素材ファイルを持たないため対象外。
    /// </summary>
    public static TemplateCreateResult CreateProject(string path)
    {
        var project = Load(path);
        var missingPaths = new List<string>();
        var missingClipNames = new List<string>();
        foreach (var clip in project.Tracks.SelectMany(t => t.Clips))
        {
            if (clip.Kind is ClipKind.Text or ClipKind.Visualization)
                continue;
            if (string.IsNullOrEmpty(clip.SourcePath))
                continue;
            if (File.Exists(clip.SourcePath))
                continue;
            missingPaths.Add(clip.SourcePath);
            missingClipNames.Add(clip.Name);
        }
        // 同じファイルを指すクリップが複数あっても、パスの一覧は重複させない。
        return new TemplateCreateResult(
            project,
            missingPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            missingClipNames);
    }

    /// <summary>テンプレートファイルを削除する。削除できたら true を返す（存在しない・削除失敗は false）。</summary>
    public static bool Delete(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>テンプレートファイルのヘッダ（名前・作成日時）だけを読む。一覧表示用。</summary>
    private static (string Name, DateTime CreatedAtUtc) ReadHeader(string path)
    {
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var name = root.TryGetProperty("Name", out var nameElement)
            ? nameElement.GetString() ?? string.Empty
            : string.Empty;
        if (string.IsNullOrWhiteSpace(name))
            name = Path.GetFileNameWithoutExtension(path);
        var createdAtUtc = root.TryGetProperty("CreatedAtUtc", out var createdElement)
            && createdElement.TryGetDateTime(out var created)
            ? created
            : File.GetLastWriteTimeUtc(path);
        return (name, createdAtUtc);
    }

    /// <summary>
    /// 同名のテンプレートのファイルパスを探す（大文字・小文字を区別しない）。
    /// 壊れたファイルはファイル名（拡張子なし）でも照合する。
    /// 見つからなければ null。
    /// </summary>
    private static string? FindByName(string directory, string name)
    {
        if (!Directory.Exists(directory))
            return null;
        var trimmed = name.Trim();
        var sanitized = SanitizeFileName(trimmed);
        foreach (var path in Directory.EnumerateFiles(directory, "*" + Extension))
        {
            if (string.Equals(Path.GetFileNameWithoutExtension(path), sanitized, StringComparison.OrdinalIgnoreCase))
                return path;
            try
            {
                var (templateName, _) = ReadHeader(path);
                if (string.Equals(templateName, trimmed, StringComparison.OrdinalIgnoreCase))
                    return path;
            }
            catch
            {
                // ヘッダが読めない壊れたファイルは、ファイル名での照合だけにする。
            }
        }
        return null;
    }

    /// <summary>テンプレート名をファイル名に使える形にする（使えない文字は「_」に置き換える）。</summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (invalid.Contains(chars[i]))
                chars[i] = '_';
        }
        return new string(chars);
    }
}
