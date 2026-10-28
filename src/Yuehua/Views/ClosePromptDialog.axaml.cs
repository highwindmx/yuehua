using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.Input;

namespace Yuehua.Views;

/// <summary>关闭前「是否保存」提示的返回结果。</summary>
internal enum CloseChoice
{
    Save,
    Discard,
    Cancel,
}

public partial class ClosePromptDialog : Window
{
    public ClosePromptDialog()
    {
        InitializeComponent();
        KeyBindings.Add(new KeyBinding
        {
            Gesture = KeyGesture.Parse("Escape"),
            Command = new RelayCommand(() => Close(CloseChoice.Cancel)),
        });
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(CloseChoice.Cancel);
    private void OnDiscard(object? sender, RoutedEventArgs e) => Close(CloseChoice.Discard);
    private void OnSave(object? sender, RoutedEventArgs e) => Close(CloseChoice.Save);
}
