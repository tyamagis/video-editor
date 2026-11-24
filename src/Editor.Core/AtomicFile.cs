// 原子的なファイル書き込み（タスク4：自動保存＋解析キャッシュ）。
// 一時ファイルに書いてから同じフォルダ内でリネーム（置換）するため、
// 書き込み途中でクラッシュしても、元のファイルが半端な内容になることはない
// （古い内容のまま残るか、新しい内容に置き換わるかのどちらか）。

using System.IO;
using System.Text;

namespace Editor.Core;

/// <summary>一時ファイル経由の原子的な置換でファイルを書き込む。</summary>
public static class AtomicFile
{
    /// <summary>テキストを原子的に書き込む（UTF-8・BOMなし。ProjectStore.Save と同じ符号化）。</summary>
    public static void WriteAllText(string path, string content)
        => WriteAllBytes(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content));

    /// <summary>バイト列を原子的に書き込む。同じフォルダ内の一時ファイルへ書いてから置換する。</summary>
    public static void WriteAllBytes(string path, byte[] bytes)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? ".";
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(
            directory, Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(tempPath, bytes);
            // 同じボリューム内のリネームは原子的な置換になる。
            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            // 失敗したら一時ファイルを残さない（本ファイルは元のまま）。
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // 消せなくても本処理の例外を優先する。
            }
            throw;
        }
    }
}
