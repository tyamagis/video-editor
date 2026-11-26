// プローブ結果。素材の基本情報。
// 純粋なデータで ffmpeg 依存はないため Core に置く
// （以前は Editor.Media.MediaProbe.cs にあったものを移動。型名・メンバーは不変）。

namespace Editor.Core;

/// <summary>プローブ結果。素材の基本情報。</summary>
/// <param name="FrameRate">平均フレームレート（avg_frame_rate）。VFR 素材では最大レート
/// （r_frame_rate）ではなくこちらが実フレーム密度に近い。</param>
/// <param name="IsVariableFrameRate">平均レートと最大レートが食い違う VFR 素材か。
/// フレーム番号 = 時刻 × fps の等間隔前提が成り立たないため、逆再生プレビューの
/// チャンクキャッシュはこの素材には使わない（StreamingFrameSource 参照）。</param>
public sealed record MediaInfo(
    string Path,
    TimeSpan Duration,
    int Width,
    int Height,
    double FrameRate,
    bool HasVideo,
    bool HasAudio,
    string? VideoCodec,
    string? AudioCodec,
    bool IsVariableFrameRate = false);
