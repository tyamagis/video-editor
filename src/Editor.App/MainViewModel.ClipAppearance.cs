// MainViewModel の分割ファイル（partial）。選択中クリップの見た目（ブレンドモード・明るさ・コントラスト）とトランジション設定（インスペクタ）。

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

public sealed partial class MainViewModel
{
    /// <summary>選択中クリップのブレンドモード。未選択時は通常。</summary>
    public BlendMode SelectedClipBlendMode
    {
        get => SelectedClip?.BlendMode ?? BlendMode.Normal;
        set
        {
            if (SelectedClip is null || SelectedClip.BlendMode == value)
                return;
            SelectedClip.BlendMode = value;
            OnPropertyChanged();
            NotifyTimelineChanged();
        }
    }

    /// <summary>選択中クリップの明るさ（-1.0〜1.0）。クリップ未選択時は 0。</summary>
    public double SelectedClipBrightness
    {
        get => SelectedClip?.Brightness ?? 0;
        set
        {
            if (SelectedClip is null)
                return;
            SelectedClip.Brightness = Math.Clamp(value, -1.0, 1.0);
            OnPropertyChanged();
            NotifyTimelineChanged();
        }
    }

    /// <summary>選択中クリップのコントラスト（0.0〜2.0）。クリップ未選択時は 1.0。</summary>
    public double SelectedClipContrast
    {
        get => SelectedClip?.Contrast ?? 1.0;
        set
        {
            if (SelectedClip is null)
                return;
            SelectedClip.Contrast = Math.Clamp(value, 0.0, 2.0);
            OnPropertyChanged();
            NotifyTimelineChanged();
        }
    }

    /// <summary>選択中クリップの開始側トランジション種別。未選択時は「なし」。</summary>
    public TransitionKind SelectedClipTransitionKind
    {
        get => SelectedClip?.TransitionKind ?? TransitionKind.None;
        set
        {
            if (SelectedClip is null || SelectedClip.TransitionKind == value)
                return;
            SelectedClip.TransitionKind = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TransitionHint));
            NotifyTimelineChanged();
        }
    }

    /// <summary>選択中クリップの開始側トランジションの長さ（秒）。0.05〜60 に丸める。</summary>
    public double SelectedClipTransitionDurationSeconds
    {
        get => SelectedClip?.TransitionDurationSeconds ?? 1.0;
        set
        {
            if (SelectedClip is null)
                return;
            var clamped = Math.Clamp(value, 0.05, 60.0);
            if (Math.Abs(SelectedClip.TransitionDurationSeconds - clamped) < 1e-9)
                return;
            SelectedClip.TransitionDurationSeconds = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TransitionHint));
            NotifyTimelineChanged();
        }
    }

    /// <summary>選択中クリップのトランジションについての説明文。</summary>
    public string TransitionHint
    {
        get
        {
            var clip = SelectedClip;
            if (clip is null)
                return string.Empty;
            if (clip.Kind == ClipKind.Audio)
                return "音声クリップのトランジション（音量のクロスフェード）は未対応です。";
            if (clip.TransitionKind == TransitionKind.None)
                return "開始側の境界にトランジションはありません。";
            var prev = FindPreviousAdjacentClip(clip);
            if (prev is null)
                return "前のクリップと隣り合っていないため、適用されません。";
            return $"前のクリップ「{prev.Name}」との境界で {clip.TransitionDurationSeconds:F1} 秒のディゾルブ。";
        }
    }

    /// <summary>同じトラック上で、指定クリップの直前に隣り合っているクリップ。無ければ null。</summary>
    private Clip? FindPreviousAdjacentClip(Clip clip)
    {
        foreach (var track in Project.Tracks)
        {
            var clips = track.Clips.OrderBy(c => c.StartSeconds).ToList();
            var index = clips.IndexOf(clip);
            if (index > 0)
            {
                var prev = clips[index - 1];
                if (Math.Abs(prev.EndSeconds - clip.StartSeconds) < 1e-6)
                    return prev;
            }
        }
        return null;
    }
}
