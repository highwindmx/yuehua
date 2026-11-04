using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Yuehua.Views;

/// <summary>轻量提示对话框：标题 + 消息 + 详情 + 「知道了」。用于打开失败等错误提示。</summary>
public partial class MessageDialog : Window
{
    public MessageDialog()
    {
        InitializeComponent();
    }

    public MessageDialog(string title, string message, string? detail = null)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        DetailText.Text = detail ?? "";
        DetailText.IsVisible = !string.IsNullOrEmpty(detail);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button) return;
        var pt = e.GetCurrentPoint(this);
        if (pt.Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }
}
