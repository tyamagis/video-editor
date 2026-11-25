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
        Closing += MainWindow_Closing;
        KeyDown += MainWindow_KeyDown;
    }

    /// <summary>終了時に自動保存する（未保存の編集が残っていれば、自動保存ファイルに残す）。</summary>
    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _viewModel?.SaveAutosaveNow();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel = (MainViewModel)DataContext;
        BuildAutosaveIntervalMenu();

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
        TimelineControl.ClipToggleRequested += (_, clip) => _viewModel.ToggleClipSelection(clip);
        TimelineControl.MarkerSelected += (_, marker) => _viewModel.SelectMarker(marker);
        TimelineControl.AddMarkerRequested += (_, _) => _viewModel.AddMarkerAtPlayhead();
        TimelineControl.PreviousMarkerRequested += (_, _) => _viewModel.JumpToPreviousMarker();
        TimelineControl.NextMarkerRequested += (_, _) => _viewModel.JumpToNextMarker();
        TimelineControl.SplitRequested += (_, _) => _viewModel.SplitAtPlayhead();
        TimelineControl.EditStarted += (_, _) => _viewModel.BeginEdit();
        TimelineControl.TimelineChanged += (_, _) =>
        {
            _viewModel.NotifyTimelineChanged();
            // マーカーのドラッグなどで時刻が変わったとき、一覧の表示も合わせる。
            _viewModel.RefreshMarkerRows();
        };
        TimelineControl.GridModeChanged += (_, _) => _viewModel.GridMode = TimelineControl.GridMode;
        TimelineControl.ShowWaveformChanged += (_, _) => _viewModel.ShowWaveform = TimelineControl.ShowWaveform;

        PreviewControl.SpectrumBandsProvider = () => _viewModel.GetSpectrumBands();
        PreviewControl.StereoWindowProvider = () => _viewModel.GetStereoWindow();

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
        else if (e.PropertyName == nameof(MainViewModel.DetectedBeatMap))
        {
            TimelineControl.DetectedBeatMap = _viewModel.DetectedBeatMap;
            TimelineControl.InvalidateTimeline();
        }
        else if (e.PropertyName == nameof(MainViewModel.SelectedClip))
        {
            TimelineControl.SelectedClip = _viewModel.SelectedClip;
            TimelineControl.InvalidateTimeline();
        }
        else if (e.PropertyName == nameof(MainViewModel.MultiSelectedClips))
        {
            TimelineControl.MultiSelectedClips = _viewModel.MultiSelectedClips;
            TimelineControl.InvalidateTimeline();
        }
        else if (e.PropertyName == nameof(MainViewModel.SelectedMarker))
        {
            TimelineControl.SelectedMarker = _viewModel.SelectedMarker?.Marker;
            TimelineControl.InvalidateTimeline();
        }
        else if (e.PropertyName == nameof(MainViewModel.ShowSpectrum))
        {
            PreviewControl.ShowSpectrum = _viewModel.ShowSpectrum;
            PreviewControl.InvalidatePreview();
        }
        else if (e.PropertyName == nameof(MainViewModel.ShowLissajous))
        {
            PreviewControl.ShowLissajous = _viewModel.ShowLissajous;
            PreviewControl.InvalidatePreview();
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
        // テキスト入力中・コンボボックス操作中・チェックボックス操作中はショートカットを無効化する。
        if (Keyboard.FocusedElement is TextBox or ComboBox or CheckBox)
            return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && e.Key == Key.G)
            _viewModel.UngroupSelectedClip();
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.G)
            _viewModel.GroupSelectedClips();
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.S)
            SaveProjectOrShowDialog();
        else if (e.Key == Key.S)
            _viewModel.SplitAtPlayhead();
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && e.Key == Key.Delete)
            _viewModel.RippleDeleteSelectedClip();
        else if (e.Key == Key.Delete)
            _viewModel.DeleteSelectedClip();
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.M)
            _viewModel.JumpToPreviousMarker();
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && e.Key == Key.M)
            _viewModel.JumpToNextMarker();
        else if (e.Key == Key.M)
            _viewModel.AddMarkerAtPlayhead();
        else if (e.Key == Key.I)
            _viewModel.SetExportInAtPlayhead();
        else if (e.Key == Key.O)
            _viewModel.SetExportOutAtPlayhead();
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Z)
            _viewModel.Undo();
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Y)
            _viewModel.Redo();
    }

    /// <summary>Space で再生・停止を切り替える。テキスト入力中とコンボボックス操作中、チェックボックス操作中は除外する。</summary>
    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is null)
            return;
        if (e.Key == Key.Space && Keyboard.FocusedElement is not TextBox and not ComboBox and not CheckBox)
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

    private void SaveMenu_Click(object sender, RoutedEventArgs e) => SaveProjectOrShowDialog();

    /// <summary>
    /// プロジェクトを保存する。保存先が決まっていればそのまま保存し（ダイアログは出さない）、
    /// 未保存のプロジェクトの場合は名前を付けて保存のダイアログを出す。
    /// </summary>
    private void SaveProjectOrShowDialog()
    {
        if (_viewModel.CurrentProjectPath is not null)
        {
            try
            {
                _viewModel.SaveProject(_viewModel.CurrentProjectPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存できませんでした：{ex.Message}", "エラー",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return;
        }
        ShowSaveDialogAndSave();
    }

    private void SaveAsMenu_Click(object sender, RoutedEventArgs e) => ShowSaveDialogAndSave();

    /// <summary>名前を付けて保存のダイアログを出し、選ばれた場所に保存する。</summary>
    private void ShowSaveDialogAndSave()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "VideoEditor プロジェクト|*.veproj",
            FileName = $"{_viewModel.Project.Name}.veproj"
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            _viewModel.SaveProject(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存できませんでした：{ex.Message}", "エラー",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>自動保存ファイルの内容でプロジェクトを復旧する。</summary>
    private void RecoverAutosaveMenu_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _viewModel.RecoverFromAutosave();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"復旧できませんでした：{ex.Message}", "エラー",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>自動保存ファイルを破棄する（復旧しないと決めたとき）。</summary>
    private void DiscardAutosaveMenu_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.DiscardAutosave();
    }

    /// <summary>自動保存の間隔メニュー（1/2/5/10/15分）を作る。今の間隔にチェックを付ける。</summary>
    private void BuildAutosaveIntervalMenu()
    {
        foreach (var minutes in new[] { 1, 2, 5, 10, 15 })
        {
            var item = new MenuItem
            {
                Header = $"{minutes}分",
                Tag = minutes,
                IsCheckable = true,
                IsChecked = _viewModel.AutosaveIntervalMinutes == minutes
            };
            item.Click += (_, _) =>
            {
                _viewModel.SetAutosaveIntervalMinutes((int)item.Tag);
                RefreshAutosaveIntervalChecks();
            };
            AutosaveIntervalMenu.Items.Add(item);
        }
    }

    /// <summary>自動保存の間隔メニューのチェックを、今の間隔に合わせ直す。</summary>
    private void RefreshAutosaveIntervalChecks()
    {
        foreach (var obj in AutosaveIntervalMenu.Items)
        {
            if (obj is MenuItem item && item.Tag is int minutes)
                item.IsChecked = _viewModel.AutosaveIntervalMinutes == minutes;
        }
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

    /// <summary>
    /// 現在のプロジェクトをテンプレートとして保存する。名前をダイアログで聞き、
    /// 同名のテンプレートがあれば上書きの確認を取ってから保存する。
    /// </summary>
    private void SaveTemplateMenu_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveTemplateDialog(_viewModel.Project.Name) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        var name = dialog.TemplateName;
        try
        {
            if (_viewModel.TemplateExists(name))
            {
                var confirm = MessageBox.Show(this,
                    $"テンプレート「{name}」は既にあります。上書きしますか？",
                    "テンプレートの保存", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes)
                    return;
            }
            var result = _viewModel.SaveAsTemplate(name);
            if (result == SaveTemplateResult.InvalidName)
            {
                MessageBox.Show(this, "テンプレート名が正しくありません。", "エラー",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            _viewModel.StatusMessage = result == SaveTemplateResult.Overwritten
                ? $"テンプレートを上書きしました：{name}"
                : $"テンプレートとして保存しました：{name}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"テンプレートを保存できませんでした：{ex.Message}", "エラー",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// テンプレートを選んで新しいプロジェクトを作る（未保存扱い）。
    /// 参照メディアが見つからないクリップは配置だけ残るので、その旨を警告する。
    /// テンプレートが壊れている場合はエラーを出して今のプロジェクトは変えない。
    /// </summary>
    private void NewFromTemplateMenu_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TemplateSelectDialog { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedTemplate is null)
            return;
        try
        {
            var result = _viewModel.NewProjectFromTemplate(dialog.SelectedTemplate.Path);
            if (result.MissingMediaPaths.Count > 0)
            {
                var shown = result.MissingMediaPaths.Take(10).ToList();
                var more = result.MissingMediaPaths.Count > 10
                    ? $"\n他 {result.MissingMediaPaths.Count - 10} 件"
                    : string.Empty;
                MessageBox.Show(this,
                    $"次のメディアファイルが見つからなかったため、該当クリップは配置だけ残しています。\n{string.Join("\n", shown)}{more}\n\n素材ファイルを元の場所に置き直すか、不要なクリップを削除してください。",
                    "メディアが見つかりません", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"テンプレートから作成できませんでした：{ex.Message}", "エラー",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ExportMenu_Click(object sender, RoutedEventArgs e)
    {
        var presetStore = ExportPresetStore.Load();
        var exportDialog = new ExportDialog(presetStore) { Owner = this };
        if (exportDialog.ShowDialog() != true)
            return;

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
            await _viewModel.ExportTimelineAsync(dialog.FileName, exportDialog.Settings, progress)
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

    private void RippleDeleteMenu_Click(object sender, RoutedEventArgs e) => _viewModel.RippleDeleteSelectedClip();

    private void RippleDeleteAllTracksMenu_Click(object sender, RoutedEventArgs e) => _viewModel.RippleDeleteSelectedClip(allTracks: true);

    private void GroupMenu_Click(object sender, RoutedEventArgs e) => _viewModel.GroupSelectedClips();

    private void UngroupMenu_Click(object sender, RoutedEventArgs e) => _viewModel.UngroupSelectedClip();

    private void AddMarkerMenu_Click(object sender, RoutedEventArgs e) => _viewModel.AddMarkerAtPlayhead();

    private void PrevMarkerMenu_Click(object sender, RoutedEventArgs e) => _viewModel.JumpToPreviousMarker();

    private void NextMarkerMenu_Click(object sender, RoutedEventArgs e) => _viewModel.JumpToNextMarker();

    /// <summary>
    /// 歌詞ファイルを選び、設定ダイアログで開始位置・間隔・長さを決めて一括流し込みする。
    /// ファイル選択・設定のどちらかをキャンセルすれば何もしない。
    /// 読み込み失敗（ファイルが無い・空・文字コード不明）はエラーを出して何も配置しない。
    /// </summary>
    private void ImportLyricsMenu_Click(object sender, RoutedEventArgs e)
    {
        var fileDialog = new OpenFileDialog
        {
            Filter = "テキストファイル|*.txt;*.lrc|すべてのファイル|*.*"
        };
        if (fileDialog.ShowDialog() != true)
            return;

        var dialog = new LyricsImportDialog(fileDialog.SafeFileName, _viewModel.PlayheadSeconds)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            _viewModel.ImportLyrics(fileDialog.FileName, dialog.Options);
        }
        catch (LyricsImportException ex)
        {
            MessageBox.Show(ex.Message, "歌詞を流し込めませんでした",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"予期しないエラーが発生しました：{ex.Message}", "エラー",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AddMarkerButton_Click(object sender, RoutedEventArgs e) => _viewModel.AddMarkerAtPlayhead();

    private void PrevMarkerButton_Click(object sender, RoutedEventArgs e) => _viewModel.JumpToPreviousMarker();

    private void NextMarkerButton_Click(object sender, RoutedEventArgs e) => _viewModel.JumpToNextMarker();

    private void DeleteMarkerButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MarkerRow row })
            _viewModel.DeleteMarker(row);
    }

    private void UndoMenu_Click(object sender, RoutedEventArgs e) => _viewModel.Undo();

    private void RedoMenu_Click(object sender, RoutedEventArgs e) => _viewModel.Redo();

    private void AddKeyframeButton_Click(object sender, RoutedEventArgs e)
        => _viewModel.AddKeyframeAtPlayhead();

    private void DeleteKeyframeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: KeyframeRow row })
            _viewModel.DeleteKeyframe(row);
    }

    private void AddEffectButton_Click(object sender, RoutedEventArgs e)
        => _viewModel.AddEffect(_viewModel.NewEffectKind);

    private void AddVisualizationClipButton_Click(object sender, RoutedEventArgs e)
        => _viewModel.AddVisualizationClip(_viewModel.NewVisualizationKind);

    private void DeleteEffectButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: EffectRow row })
            _viewModel.DeleteEffect(row);
    }

    /// <summary>LUTエフェクトの .cube ファイルを選ぶ。キャンセルすれば何もしない。</summary>
    private void ChooseLutFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: EffectRow row })
            return;
        var dialog = new OpenFileDialog
        {
            Filter = "3D LUT|*.cube|すべてのファイル|*.*",
            FileName = row.LutPath
        };
        if (dialog.ShowDialog() == true)
            row.LutPath = dialog.FileName;
    }

    private void AddAudioBindingButton_Click(object sender, RoutedEventArgs e)
        => _viewModel.AddAudioBinding();

    private void DeleteAudioBindingButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AudioBindingRow row })
            _viewModel.DeleteAudioBinding(row);
    }

    /// <summary>
    /// インスペクタや BPM の入力欄にフォーカスが入ったときに、アンドゥ用の
    /// スナップショットを取る。入力欄はキー入力のたびに直接モデルを変えるため、
    /// フォーカス時の一回だけが「1回の編集」になる。
    /// </summary>
    private void EditableField_GotFocus(object sender, RoutedEventArgs e) => _viewModel.BeginEdit();

    /// <summary>BPM自動検出を実行する（非同期。検出中はボタンが無効になる）。</summary>
    private async void DetectBpmButton_Click(object sender, RoutedEventArgs e)
        => await _viewModel.DetectBpmAsync().ConfigureAwait(true);

    /// <summary>保留中の検出結果をプロジェクトへ反映する。</summary>
    private void ApplyDetectedBpmButton_Click(object sender, RoutedEventArgs e)
        => _viewModel.ApplyDetectedBpm();

    /// <summary>選択中クリップの無音区間を検出する（非同期。検出中はボタンが無効になる）。</summary>
    private async void DetectSilenceButton_Click(object sender, RoutedEventArgs e)
        => await _viewModel.DetectSilenceAsync().ConfigureAwait(true);

    /// <summary>検出した無音区間を切り取る。</summary>
    private void ApplySilenceCutButton_Click(object sender, RoutedEventArgs e)
        => _viewModel.ApplySilenceCut();

    private void PreviewResBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_viewModel is null || PreviewResBox.SelectedIndex < 0)
            return;
        _viewModel.PreviewResolution = (PreviewResolution)PreviewResBox.SelectedIndex;
    }
}
