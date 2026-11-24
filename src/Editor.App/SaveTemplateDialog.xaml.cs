// テンプレートとして保存するダイアログ。名前を入力するだけの小さなダイアログ。
// 名前の検証は TemplateStore.ValidateName と同じ条件。不正な入力では
// エラーを出してダイアログを開いたままにする。

using System.Windows;
using Editor.Core;

namespace Editor.App;

public partial class SaveTemplateDialog : Window
{
    public SaveTemplateDialog(string defaultName)
    {
        InitializeComponent();
        TemplateNameBox.Text = defaultName;
        TemplateNameBox.SelectAll();
        TemplateNameBox.Focus();
    }

    /// <summary>入力されたテンプレート名。OK で閉じたときだけ有効。</summary>
    public string TemplateName { get; private set; } = string.Empty;

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var error = TemplateStore.ValidateName(TemplateNameBox.Text);
        if (error is not null)
        {
            MessageBox.Show(this, error, "入力エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        TemplateName = TemplateNameBox.Text.Trim();
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
