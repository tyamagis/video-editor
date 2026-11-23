// プレビュー解像度の切替（設計プラン 5章・6章）。
// 細部確認ではフル解像度、タイミング調整では軽量表示を選べる。
// 書き出し品質には影響しない。プレビュー専用の設定。

namespace Editor.Render;

/// <summary>プレビュー表示の解像度。</summary>
public enum PreviewResolution
{
    Full,
    Half,
    Quarter
}

public static class PreviewResolutionExtensions
{
    public static double GetScale(this PreviewResolution resolution) => resolution switch
    {
        PreviewResolution.Full => 1.0,
        PreviewResolution.Half => 0.5,
        PreviewResolution.Quarter => 0.25,
        _ => 1.0
    };

    public static string GetDisplayName(this PreviewResolution resolution) => resolution switch
    {
        PreviewResolution.Full => "フル",
        PreviewResolution.Half => "1/2",
        PreviewResolution.Quarter => "1/4",
        _ => "フル"
    };

    public static (int Width, int Height) ApplyTo(this PreviewResolution resolution, int width, int height)
    {
        var scale = resolution.GetScale();
        return (
            Math.Max(2, (int)Math.Round(width * scale)),
            Math.Max(2, (int)Math.Round(height * scale)));
    }
}
