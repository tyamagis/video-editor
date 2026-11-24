// 書き出しプリセット。書き出し設定（解像度・フレームレート・画質・音声）に名前を付けたもの。
// プロジェクトとは別に %APPDATA%\VideoEditor\export-presets.json で管理する（ExportPresetStore）。
// 既定プリセットはアプリに内蔵し、初回起動時（ファイルが無い・壊れている場合を含む）に使える。

namespace Editor.Media;

/// <summary>名前付きの書き出し設定。IsBuiltIn が true のものは既定プリセットで、削除・上書きできない。</summary>
public sealed record ExportPreset(string Name, ExportSettings Settings, bool IsBuiltIn = false)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("プリセット名を入力してください。", nameof(Name));
        Settings.Validate();
    }
}
