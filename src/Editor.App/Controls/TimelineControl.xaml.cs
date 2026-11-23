// タイムライン表示・操作（設計プラン 6章）。
// WPF のカスタムコントロールとして自作し、描画は SkiaSharp で行う。
// グリッド（フレーム／秒／拍）、波形表示の切替、クリップの移動・
// トリミング、再生ヘッドへのシークをここで扱う。

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Editor.Audio;
using Editor.Core;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace Editor.App.Controls;

public partial class TimelineControl : UserControl
{
    private const float HeaderHeight = 24f;
    private const float TrackHeight = 64f;
    private const float TrackGap = 4f;

    private readonly Dictionary<Clip, SKRect> _clipRects = new();

    private enum DragMode { None, Move, Trim }
    private DragMode _dragMode = DragMode.None;
    private Clip? _dragClip;
    private double _dragOffsetX;

    private GridMode _gridMode = GridMode.Frame;
    private bool _showWaveform = true;

    public TimelineControl()
    {
        InitializeComponent();
    }

    public EditorProject? Project { get; set; }
    public double PlayheadSeconds { get; set; }
    public double PixelsPerSecond { get; set; } = 80;

    /// <summary>クリップ → 波形データ。MainWindow が ViewModel 経由で渡す。</summary>
    public Func<Clip, WaveformData?>? WaveformProvider { get; set; }

    public GridMode GridMode
    {
        get => _gridMode;
        set
        {
            _gridMode = value;
            if (GridModeBox is not null)
                GridModeBox.SelectedIndex = (int)value;
            InvalidateTimeline();
        }
    }

    public bool ShowWaveform
    {
        get => _showWaveform;
        set
        {
            _showWaveform = value;
            if (ShowWaveformBox is not null)
                ShowWaveformBox.IsChecked = value;
            InvalidateTimeline();
        }
    }

    /// <summary>再生ヘッドの移動要求（時刻・秒）。</summary>
    public event EventHandler<double>? SeekRequested;
    /// <summary>クリップがクリックされた。</summary>
    public event EventHandler<Clip>? ClipSelected;
    public event EventHandler? SplitRequested;
    /// <summary>ドラッグなどの編集が終わった。</summary>
    public event EventHandler? TimelineChanged;
    public event EventHandler? GridModeChanged;
    public event EventHandler? ShowWaveformChanged;

    /// <summary>
    /// 再描画要求。XAML読み込み中は GridModeBox 等のイベントが先に発火し、
    /// TimelineElement がまだ null の場合があるため、null 条件で呼ぶ。
    /// </summary>
    public void InvalidateTimeline() => TimelineElement?.InvalidateVisual();

    private void GridModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GridModeBox.SelectedItem is ComboBoxItem item
            && Enum.TryParse<GridMode>(item.Tag?.ToString(), out var mode))
        {
            _gridMode = mode;
            GridModeChanged?.Invoke(this, EventArgs.Empty);
            InvalidateTimeline();
        }
    }

    private void ShowWaveformBox_Changed(object sender, RoutedEventArgs e)
    {
        _showWaveform = ShowWaveformBox.IsChecked == true;
        ShowWaveformChanged?.Invoke(this, EventArgs.Empty);
        InvalidateTimeline();
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        PixelsPerSecond = e.NewValue;
        InvalidateTimeline();
    }

    private void SplitButton_Click(object sender, RoutedEventArgs e)
        => SplitRequested?.Invoke(this, EventArgs.Empty);

    private void OnPaintSurface(object sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        var info = e.Info;
        canvas.Clear(new SKColor(0x1E, 0x1E, 0x1E));

        if (Project is null)
        {
            using var emptyPaint = new SKPaint { Color = SKColors.Gray, TextSize = 14, IsAntialias = true };
            canvas.DrawText("プロジェクトがありません", 12, 32, emptyPaint);
            return;
        }

        _clipRects.Clear();
        DrawRuler(canvas, info.Width);
        DrawTracks(canvas, info.Width);
        DrawPlayhead(canvas, info.Height);
    }

    private void DrawRuler(SKCanvas canvas, int width)
    {
        if (Project is null)
            return;

        var duration = Math.Max(Project.DurationSeconds, 10);
        using var majorPaint = new SKPaint { Color = new SKColor(0x66, 0x66, 0x66), StrokeWidth = 1 };
        using var minorPaint = new SKPaint { Color = new SKColor(0x3A, 0x3A, 0x3A), StrokeWidth = 1 };
        using var textPaint = new SKPaint { Color = SKColors.LightGray, TextSize = 11, IsAntialias = true };

        if (GridMode == GridMode.Beat)
        {
            var beats = BeatGridCalculator.GetBeatTimes(Project.Bpm, Project.BeatOffsetSeconds, duration);
            for (var i = 0; i < beats.Count; i++)
            {
                var x = (float)(beats[i] * PixelsPerSecond);
                if (x > width)
                    break;
                var isBar = BeatGridCalculator.IsBarStart(i);
                canvas.DrawLine(x, 0, x, HeaderHeight, isBar ? majorPaint : minorPaint);
                if (isBar)
                    canvas.DrawText($"小節 {i / 4 + 1}", x + 3, 16, textPaint);
            }
            return;
        }

        // フレーム／秒：1秒ごとの線とラベル。フレーム表示ではフレーム刻みも描く。
        for (var second = 0; second <= duration; second++)
        {
            var x = (float)(second * PixelsPerSecond);
            if (x > width)
                break;
            canvas.DrawLine(x, 0, x, HeaderHeight, majorPaint);
            canvas.DrawText($"{second}s", x + 3, 16, textPaint);
        }

        if (GridMode == GridMode.Frame && PixelsPerSecond >= 100)
        {
            var frameInterval = 1.0 / Project.FrameRate;
            for (var t = 0.0; t <= duration; t += frameInterval)
            {
                var x = (float)(t * PixelsPerSecond);
                if (x > width)
                    break;
                canvas.DrawLine(x, HeaderHeight - 8, x, HeaderHeight, minorPaint);
            }
        }
    }

    private void DrawTracks(SKCanvas canvas, int width)
    {
        if (Project is null)
            return;

        using var lanePaint = new SKPaint { Color = new SKColor(0x2A, 0x2A, 0x2A) };
        using var laneStroke = new SKPaint { Color = new SKColor(0x3A, 0x3A, 0x3A), Style = SKPaintStyle.Stroke };
        using var textPaint = new SKPaint { Color = SKColors.LightGray, TextSize = 12, IsAntialias = true };

        for (var i = 0; i < Project.Tracks.Count; i++)
        {
            var track = Project.Tracks[i];
            var y = HeaderHeight + i * (TrackHeight + TrackGap);
            canvas.DrawRect(0, y, width, TrackHeight, lanePaint);
            canvas.DrawRect(0, y, width, TrackHeight, laneStroke);
            canvas.DrawText(track.Name, 6, y + 16, textPaint);

            foreach (var clip in track.Clips)
                DrawClip(canvas, clip, y);
        }
    }

    private void DrawClip(SKCanvas canvas, Clip clip, float laneY)
    {
        var x = (float)(clip.StartSeconds * PixelsPerSecond);
        var w = (float)(clip.DurationSeconds * PixelsPerSecond);
        var rect = new SKRect(x, laneY + 4, x + w, laneY + TrackHeight - 4);
        _clipRects[clip] = rect;

        var fill = clip.Kind switch
        {
            ClipKind.Video => new SKColor(0x3A, 0x6E, 0xA5),
            ClipKind.Audio => new SKColor(0x4A, 0x8F, 0x5D),
            ClipKind.Text => new SKColor(0x8A, 0x6D, 0x3B),
            ClipKind.Image => new SKColor(0x6A, 0x5A, 0x9E),
            _ => new SKColor(0x55, 0x55, 0x55)
        };

        using var fillPaint = new SKPaint { Color = fill, IsAntialias = true };
        using var strokePaint = new SKPaint
        {
            Color = SKColors.White.WithAlpha(80),
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };
        canvas.DrawRoundRect(rect, 4, 4, fillPaint);
        canvas.DrawRoundRect(rect, 4, 4, strokePaint);

        if (ShowWaveform && clip.Kind == ClipKind.Audio)
            DrawWaveform(canvas, clip, rect);

        if (w > 30)
        {
            using var textPaint = new SKPaint { Color = SKColors.White, TextSize = 12, IsAntialias = true };
            canvas.DrawText(clip.Name, rect.Left + 6, rect.Top + 18, textPaint);
        }
    }

    private void DrawWaveform(SKCanvas canvas, Clip clip, SKRect rect)
    {
        var data = WaveformProvider?.Invoke(clip);
        if (data is null || data.BucketCount == 0)
            return;

        var centerY = rect.MidY;
        var halfHeight = rect.Height / 2 - 4;
        var steps = Math.Max(2, (int)rect.Width);

        using var path = new SKPath();
        for (var sx = 0; sx <= steps; sx++)
        {
            var bucket = Math.Min(data.BucketCount - 1, (int)((double)sx / steps * data.BucketCount));
            var x = rect.Left + sx;
            var yTop = centerY - data.MaxPeaks[bucket] * halfHeight;
            if (sx == 0)
                path.MoveTo(x, (float)yTop);
            else
                path.LineTo(x, (float)yTop);
        }
        for (var sx = steps; sx >= 0; sx--)
        {
            var bucket = Math.Min(data.BucketCount - 1, (int)((double)sx / steps * data.BucketCount));
            var x = rect.Left + sx;
            var yBottom = centerY - data.MinPeaks[bucket] * halfHeight;
            path.LineTo(x, (float)yBottom);
        }
        path.Close();

        using var fillPaint = new SKPaint
        {
            Color = SKColors.White.WithAlpha(110),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        canvas.DrawPath(path, fillPaint);
    }

    private void DrawPlayhead(SKCanvas canvas, float height)
    {
        var x = (float)(PlayheadSeconds * PixelsPerSecond);
        using var paint = new SKPaint { Color = SKColors.Red, StrokeWidth = 2 };
        canvas.DrawLine(x, 0, x, height, paint);
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(TimelineElement);
        TimelineElement.CaptureMouse();

        if (pos.Y < HeaderHeight)
        {
            SeekRequested?.Invoke(this, pos.X / PixelsPerSecond);
            return;
        }

        foreach (var (clip, rect) in _clipRects.Reverse())
        {
            if (!rect.Contains((float)pos.X, (float)pos.Y))
                continue;

            ClipSelected?.Invoke(this, clip);
            if (Math.Abs(pos.X - rect.Right) <= 6)
            {
                _dragMode = DragMode.Trim;
                _dragClip = clip;
            }
            else
            {
                _dragMode = DragMode.Move;
                _dragClip = clip;
                _dragOffsetX = pos.X - rect.Left;
            }
            return;
        }

        // 空き領域のクリックはシーク扱い。
        SeekRequested?.Invoke(this, pos.X / PixelsPerSecond);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragMode == DragMode.None || _dragClip is null || Project is null)
            return;

        var pos = e.GetPosition(TimelineElement);
        if (_dragMode == DragMode.Move)
        {
            var newStart = (pos.X - _dragOffsetX) / PixelsPerSecond;
            if (SnapBox.IsChecked == true && GridMode == GridMode.Beat)
            {
                var snapped = BeatGridCalculator.SnapToBeat(
                    newStart, Project.Bpm, Project.BeatOffsetSeconds);
                if (snapped.HasValue)
                    newStart = snapped.Value;
            }
            TimelineEditor.MoveClip(_dragClip, newStart);
        }
        else if (_dragMode == DragMode.Trim)
        {
            var newEnd = pos.X / PixelsPerSecond;
            try
            {
                TimelineEditor.TrimClipEnd(_dragClip, newEnd);
            }
            catch (ArgumentOutOfRangeException)
            {
                // 範囲外のトリミングは無視する。
            }
        }
        InvalidateTimeline();
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragMode != DragMode.None)
            TimelineChanged?.Invoke(this, EventArgs.Empty);
        _dragMode = DragMode.None;
        _dragClip = null;
        TimelineElement.ReleaseMouseCapture();
        InvalidateTimeline();
    }
}
