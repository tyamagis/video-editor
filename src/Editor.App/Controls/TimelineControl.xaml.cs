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

    // 日本語（「小節」など）が□で表示されないよう、日本語対応フォントを使う。
    private static readonly SKTypeface JapaneseTypeface =
        SKTypeface.FromFamilyName("Yu Gothic UI") ?? SKTypeface.Default;

    private readonly Dictionary<Clip, SKRect> _clipRects = new();

    private enum DragMode { None, Move, Trim, Seek }
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

    /// <summary>素材がタイムラインにドロップされた（パスとドロップ位置の秒数）。</summary>
    public sealed record MediaDropInfo(string SourcePath, double TimeSeconds);

    /// <summary>素材プールから素材がドロップされた。</summary>
    public event EventHandler<MediaDropInfo>? MediaDropped;

    private static SKPaint CreateTextPaint(float textSize, SKColor color) => new()
    {
        Color = color,
        TextSize = textSize,
        IsAntialias = true,
        Typeface = JapaneseTypeface
    };

    /// <summary>再生ヘッドの移動要求（時刻・秒）。</summary>
    public event EventHandler<double>? SeekRequested;
    /// <summary>シークのドラッグ（またはクリック）が終わった。音声の再開などに使う。</summary>
    public event EventHandler? SeekDragFinished;
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

    private void TimelineControl_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.StringFormat)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void TimelineControl_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.StringFormat))
            return;
        if (e.Data.GetData(DataFormats.StringFormat) is not string path)
            return;
        var pos = e.GetPosition(TimelineElement);
        MediaDropped?.Invoke(this, new MediaDropInfo(path, Math.Max(0, pos.X / PixelsPerSecond)));
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
            using var emptyPaint = CreateTextPaint(14, SKColors.Gray);
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
        using var textPaint = CreateTextPaint(11, SKColors.LightGray);
        using var beatPaint = CreateTextPaint(10, new SKColor(0x99, 0x99, 0x99));

        if (GridMode == GridMode.Beat)
        {
            // 毎フレーム List を確保しないよう、拍時刻は等間隔の計算で回す。
            // i は拍番号（0始まり）で、リスト版と一致する。
            var interval = BeatGridCalculator.BeatIntervalSeconds(Project.Bpm);
            var lastLabelX = -1000f;
            for (var i = 0; ; i++)
            {
                var t = Project.BeatOffsetSeconds + i * interval;
                if (t > duration)
                    break;
                var x = (float)(t * PixelsPerSecond);
                if (x > width)
                    break;
                var isBar = BeatGridCalculator.IsBarStart(i);
                canvas.DrawLine(x, 0, x, HeaderHeight, isBar ? majorPaint : minorPaint);
                var bar = i / 4 + 1;
                var beat = i % 4 + 1;
                if (isBar)
                {
                    // 小節先頭は「小節.拍」形式で描く（例：1.1、2.1）。
                    // ラベルが重ならない間隔があるときだけ描く
                    if (x - lastLabelX >= 56)
                    {
                        canvas.DrawText($"{bar}.1", x + 3, 16, textPaint);
                        lastLabelX = x;
                    }
                }
                else if (PixelsPerSecond >= 50)
                {
                    // 拡大時だけ拍番号を「小節.拍」形式で小さく描く（例：1.2）
                    canvas.DrawText($"{bar}.{beat}", x + 3, 16, beatPaint);
                }
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
        using var textPaint = CreateTextPaint(12, SKColors.LightGray);

        for (var i = 0; i < Project.Tracks.Count; i++)
        {
            var track = Project.Tracks[i];
            var y = HeaderHeight + i * (TrackHeight + TrackGap);
            canvas.DrawRect(0, y, width, TrackHeight, lanePaint);
            canvas.DrawRect(0, y, width, TrackHeight, laneStroke);
            canvas.DrawText(track.Name, 6, y + 16, textPaint);

            foreach (var clip in track.Clips)
                DrawClip(canvas, clip, y, width);
        }
    }

    private void DrawClip(SKCanvas canvas, Clip clip, float laneY, float visibleWidth)
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
            DrawWaveform(canvas, clip, rect, visibleWidth);

        if (w > 30)
        {
            using var textPaint = CreateTextPaint(12, SKColors.White);
            canvas.DrawText(clip.Name, rect.Left + 6, rect.Top + 18, textPaint);
        }
    }

    private void DrawWaveform(SKCanvas canvas, Clip clip, SKRect rect, float visibleWidth)
    {
        var data = WaveformProvider?.Invoke(clip);
        if (data is null || data.BucketCount == 0 || rect.Width <= 0)
            return;

        // 画面に見えている範囲だけ描く。長いクリップ全体のパスを作ると、
        // 再生中の毎フレームで数万点のパスを作り直すことになる。
        var visibleLeft = Math.Max(rect.Left, 0);
        var visibleRight = Math.Min(rect.Right, visibleWidth);
        if (visibleRight <= visibleLeft)
            return;

        var centerY = rect.MidY;
        var halfHeight = rect.Height / 2 - 4;
        var steps = Math.Max(2, (int)(visibleRight - visibleLeft));

        using var path = new SKPath();
        for (var sx = 0; sx <= steps; sx++)
        {
            var x = visibleLeft + sx;
            var bucket = Math.Min(data.BucketCount - 1, (int)((x - rect.Left) / rect.Width * data.BucketCount));
            var yTop = centerY - data.MaxPeaks[bucket] * halfHeight;
            if (sx == 0)
                path.MoveTo(x, (float)yTop);
            else
                path.LineTo(x, (float)yTop);
        }
        for (var sx = steps; sx >= 0; sx--)
        {
            var x = visibleLeft + sx;
            var bucket = Math.Min(data.BucketCount - 1, (int)((x - rect.Left) / rect.Width * data.BucketCount));
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
            _dragMode = DragMode.Seek;
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

        // 空き領域はシーク扱い。ドラッグ中は再生ヘッドが追従する。
        _dragMode = DragMode.Seek;
        SeekRequested?.Invoke(this, pos.X / PixelsPerSecond);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragMode == DragMode.Seek)
        {
            var seekPos = e.GetPosition(TimelineElement);
            SeekRequested?.Invoke(this, seekPos.X / PixelsPerSecond);
            return;
        }

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
        var wasSeek = _dragMode == DragMode.Seek;
        if (_dragMode != DragMode.None)
            TimelineChanged?.Invoke(this, EventArgs.Empty);
        _dragMode = DragMode.None;
        _dragClip = null;
        TimelineElement.ReleaseMouseCapture();
        InvalidateTimeline();
        if (wasSeek)
            SeekDragFinished?.Invoke(this, EventArgs.Empty);
    }
}
