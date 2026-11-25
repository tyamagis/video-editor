// 素材のプローブの提供口（DIP）。
// Render は素材の情報を調べるのにプローブが必要だが、ffmpeg への依存は
// 持たない。Media 側がこのインターフェースを実装し、App が差し込む
// （IAudioPeakProvider と同じ形）。

namespace Editor.Core;

/// <summary>動画・音声ファイルの情報を調べる提供口。実装は Editor.Media。</summary>
public interface IMediaProbe
{
    /// <summary>素材の基本情報を調べる。素材が読めなければ例外を投げる。</summary>
    Task<MediaInfo> ProbeAsync(string path, CancellationToken ct = default);
}
