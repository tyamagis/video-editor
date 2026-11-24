// テンプレートから新規作成するダイアログ。テンプレート一覧（名前＋作成日時）から
// 選んで新規作成する。壊れたテンプレートは「読み込めません」と表示され、選んでも
// 作成できない（エラーを出してダイアログを開いたままにする）。
// 削除ボタンで選んだテンプレートを削除できる（確認あり）。

using System.Windows;
using System.Windows.Input;
using Editor.Core;

namespace Editor.App;

public partial class TemplateSelectDialog : Window
{
    public TemplateSelectDialog()
    {
        InitializeComponent();
        RefreshTemplates();
    }

    /// <summary>選ばれたテンプレート。OK で閉じたときだけ有効。</summary>
    public TemplateInfo? SelectedTemplate { get; private set; }

    private void RefreshTemplates()
    {
        var templates = TemplateStore.List();
        TemplateList.ItemsSource = templates;
        EmptyText.Visibility = templates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => TryConfirm();

    private void TemplateList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => TryConfirm();

    private void TryConfirm()
    {
        if (TemplateList.SelectedItem is not TemplateInfo selected)
        {
            MessageBox.Show(this, "テンプレートを選んでください。", "テンプレートから新規作成",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!selected.IsValid)
        {
            MessageBox.Show(this,
                $"テンプレート「{selected.Name}」は読み込めません。\n{selected.ErrorMessage}\n\n削除して作り直してください。",
                "テンプレートが壊れています", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        SelectedTemplate = selected;
        DialogResult = true;
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (TemplateList.SelectedItem is not TemplateInfo selected)
        {
            MessageBox.Show(this, "削除するテンプレートを選んでください。", "テンプレートの削除",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var confirm = MessageBox.Show(this,
            $"テンプレート「{selected.Name}」を削除しますか？",
            "テンプレートの削除", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;
        if (!TemplateStore.Delete(selected.Path))
        {
            MessageBox.Show(this, $"テンプレート「{selected.Name}」を削除できませんでした。",
                "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        RefreshTemplates();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
