// フレーム取得の口。Core は Media を参照しない。
// Media 側（ThumbnailService）が実装し、Render / App が差し込む。
// 旧 Editor.Media.IVideoFrameSource を型名・メンバー名そのままに移動。

namespace Editor.Core;

/// <summary>指定時刻の映像フレームを PNG バイト列で取得する口。</summary>
public interface IVideoFrameSource
{
    Task<byte[]?> GetFramePngAsync(string sourcePath, double timeSeconds, int maxWidth, CancellationToken ct = default);
}
