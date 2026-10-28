using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Yuehua.ViewModels;

namespace Yuehua.Views;

/// <summary>
/// 工具箱按钮：知道自己代表哪个工具，并自动跟随 MainViewModel.CurrentTool 切换 :checked 伪类。
/// 这样样式可以纯声明式地表达「选中高亮」，无需 Converter / 反射，对 NativeAOT 友好。
/// </summary>
public class ToolButton : Button
{
    public static readonly StyledProperty<ToolType> ToolProperty =
        AvaloniaProperty.Register<ToolButton, ToolType>(nameof(Tool));

    /// <summary>该工具是否为当前选中工具（与 :checked 伪类同步）。</summary>
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<ToolButton, bool>(nameof(IsActive));

    public ToolType Tool
    {
        get => GetValue(ToolProperty);
        set => SetValue(ToolProperty, value);
    }

    public bool IsActive => GetValue(IsActiveProperty);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is INotifyPropertyChanged npc) npc.PropertyChanged += OnVmPropertyChanged;
        SyncChecked();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is INotifyPropertyChanged npc) npc.PropertyChanged -= OnVmPropertyChanged;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        SyncChecked();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null || e.PropertyName == nameof(MainViewModel.CurrentTool))
            SyncChecked();
    }

    private void SyncChecked()
    {
        var on = DataContext is MainViewModel vm && vm.CurrentTool == Tool;
        SetValue(IsActiveProperty, on);
        PseudoClasses.Set(":checked", on);
    }
}
