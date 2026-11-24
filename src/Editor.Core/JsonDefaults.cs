// JSON の共通設定。保存する JSON はすべてインデント付き（人間が読める形式）に統一する。
// ProjectStore / AppSettings / 両キャッシュで同じものを使う。

using System.Text.Json;

namespace Editor.Core;

/// <summary>JSON シリアライズの共通設定。</summary>
public static class JsonDefaults
{
    /// <summary>インデント付き JSON。プロジェクト・設定・キャッシュヘッダーの保存と読み込みで共用する。</summary>
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}
