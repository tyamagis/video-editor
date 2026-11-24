// キーフレームのパラメータ選択肢。MainViewModel.cs から分離（型・名前空間・公開メンバーは不変）。

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

/// <summary>キーフレーム編集で選べるパラメータ1つ（表示名と、KeyframeTrack のパラメータ名）。</summary>
public sealed class KeyframeParameterOption
{
    public required string Display { get; init; }
    public required string Value { get; init; }
}
