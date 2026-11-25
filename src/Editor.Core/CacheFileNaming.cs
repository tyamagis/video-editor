// ディスクキャッシュの命名（キャッシュファイル名の生成）。
// 従来は AudioAnalysisCache が持っていたが、波形キャッシュも同じ基名を
// 使っており、命名規則が2か所に散っていたためここに1つにまとめる。
// 生成規則は従来と同一：素材のフルパス（小文字化）の SHA-256 を16進数
// （小文字）にしたものを、プロジェクトのキャッシュフォルダに置く。
// 規則を変えると既存キャッシュが無効になるため、変えてはならない。

using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Editor.Core;

/// <summary>プロジェクトのキャッシュフォルダと素材ごとのキャッシュファイル名を決める。</summary>
public static class CacheFileNaming
{
    /// <summary>プロジェクトのキャッシュフォルダ。「MyProject.veproj」→ 同じフォルダの「MyProject.cache」。</summary>
    public static string GetCacheDirectory(string projectPath)
    {
        var directory = Path.GetDirectoryName(projectPath) ?? string.Empty;
        return Path.Combine(directory, Path.GetFileNameWithoutExtension(projectPath) + ".cache");
    }

    /// <summary>
    /// 素材1件分のキャッシュファイルの基名（拡張子なし）。
    /// 素材のフルパス（小文字化）の SHA-256 を16進数にしたもので、パスに使えない文字や
    /// 長すぎるパスを避ける。同じ素材でもプロジェクトが違えばフォルダが違うため衝突しない。
    /// </summary>
    public static string GetCacheFileBase(string cacheDirectory, string sourcePath)
    {
        var fullPath = Path.GetFullPath(sourcePath).ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(fullPath));
        return Path.Combine(cacheDirectory, Convert.ToHexString(hash).ToLowerInvariant());
    }

    /// <summary>素材1件分のキャッシュファイルのパス（基名＋拡張子などの接尾辞）。</summary>
    public static string GetCacheFilePath(string cacheDirectory, string sourcePath, string suffix)
        => GetCacheFileBase(cacheDirectory, sourcePath) + suffix;
}
