// 書き出し設定。解像度とフレームレート、画質の組み合わせ。
// 純粋なデータで ffmpeg 依存はないため Core に置く
// （以前は Editor.Media.ExportService.cs にあったものを移動。型名・メンバーは不変）。
// Render の ITimelineExporter が公開シグネチャで使うため、
// Render→Media の参照を外すにはこの移動が必要。

namespace Editor.Core;

/// <summary>書き出し設定。解像度とフレームレート、画質の組み合わせ。</summary>
public sealed record ExportSettings(
    int Width = 1920,
    int Height = 1080,
    int FrameRate = 30,
    int Crf = 20,
    string Preset = "medium",
    int AudioBitrateKbps = 192)
{
    public static ExportSettings Hd30 { get; } = new(1920, 1080, 30);
    public static ExportSettings Hd60 { get; } = new(1920, 1080, 60);
    public static ExportSettings Uhd30 { get; } = new(3840, 2160, 30);
    public static ExportSettings Uhd60 { get; } = new(3840, 2160, 60);

    /// <summary>x264 のエンコーダープリセット一覧（速い→遅い・高画質の順）。</summary>
    public static IReadOnlyList<string> EncoderPresets { get; } =
    [
        "ultrafast", "superfast", "veryfast", "faster", "fast",
        "medium", "slow", "slower", "veryslow", "placebo"
    ];

    public void Validate()
    {
        if (Width <= 0 || Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(Width), "解像度は正の値にしてください。");
        if (FrameRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(FrameRate), "フレームレートは正の値にしてください。");
        if (Crf is < 0 or > 51)
            throw new ArgumentOutOfRangeException(nameof(Crf), "CRFは0〜51の範囲にしてください。");
        if (!EncoderPresets.Contains(Preset, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"エンコーダープリセットは {string.Join(" / ", EncoderPresets)} のいずれかにしてください。", nameof(Preset));
        if (AudioBitrateKbps is < 16 or > 512)
            throw new ArgumentOutOfRangeException(nameof(AudioBitrateKbps), "音声ビットレートは16〜512 kbpsの範囲にしてください。");
    }
}
