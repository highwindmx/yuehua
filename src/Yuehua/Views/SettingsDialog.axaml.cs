using Avalonia.Controls;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;
using Yuehua.ViewModels;

namespace Yuehua.Views;

/// <summary>设置对话框：工具条浮动样式 / 显示行为 / 浮动位置复位。改动即时生效并落盘。</summary>
public partial class SettingsDialog : Window
{
    private readonly MainViewModel _vm;

    /// <summary>仅用于 XAML 资源可达性（Avalonia 加载器）；实际使用请传 ViewModel。</summary>
    public SettingsDialog()
    {
        InitializeComponent();
        _vm = null!;
    }

    public SettingsDialog(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        KeyBindings.Add(new KeyBinding
        {
            Gesture = KeyGesture.Parse("Escape"),
            Command = new RelayCommand(() => Close(false))
        });
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(false);

    private void OnResetPositions(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _vm.ResetFloatPositions();
}
