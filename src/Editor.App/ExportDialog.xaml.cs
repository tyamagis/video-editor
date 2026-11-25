// MP4 書き出しダイアログ。書き出し設定（解像度・フレームレート・CRF・
// エンコーダープリセット・音声ビットレート）の入力と、プリセットの選択・保存・削除を行う。
// プリセットは ExportPresetStore（%APPDATA%\VideoEditor\export-presets.json）で管理する。
// 書き出し自体は MainWindow 側で、ここで決まった Settings を使って行う。
// 不正な入力ではエラーを出してダイアログを開いたままにする。

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Editor.Core;
using Editor.Media;

namespace Editor.App;

public partial class ExportDialog : Window
{
    private readonly ExportPresetStore _store;
    private bool _refreshingPresetList;

    public ExportDialog(ExportPresetStore store)
    {
        InitializeComponent();
        _store = store;
        EncoderPresetBox.ItemsSource = ExportSettings.EncoderPresets;
        EncoderPresetBox.SelectedItem = "medium";
        RefreshPresetList(_store.LastSelectedPresetName);
    }

    /// <summary>入力された書き出し設定。OK で閉じたときだけ有効。</summary>
    public ExportSettings Settings { get; private set; } = ExportSettings.Hd30;

    /// <summary>選択中のプリセット（無ければ null）。</summary>
    private ExportPreset? SelectedPreset
        => PresetBox.SelectedItem is string name ? _store.FindPreset(name) : null;

    private void PresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingPresetList)
            return;
        var preset = SelectedPreset;
        if (preset is null)
            return;
        ApplySettings(preset.Settings);
        PresetNameBox.Text = preset.Name;
        RefreshDeleteButton();
    }

    private void ApplySettings(ExportSettings settings)
    {
        WidthBox.Text = settings.Width.ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = settings.Height.ToString(CultureInfo.InvariantCulture);
        FrameRateBox.Text = settings.FrameRate.ToString(CultureInfo.InvariantCulture);
        CrfBox.Text = settings.Crf.ToString(CultureInfo.InvariantCulture);
        EncoderPresetBox.SelectedItem = settings.Preset;
        AudioBitrateBox.Text = settings.AudioBitrateKbps.ToString(CultureInfo.InvariantCulture);
    }

    private void SavePresetButton_Click(object sender, RoutedEventArgs e)
    {
        var name = PresetNameBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowError("プリセット名を入力してください。");
            return;
        }
        if (!TryBuildSettings(out var settings))
            return;

        var existing = _store.FindPreset(name);
        if (existing is not null && !existing.IsBuiltIn)
        {
            var confirm = MessageBox.Show(this,
                $"「{name}」は既にあります。上書きしますか？",
                "プリセットの上書き", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
                return;
        }

        var result = _store.SavePreset(new ExportPreset(name, settings));
        switch (result)
        {
            case SavePresetResult.BuiltInNameConflict:
                ShowError($"「{name}」は既定プリセットと同じ名前です。別の名前を付けてください。");
                return;
            case SavePresetResult.Invalid:
                ShowError("プリセット名または設定値が不正です。");
                return;
        }

        try
        {
            _store.Save();
        }
        catch (Exception ex)
        {
            ShowError($"プリセットを保存できませんでした：{ex.Message}");
            return;
        }
        RefreshPresetList(name);
    }

    private void DeletePresetButton_Click(object sender, RoutedEventArgs e)
    {
        var preset = SelectedPreset;
        if (preset is null || preset.IsBuiltIn)
            return;
        var confirm = MessageBox.Show(this,
            $"プリセット「{preset.Name}」を削除しますか？",
            "プリセットの削除", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;
        if (!_store.TryDeletePreset(preset.Name))
        {
            ShowError($"プリセット「{preset.Name}」を削除できませんでした。");
            return;
        }
        try
        {
            _store.Save();
        }
        catch (Exception ex)
        {
            ShowError($"プリセットを保存できませんでした：{ex.Message}");
            return;
        }
        PresetNameBox.Text = string.Empty;
        RefreshPresetList(null);
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildSettings(out var settings))
            return;
        Settings = settings;
        // 次回ダイアログを開いたときに同じプリセットが選ばれるように覚えておく。
        // 保存に失敗しても書き出し自体は続ける（選択の復元は必須ではないため）。
        _store.LastSelectedPresetName = SelectedPreset?.Name;
        try
        {
            _store.Save();
        }
        catch
        {
            // 書き出しを止めない。
        }
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>プリセット一覧をストアから作り直し、指定した名前（無ければ先頭）を選択する。</summary>
    private void RefreshPresetList(string? selectName)
    {
        _refreshingPresetList = true;
        try
        {
            var names = _store.Presets.Select(p => p.Name).ToList();
            PresetBox.ItemsSource = names;
            var index = selectName is null
                ? 0
                : names.FindIndex(n => string.Equals(n, selectName, StringComparison.OrdinalIgnoreCase));
            PresetBox.SelectedIndex = index >= 0 ? index : 0;
        }
        finally
        {
            _refreshingPresetList = false;
        }
        // SelectionChanged は _refreshingPresetList 中に抑制されるため、ここで明示的に反映する。
        var preset = SelectedPreset;
        if (preset is not null)
        {
            ApplySettings(preset.Settings);
            PresetNameBox.Text = preset.Name;
        }
        RefreshDeleteButton();
    }

    /// <summary>削除ボタンはユーザー作成プリセットを選んでいるときだけ有効にする。</summary>
    private void RefreshDeleteButton()
    {
        var preset = SelectedPreset;
        DeletePresetButton.IsEnabled = preset is not null && !preset.IsBuiltIn;
    }

    /// <summary>
    /// 入力欄から書き出し設定を作る。数値でない・範囲外の場合はエラーを出して false を返す。
    /// 範囲チェックは ExportSettings.Validate と同じ条件。
    /// </summary>
    private bool TryBuildSettings(out ExportSettings settings)
    {
        settings = ExportSettings.Hd30;
        if (!TryParseInt(WidthBox.Text, out var width)
            || !TryParseInt(HeightBox.Text, out var height)
            || !TryParseInt(FrameRateBox.Text, out var frameRate)
            || !TryParseInt(CrfBox.Text, out var crf)
            || !TryParseInt(AudioBitrateBox.Text, out var audioBitrateKbps))
        {
            ShowError("解像度・フレームレート・CRF・音声ビットレートは整数で入力してください。");
            return false;
        }
        var encoderPreset = EncoderPresetBox.SelectedItem as string ?? "medium";
        settings = new ExportSettings(width, height, frameRate, crf, encoderPreset, audioBitrateKbps);
        try
        {
            settings.Validate();
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or ArgumentException)
        {
            ShowError(ex.Message);
            return false;
        }
        return true;
    }

    private static bool TryParseInt(string text, out int value)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private void ShowError(string message)
        => MessageBox.Show(this, message, "入力エラー", MessageBoxButton.OK, MessageBoxImage.Error);
}
