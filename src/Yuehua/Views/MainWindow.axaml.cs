using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Yuehua.Models;
using Yuehua.Services;
using Yuehua.ViewModels;

namespace Yuehua.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    private static readonly StreamGeometry GeoMaximize =
        StreamGeometry.Parse("M5.5 5.5 L18.5 5.5 L18.5 18.5 L5.5 18.5 Z");
    private static readonly StreamGeometry GeoRestore =
        StreamGeometry.Parse("M8.5 5.5 L18.5 5.5 L18.5 15.5 L8.5 15.5 Z M5.5 8.5 L15.5 8.5 L15.5 18.5 L5.5 18.5 Z M8.5 5.5 L8.5 8.5 M18.5 15.5 L18.5 18.5");

    // 画布指针交互状态
    private enum DragMode { None, Pan, Draw }
    private DragMode _drag = DragMode.None;
    private Point _dragStartDoc;
    private Point _screenStart;
    private Point _offsetStart;
    private FreehandAnnotation? _penAnno;

    // 浮动工具条：拖动状态
    private Border? _dragCard;
    private string _dragWhich = "";
    private Point _dragOrigin;
    private double _cardX, _cardY;
    private bool _dragMoved;

    // 空闲自动淡出
    private readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private DateTime _lastInput = DateTime.Now;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        // 默认窗口大小：取主屏工作区的黄金分割比例（0.618）并居中；侧边栏 / 缩略图条默认可见、不限制。
        var golden = 0.6180339887498949;
        var screen = Screens.Primary;
        if (screen != null)
        {
            var wa = screen.WorkingArea;
            Width = Math.Clamp((int)(wa.Width * golden), MinWidth, wa.Width);
            Height = Math.Clamp((int)(wa.Height * golden), MinHeight, wa.Height);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        // 关闭前：若有未保存的视图变换（旋转 / 翻转），弹「是否保存」提示
        Closing += OnClosing;

        // 图标（任务栏 / 系统标题栏 / 左上角品牌位）：从 Avalonia 资源加载，AOT 下同样可用。
        // 用 try 包裹：资源缺失只影响观感，不应导致窗口构造失败。
        try
        {
            using var s = AssetLoader.Open(new Uri("avares://Yuehua/Assets/yuehua.png"));
            var bmp = new Bitmap(s);
            Icon = new WindowIcon(bmp);
            BrandIcon.Source = bmp;
        }
        catch { /* 图标缺失不影响主功能 */ }

        // 需要弹文件对话框的快捷键（无法纯声明式绑定，走代码注入）
        KeyBindings.Add(new KeyBinding { Gesture = KeyGesture.Parse("Ctrl+O"), Command = new AsyncRelayCommand(OpenFileAsync) });
        KeyBindings.Add(new KeyBinding { Gesture = KeyGesture.Parse("Ctrl+Shift+O"), Command = new AsyncRelayCommand(OpenFolderAsync) });
        KeyBindings.Add(new KeyBinding { Gesture = KeyGesture.Parse("Ctrl+S"), Command = new AsyncRelayCommand(ExportAsync) });
        KeyBindings.Add(new KeyBinding { Gesture = KeyGesture.Parse("Ctrl+OemComma"), Command = new RelayCommand(OpenSettings) });

        // 空闲自动淡出：任何鼠标 / 键盘输入都算「活跃」
        this.PointerMoved += (_, _) => _lastInput = DateTime.Now;
        this.PointerWheelChanged += (_, _) => _lastInput = DateTime.Now;
        this.KeyDown += (_, _) => _lastInput = DateTime.Now;
        _idleTimer.Tick += OnIdleTick;
        _idleTimer.Start();
        Closed += (_, _) => _idleTimer.Stop();

        // 拖拽打开
        DragDrop.SetAllowDrop(this, true);
        this.AddHandler(DragDrop.DropEvent, OnDrop);

        // 图层高亮跟随「当前图层」：新建图层 / 撤销 / 重做 / 打开图片后都重新选中对应行，
        // 否则 ListBox 会因 ItemsSource 重建而丢失选中态（标注时就看不到高亮）。
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.Layers) or nameof(MainViewModel.CurrentLayerId))
                Dispatcher.UIThread.Post(SyncLayerSelection, DispatcherPriority.Background);
        };
        SyncLayerSelection();
    }

    private bool _syncingLayerSelection;

    private void SyncLayerSelection()
    {
        if (_syncingLayerSelection) return;
        if (LayerList == null) return;   // 标注页尚未构建（首次布局前）
        _syncingLayerSelection = true;
        try
        {
            var target = _vm.Layers.FirstOrDefault(l => l.Id == _vm.CurrentLayerId);
            if (target != null && !ReferenceEquals(LayerList.SelectedItem, target))
                LayerList.SelectedItem = target;
        }
        finally { _syncingLayerSelection = false; }
    }

    // ---------- 设置 ----------
    private void OnSettingsClick(object? sender, RoutedEventArgs e) => OpenSettings();

    private void OpenSettings() => new SettingsDialog(_vm).ShowDialog(this);

    /// <summary>空闲计时：达到阈值且鼠标不在工具条上时淡出（只影响浮动卡片）。</summary>
    private void OnIdleTick(object? sender, EventArgs e)
    {
        if (!_vm.AutoFade)
        {
            if (_vm.IsFaded) _vm.SetFaded(false);
            return;
        }

        var overCard = TopBarCard.IsPointerOver || ToolBoxCard.IsPointerOver || ThumbsCard.IsPointerOver;
        var idle = (DateTime.Now - _lastInput).TotalSeconds;
        _vm.SetFaded(!overCard && idle >= Math.Max(0.5, _vm.FadeDelaySeconds));
    }

    // ---------- 浮动工具条拖动 ----------
    private void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border b || b.Tag is not string which) return;
        _dragCard = which switch
        {
            "top" => TopBarCard,
            "tools" => ToolBoxCard,
            "thumbs" => ThumbsCard,
            _ => null,
        };
        if (_dragCard == null) return;

        _dragWhich = which;
        _dragOrigin = e.GetPosition(Overlay);
        _cardX = _dragCard.Bounds.X;
        _cardY = _dragCard.Bounds.Y;
        _dragMoved = false;
        e.Pointer.Capture(b);
        e.Handled = true;
    }

    private void OnGripMoved(object? sender, PointerEventArgs e)
    {
        if (_dragCard == null) return;

        var p = e.GetPosition(Overlay);
        var maxX = Overlay.Bounds.Width - _dragCard.Bounds.Width;
        var maxY = Overlay.Bounds.Height - _dragCard.Bounds.Height;
        var nx = Math.Clamp(_cardX + (p.X - _dragOrigin.X), 0, Math.Max(0, maxX));
        var ny = Math.Clamp(_cardY + (p.Y - _dragOrigin.Y), 0, Math.Max(0, maxY));

        _vm.SetFloatPosition(_dragWhich, nx, ny);
        _dragMoved = true;
        e.Handled = true;
    }

    private void OnGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragCard == null) return;
        if (_dragMoved) SettingsService.Save(_vm.Settings);   // 只在真的拖动过才落盘
        _dragCard = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    // ---------- 打开 ----------
    private async void OnOpenFile(object? sender, RoutedEventArgs e) => await OpenFileAsync();

    private async Task OpenFileAsync()
    {
        var files = await this.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开图片",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("图片")
            {
                Patterns = new[] { "*.jpg","*.jpeg","*.png","*.webp","*.bmp","*.gif","*.tif","*.tiff",
                                   "*.heic","*.heif","*.avif","*.cr2","*.nef","*.arw","*.dng","*.raw","*.psd" }
            } }
        });
        var f = files.FirstOrDefault();
        if (f != null)
        {
            var path = f.Path.LocalPath;
            _vm.LoadFolder(Path.GetDirectoryName(path)!, path);
        }
    }

    private async void OnOpenFolder(object? sender, RoutedEventArgs e) => await OpenFolderAsync();

    private async Task OpenFolderAsync()
    {
        var folders = await this.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "打开文件夹",
            AllowMultiple = false,
        });
        var fo = folders.FirstOrDefault();
        if (fo != null) _vm.LoadFolder(fo.Path.LocalPath);
    }

    // ---------- 自定义标题栏：拖拽 / 窗口控制 ----------
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button) return;                       // 按钮留给 Click，不触发拖动
        var pt = e.GetCurrentPoint(this);
        if (pt.Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void OnMinimize(object? sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnMaxRestore(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaxIcon.Data = WindowState == WindowState.Maximized ? GeoRestore : GeoMaximize;
    }

    private void OnCloseWindow(object? sender, RoutedEventArgs e) => Close();

    // ---------- 关闭前保存提示（旋转 / 翻转未保存时）----------
    private bool _allowClose;

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose) return;                                  // 已确认关闭（取消提示后二次触发）
        if (_vm == null || !_vm.HasUnsavedViewChanges) return;     // 无未保存改动：直接关闭

        e.Cancel = true;                                          // 先挡下，等用户决定
        var choice = await new ClosePromptDialog().ShowDialog<CloseChoice?>(this);
        if (choice is null or CloseChoice.Cancel) return;          // 取消：留在程序内

        if (choice == CloseChoice.Discard)
        {
            _allowClose = true;
            Close();
            return;
        }

        // 保存：打开「另存为」；用户若取消导出（未真正写出），保持程序打开
        var saved = await new ExportDialog(_vm).ShowDialog<bool?>(this);
        if (saved != true) return;
        _allowClose = true;
        Close();
    }

    // ---------- 缩略图条选中：切换当前图片 ----------
    private void OnThumbSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox lb && lb.SelectedIndex >= 0)
            _vm.ShowImage(lb.SelectedIndex);
    }

    // ---------- 预设色 ----------
    private void OnPresetColor(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string c)
        {
            _vm.CurrentColor = c;
            ColorChip.Flyout?.Hide();
        }
    }

    // ---------- 导出（另存为）----------
    private async void OnExportClick(object? sender, RoutedEventArgs e) => await ExportAsync();

    /// <summary>唤出「另存为」对话框；尺寸 / 体积 / 格式 / 质量都在对话框里设置。</summary>
    private async Task ExportAsync()
    {
        if (_vm.CurrentDisplay == null) return;
        await new ExportDialog(_vm).ShowDialog(this);
    }

    // ---------- 图层选中：把当前图层指向选中的层，新标注落入该层 ----------
    private void OnLayerSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingLayerSelection) return;
        if (sender is ListBox lb && lb.SelectedItem is AnnotationLayer l)
            _vm.SelectLayer(l.Id);
    }

    // ---------- 历史记录：跳到某一步 ----------
    private void OnHistoryClick(object? sender, RoutedEventArgs e)
    {
        if (LayerIdOf(sender) is { } index) _vm.JumpToHistoryCommand.Execute(index);
    }

    // ---------- 图层行内按钮：显示 / 烙印 / 删除 ----------
    private static int? LayerIdOf(object? sender)
        => (sender as ICommandSource)?.CommandParameter as int?;

    private void OnLayerVisibleClick(object? sender, RoutedEventArgs e)
    {
        if (LayerIdOf(sender) is { } id) _vm.ToggleLayerVisibleCommand.Execute(id);
    }

    private void OnLayerBakeClick(object? sender, RoutedEventArgs e)
    {
        if (LayerIdOf(sender) is { } id) _vm.ToggleLayerBakeCommand.Execute(id);
    }

    private void OnLayerDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (LayerIdOf(sender) is { } id) _vm.DeleteLayerCommand.Execute(id);
    }

    // ---------- 拖拽打开 ----------
    private void OnDrop(object? sender, DragEventArgs e)
    {
        var items = e.DataTransfer.TryGetFiles();
        if (items == null) return;

        // 拖入文件夹 → 直接加载整个目录
        var dirs = items.OfType<IStorageFolder>().Select(x => x.Path.LocalPath)
            .Where(Directory.Exists).ToList();
        if (dirs.Count > 0)
        {
            _vm.LoadFolder(dirs[0]);
            return;
        }

        var paths = items.OfType<IStorageFile>().Select(x => x.Path.LocalPath)
            .Where(File.Exists).ToList();
        if (paths.Count > 0) _vm.LoadFiles(paths);
    }

    // ---------- 画布指针交互：创建/平移标注 ----------
    private Point ToDoc(Point screen)
    {
        try { return ImageView.GetDisplayMatrix().Invert().Transform(screen); }
        catch { return screen; }
    }

    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm.CurrentDisplay == null) return;
        var screen = e.GetPosition(ImageView);
        var doc = ToDoc(screen);

        if (_vm.CurrentTool == ToolType.Select)
        {
            _drag = DragMode.Pan;
            _screenStart = screen;
            _offsetStart = new Point(_vm.OffsetX, _vm.OffsetY);
        }
        else if (_vm.CurrentTool == ToolType.Pen)
        {
            _penAnno = _vm.NewFreehand(doc);
            _vm.InProgress = _penAnno;
            _drag = DragMode.Draw;
        }
        else
        {
            _dragStartDoc = doc;
            _vm.InProgress = _vm.NewShapeForTool(_vm.CurrentTool, doc, doc);
            _drag = DragMode.Draw;
        }

        e.Pointer.Capture(ImageView);
        e.Handled = true;
    }

    private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_drag == DragMode.None) return;
        var screen = e.GetPosition(ImageView);

        if (_drag == DragMode.Pan)
        {
            _vm.OffsetX = _offsetStart.X + (screen.X - _screenStart.X);
            _vm.OffsetY = _offsetStart.Y + (screen.Y - _screenStart.Y);
        }
        else if (_drag == DragMode.Draw)
        {
            var doc = ToDoc(screen);
            if (_vm.CurrentTool == ToolType.Pen && _penAnno != null)
            {
                _penAnno.Points.Add(new DPoint(doc.X, doc.Y));
                _vm.InProgress = _penAnno;
            }
            else if (_vm.InProgress != null)
            {
                _vm.InProgress = _vm.NewShapeForTool(_vm.CurrentTool, _dragStartDoc, doc);
            }
        }
        e.Handled = true;
    }

    private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_drag == DragMode.Draw && _vm.InProgress != null)
            _vm.AddAnnotation(_vm.InProgress);

        _vm.InProgress = null;
        _penAnno = null;
        _drag = DragMode.None;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    // ---------- 滚轮缩放：以指针位置为锚点，保持指针下的像素不动 ----------
    private void OnCanvasPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_vm.CurrentDisplay == null) return;
        var step = e.Delta.Y;
        if (step == 0) return;

        var factor = step > 0 ? 1.12 : 1.0 / 1.12;
        var p = e.GetPosition(ImageView);

        // 变换链为 T(中心+偏移) · R · S(缩放)，故偏移需补偿 (1-factor)·(指针相对中心的向量)
        var vx = p.X - ImageView.Bounds.Width / 2 - _vm.OffsetX;
        var vy = p.Y - ImageView.Bounds.Height / 2 - _vm.OffsetY;
        _vm.OffsetX += (1 - factor) * vx;
        _vm.OffsetY += (1 - factor) * vy;
        _vm.Zoom = Math.Clamp(_vm.Zoom * factor, 0.02, 64);

        e.Handled = true;
    }

    // ---------- 双击：在「适应窗口」与「1:1 原始像素」之间切换 ----------
    private void OnCanvasDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (_vm.ViewScaleMode == ScaleMode.Actual) _vm.FitCommand.Execute(null);
        else _vm.ActualSizeCommand.Execute(null);
    }
}
