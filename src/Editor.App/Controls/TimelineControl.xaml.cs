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

    private enum DragMode { None, Move, Trim, TrimStart, Seek, Marker }
    private DragMode _dragMode = DragMode.None;
    private Clip? _dragClip;
    private Marker? _dragMarker;
    private double _dragOffsetX;

    /// <summary>スナップ閾値（ピクセル）。秒への換算は SnapToleranceSeconds（ズームに連動する）。</summary>
    private const double SnapThresholdPixels = 8.0;

    /// <summary>ドラッグ開始時点のクリップ状態（ドラッグ対象とそのグループ全員）。ドラッグ中はこの原点から計算し直すため、行き過ぎて戻す操作も正しく効く。</summary>
    private readonly Dictionary<Clip, (double Start, double Duration, double InPoint)> _dragOrigins = new();

    /// <summary>ドラッグ開始時点の全クリップの開始位置（リップルトリミングで後続を原点から計算し直すため）。</summary>
    private readonly Dictionary<Clip, double> _dragAllStarts = new();

    /// <summary>スナップ閾値（秒）＝ 8px ÷ ズーム。既定ズーム 80px/s で 0.1 秒。</summary>
    private double SnapToleranceSeconds => SnapThresholdPixels / Math.Max(1, PixelsPerSecond);

    private Clip? _selectedClip;

    /// <summary>選択中クリップ（描画で強調する）。MainWindow が ViewModel と同期する。</summary>
    public Clip? SelectedClip
    {
        get => _selectedClip;
        set { _selectedClip = value; InvalidateTimeline(); }
    }

    private IReadOnlyCollection<Clip> _multiSelectedClips = Array.Empty<Clip>();

    /// <summary>複数選択中のクリップ（Ctrl+クリック。グループ化の対象）。MainWindow が ViewModel と同期する。</summary>
    public IReadOnlyCollection<Clip> MultiSelectedClips
    {
        get => _multiSelectedClips;
        set { _multiSelectedClips = value ?? Array.Empty<Clip>(); InvalidateTimeline(); }
    }

    /// <summary>このドラッグでアンドゥ用の合図を出したか（1回のドラッグで1回だけ出す）。</summary>
    private bool _editSnapshotTaken;

    private GridMode _gridMode = GridMode.Frame;
    private bool _showWaveform = true;

    public TimelineControl()
    {
        InitializeComponent();
    }

    public EditorProject? Project { get; set; }
    public double PlayheadSeconds { get; set; }
    public double PixelsPerSecond { get; set; } = 80;

    private Marker? _selectedMarker;

    /// <summary>選択中のマーカー（ルーラーでクリックしたもの・一覧で選んだもの）。選択中は目立つ色で描く。</summary>
    public Marker? SelectedMarker
    {
        get => _selectedMarker;
        set
        {
            _selectedMarker = value;
            InvalidateTimeline();
        }
    }

    private BeatMap? _detectedBeatMap;

    /// <summary>
    /// 検出済みのビートマップ（BPM自動検出・波形検出の結果。タイムライン時刻）。
    /// 設定されているとき、拍グリッド表示は BPM からの算出ではなくこの拍時刻を描く。
    /// </summary>
    public BeatMap? DetectedBeatMap
    {
        get => _detectedBeatMap;
        set
        {
            _detectedBeatMap = value;
            InvalidateTimeline();
        }
    }

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
    /// <summary>クリップが Ctrl+クリックされた（複数選択の切り替え。グループ化用）。</summary>
    public event EventHandler<Clip>? ClipToggleRequested;
    /// <summary>マーカーがルーラー上でクリックされた（選択する）。</summary>
    public event EventHandler<Marker>? MarkerSelected;
    /// <summary>マーカー追加ボタンが押された。</summary>
    public event EventHandler? AddMarkerRequested;
    /// <summary>前のマーカーボタンが押された。</summary>
    public event EventHandler? PreviousMarkerRequested;
    /// <summary>次のマーカーボタンが押された。</summary>
    public event EventHandler? NextMarkerRequested;
    public event EventHandler? SplitRequested;
    /// <summary>ドラッグなどの編集が終わった。</summary>
    public event EventHandler? TimelineChanged;
    /// <summary>クリップの移動・トリミングが実際に始まった（ドラッグの最初の動き）。アンドゥ用スナップショットを取る合図。</summary>
    public event EventHandler? EditStarted;
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

    private void AddMarkerButton_Click(object sender, RoutedEventArgs e)
        => AddMarkerRequested?.Invoke(this, EventArgs.Empty);

    private void PrevMarkerButton_Click(object sender, RoutedEventArgs e)
        => PreviousMarkerRequested?.Invoke(this, EventArgs.Empty);

    private void NextMarkerButton_Click(object sender, RoutedEventArgs e)
        => NextMarkerRequested?.Invoke(this, EventArgs.Empty);

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
        DrawTransitions(canvas);
        DrawExportRange(canvas, info.Width, info.Height);
        DrawMarkers(canvas, info.Width, info.Height);
        DrawPlayhead(canvas, info.Height);
    }

    /// <summary>トランジション区間を描く。境界を中心に前後 D/2 ずつの範囲を薄く色付けし、境界に線を引く。</summary>
    private void DrawTransitions(SKCanvas canvas)
    {
        if (Project is null)
            return;

        using var zonePaint = new SKPaint { Color = SKColors.White.WithAlpha(28) };
        using var linePaint = new SKPaint { Color = new SKColor(0xE8, 0xA3, 0x3D), StrokeWidth = 2, IsAntialias = true };

        for (var i = 0; i < Project.Tracks.Count; i++)
        {
            var track = Project.Tracks[i];
            var y = HeaderHeight + i * (TrackHeight + TrackGap);
            var clips = track.Clips.OrderBy(c => c.StartSeconds).ToList();
            for (var j = 1; j < clips.Count; j++)
            {
                var clip = clips[j];
                if (clip.TransitionKind != TransitionKind.Dissolve)
                    continue;
                var prev = clips[j - 1];
                var duration = TimelineEvaluator.GetEffectiveTransitionDuration(prev, clip);
                if (duration <= 0)
                    continue; // 隣り合っていない等、適用されない。
                var x0 = (float)((clip.StartSeconds - duration / 2) * PixelsPerSecond);
                var x1 = (float)((clip.StartSeconds + duration / 2) * PixelsPerSecond);
                canvas.DrawRect(new SKRect(x0, y + 4, x1, y + TrackHeight - 4), zonePaint);
                var bx = (float)(clip.StartSeconds * PixelsPerSecond);
                canvas.DrawLine(bx, y + 4, bx, y + TrackHeight - 4, linePaint);
            }
        }
    }

    /// <summary>書き出し範囲を描く。範囲外を暗くし、In/Out に線を引く。全体のときは描かない。</summary>
    private void DrawExportRange(SKCanvas canvas, int width, float height)
    {
        if (Project is null)
            return;
        var start = Project.EffectiveExportInSeconds;
        var end = Project.EffectiveExportOutSeconds;
        if (start <= 0 && end >= Project.DurationSeconds)
            return; // 全体が範囲のときは描かない。
        if (end <= start)
            return;

        var x0 = (float)(start * PixelsPerSecond);
        var x1 = (float)(end * PixelsPerSecond);
        using var dimPaint = new SKPaint { Color = SKColors.Black.WithAlpha(90) };
        if (x0 > 0)
            canvas.DrawRect(0, 0, x0, height, dimPaint);
        if (x1 < width)
            canvas.DrawRect(x1, 0, width - x1, height, dimPaint);

        using var inPaint = new SKPaint { Color = SKColors.LightGreen, StrokeWidth = 2 };
        using var outPaint = new SKPaint { Color = SKColors.OrangeRed, StrokeWidth = 2 };
        canvas.DrawLine(x0, 0, x0, height, inPaint);
        canvas.DrawLine(x1, 0, x1, height, outPaint);
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
            // ラベル重なり防止用の直前ラベル位置。2つの描画経路（検出拍／等間隔）で共有する。
            // （2経路は return で排他のため、1つの変数で十分。）
            var lastLabelX = -1000f;
            // 検出済みビートマップがあれば、その拍時刻を描く（BPMグリッドの代わり）。
            // 検出拍はオンセットに寄せてあるため、わずかなテンポの揺れもそのまま表示される。
            if (DetectedBeatMap is { BeatTimes.Count: > 0 } detected)
            {
                for (var i = 0; i < detected.BeatTimes.Count; i++)
                {
                    var t = detected.BeatTimes[i];
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

            // 毎フレーム List を確保しないよう、拍時刻は等間隔の計算で回す。
            // i は拍番号（0始まり）で、リスト版と一致する。
            var interval = BeatGridCalculator.BeatIntervalSeconds(Project.Bpm);
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
            ClipKind.Visualization => new SKColor(0x7E, 0x57, 0xC2),
            _ => new SKColor(0x55, 0x55, 0x55)
        };

        using var fillPaint = new SKPaint { Color = fill, IsAntialias = true };
        // 選択中は白く太く、複数選択中は琥珀色で枠を強調する。
        var isSelected = SelectedClip == clip;
        var isMultiSelected = _multiSelectedClips.Contains(clip);
        using var strokePaint = new SKPaint
        {
            Color = isSelected ? SKColors.White
                : isMultiSelected ? new SKColor(0xFF, 0xD1, 0x66)
                : SKColors.White.WithAlpha(80),
            StrokeWidth = isSelected || isMultiSelected ? 2.5f : 1f,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true
        };
        canvas.DrawRoundRect(rect, 4, 4, fillPaint);
        canvas.DrawRoundRect(rect, 4, 4, strokePaint);

        // グループ化されたクリップは上端にマゼンタの帯を引く（同じ帯同士が1グループ）。
        if (ClipGroups.IsGrouped(clip))
        {
            using var groupPaint = new SKPaint { Color = new SKColor(0xFF, 0x2E, 0x88), IsAntialias = true };
            canvas.DrawRect(new SKRect(rect.Left + 4, rect.Top + 2, rect.Right - 4, rect.Top + 5), groupPaint);
        }

        if (ShowWaveform && clip.Kind == ClipKind.Audio)
            DrawWaveform(canvas, clip, rect, visibleWidth);

        if (w > 30)
        {
            using var textPaint = CreateTextPaint(12, SKColors.White);
            // 速度変更クリップは名前の後ろに倍率を出す（等速は従来どおり名前だけ）。
            var label = Math.Abs(clip.Speed - 1.0) > 1e-9
                ? $"{clip.Name} ×{clip.Speed:0.##}"
                : clip.Name;
            canvas.DrawText(label, rect.Left + 6, rect.Top + 18, textPaint);
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
            if (clip.IsReversed)
                // 逆再生では波形も左右反転する（左端が素材の末尾側になる）。
                bucket = data.BucketCount - 1 - bucket;
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
            if (clip.IsReversed)
                bucket = data.BucketCount - 1 - bucket;
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

    /// <summary>
    /// マーカーを描く。ルーラーに下向きの三角の旗と名前ラベル、タイムライン全体に
    /// 薄い縦線を引く。選択中のマーカーは琥珀色で目立たせる。
    /// 名前ラベルは次のマーカーの旗や右端と重なりそうなときは描かない
    /// （ルーラーが文字で埋まるのを防ぐ）。
    /// </summary>
    private void DrawMarkers(SKCanvas canvas, int width, float height)
    {
        if (Project is null || Project.Markers.Count == 0)
            return;

        using var linePaint = new SKPaint
        {
            Color = new SKColor(0xFF, 0x2E, 0x88).WithAlpha(110),
            StrokeWidth = 1.5f,
            IsAntialias = true
        };
        using var selectedLinePaint = new SKPaint
        {
            Color = new SKColor(0xFF, 0xD1, 0x66).WithAlpha(180),
            StrokeWidth = 2,
            IsAntialias = true
        };
        using var flagPaint = new SKPaint { Color = new SKColor(0xFF, 0x2E, 0x88), IsAntialias = true };
        using var selectedFlagPaint = new SKPaint { Color = new SKColor(0xFF, 0xD1, 0x66), IsAntialias = true };
        using var textPaint = CreateTextPaint(11, new SKColor(0xFF, 0x6E, 0xB4));
        using var selectedTextPaint = CreateTextPaint(11, new SKColor(0xFF, 0xD1, 0x66));

        var markers = Project.Markers; // 時刻順が不変条件。
        for (var i = 0; i < markers.Count; i++)
        {
            var marker = markers[i];
            var x = (float)(marker.TimeSeconds * PixelsPerSecond);
            if (x < 0 || x > width)
                continue;

            var selected = SelectedMarker == marker;
            canvas.DrawLine(x, 0, x, height, selected ? selectedLinePaint : linePaint);

            // ルーラー内の旗（下向きの三角）。
            using var flag = new SKPath();
            flag.MoveTo(x - 6, 0);
            flag.LineTo(x + 6, 0);
            flag.LineTo(x, 11);
            flag.Close();
            canvas.DrawPath(flag, selected ? selectedFlagPaint : flagPaint);

            if (string.IsNullOrEmpty(marker.Name))
                continue;
            // 次のマーカーの旗より手前・右端より手前に収まるときだけ名前を描く。
            var nextX = i + 1 < markers.Count
                ? (float)(markers[i + 1].TimeSeconds * PixelsPerSecond)
                : float.MaxValue;
            var textWidth = (selected ? selectedTextPaint : textPaint).MeasureText(marker.Name);
            if (x + 8 + textWidth > Math.Min(width, nextX - 4))
                continue;
            canvas.DrawText(marker.Name, x + 8, 16, selected ? selectedTextPaint : textPaint);
        }
    }

    /// <summary>ルーラー上でクリックされた位置のマーカー（旗の左右6px以内）。無ければ null。</summary>
    private Marker? HitTestMarker(Point pos)
    {
        if (Project is null || pos.Y >= HeaderHeight)
            return null;
        foreach (var marker in Project.Markers)
        {
            var x = marker.TimeSeconds * PixelsPerSecond;
            if (Math.Abs(pos.X - x) <= 6)
                return marker;
        }
        return null;
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
        _editSnapshotTaken = false;

        if (pos.Y < HeaderHeight)
        {
            // マーカーの旗を掴んだらマーカーのドラッグ（シークにはしない）。
            var hitMarker = HitTestMarker(pos);
            if (hitMarker is not null)
            {
                _dragMode = DragMode.Marker;
                _dragMarker = hitMarker;
                MarkerSelected?.Invoke(this, hitMarker);
                return;
            }
            _dragMode = DragMode.Seek;
            SeekRequested?.Invoke(this, pos.X / PixelsPerSecond);
            return;
        }

        foreach (var (clip, rect) in _clipRects.Reverse())
        {
            if (!rect.Contains((float)pos.X, (float)pos.Y))
                continue;

            // Ctrl+クリックは複数選択の切り替え（ドラッグは始めない）。グループ化の対象を選ぶ操作。
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                ClipToggleRequested?.Invoke(this, clip);
                return;
            }

            ClipSelected?.Invoke(this, clip);
            CaptureDragOrigins(clip);
            if (Math.Abs(pos.X - rect.Right) <= 6)
            {
                _dragMode = DragMode.Trim;
                _dragClip = clip;
            }
            else if (Math.Abs(pos.X - rect.Left) <= 6 && rect.Width > 12)
            {
                _dragMode = DragMode.TrimStart;
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

    /// <summary>ドラッグ開始時点の状態を記録する（対象クリップ＋そのグループ全員＋リップル用の全クリップ開始位置）。</summary>
    private void CaptureDragOrigins(Clip clip)
    {
        _dragOrigins.Clear();
        _dragAllStarts.Clear();
        if (Project is null)
            return;
        var targets = ClipGroups.IsGrouped(clip)
            ? ClipGroups.GetClips(Project, clip.GroupId).ToList()
            : new List<Clip> { clip };
        if (!targets.Contains(clip))
            targets.Add(clip);
        foreach (var target in targets)
            _dragOrigins[target] = (target.StartSeconds, target.DurationSeconds, target.InPointSeconds);
        foreach (var any in Project.Tracks.SelectMany(t => t.Clips))
            _dragAllStarts[any] = any.StartSeconds;
    }

    /// <summary>ドラッグ対象（グループ含む）を開始時点の状態へ戻す。トリミング系は毎回のマウス移動で原点から適用し直す。</summary>
    private void RestoreDragOrigins()
    {
        foreach (var (clip, origin) in _dragOrigins)
        {
            clip.StartSeconds = origin.Start;
            clip.DurationSeconds = origin.Duration;
            clip.InPointSeconds = origin.InPoint;
        }
        foreach (var (clip, start) in _dragAllStarts)
            clip.StartSeconds = start;
    }

    /// <summary>
    /// スナップ候補を作る。マーカー・再生ヘッド・他クリップの端・先頭（SnapCalculator）に、
    /// 拍グリッド表示のときだけ拍（検出拍があればそちら、無ければ BPM グリッド）を
    /// 対象時刻の近傍だけ足す。スナップが無効なら null（呼び出し側は吸着しない）。
    /// </summary>
    private List<double>? BuildSnapCandidates(IReadOnlyCollection<Clip> excluded, double nearTimeSeconds, double nearTime2Seconds)
    {
        if (Project is null || SnapBox.IsChecked != true)
            return null;
        var candidates = SnapCalculator.CollectSnapTimes(Project, excluded, PlayheadSeconds);
        if (GridMode == GridMode.Beat)
        {
            var beats = DetectedBeatMap is { BeatTimes.Count: > 0 } detected
                ? detected.BeatTimes
                : BeatGridCalculator.GetBeatTimes(Project.Bpm, Project.BeatOffsetSeconds, Math.Max(Project.DurationSeconds, nearTime2Seconds + 1));
            candidates = SnapCalculator.WithNearbyBeatTimes(candidates, beats, nearTimeSeconds, SnapToleranceSeconds);
            candidates = SnapCalculator.WithNearbyBeatTimes(candidates, beats, nearTime2Seconds, SnapToleranceSeconds);
        }
        return candidates;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragMode == DragMode.Seek)
        {
            var seekPos = e.GetPosition(TimelineElement);
            SeekRequested?.Invoke(this, seekPos.X / PixelsPerSecond);
            return;
        }

        if (_dragMode == DragMode.Marker)
        {
            if (_dragMarker is null || Project is null)
                return;
            // 実際に動き始めたときだけ1回、アンドゥ用の合図を出す。
            if (!_editSnapshotTaken)
            {
                _editSnapshotTaken = true;
                EditStarted?.Invoke(this, EventArgs.Empty);
            }
            var markerPos = e.GetPosition(TimelineElement);
            _dragMarker.TimeSeconds = Math.Max(0, markerPos.X / PixelsPerSecond);
            Project.SortMarkers();
            InvalidateTimeline();
            return;
        }

        if (_dragMode == DragMode.None || _dragClip is null || Project is null)
            return;

        // 実際に動き始めたときだけ1回、アンドゥ用の合図を出す。
        // クリックだけ（動かない場合）ではスナップショットを積まない。
        if (!_editSnapshotTaken)
        {
            _editSnapshotTaken = true;
            EditStarted?.Invoke(this, EventArgs.Empty);
        }

        var pos = e.GetPosition(TimelineElement);
        var dragTargets = _dragOrigins.Keys.ToList();
        var grouped = dragTargets.Count > 1 || ClipGroups.IsGrouped(_dragClip);
        if (_dragMode == DragMode.Move)
        {
            var requestedStart = (pos.X - _dragOffsetX) / PixelsPerSecond;
            var origin = _dragOrigins[_dragClip];
            if (grouped)
            {
                // グループ移動：外接範囲の両端でスナップし、全員に同じ delta を適用する（相対位置を保つ）。
                var groupStart = dragTargets.Min(c => _dragOrigins[c].Start);
                var groupEnd = dragTargets.Max(c => _dragOrigins[c].Start + _dragOrigins[c].Duration);
                var desiredGroupStart = groupStart + (requestedStart - origin.Start);
                var candidates = BuildSnapCandidates(dragTargets, desiredGroupStart, desiredGroupStart + (groupEnd - groupStart));
                var snappedGroupStart = candidates is null
                    ? desiredGroupStart
                    : SnapCalculator.SnapMoveStart(desiredGroupStart, groupEnd - groupStart, candidates, SnapToleranceSeconds);
                var delta = Math.Max(snappedGroupStart - groupStart, -groupStart);
                foreach (var target in dragTargets)
                    target.StartSeconds = _dragOrigins[target].Start + delta;
            }
            else
            {
                var candidates = BuildSnapCandidates(dragTargets, requestedStart, requestedStart + _dragClip.DurationSeconds);
                var newStart = candidates is null
                    ? requestedStart
                    : SnapCalculator.SnapMoveStart(requestedStart, origin.Duration, candidates, SnapToleranceSeconds);
                TimelineEditor.MoveClip(_dragClip, newStart);
            }
        }
        else if (_dragMode == DragMode.Trim || _dragMode == DragMode.TrimStart)
        {
            // トリミングはドラッグ開始時点の状態から毎回計算し直す（行き過ぎて戻しても効く）。
            // Alt を押しながらのトリミングはリップル（後続が追随する。同トラックのみ・単体クリップのみ）。
            var ripple = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && !grouped;
            var track = Project.Tracks.FirstOrDefault(t => t.Clips.Contains(_dragClip));
            RestoreDragOrigins();
            try
            {
                if (_dragMode == DragMode.Trim)
                {
                    var requestedEnd = pos.X / PixelsPerSecond;
                    var candidates = BuildSnapCandidates(dragTargets, requestedEnd, requestedEnd);
                    var newEnd = candidates is null
                        ? requestedEnd
                        : SnapCalculator.SnapToNearest(requestedEnd, candidates, SnapToleranceSeconds) ?? requestedEnd;
                    if (ripple && track is not null)
                        TimelineEditor.RippleTrimClipEnd(track, _dragClip, newEnd);
                    else if (grouped && ClipGroups.GetBounds(Project, _dragClip.GroupId) is { } bounds
                        && Math.Abs(bounds.EndSeconds - _dragClip.EndSeconds) <= ClipGroups.EdgeToleranceSeconds)
                        ClipGroups.TrimGroupEnd(Project, _dragClip.GroupId, newEnd);
                    else
                        TimelineEditor.TrimClipEnd(_dragClip, newEnd);
                }
                else
                {
                    var requestedStart = pos.X / PixelsPerSecond;
                    var candidates = BuildSnapCandidates(dragTargets, requestedStart, requestedStart);
                    var newStart = candidates is null
                        ? requestedStart
                        : SnapCalculator.SnapToNearest(requestedStart, candidates, SnapToleranceSeconds) ?? requestedStart;
                    if (ripple && track is not null)
                        TimelineEditor.RippleTrimClipStart(track, _dragClip, newStart);
                    else if (grouped && ClipGroups.GetBounds(Project, _dragClip.GroupId) is { } bounds
                        && Math.Abs(bounds.StartSeconds - _dragClip.StartSeconds) <= ClipGroups.EdgeToleranceSeconds)
                        ClipGroups.TrimGroupStart(Project, _dragClip.GroupId, newStart);
                    else
                        TimelineEditor.TrimClipStart(_dragClip, newStart);
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                // 範囲外のトリミングは無視する（原点の状態のまま）。
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
        _dragMarker = null;
        _editSnapshotTaken = false;
        TimelineElement.ReleaseMouseCapture();
        InvalidateTimeline();
        if (wasSeek)
            SeekDragFinished?.Invoke(this, EventArgs.Empty);
    }
}
