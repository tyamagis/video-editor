// .cube 形式 3D LUT のパーサとトリリニアサンプリング（Editor.Core：UI・描画に依存しない）。
// 対応形式：TITLE "..." / LUT_3D_SIZE n / DOMAIN_MIN r g b / DOMAIN_MAX r g b /
// コメント行（#）/ 空行 / データ行（"r g b" の空白区切り、赤が最速で変化する順）。
// LUT_1D_SIZE が宣言されているファイルは、データ先頭の 1D テーブル（n 行）を読み飛ばして
// 3D テーブルを読む（.cube のデータ並び順の定義どおり）。
// 不正なファイルは例外を投げず、TryParse / TryLoad が false + 理由文字列を返す
// （呼び出し側は「読み込み失敗＝LUT無効」として扱える）。

using System.Globalization;
using System.IO;

namespace Editor.Core;

/// <summary>
/// パース済みの 3D LUT。Data は RGB 3値 × Size^3、並びは .cube のデータ順
/// （インデックス = ((b * Size) + g) * Size + r、赤が最速）。
/// 値は 0〜1 に丸めない（LUT には 1 超・負の値もあり得るため、丸めはサンプリング結果側で行う）。
/// </summary>
public sealed class CubeLut
{
    /// <summary>受け付ける LUT サイズの上限（128^3 × 3 float ≒ 25MB。これより大きいものは読み込み失敗にする）。</summary>
    public const int MaxSize = 128;

    /// <summary>LUT の一辺のサイズ（2〜<see cref="MaxSize"/>）。</summary>
    public int Size { get; }

    /// <summary>RGB 値のテーブル（長さ Size^3 × 3）。</summary>
    public float[] Data { get; }

    /// <summary>タイトル（TITLE 行。無ければ空）。</summary>
    public string Title { get; }

    /// <summary>入力ドメインの下限（DOMAIN_MIN。既定は全チャンネル 0）。</summary>
    public (float R, float G, float B) DomainMin { get; }

    /// <summary>入力ドメインの上限（DOMAIN_MAX。既定は全チャンネル 1）。</summary>
    public (float R, float G, float B) DomainMax { get; }

    public CubeLut(
        int size, float[] data, string title = "",
        (float R, float G, float B)? domainMin = null,
        (float R, float G, float B)? domainMax = null)
    {
        Size = size;
        Data = data;
        Title = title ?? string.Empty;
        DomainMin = domainMin ?? (0f, 0f, 0f);
        DomainMax = domainMax ?? (1f, 1f, 1f);
    }

    /// <summary>
    /// .cube のテキストをパースする。不正な内容では例外を投げず false を返し、
    /// error に理由（日本語）を入れる。
    /// </summary>
    public static bool TryParse(string text, out CubeLut? lut, out string? error)
    {
        lut = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "ファイルが空です。";
            return false;
        }

        var title = string.Empty;
        var size = 0;
        var size1D = 0;
        var domainMin = (R: 0f, G: 0f, B: 0f);
        var domainMax = (R: 1f, G: 1f, B: 1f);
        var values = new List<float>();

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var firstToken = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0];
            if (char.IsLetter(firstToken[0]) || firstToken[0] == '_')
            {
                // キーワード行。
                var keyword = firstToken.ToUpperInvariant();
                switch (keyword)
                {
                    case "TITLE":
                        var open = line.IndexOf('"');
                        var close = line.LastIndexOf('"');
                        if (open >= 0 && close > open)
                            title = line[(open + 1)..close];
                        break;
                    case "LUT_3D_SIZE":
                        if (!TryParseInt(line, out size))
                        {
                            error = "LUT_3D_SIZE の値が読めません。";
                            return false;
                        }
                        break;
                    case "LUT_1D_SIZE":
                        if (!TryParseInt(line, out size1D))
                        {
                            error = "LUT_1D_SIZE の値が読めません。";
                            return false;
                        }
                        break;
                    case "DOMAIN_MIN":
                        if (!TryParseTriple(line, out domainMin))
                        {
                            error = "DOMAIN_MIN の値が読めません。";
                            return false;
                        }
                        break;
                    case "DOMAIN_MAX":
                        if (!TryParseTriple(line, out domainMax))
                        {
                            error = "DOMAIN_MAX の値が読めません。";
                            return false;
                        }
                        break;
                    default:
                        // 未知のキーワード行（LUT_3D_INPUT_RANGE 等）は無視する。
                        break;
                }
                continue;
            }

            // データ行（3値）。
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3
                || !TryParseFloat(parts[0], out var r)
                || !TryParseFloat(parts[1], out var g)
                || !TryParseFloat(parts[2], out var b))
            {
                error = $"データ行が読めません：「{line}」";
                return false;
            }
            values.Add(r);
            values.Add(g);
            values.Add(b);
        }

        if (size <= 0)
        {
            error = "LUT_3D_SIZE がありません（3D LUT ではないファイルです）。";
            return false;
        }
        if (size < 2 || size > MaxSize)
        {
            error = $"LUT_3D_SIZE が範囲外です（{size}。2〜{MaxSize} に対応）。";
            return false;
        }
        for (var channel = 0; channel < 3; channel++)
        {
            var min = channel == 0 ? domainMin.R : channel == 1 ? domainMin.G : domainMin.B;
            var max = channel == 0 ? domainMax.R : channel == 1 ? domainMax.G : domainMax.B;
            if (!(max > min))
            {
                error = "DOMAIN_MAX は DOMAIN_MIN より大きくなければなりません。";
                return false;
            }
        }

        // 1D テーブル（あれば先頭 size1D 行）を読み飛ばす。
        // size1D が負だと skip が負になり、件数チェックをすり抜けて CopyTo が
        // 例外を投げるため、ここで弾く。skip の計算は int 溢れを避けて long で行う。
        if (size1D < 0)
        {
            error = "LUT_1D_SIZE が負です。";
            return false;
        }
        var skip = (long)size1D * 3;
        var expected = size * size * size * 3;
        if ((long)values.Count - skip != expected)
        {
            error = $"データ数が一致しません（{size}^3 = {expected / 3} 行が必要、実際は {Math.Max(0L, values.Count - skip) / 3} 行）。";
            return false;
        }
        var data = new float[expected];
        values.CopyTo((int)skip, data, 0, expected);
        lut = new CubeLut(size, data, title, domainMin, domainMax);
        return true;
    }

    /// <summary>
    /// ファイルから読み込む。ファイルが無い・読めない・不正な場合は例外を投げず
    /// false を返し、error に理由を入れる。
    /// </summary>
    public static bool TryLoad(string path, out CubeLut? lut, out string? error)
    {
        lut = null;
        error = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "LUTファイルが指定されていません。";
            return false;
        }
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = $"LUTファイルが読めません：{ex.Message}";
            return false;
        }
        return TryParse(text, out lut, out error);
    }

    /// <summary>
    /// 入力色（0〜1 スケール。範囲外はドメインで丸める）をトリリニア補間で変換する。
    /// まず DOMAIN_MIN〜DOMAIN_MAX で 0〜1 に写し、格子点の間を3軸線形補間する。
    /// 結果は丸めない（呼び出し側で必要に応じて 0〜1 に丸める）。
    /// </summary>
    public (float R, float G, float B) Sample(float r, float g, float b)
    {
        var x = ToGrid(r, DomainMin.R, DomainMax.R);
        var y = ToGrid(g, DomainMin.G, DomainMax.G);
        var z = ToGrid(b, DomainMin.B, DomainMax.B);

        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        var z0 = (int)MathF.Floor(z);
        var x1 = Math.Min(x0 + 1, Size - 1);
        var y1 = Math.Min(y0 + 1, Size - 1);
        var z1 = Math.Min(z0 + 1, Size - 1);
        var fx = x - x0;
        var fy = y - y0;
        var fz = z - z0;

        var c000 = Get(x0, y0, z0);
        var c100 = Get(x1, y0, z0);
        var c010 = Get(x0, y1, z0);
        var c110 = Get(x1, y1, z0);
        var c001 = Get(x0, y0, z1);
        var c101 = Get(x1, y0, z1);
        var c011 = Get(x0, y1, z1);
        var c111 = Get(x1, y1, z1);

        return (
            Lerp(
                Lerp(Lerp(c000.R, c100.R, fx), Lerp(c010.R, c110.R, fx), fy),
                Lerp(Lerp(c001.R, c101.R, fx), Lerp(c011.R, c111.R, fx), fy), fz),
            Lerp(
                Lerp(Lerp(c000.G, c100.G, fx), Lerp(c010.G, c110.G, fx), fy),
                Lerp(Lerp(c001.G, c101.G, fx), Lerp(c011.G, c111.G, fx), fy), fz),
            Lerp(
                Lerp(Lerp(c000.B, c100.B, fx), Lerp(c010.B, c110.B, fx), fy),
                Lerp(Lerp(c001.B, c101.B, fx), Lerp(c011.B, c111.B, fx), fy), fz));
    }

    /// <summary>ドメイン写像後の格子座標（0〜Size-1）に変換する。</summary>
    private float ToGrid(float value, float min, float max)
    {
        var normalized = (value - min) / (max - min);
        if (float.IsNaN(normalized))
            normalized = 0;
        return Math.Clamp(normalized, 0f, 1f) * (Size - 1);
    }

    private (float R, float G, float B) Get(int r, int g, int b)
    {
        var i = ((b * Size) + g) * Size + r;
        return (Data[i * 3], Data[i * 3 + 1], Data[i * 3 + 2]);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static bool TryParseFloat(string token, out float value)
        => float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
           && float.IsFinite(value);

    private static bool TryParseInt(string line, out int value)
    {
        value = 0;
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
               && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseTriple(string line, out (float R, float G, float B) value)
    {
        value = default;
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4
            || !TryParseFloat(parts[1], out var r)
            || !TryParseFloat(parts[2], out var g)
            || !TryParseFloat(parts[3], out var b))
            return false;
        value = (r, g, b);
        return true;
    }
}
