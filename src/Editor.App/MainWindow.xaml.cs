// メインウィンドウ。ファイルダイアログと、ViewModel・タイムライン・
// プレビュー間の配線だけを担う。編集ロジックは ViewModel / Core 側にある。

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Editor.Core;
using Editor.Media;
using Editor.Render;
using Microsoft.Win32;

namespace Editor.App;

public partial class MainWindow : Window
{
    private MainViewModel _viewModel = null!;
    private bool _rendering;
    private Point _mediaDragStart;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        KeyDown += MainWindow_KeyDown;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel = (MainViewModel)DataContext;

        TimelineControl.Project = _viewModel.Project;
        TimelineControl.GridMode = _viewModel.GridMode;
        TimelineControl.ShowWaveform = _viewModel.ShowWaveform;
        TimelineControl.WaveformProvider = clip => _viewModel.GetWaveform(clip.SourcePath);

        TimelineControl.SeekRequested += (_, time) => _viewModel.PlayheadSeconds = time;
        TimelineControl.SeekDragFinished += (_, _) => _viewModel.RestartAudioIfPlaying();
        TimelineControl.MediaDropped += (_, drop) =>
        {
            var item = _viewModel.MediaItems.FirstOrDefault(m => m.SourcePath == drop.SourcePath);
            if (item is not null)
                _viewModel.AddMediaToTimeline(item, drop.TimeSeconds);
        };
        TimelineControl.ClipSelected += (_, clip) => _viewModel.SelectedClip = clip;
        TimelineControl.SplitRequested += (_, _) => _viewModel.SplitAtPlayhead();
        TimelineControl.TimelineChanged += (_, _) => _viewModel.NotifyTimelineChanged();
        TimelineControl.GridModeChanged += (_, _) => _viewModel.GridMode = TimelineControl.GridMode;
        TimelineControl.ShowWaveformChanged += (_, _) => _viewModel.ShowWaveform = TimelineControl.ShowWaveform;

        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.PreviewInvalidated += async (_, _) => await UpdatePreviewAsync().ConfigureAwait(true);

        if (!await _viewModel.CheckFfmpegAsync().ConfigureAwait(true))
        {
            MessageBox.Show(
                "ffmpeg が見つかりません。ffmpeg をインストールし、PATHを通してから使ってください。\n" +
                "読み込み・書き出しは ffmpeg が無いと動きません。",
                "ffmpeg が見つかりません", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        await UpdatePreviewAsync().ConfigureAwait(true);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.PlayheadSeconds))
        {
            TimelineControl.PlayheadSeconds = _viewModel.PlayheadSeconds;
            TimelineControl.InvalidateTimeline();
        }
        else if (e.PropertyName == nameof(MainViewModel.Project))
        {
            TimelineControl.Project = _viewModel.Project;
            TimelineControl.InvalidateTimeline();
        }
        else if (e.PropertyName == nameof(MainViewModel.GridMode))
        {
            TimelineControl.GridMode = _viewModel.GridMode;
        }
    }

    private async Task UpdatePreviewAsync()
    {
        if (_rendering)
            return;
        _rendering = true;
        try
        {
            var maxSize = GetPreviewDisplaySize();
            var bitmap = await _viewModel.RenderPreviewAsync(_viewModel.PlayheadSeconds, maxSize).ConfigureAwait(true);
            PreviewControl.Bitmap = bitmap;
            PreviewControl.InvalidatePreview();
        }
        catch (Exception ex)
        {
            _viewModel.StatusMessage = $"プレビュー描画に失敗しました：{ex.Message}";
        }
        finally
        {
            _rendering = false;
        }
    }

    /// <summary>プレビュー表示の実寸（物理ピクセル）。画面より大きく描かないための上限。</summary>
    private (int Width, int Height)? GetPreviewDisplaySize()
    {
        if (PreviewControl.ActualWidth <= 0 || PreviewControl.ActualHeight <= 0)
            return null;
        var dpi = VisualTreeHelper.GetDpi(PreviewControl);
        var w = (int)(PreviewControl.ActualWidth * dpi.PixelsPerDip);
        var h = (int)(PreviewControl.ActualHeight * dpi.PixelsPerDip);
        return (Math.Max(2, w), Math.Max(2, h));
    }

    private void MainWindow_KeyDown(object sender, KeyEventArgs e)
    {
        // テキスト入力中・コンボボックス操作中はショートカットを無効化する。
        if (Keyboard.FocusedElement is TextBox or ComboBox)
            return;
        if (e.Key == Key.S)
            _viewModel.SplitAtPlayhead();
        else if (e.Key == Key.Delete)
            _viewModel.DeleteSelectedClip();
        else if (e.Key == Key.I)
            _viewModel.SetExportInAtPlayhead();
        else if (e.Key == Key.O)
            _viewModel.SetExportOutAtPlayhead();
    }

    /// <summary>Space で再生・停止を切り替える。テキスト入力中とコンボボックス操作中は除外する。</summary>
    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is null)
            return;
        if (e.Key == Key.Space && Keyboard.FocusedElement is not TextBox and not ComboBox)
        {
            _viewModel.IsPlaying = !_viewModel.IsPlaying;
            e.Handled = true;
        }
    }

    private async void ImportMenu_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "動画・音声|*.mp4;*.mov;*.mkv;*.wav;*.mp3;*.m4a;*.aac|画像|*.png;*.jpg;*.jpeg|すべてのファイル|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog() != true)
            return;

        foreach (var file in dialog.FileNames)
        {
            try
            {
                await _viewModel.AddMediaAsync(file).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{file} の読み込みに失敗しました：{ex.Message}",
                    "読み込みエラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void MediaList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.SelectedMediaItem is not null)
            _viewModel.AddMediaToTimeline(_viewModel.SelectedMediaItem);
    }

    private void MediaItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: MediaItem item })
            _viewModel.AddMediaToTimeline(item);
    }

    private void AddToTimelineButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedMediaItem is { } item)
            _viewModel.AddMediaToTimeline(item);
    }

    private void MediaList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _mediaDragStart = e.GetPosition(null);
    }

    private void MediaList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _mediaDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - _mediaDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        if (_viewModel.SelectedMediaItem is not { } item)
            return;
        DragDrop.DoDragDrop((DependencyObject)sender,
            new DataObject(DataFormats.StringFormat, item.SourcePath), DragDropEffects.Copy);
    }

    private void SaveMenu_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "VideoEditor プロジェクト|*.veproj",
            FileName = $"{_viewModel.Project.Name}.veproj"
        };
        if (dialog.ShowDialog() == true)
            _viewModel.SaveProject(dialog.FileName);
    }

    private void LoadMenu_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "VideoEditor プロジェクト|*.veproj" };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            _viewModel.LoadProject(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"開けませんでした：{ex.Message}", "エラー",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ExportMenu_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "MP4 動画|*.mp4",
            FileName = $"{_viewModel.Project.Name}.mp4"
        };
        if (dialog.ShowDialog() != true)
            return;

        _viewModel.StatusMessage = "書き出しています…";
        try
        {
            var progress = new Progress<double>(p =>
                _viewModel.StatusMessage = $"書き出し中… {p:P0}");
            await _viewModel.ExportTimelineAsync(dialog.FileName, ExportSettings.Hd30, progress)
                .ConfigureAwait(true);
            _viewModel.StatusMessage = $"書き出しました：{dialog.FileName}";
            MessageBox.Show($"書き出しました：{dialog.FileName}", "書き出し完了",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _viewModel.StatusMessage = $"書き出しに失敗しました：{ex.Message}";
            MessageBox.Show($"書き出しに失敗しました：{ex.Message}", "エラー",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportInMenu_Click(object sender, RoutedEventArgs e) => _viewModel.SetExportInAtPlayhead();

    private void ExportOutMenu_Click(object sender, RoutedEventArgs e) => _viewModel.SetExportOutAtPlayhead();

    private void ExportRangeClearMenu_Click(object sender, RoutedEventArgs e) => _viewModel.ClearExportRange();

    private void SplitMenu_Click(object sender, RoutedEventArgs e) => _viewModel.SplitAtPlayhead();

    private void DeleteMenu_Click(object sender, RoutedEventArgs e) => _viewModel.DeleteSelectedClip();

    private void PreviewResBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_viewModel is null || PreviewResBox.SelectedIndex < 0)
            return;
        _viewModel.PreviewResolution = (PreviewResolution)PreviewResBox.SelectedIndex;
    }
}
