// プローブ結果。素材の基本情報。
// 純粋なデータで ffmpeg 依存はないため Core に置く
// （以前は Editor.Media.MediaProbe.cs にあったものを移動。型名・メンバーは不変）。

namespace Editor.Core;

/// <summary>プローブ結果。素材の基本情報。</summary>
public sealed record MediaInfo(
    string Path,
    TimeSpan Duration,
    int Width,
    int Height,
    double FrameRate,
    bool HasVideo,
    bool HasAudio,
    string? VideoCodec,
    string? AudioCodec);
