// 素材プールの1行。MainViewModel.cs から分離（型・名前空間・公開メンバーは不変）。

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using Editor.Audio;
using Editor.Core;
using Editor.Media;
using Editor.Render;
using SkiaSharp;

namespace Editor.App;

/// <summary>素材プールに表示する1素材。</summary>
public sealed class MediaItem
{
    public required string SourcePath { get; init; }
    public required MediaInfo Info { get; init; }
    public string Name => Path.GetFileName(SourcePath);
    public string Display => $"{Name}  ({Info.Duration:mm\\:ss}, {Info.Width}x{Info.Height})";
}
