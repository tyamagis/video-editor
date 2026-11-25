// ffmpeg の所在設定のうち、Render が必要とする部分だけの抽象（DIP）。
// Render が設定から必要とするのは ffmpeg の実行ファイルパスだけで、
// ffprobe のパスは使わないので抽象に含めない。実装は Editor.Media。

namespace Editor.Core;

/// <summary>ffmpeg の実行ファイルパスの提供口。実装は Editor.Media.FfmpegSettings。</summary>
public interface IFfmpegConfiguration
{
    /// <summary>ffmpeg の実行ファイルパス（既定は PATH 頼みの "ffmpeg"）。</summary>
    string FfmpegExePath { get; }
}
