// ストリーミングによるフレーム取得の口。Core は Media を参照しない。
// Media 側（StreamingFrameSource）が実装し、Render が差し込む。
// 旧 Editor.Media.IStreamingFrameSource / VideoFrameData を
// 型名・メンバー名そのままに移動。

namespace Editor.Core;

/// <summary>1フレーム分の生画素（BGRA）とその時刻・サイズ。</summary>
public sealed record VideoFrameData(byte[] Bgra, int Width, int Height, double TimeSeconds);

/// <summary>常駐 ffmpeg からのフレーム取得口。</summary>
public interface IStreamingFrameSource
{
    /// <summary>
    /// 指定時刻のフレームを返す。映像の終端より後の時刻は null。
    /// 取得できない場合（ffmpeg 不在など）は例外を投げ、呼び出し側が
    /// 単発抽出にフォールバックする。
    /// </summary>
    Task<VideoFrameData?> GetFrameAsync(
        string sourcePath, double timeSeconds, int maxWidth,
        CancellationToken ct = default);
}
