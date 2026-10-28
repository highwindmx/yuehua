using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using Yuehua.ViewModels;

namespace Yuehua.Views;

/// <summary>
/// 「另存为」对话框：另存为指定尺寸 / 指定体积的唯一入口（Ctrl+S 或顶栏导出图标唤出）。
/// 参数真源在 MainViewModel 上，对话框只负责呈现与触发保存。
/// </summary>
public partial class ExportDialog : Window
{
    private readonly MainViewModel _vm;

    /// <summary>仅用于 XAML 资源可达性（Avalonia 加载器）；实际使用请传 ViewModel。</summary>
    public ExportDialog()
    {
        InitializeComponent();
        _vm = null!;
    }

    public ExportDialog(MainViewModel vm)
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

    private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(false);

    /// <summary>尺寸预设：Tag 为百分比（"0.5"）或 "w1920"（宽度定值）。</summary>
    private void OnScalePreset(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;

        if (tag.Length > 0 && tag[0] == 'w')
        {
            if (int.TryParse(tag.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)
                && _vm.ExportSourceWidth > 0)
                _vm.ApplyExportScale((double)w / _vm.ExportSourceWidth);
            return;
        }

        if (double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
            _vm.ApplyExportScale(p);
    }

    private async void OnSaveClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await SaveAsync();

    private async Task SaveAsync()
    {
        var def = Path.GetFileNameWithoutExtension(_vm.FileName);
        if (string.IsNullOrEmpty(def)) def = "image";

        // 默认定位到原文件所在目录（没有则回退到当前目录）
        IStorageFolder? start = null;
        if (!string.IsNullOrEmpty(_vm.FolderPath) && Directory.Exists(_vm.FolderPath))
        {
            try { start = await StorageProvider.TryGetFolderFromPathAsync(_vm.FolderPath); }
            catch { start = null; }
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "另存为",
            SuggestedFileName = def + "_" + _vm.ExportFormat,
            DefaultExtension = "." + _vm.ExportFormat,
            SuggestedStartLocation = start,
            FileTypeChoices = new[]
            {
                new FilePickerFileType(_vm.ExportFormat.ToUpper())
                { Patterns = new[] { "*." + _vm.ExportFormat } }
            }
        });

        if (file == null) return;
        await _vm.ExportAsync(file.Path.LocalPath);
        Close(true);
    }
}
