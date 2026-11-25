// キャッシュの身元照合（素材ファイルが変わっていないかの確認）。
// 波形・周波数帯解析・ビート検出・サムネイル・メディア情報など、
// 素材ファイルごとに結果をキャッシュする場所はどれも同じ照合をしていた
// （フルパス＋サイズ＋更新日時）。ここに1つにまとめる。
// 照合の意味は従来と同一：パスは GetFullPath で正規化してから
// OrdinalIgnoreCase で比べ、サイズと更新日時（UTC・ティック単位）が
// 1つでも違えば別物とみなす。パス比較の大文字小文字の扱いも従来通り
// （ディスクキャッシュのヘッダー照合と同じ）。

using System.IO;

namespace Editor.Core;

/// <summary>
/// 素材ファイル1件分の身元（フルパス＋サイズ＋更新日時）。
/// キャッシュの有効・無効を決める値。ファイルの中身そのものは見ない
/// （サイズ＋更新日時が同じ別内容は、従来通り同一とみなす）。
/// </summary>
public readonly record struct SourceIdentity
{
    /// <summary>正規化したフルパス（<see cref="Path.GetFullPath(string)"/> 済み）。</summary>
    public string FullPath { get; init; }

    /// <summary>ファイルのサイズ（バイト）。</summary>
    public long Length { get; init; }

    /// <summary>最終更新日時の UTC ティック（<see cref="FileInfo.LastWriteTimeUtc"/>）。</summary>
    public long LastWriteTimeUtcTicks { get; init; }

    public SourceIdentity(string fullPath, long length, long lastWriteTimeUtcTicks)
    {
        FullPath = fullPath;
        Length = length;
        LastWriteTimeUtcTicks = lastWriteTimeUtcTicks;
    }

    /// <summary>最終更新日時（UTC）。</summary>
    public DateTime LastWriteTimeUtc => new(LastWriteTimeUtcTicks, DateTimeKind.Utc);

    /// <summary>
    /// ファイルの現在の身元を取る。ファイルが無い・パスが不正なら false。
    /// 例外は出さない。
    /// </summary>
    public static bool TryCapture(string sourcePath, out SourceIdentity identity)
    {
        identity = default;
        try
        {
            var info = new FileInfo(sourcePath);
            if (!info.Exists)
                return false;
            identity = Capture(info);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>FileInfo から身元を作る（<see cref="FileInfo.FullName"/> を正規化する）。</summary>
    public static SourceIdentity Capture(FileInfo info)
        => new(Path.GetFullPath(info.FullName), info.Length, info.LastWriteTimeUtc.Ticks);

    /// <summary>
    /// この身元が、ファイルの現在の状態と一致するか。
    /// パスは両辺とも GetFullPath で正規化してから OrdinalIgnoreCase で比べる
    /// （従来のディスクキャッシュのヘッダー照合と同じ）。
    /// 身元が空（パスなし）の場合は一致しない。
    /// </summary>
    public bool Matches(FileInfo info)
    {
        if (string.IsNullOrEmpty(FullPath))
            return false;
        return string.Equals(
                Path.GetFullPath(FullPath),
                Path.GetFullPath(info.FullName),
                StringComparison.OrdinalIgnoreCase)
            && Length == info.Length
            && LastWriteTimeUtcTicks == info.LastWriteTimeUtc.Ticks;
    }

    /// <summary>
    /// この身元が、別の身元と一致するか（パスは正規化してから OrdinalIgnoreCase で比べる）。
    /// メモリキャッシュ同士の照合用。どちらかが空（パスなし）の場合は一致しない。
    /// </summary>
    public bool Matches(SourceIdentity other)
    {
        if (string.IsNullOrEmpty(FullPath) || string.IsNullOrEmpty(other.FullPath))
            return false;
        return string.Equals(
                Path.GetFullPath(FullPath),
                Path.GetFullPath(other.FullPath),
                StringComparison.OrdinalIgnoreCase)
            && Length == other.Length
            && LastWriteTimeUtcTicks == other.LastWriteTimeUtcTicks;
    }
}
