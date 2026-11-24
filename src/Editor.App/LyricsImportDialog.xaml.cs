// 歌詞の一括流し込みの設定ダイアログ。開始位置・間隔・長さ・空行スキップを入力する。
// 検証は LyricsImportOptions.Validate と同じ条件（開始位置は 0 以上、間隔・長さは 0 より大きい）。
// 不正な入力ではエラーを出してダイアログを開いたままにする。

using System.Globalization;
using System.Windows;
using Editor.Core;

namespace Editor.App;

public partial class LyricsImportDialog : Window
{
    public LyricsImportDialog(string fileName, double playheadSeconds)
    {
        InitializeComponent();
        FileNameText.Text = $"ファイル：{fileName}";
        StartBox.Text = playheadSeconds.ToString("F2", CultureInfo.InvariantCulture);
        IntervalBox.Text = "3";
        DurationBox.Text = "3";
        SkipEmptyCheck.IsChecked = true;
    }

    /// <summary>入力された設定。OK で閉じたときだけ有効。</summary>
    public LyricsImportOptions Options { get; private set; } = new();

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseSeconds(StartBox.Text, out var start))
        {
            ShowError("開始位置は数値（秒）で入力してください。");
            return;
        }
        if (start < 0)
        {
            ShowError("開始位置は 0 以上の秒数にしてください。");
            return;
        }
        if (!TryParseSeconds(IntervalBox.Text, out var interval))
        {
            ShowError("配置間隔は数値（秒）で入力してください。");
            return;
        }
        if (interval <= 0)
        {
            ShowError("配置間隔は 0 より大きい秒数にしてください。");
            return;
        }
        if (!TryParseSeconds(DurationBox.Text, out var duration))
        {
            ShowError("1クリップの長さは数値（秒）で入力してください。");
            return;
        }
        if (duration <= 0)
        {
            ShowError("1クリップの長さは 0 より大きい秒数にしてください。");
            return;
        }

        Options = new LyricsImportOptions
        {
            StartSeconds = start,
            IntervalSeconds = interval,
            ClipDurationSeconds = duration,
            SkipEmptyLines = SkipEmptyCheck.IsChecked == true
        };
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>
    /// 秒数をパースする。小数点は「.」で入力する（"," は桁区切りと解釈されて
    /// "3,5" が 35 になるのを防ぐため、InvariantCulture だけを使う）。
    /// </summary>
    private static bool TryParseSeconds(string text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private void ShowError(string message)
        => MessageBox.Show(this, message, "入力エラー", MessageBoxButton.OK, MessageBoxImage.Error);
}
