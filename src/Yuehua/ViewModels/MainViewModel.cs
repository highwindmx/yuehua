using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yuehua.Models;
using Yuehua.Services;

namespace Yuehua.ViewModels;

public enum ToolType
{
    Select, Rectangle, Ellipse, Arrow, Line, Text, Pen, Mosaic, Blur,
}

public enum ScaleMode
{
    Uniform, // 适应窗口（保持比例）
    Actual,  // 原始像素 1:1
}

public class ThumbnailItem : INotifyPropertyChanged
{
    public int Index { get; set; }
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";

    private Bitmap? _image;
    public Bitmap? Image
    {
        get => _image;
        set
        {
            if (!ReferenceEquals(_image, value))
            {
                _image = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class ExifEntry
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>历史记录列表项。列表每次变更整体重建，因此无需属性变更通知。</summary>
public class HistoryItem
{
    public int Index { get; init; }
    public string Text { get; init; } = "";
    /// <summary>是否就是当前所处的这一步。</summary>
    public bool IsCurrent { get; init; }
    /// <summary>位于当前步骤之后（撤销掉的“未来分支”，点它等于重做）。</summary>
    public bool IsFuture { get; init; }
    /// <summary>当前步左侧的小圆点。</summary>
    public bool ShowDot => IsCurrent;
}

public partial class MainViewModel : ObservableObject
{
    private readonly ImageCache _cache = new();
    private AnnotationEngine _engine = new();
    private List<string> _files = new();
    private LoadedImage? _current;

    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff", ".ico",
        ".heic", ".heif", ".avif", ".cr2", ".nef", ".arw", ".dng", ".raw", ".psd",
    };

    [ObservableProperty] private string? _folderPath;
    [ObservableProperty] private int _currentIndex = -1;
    [ObservableProperty] private int _imageCount;
    [ObservableProperty] private Bitmap? _currentDisplay;
    [ObservableProperty] private string _statusText = "阅画 Yuehua — 拖入图片或点击「打开」";
    [ObservableProperty] private string _fileName = "";
    [ObservableProperty] private Dictionary<string, string> _exif = new();
    [ObservableProperty] private List<ExifEntry> _exifList = new();
    [ObservableProperty] private ObservableCollection<ThumbnailItem> _thumbnails = new();

    /// <summary>缩略图是否正在后台生成（用于进度提示）。</summary>
    [ObservableProperty] private bool _thumbsLoading;
    /// <summary>缩略图生成进度文本，如「生成缩略图 12/200…」。</summary>
    [ObservableProperty] private string _thumbsProgress = "";
    /// <summary>取消上一次未完成的缩略图生成（切换文件夹时）。</summary>
    private CancellationTokenSource? _thumbsCts;
    /// <summary>当前文件夹是否已触发过缩略图生成（避免重复生成）。</summary>
    private bool _thumbsBuilt;

    [ObservableProperty] private double _zoom = 1.0;
    [ObservableProperty] private double _offsetX, _offsetY, _rotation;
    [ObservableProperty] private bool _flipX, _flipY;
    [ObservableProperty] private ScaleMode _viewScaleMode = ScaleMode.Uniform;

    /// <summary>画布真实缩放比（由 ImageView 反向写入，用于校准状态栏百分比）。</summary>
    [ObservableProperty] private double _displayScale = 1;
    /// <summary>适应窗口的基准比例 = DisplayScale / Zoom。滚轮改 Zoom 时用它即时算百分比，
    /// 不必等 ImageView 下一帧回写，避免数字滞后或看起来「不显示」。</summary>
    private double _fitBaseScale = 1;

    [ObservableProperty] private ToolType _currentTool = ToolType.Select;
    [ObservableProperty] private string _currentColor = "#FFFF0000";
    [ObservableProperty] private double _strokeWidth = 3;
    [ObservableProperty] private string _currentText = "文字";
    [ObservableProperty] private double _effectStrength = 12;

    /// <summary>视图变换是否已改动且尚未保存：旋转 / 翻转会置 true，关闭图片或成功导出后清零。
    /// 关闭窗口时据此弹「是否保存」提示（仅旋转 / 翻转触发，标注本身已自动存侧车）。
    /// 非绑定字段，纯代码读取，故用普通字段而非 [ObservableProperty]（避免无谓的 INPC 与告警）。</summary>
    private bool _transformDirty;
    /// <summary>是否有未保存的视图改动（旋转 / 翻转）。供关闭窗口前的保存提示使用。</summary>
    public bool HasUnsavedViewChanges => _transformDirty;

    [ObservableProperty] private bool _isUiHidden;
    [ObservableProperty] private bool _uiVisible = true;
    [ObservableProperty] private int _currentLayerId;
    [ObservableProperty] private Annotation? _inProgress;

    // ---------- 面板 / 主题 ----------
    // 默认打开即「纯看图」：侧边面板与缩略图条均隐藏，打开大文件夹更轻快；需要时再点顶栏按钮唤出。
    [ObservableProperty] private bool _panelVisible = false;
    /// <summary>侧边面板整体可见性：常显时需用户开启，专注模式（UiVisible=false）下一并隐藏，避免留下空白面板。</summary>
    public bool PanelChromeVisible => PanelVisible && UiVisible;
    [ObservableProperty] private bool _thumbsVisible = false;
    [ObservableProperty] private bool _isDark = true;
    /// <summary>底部工具条是否可见（关闭后可由顶栏「底部栏」按钮重新唤出）。</summary>
    [ObservableProperty] private bool _bottomBarVisible = true;

    // ---------- 看图 / 标注 两种模式 ----------
    /// <summary>标注模式：默认关闭，产品定位是「先看图」，标注工具按需唤出。</summary>
    [ObservableProperty] private bool _annotateMode;
    /// <summary>右侧面板当前页：0=标注 1=信息 2=导出</summary>
    [ObservableProperty] private int _panelTabIndex = 1;

    // 导出 UI 绑定包装（类型对齐 Avalonia 控件，导出时同步进 ExportOpts）
    [ObservableProperty] private double _exportTargetWidth = 1920;
    [ObservableProperty] private double _exportTargetHeight = 1080;
    [ObservableProperty] private bool _exportKeepAspect = true;
    [ObservableProperty] private double _exportQuality = 90;
    [ObservableProperty] private double _exportTargetKb = 500;
    [ObservableProperty] private string _exportFormat = "jpeg";
    [ObservableProperty] private ExportMode _exportModeEnum = ExportMode.Pixel;
    [ObservableProperty] private InterpolationFilter _exportFilterEnum = InterpolationFilter.Lanczos;
    [ObservableProperty] private IcoBackgroundMode _exportIcoBackground = IcoBackgroundMode.Transparent;
    [ObservableProperty] private string _exportIcoBgColor = "#FFFFFF";
    /// <summary>ICO 最大边长（生成的标准帧中最大的那个）。</summary>
    [ObservableProperty] private int _exportIcoMaxSize = 256;
    [ObservableProperty] private bool _exportBake = true;

    /// <summary>当前图片原始像素尺寸（导出尺寸的基准）。</summary>
    [ObservableProperty] private int _exportSourceWidth;
    [ObservableProperty] private int _exportSourceHeight;

    // 导出尺寸联动控制：翻页时自动跟随原图，直到用户手动改过宽/高；_suppressAspect 防递归
    private bool _exportSizeAutoSync = true;
    private bool _suppressAspect;

    // 导出 UI 下拉选项（枚举/格式列表，供 ComboBox ItemsSource 绑定）
    public IEnumerable<ExportMode> ExportModeOptions => Enum.GetValues<ExportMode>();
    public IEnumerable<InterpolationFilter> InterpolationFilterOptions => Enum.GetValues<InterpolationFilter>();
    public IEnumerable<string> FormatOptions => new[] { "jpeg", "png", "webp", "bmp", "tiff", "ico" };
    public IEnumerable<IcoBackgroundMode> IcoBackgroundOptions => Enum.GetValues<IcoBackgroundMode>();
    /// <summary>ICO 可选的最大边长（标准图标尺寸）。</summary>
    public IEnumerable<int> IcoMaxSizeOptions => new[] { 16, 24, 32, 48, 64, 72, 96, 128, 256 };
    /// <summary>仅 ICO 格式且背景为「自定义」时，才显示底色输入框。</summary>
    public bool IsIcoCustomBg => IsIcoFormat && ExportIcoBackground == IcoBackgroundMode.Custom;

    /// <summary>当前是否为 ICO 格式（决定隐藏无效选项、显示 ICO 专属选项）。</summary>
    public bool IsIcoFormat => ExportFormat == "ico";
    /// <summary>非 ICO 的常规位图格式（方式 / 质量 / 插值 对其有效）。</summary>
    public bool IsRasterFormat => ExportFormat != "ico";
    /// <summary>栅格格式的宽/高输入区：ICO 有专属尺寸下拉，不再显示这两项，避免两套尺寸互相干扰。</summary>
    public bool ShowPixelSizeControls => IsRasterFormat && IsPixelExportMode;
    /// <summary>按当前最大边长，列出将生成的 ICO 帧尺寸（给用户明确反馈「大小可调」）。</summary>
    public string IcoFramesHint
    {
        get
        {
            var sizes = IcoMaxSizeOptions.Where(s => s <= ExportIcoMaxSize).ToList();
            return sizes.Count == 0
                ? "无可用尺寸"
                : $"将生成 {sizes.Count} 帧：{string.Join(" / ", sizes)}（最大 {sizes[^1]} × {sizes[^1]}）";
        }
    }

    /// <summary>目标宽高相对原图的缩放百分比文本。</summary>
    public string ExportScaleText => ExportSourceWidth > 0
        ? $"{(int)Math.Round(ExportTargetWidth / ExportSourceWidth * 100)}%"
        : "—";

    /// <summary>导出设置一句话摘要（侧栏与对话框共用）。ICO 摘要反映专属尺寸与背景，不再显示栅格宽高。</summary>
    public string ExportSummaryText => IsIcoFormat
        ? $"ICO 最大 {ExportIcoMaxSize} × {ExportIcoMaxSize}  ·  {IcoBackgroundLabel}  ·  {IcoMaxSizeOptions.Count(s => s <= ExportIcoMaxSize)} 帧"
        : ExportModeEnum == ExportMode.Pixel
            ? $"{(int)ExportTargetWidth} × {(int)ExportTargetHeight}  ·  {ExportFormat.ToUpper()}  ·  Q{(int)ExportQuality}"
            : $"≤ {(int)ExportTargetKb} KB  ·  {ExportFormat.ToUpper()}  ·  Q{(int)ExportQuality}";

    private string IcoBackgroundLabel => ExportIcoBackground switch
    {
        IcoBackgroundMode.White => "白底",
        IcoBackgroundMode.Black => "黑底",
        IcoBackgroundMode.Custom => $"底色 {ExportIcoBgColor}",
        _ => "透明底",
    };

    public LoadedImage? Current => _current;
    // 每次取一个新列表引用：撤销 / 重做会原地清空再填充 _engine.Layers（同一对象），
    // 若直接返回该引用，绑定会因「引用未变」而跳过更新，导致画布与图层列表不刷新。
    // 返回副本可保证属性变更通知后界面重新绑定。
    public IReadOnlyList<AnnotationLayer> Layers => _engine.Layers.ToList();
    public AnnotationEngine Engine => _engine;
    public bool CanUndo => _engine.CanUndo;
    public bool CanRedo => _engine.CanRedo;

    // ---------- 历史记录 ----------
    public ObservableCollection<HistoryItem> History { get; } = new();
    public string HistoryHeaderText => _engine.History.Count <= 1
        ? "历史记录"
        : $"历史记录  {_engine.Cursor} / {_engine.History.Count - 1}";
    public string UndoTip => CanUndo ? $"撤销  Ctrl+Z（还可退 {_engine.Cursor} 步）" : "撤销  Ctrl+Z";
    public string RedoTip => CanRedo ? $"重做  Ctrl+Y（还可进 {_engine.History.Count - 1 - _engine.Cursor} 步）" : "重做  Ctrl+Y";

    // ---------- 派生 UI 属性（状态栏 / 面板可见性联动）----------
    public string PositionText => ImageCount > 0 ? $"{CurrentIndex + 1} / {ImageCount}" : "—";
    public string ZoomPercentText => $"{(int)Math.Round(_fitBaseScale * Zoom * 100)}%";
    public bool HasMultiple => ImageCount > 1;
    public bool HasAnnotations => _engine.Layers.Any(l => l.Items.Count > 0);
    public bool IsEffectTool => CurrentTool is ToolType.Mosaic or ToolType.Blur;
    public bool IsPixelExportMode => ExportModeEnum == ExportMode.Pixel;
    public bool IsFileSizeExportMode => ExportModeEnum == ExportMode.FileSize;

    private SolidColorBrush _colorBrush = new(Colors.Red);
    public SolidColorBrush CurrentColorBrush => _colorBrush;

    private void SyncColorBrush()
    {
        try { _colorBrush = new SolidColorBrush(Color.Parse(CurrentColor)); }
        catch { _colorBrush = new SolidColorBrush(Colors.Red); }
        OnPropertyChanged(nameof(CurrentColorBrush));
    }

    // ================= 设置：工具条浮动样式 =================
    /// <summary>持久化设置（读写 %LOCALAPPDATA%\Yuehua\settings.json）。</summary>
    public AppSettings Settings { get; } = SettingsService.Load();

    [ObservableProperty] private bool _toolboxFloating;
    [ObservableProperty] private bool _thumbsFloating;
    [ObservableProperty] private ToolbarRevealMode _revealMode = ToolbarRevealMode.IdleFade;
    [ObservableProperty] private double _fadeDelaySeconds = 2;

    /// <summary>当前是否处于「空闲淡出」态（IdleFade 模式），由窗口空闲计时器驱动。</summary>
    private bool _faded;

    public MainViewModel()
    {
        SyncColorBrush();
        _toolboxFloating = Settings.ToolboxFloating;
        _thumbsFloating = Settings.ThumbsFloating;
        _revealMode = Settings.RevealMode;
        _fadeDelaySeconds = Settings.FadeDelaySeconds;

        // 旧版设置文件只有 AutoFade（bool）没有 RevealMode：迁移一次后清掉旧字段。
        if (Settings.AutoFade is { } legacyAutoFade)
        {
            _revealMode = legacyAutoFade ? ToolbarRevealMode.IdleFade : ToolbarRevealMode.Always;
            Settings.RevealMode = _revealMode;
            Settings.AutoFade = null;
            SettingsService.Save(Settings);
        }
    }

    private static readonly BoxShadows CardShadow =
        new(new BoxShadow { Blur = 20, OffsetX = 0, OffsetY = 4, Color = Color.FromArgb(0x5A, 0, 0, 0) });

    /// <summary>0=常驻 1=空闲自动淡出（供设置对话框的 ComboBox；旧版「悬停上缘」已随顶栏固定而废弃，读到该值回落为「空闲自动淡出」）。</summary>
    public int RevealModeIndex
    {
        get => (int)RevealMode > 1 ? 1 : (int)RevealMode;
        set => RevealMode = (ToolbarRevealMode)Math.Clamp(value, 0, 1);
    }

    /// <summary>设置对话框里「延时」滑块的可用条件。</summary>
    public bool IsIdleFade => RevealMode == ToolbarRevealMode.IdleFade;

    // ---------- 卡片外观：浮动=圆角+阴影+半透明，固定=贴边+方角 ----------
    public CornerRadius ToolboxRadius => ToolboxFloating ? new CornerRadius(10) : new CornerRadius(0);
    public CornerRadius ThumbsRadius => ThumbsFloating ? new CornerRadius(10) : new CornerRadius(0);

    public BoxShadows ToolboxShadow => ToolboxFloating ? CardShadow : default;
    public BoxShadows ThumbsShadow => ThumbsFloating ? CardShadow : default;

    public Thickness ToolboxBorderThickness => ToolboxFloating ? new Thickness(0) : new Thickness(0, 0, 1, 0);
    public Thickness ThumbsBorderThickness => ThumbsFloating ? new Thickness(0) : new Thickness(0, 1, 0, 0);

    // ---------- 卡片位置：null=默认，有值=相对画布左上角的像素 ----------
    public Thickness ToolboxMargin => ToolboxFloating
        ? new Thickness(Settings.ToolboxX ?? 12, Settings.ToolboxY ?? 0, 0, 0) : new Thickness(0);
    public Thickness ThumbsMargin => ThumbsFloating
        ? new Thickness(Settings.ThumbsX ?? 0, Settings.ThumbsY ?? 12, 0, 0) : new Thickness(0);

    public HorizontalAlignment ToolboxHAlign => HorizontalAlignment.Left;
    public VerticalAlignment ToolboxVAlign => !ToolboxFloating ? VerticalAlignment.Stretch
        : Settings.ToolboxY.HasValue ? VerticalAlignment.Top : VerticalAlignment.Center;

    public HorizontalAlignment ThumbsHAlign => !ThumbsFloating ? HorizontalAlignment.Stretch
        : Settings.ThumbsX.HasValue ? HorizontalAlignment.Left : HorizontalAlignment.Center;
    public VerticalAlignment ThumbsVAlign => !ThumbsFloating ? VerticalAlignment.Bottom
        : Settings.ThumbsY.HasValue ? VerticalAlignment.Top : VerticalAlignment.Bottom;

    /// <summary>浮动时缩略图条限制宽度，避免横跨整个画布。</summary>
    public double ThumbsMaxWidth => ThumbsFloating ? 760 : double.PositiveInfinity;

    /// <summary>
    /// 画布留白：固定模式的工具条会占用画布外围空间（与旧布局一致，图片不被遮挡）；
    /// 浮动模式下工具条浮在画面之上，画布占满整格。
    /// </summary>
    public Thickness CanvasMargin => new Thickness(
        !ToolboxFloating && AnnotateMode ? 44 : 0,
        34,
        0,
        !ThumbsFloating && ThumbsVisible ? 86 : 0);

    // ---------- 淡出 / 隐藏：只作用于浮动卡片；固定卡片恒不透明、恒可点击 ----------
    /// <summary>专注模式（F11）下浮动工具条一并隐藏，画面只留图像本身。</summary>
    public bool ToolboxVisible => UiVisible && AnnotateMode;
    public bool ThumbsBarVisible => UiVisible && ThumbsVisible;

    // 悬浮卡片本身的半透明由画刷（YhGlassPanel）负责，这里只管「显示 / 隐藏」，
    // 所以可见时保持 1.0：图标与文字不被整体降透明度，玻璃感来自底下的 alpha。
    public double ToolboxOpacity => !ToolboxFloating ? 1.0 : _faded ? 0.0 : 1.0;
    public double ThumbsOpacity => !ThumbsFloating ? 1.0 : _faded ? 0.0 : 1.0;

    public bool ToolboxHitTest => !ToolboxFloating || !_faded;
    public bool ThumbsHitTest => !ThumbsFloating || !_faded;
    public bool IsFaded => _faded;

    /// <summary>由窗口空闲计时器调用：切换淡出态（仅浮动卡片受影响）。</summary>
    public void SetFaded(bool faded)
    {
        if (_faded == faded) return;
        _faded = faded;
        NotifyChrome();
    }

    /// <summary>拖动浮动卡片后写入位置（which: top / tools / thumbs）。</summary>
    public void SetFloatPosition(string which, double x, double y)
    {
        switch (which)
        {
            case "tools": Settings.ToolboxX = x; Settings.ToolboxY = y; break;
            case "thumbs": Settings.ThumbsX = x; Settings.ThumbsY = y; break;
            default: return;
        }
        NotifyChrome();
    }

    /// <summary>所有浮动卡片回到默认位置。</summary>
    public void ResetFloatPositions()
    {
        Settings.ToolboxX = Settings.ToolboxY = null;
        Settings.ThumbsX = Settings.ThumbsY = null;
        NotifyChrome();
        SettingsService.Save(Settings);
    }

    private void NotifyChrome()
    {
        OnPropertyChanged(nameof(ToolboxRadius));
        OnPropertyChanged(nameof(ThumbsRadius));
        OnPropertyChanged(nameof(ToolboxShadow));
        OnPropertyChanged(nameof(ThumbsShadow));
        OnPropertyChanged(nameof(ToolboxBorderThickness));
        OnPropertyChanged(nameof(ThumbsBorderThickness));
        OnPropertyChanged(nameof(ToolboxMargin));
        OnPropertyChanged(nameof(ThumbsMargin));
        OnPropertyChanged(nameof(ToolboxHAlign));
        OnPropertyChanged(nameof(ToolboxVAlign));
        OnPropertyChanged(nameof(ThumbsHAlign));
        OnPropertyChanged(nameof(ThumbsVAlign));
        OnPropertyChanged(nameof(ThumbsMaxWidth));
        OnPropertyChanged(nameof(CanvasMargin));
        OnPropertyChanged(nameof(ToolboxVisible));
        OnPropertyChanged(nameof(ThumbsBarVisible));
        OnPropertyChanged(nameof(ToolboxOpacity));
        OnPropertyChanged(nameof(ThumbsOpacity));
        OnPropertyChanged(nameof(ToolboxHitTest));
        OnPropertyChanged(nameof(ThumbsHitTest));
        OnPropertyChanged(nameof(IsFaded));
        OnPropertyChanged(nameof(RevealModeIndex));
        OnPropertyChanged(nameof(IsIdleFade));
    }

    partial void OnToolboxFloatingChanged(bool value)
    {
        Settings.ToolboxFloating = value;
        SettingsService.Save(Settings);
        NotifyChrome();
    }

    partial void OnThumbsFloatingChanged(bool value)
    {
        Settings.ThumbsFloating = value;
        SettingsService.Save(Settings);
        NotifyChrome();
    }

    partial void OnRevealModeChanged(ToolbarRevealMode value)
    {
        Settings.RevealMode = value;
        SettingsService.Save(Settings);

        // 切换方式时复位淡出态，避免残留不可见状态
        if (_faded) SetFaded(false);
        NotifyChrome();
    }

    partial void OnFadeDelaySecondsChanged(double value)
    {
        Settings.FadeDelaySeconds = Math.Clamp(value, 0.5, 30);
        SettingsService.Save(Settings);
    }

    partial void OnCurrentIndexChanged(int value) => OnPropertyChanged(nameof(PositionText));
    partial void OnImageCountChanged(int value)
    {
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(HasMultiple));
    }

    partial void OnAnnotateModeChanged(bool value)
    {
        OnPropertyChanged(nameof(AnnotateMode));
        OnPropertyChanged(nameof(CanvasMargin));
        OnPropertyChanged(nameof(ToolboxVisible));
        if (value)
        {
            PanelVisible = true;
            PanelTabIndex = 0;          // 标注页
        }
        else
        {
            CurrentTool = ToolType.Select;
            PanelTabIndex = 1;          // 信息页（看图模式默认看信息）
        }
    }

    /// <summary>切到「标注」页时一并进入标注模式，呼出左侧标注工具箱；离开该页不再强制退出，
    /// 以保留「标注模式开着、临时切到信息/导出页看一眼」的使用场景。</summary>
    partial void OnPanelTabIndexChanged(int value)
    {
        if (value == 0 && !AnnotateMode) AnnotateMode = true;
    }
    partial void OnZoomChanged(double value)
    {
        // 滚轮 / +/- 缩放立即反映到百分比，不等渲染帧。
        // 注意不能用旧的 DisplayScale 除以新 Zoom 去反推基准（会互相抵消，数字不动）；
        // 基准保持上次校准值，等 ImageView 下一帧回写 DisplayScale 时再校准。
        OnPropertyChanged(nameof(ZoomPercentText));
    }
    partial void OnDisplayScaleChanged(double value)
    {
        if (Zoom > 0) _fitBaseScale = value / Zoom;
        OnPropertyChanged(nameof(ZoomPercentText));
    }
    partial void OnCurrentToolChanged(ToolType value) => OnPropertyChanged(nameof(IsEffectTool));
    partial void OnCurrentColorChanged(string value) => SyncColorBrush();

    partial void OnThumbsVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(CanvasMargin));
        OnPropertyChanged(nameof(ThumbsBarVisible));
        // 由隐藏切到显示：若当前文件夹尚未生成缩略图，则懒加载
        if (value && !_thumbsBuilt) BuildThumbnailsAsync();
    }

    partial void OnUiVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(ToolboxVisible));
        OnPropertyChanged(nameof(ThumbsBarVisible));
        OnPropertyChanged(nameof(PanelChromeVisible));
    }

    partial void OnPanelVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(PanelChromeVisible));
    }

    partial void OnExportModeEnumChanged(ExportMode value)
    {
        OnPropertyChanged(nameof(IsPixelExportMode));
        OnPropertyChanged(nameof(IsFileSizeExportMode));
        OnPropertyChanged(nameof(ShowPixelSizeControls));
        OnPropertyChanged(nameof(ExportSummaryText));
    }

    partial void OnExportQualityChanged(double value) => OnPropertyChanged(nameof(ExportSummaryText));
    partial void OnExportFormatChanged(string value)
    {
        OnPropertyChanged(nameof(ExportSummaryText));
        OnPropertyChanged(nameof(IsIcoFormat));
        OnPropertyChanged(nameof(IsRasterFormat));
        OnPropertyChanged(nameof(ShowPixelSizeControls));
        OnPropertyChanged(nameof(IcoFramesHint));
        OnPropertyChanged(nameof(IsIcoCustomBg));
    }

    partial void OnExportIcoBackgroundChanged(IcoBackgroundMode value)
    {
        OnPropertyChanged(nameof(IsIcoCustomBg));
        OnPropertyChanged(nameof(ExportSummaryText));
    }

    partial void OnExportIcoBgColorChanged(string value) => OnPropertyChanged(nameof(ExportSummaryText));

    partial void OnExportIcoMaxSizeChanged(int value)
    {
        OnPropertyChanged(nameof(IcoFramesHint));
        OnPropertyChanged(nameof(ExportSummaryText));
    }
    partial void OnExportTargetKbChanged(double value) => OnPropertyChanged(nameof(ExportSummaryText));

    partial void OnExportTargetWidthChanged(double value)
    {
        if (_suppressAspect) return;
        _exportSizeAutoSync = false;                 // 用户手动介入后不再随翻页重置
        if (ExportKeepAspect && ExportSourceWidth > 0 && ExportSourceHeight > 0)
        {
            _suppressAspect = true;
            ExportTargetHeight = Math.Max(1, Math.Round(value * ExportSourceHeight / ExportSourceWidth));
            _suppressAspect = false;
        }
        OnPropertyChanged(nameof(ExportScaleText));
        OnPropertyChanged(nameof(ExportSummaryText));
    }

    partial void OnExportTargetHeightChanged(double value)
    {
        if (_suppressAspect) return;
        _exportSizeAutoSync = false;
        if (ExportKeepAspect && ExportSourceWidth > 0 && ExportSourceHeight > 0)
        {
            _suppressAspect = true;
            ExportTargetWidth = Math.Max(1, Math.Round(value * ExportSourceWidth / ExportSourceHeight));
            _suppressAspect = false;
        }
        OnPropertyChanged(nameof(ExportScaleText));
        OnPropertyChanged(nameof(ExportSummaryText));
    }

    partial void OnExportSourceWidthChanged(int value) => OnPropertyChanged(nameof(ExportScaleText));

    /// <summary>按原图百分比设定导出尺寸（0.5 = 原图一半）。</summary>
    public void ApplyExportScale(double percent)
    {
        if (ExportSourceWidth <= 0 || ExportSourceHeight <= 0) return;
        _suppressAspect = true;
        ExportTargetWidth = Math.Max(1, Math.Round(ExportSourceWidth * percent));
        ExportTargetHeight = Math.Max(1, Math.Round(ExportSourceHeight * percent));
        _suppressAspect = false;
        _exportSizeAutoSync = false;
        OnPropertyChanged(nameof(ExportScaleText));
        OnPropertyChanged(nameof(ExportSummaryText));
    }

    /// <summary>恢复到原图尺寸（100%），并重新开启随翻页自动同步。</summary>
    public void ResetExportSize()
    {
        if (ExportSourceWidth <= 0 || ExportSourceHeight <= 0) return;
        _suppressAspect = true;
        ExportTargetWidth = ExportSourceWidth;
        ExportTargetHeight = ExportSourceHeight;
        _suppressAspect = false;
        _exportSizeAutoSync = true;
        OnPropertyChanged(nameof(ExportScaleText));
        OnPropertyChanged(nameof(ExportSummaryText));
    }

    // ---------- 打开 / 导航 ----------
    /// <summary>自然排序（数字按数值而非字典序）：IMG_2 排在 IMG_10 之前。</summary>
    private static int NaturalCompare(string a, string b)
    {
        var cmp = StringComparer.OrdinalIgnoreCase;
        var i = 0; var j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                var si = i; var sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                // 去掉前导零后比较长度，再比较数值，避免 long 溢出
                var na = a.Substring(si, i - si).TrimStart('0');
                var nb = b.Substring(sj, j - sj).TrimStart('0');
                var r = na.Length.CompareTo(nb.Length);
                if (r != 0) return r;
                r = cmp.Compare(na, nb);
                if (r != 0) return r;
            }
            else
            {
                var r = cmp.Compare(a[i].ToString(), b[j].ToString());
                if (r != 0) return r;
                i++; j++;
            }
        }
        return a.Length.CompareTo(b.Length);
    }

    /// <summary>安全枚举目录下的图片（遇到无权限/损坏项跳过，不中断整体加载）。</summary>
    private static List<string> ScanImages(string folder)
    {
        var list = new List<string>();
        try
        {
            using var e = Directory.EnumerateFiles(folder).GetEnumerator();
            while (true)
            {
                try { if (!e.MoveNext()) break; }
                catch { break; }                       // IO 异常：停止枚举但保留已有结果
                var f = e.Current;
                if (f != null && ImageExts.Contains(Path.GetExtension(f))) list.Add(f);
            }
        }
        catch { /* 目录不可访问：返回空列表 */ }
        list.Sort(NaturalCompare);
        return list;
    }

    /// <summary>定位文件在列表中的下标（忽略大小写与路径分隔符差异）。</summary>
    private static int IndexOfPath(List<string> files, string? path)
    {
        if (string.IsNullOrEmpty(path)) return -1;
        var target = Path.GetFullPath(path);
        for (var i = 0; i < files.Count; i++)
            if (string.Equals(Path.GetFullPath(files[i]), target, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>打开文件夹；initialFile 为可选的高亮文件，directOpen 表示用户「主动打开」（失败时弹错误提示）。</summary>
    public void LoadFolder(string folder, string? initialFile = null, bool directOpen = false)
    {
        if (!Directory.Exists(folder)) return;
        var files = ScanImages(folder);
        if (files.Count == 0) { StatusText = "该文件夹没有支持的图片格式"; return; }
        LoadList(files, Path.GetFullPath(folder), initialFile, directOpen);
    }

    /// <summary>加载一组文件：单文件时自动扩展到同目录全部图片（保证上下张可用）。</summary>
    public void LoadFiles(IEnumerable<string> paths, bool directOpen = false)
    {
        var list = paths.Where(File.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) return;

        if (list.Count == 1)
        {
            var dir = Path.GetDirectoryName(list[0]);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                LoadFolder(dir, list[0], directOpen);
                return;
            }
        }

        var sameDir = list.Select(Path.GetDirectoryName)
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() == 1;
        if (sameDir)
        {
            list.Sort(NaturalCompare);
            LoadList(list, Path.GetDirectoryName(list[0]) ?? "", list[0], directOpen);
            return;
        }

        list.Sort(NaturalCompare);
        LoadList(list, "", list[0], directOpen);
    }

    /// <summary>
    /// 按「文件夹优先 → 单个文件 → 多个文件」打开一组路径。
    /// 拖放进窗口、拖到桌面快捷方式、命令行参数、右键「打开方式」最终都汇聚到这里。
    /// 这些都属于用户主动打开，标记 directOpen = true（失败时给出错误提示）。
    /// </summary>
    public void OpenPaths(IEnumerable<string> paths)
    {
        var dirs = new List<string>();
        var files = new List<string>();
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string full;
            try { full = Path.GetFullPath(raw.Trim().Trim('"')); }
            catch { continue; }                        // 非法路径：忽略
            if (Directory.Exists(full)) dirs.Add(full);
            else if (File.Exists(full)) files.Add(full);
        }

        if (dirs.Count > 0) { LoadFolder(dirs[0], null, true); return; }
        if (files.Count > 0) LoadFiles(files, true);
    }

    /// <summary>主动打开文件失败时由 ViewModel 抛出，供视图层弹出错误对话框。</summary>
    public event Action<string, string>? RequestOpenError;

    private void LoadList(List<string> files, string folder, string? initialFile, bool directOpen)
    {
        _files = files;
        FolderPath = folder;
        ImageCount = files.Count;
        BuildThumbnailsAsync();
        int idx = IndexOfPath(files, initialFile);
        if (idx < 0 && initialFile != null && File.Exists(initialFile))
        {
            // 请求的初始文件不在扫描列表里（扩展名未被收录，例如罕见格式）：
            // 直接尝试打开它本身，而不是静默跳到别的文件。
            _files = new List<string> { Path.GetFullPath(initialFile) };
            ImageCount = 1;
            idx = 0;
        }
        if (idx < 0) idx = 0;
        _ = ShowImageAsync(idx, directOpen);
    }

    /// <summary>
    /// 缩略图异步 / 懒加载：
    /// - 缩略图条隐藏时不创建任何占位项、也不解码，打开文件夹几乎瞬时（避免同目录大量图片时卡顿）；
    /// - 用户点开缩略图条后才在后台逐张生成，前台先显示文件名占位；
    /// - 切换文件夹会取消上一次未完成的生成；生成中给出「生成缩略图 n/总」进度提示。
    /// </summary>
    private void BuildThumbnailsAsync()
    {
        _thumbsCts?.Cancel();
        _thumbsCts?.Dispose();
        _thumbsCts = new CancellationTokenSource();
        var cts = _thumbsCts;
        _thumbsBuilt = false;

        // 缩略图条隐藏：不建占位、不解码，省去开销（之后展开时再构建）
        if (_files.Count == 0 || !ThumbsVisible)
        {
            ThumbsLoading = false;
            ThumbsProgress = "";
            return;
        }

        var cap = Math.Min(_files.Count, 200);
        var items = new ObservableCollection<ThumbnailItem>();
        for (var i = 0; i < cap; i++)
            items.Add(new ThumbnailItem { Index = i, Path = _files[i], Name = Path.GetFileName(_files[i]) });
        Thumbnails = items;
        _thumbsBuilt = true;

        ThumbsLoading = true;
        ThumbsProgress = $"生成缩略图 0/{cap}…";
        _ = Task.Run(() =>
        {
            var done = 0;
            foreach (var it in items)
            {
                if (cts.IsCancellationRequested) return;
                Bitmap? bmp = null;
                try { bmp = ImageLoader.LoadThumbnail(it.Path, 160); }
                catch { /* 单张失败忽略，继续后续 */ }
                var item = it; var b = bmp; var d = done + 1;
                Dispatcher.UIThread.Post(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    item.Image = b;
                    ThumbsProgress = $"生成缩略图 {d}/{cap}…";
                    if (d >= cap) ThumbsLoading = false;
                });
                done = d;
            }
            Dispatcher.UIThread.Post(() =>
            {
                if (!cts.IsCancellationRequested) { ThumbsLoading = false; ThumbsProgress = ""; }
            });
        }, cts.Token);
    }

    private int _loadToken;

    /// <summary>
    /// 异步打开指定图片：解码放到后台线程，避免大图 / 慢盘时界面冻结；解码完成后回到 UI 线程更新显示。
    /// _loadToken 保证「最新一次请求」才生效，快速翻页 / 切换文件夹时旧请求的结果会被丢弃。
    /// directOpen = true 表示这是用户主动打开（失败弹错误提示），导航翻页则只更新状态栏。
    /// </summary>
    public async Task ShowImageAsync(int index, bool directOpen = false)
    {
        if (index < 0 || index >= _files.Count) return;
        var token = ++_loadToken;
        CurrentIndex = index;
        var path = _files[index];
        StatusText = $"正在打开 {Path.GetFileName(path)}…";

        LoadedImage? loaded = null;
        Exception? err = null;
        try
        {
            // 注意：_current 来自 ImageCache，其 Full/Display 由缓存管理生命周期，
            // 这里不能 Dispose，否则缓存中的位图失效导致后续翻页崩溃。
            loaded = await Task.Run(() => _cache.GetOrLoad(path));
        }
        catch (Exception ex) { err = ex; }

        if (token != _loadToken) return;       // 已有更新的加载请求，丢弃本次结果

        if (err != null || loaded == null)
        {
            StatusText = $"打开失败: {err?.Message}";
            if (directOpen) RequestOpenError?.Invoke(Path.GetFileName(path), err?.Message ?? "未知错误");
            return;
        }

        _current = loaded;
        CurrentDisplay = loaded.Display;
        FileName = Path.GetFileName(path);
        Exif = loaded.Exif;
        ExifList = loaded.Exif.Select(kv => new ExifEntry { Key = kv.Key, Value = kv.Value }).ToList();
        // ICO 等多帧容器：明确标注「显示的是最大那一帧」，避免把 256 × 256 误认为文件只有一个尺寸
        StatusText = loaded.FrameCount > 1
            ? $"{loaded.OriginalWidth} × {loaded.OriginalHeight}  ·  {loaded.Format}（共 {loaded.FrameCount} 帧，当前显示最大帧）"
            : $"{loaded.OriginalWidth} × {loaded.OriginalHeight}  ·  {loaded.Format}";

        // 导出基准尺寸；用户没手动改过时，默认导出原图尺寸（100%）
        _suppressAspect = true;
        ExportSourceWidth = loaded.OriginalWidth;
        ExportSourceHeight = loaded.OriginalHeight;
        if (_exportSizeAutoSync)
        {
            ExportTargetWidth = loaded.OriginalWidth;
            ExportTargetHeight = loaded.OriginalHeight;
        }
        _suppressAspect = false;
        OnPropertyChanged(nameof(ExportScaleText));
        OnPropertyChanged(nameof(ExportSummaryText));

        Zoom = 1; OffsetX = 0; OffsetY = 0; Rotation = 0;
        FlipX = false; FlipY = false; ViewScaleMode = ScaleMode.Uniform;
        _transformDirty = false;

        LoadSidecar(path);

        var neigh = new List<string>();
        if (index > 0) neigh.Add(_files[index - 1]);
        if (index < _files.Count - 1) neigh.Add(_files[index + 1]);
        _cache.Prefetch(neigh);
    }

    [RelayCommand]
    private async Task Prev() { if (ImageCount > 0) await ShowImageAsync((CurrentIndex - 1 + ImageCount) % ImageCount); }

    [RelayCommand]
    private async Task Next() { if (ImageCount > 0) await ShowImageAsync((CurrentIndex + 1) % ImageCount); }

    [RelayCommand]
    private async Task GotoIndex(int index) => await ShowImageAsync(index);

    // ---------- 视图变换 ----------
    [RelayCommand]
    private void ZoomIn() => Zoom *= 1.2;

    [RelayCommand]
    private void ZoomOut() => Zoom /= 1.2;

    [RelayCommand]
    private void Fit()
    {
        Zoom = 1; OffsetX = 0; OffsetY = 0; Rotation = 0;
        ViewScaleMode = ScaleMode.Uniform;
    }

    [RelayCommand]
    private void ActualSize()
    {
        Zoom = 1; OffsetX = 0; OffsetY = 0; Rotation = 0;
        ViewScaleMode = ScaleMode.Actual;
    }

    [RelayCommand]
    private void RotateLeft()
    {
        Rotation -= 90;
        if (_current != null) _transformDirty = true;
    }

    [RelayCommand]
    private void RotateRight()
    {
        Rotation += 90;
        if (_current != null) _transformDirty = true;
    }

    [RelayCommand]
    private void FlipH()
    {
        FlipX = !FlipX;
        if (_current != null) _transformDirty = true;
    }

    [RelayCommand]
    private void FlipV()
    {
        FlipY = !FlipY;
        if (_current != null) _transformDirty = true;
    }

    [RelayCommand]
    private void ToggleUi()
    {
        IsUiHidden = !IsUiHidden;
        UiVisible = !IsUiHidden;
    }

    /// <summary>进入 / 退出标注模式。看图是默认形态，标注工具按需唤出。</summary>
    [RelayCommand]
    private void ToggleAnnotate() => AnnotateMode = !AnnotateMode;

    /// <summary>Esc：优先退出标注模式；已在看图模式则切换专注模式。</summary>
    [RelayCommand]
    private void Escape()
    {
        if (AnnotateMode) AnnotateMode = false;
        else ToggleUi();
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        var app = Application.Current;
        if (app == null) return;
        app.RequestedThemeVariant = IsDark ? ThemeVariant.Light : ThemeVariant.Dark;
        IsDark = !IsDark;
    }

    [RelayCommand]
    private void SelectTool(ToolType tool) => CurrentTool = tool;

    // ---------- 标注 ----------
    public void AddAnnotation(Annotation a)
    {
        _engine.AddAnnotation(a);
        NotifyLayersChanged();
        SaveSidecar();
    }

    [RelayCommand]
    private void Undo() { _engine.Undo(); NotifyLayersChanged(); SaveSidecar(); }

    [RelayCommand]
    private void Redo() { _engine.Redo(); NotifyLayersChanged(); SaveSidecar(); }

    /// <summary>跳到历史中的某一步（历史列表点击）。</summary>
    [RelayCommand]
    private void JumpToHistory(int index) { _engine.JumpTo(index); NotifyLayersChanged(); SaveSidecar(); }

    public void SelectLayer(int id) { _engine.CurrentLayerId = id; CurrentLayerId = id; }

    [RelayCommand]
    private void NewLayer()
    {
        _engine.NewLayer();
        CurrentLayerId = _engine.CurrentLayerId;
        NotifyLayersChanged();
    }

    [RelayCommand]
    private void DeleteLayer(int id)
    {
        _engine.RemoveLayer(id);
        CurrentLayerId = _engine.CurrentLayerId;
        NotifyLayersChanged();
        SaveSidecar();
    }

    [RelayCommand]
    private void ToggleLayerVisible(int id)
    {
        var l = GetLayer(id);
        if (l != null) _engine.SetLayerVisible(id, !l.Visible);
        NotifyLayersChanged();
        SaveSidecar();
    }

    [RelayCommand]
    private void ToggleLayerBake(int id)
    {
        var l = GetLayer(id);
        if (l != null) _engine.SetLayerBake(id, !l.BakeIntoExport);
        NotifyLayersChanged();
        SaveSidecar();
    }

    private AnnotationLayer? GetLayer(int id) => _engine.Layers.FirstOrDefault(l => l.Id == id);

    /// <summary>为当前工具创建一个标注（矩形/椭圆/箭头/直线/文字/马赛克/模糊）。</summary>
    public Annotation NewShapeForTool(ToolType tool, Point start, Point end)
    {
        var color = CurrentColor;
        var sw = StrokeWidth;
        var layer = CurrentLayerId;
        switch (tool)
        {
            case ToolType.Rectangle:
            {
                double x = start.X, y = start.Y, w = end.X - start.X, h = end.Y - start.Y;
                Annotation.Normalize(ref x, ref y, ref w, ref h);
                return new RectangleAnnotation { X = x, Y = y, W = w, H = h, Color = color, StrokeWidth = sw, LayerId = layer };
            }
            case ToolType.Ellipse:
            {
                double x = start.X, y = start.Y, w = end.X - start.X, h = end.Y - start.Y;
                Annotation.Normalize(ref x, ref y, ref w, ref h);
                return new EllipseAnnotation { X = x, Y = y, W = w, H = h, Color = color, StrokeWidth = sw, LayerId = layer };
            }
            case ToolType.Line:
                return new LineAnnotation { X1 = start.X, Y1 = start.Y, X2 = end.X, Y2 = end.Y, Color = color, StrokeWidth = sw, LayerId = layer };
            case ToolType.Arrow:
                return new ArrowAnnotation { X1 = start.X, Y1 = start.Y, X2 = end.X, Y2 = end.Y, Color = color, StrokeWidth = sw, LayerId = layer };
            case ToolType.Text:
                return new TextAnnotation { X = start.X, Y = start.Y, Text = CurrentText, FontSize = Math.Max(10, sw * 8), Color = color, LayerId = layer };
            case ToolType.Mosaic:
            {
                double x = start.X, y = start.Y, w = end.X - start.X, h = end.Y - start.Y;
                Annotation.Normalize(ref x, ref y, ref w, ref h);
                return new MosaicAnnotation { X = x, Y = y, W = w, H = h, Color = color, StrokeWidth = sw, Strength = EffectStrength, LayerId = layer };
            }
            case ToolType.Blur:
            {
                double x = start.X, y = start.Y, w = end.X - start.X, h = end.Y - start.Y;
                Annotation.Normalize(ref x, ref y, ref w, ref h);
                return new BlurAnnotation { X = x, Y = y, W = w, H = h, Color = color, StrokeWidth = sw, Strength = EffectStrength, LayerId = layer };
            }
            default:
                return new RectangleAnnotation { X = start.X, Y = start.Y, W = 1, H = 1, Color = color, LayerId = layer };
        }
    }

    public FreehandAnnotation NewFreehand(Point p)
        => new() { Points = new List<DPoint> { new(p.X, p.Y) }, Color = CurrentColor, StrokeWidth = StrokeWidth, LayerId = CurrentLayerId };

    // ---------- 侧车文件（标注持久化）----------
    private void SaveSidecar()
    {
        if (_current == null) return;
        try
        {
            var sc = new AnnotationSidecar { ImagePath = _current.Path, Layers = _engine.Layers.ToList() };
            var json = System.Text.Json.JsonSerializer.Serialize(sc, JsonContext.Default.AnnotationSidecar);
            File.WriteAllText(_current.Path + ".yuehua.json", json);
        }
        catch { /* 标注持久化失败不阻塞看图 */ }
    }

    private void LoadSidecar(string path)
    {
        var p = path + ".yuehua.json";
        if (File.Exists(p))
        {
            try
            {
                var sc = System.Text.Json.JsonSerializer.Deserialize<AnnotationSidecar>(
                    File.ReadAllText(p), JsonContext.Default.AnnotationSidecar);
                if (sc != null && sc.Layers.Count > 0)
                {
                    _engine.LoadLayers(sc.Layers);
                    CurrentLayerId = _engine.CurrentLayerId;
                    NotifyLayersChanged();
                    return;
                }
            }
            catch { /* 解析失败则用空白图层 */ }
        }
        _engine = new AnnotationEngine();
        CurrentLayerId = _engine.CurrentLayerId;
        NotifyLayersChanged();
    }

    private void NotifyLayersChanged()
    {
        OnPropertyChanged(nameof(Layers));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(HasAnnotations));
        CurrentLayerId = _engine.CurrentLayerId;
        RefreshHistory();
    }

    private void RefreshHistory()
    {
        History.Clear();
        var steps = _engine.History;
        var cur = _engine.Cursor;
        for (var i = 0; i < steps.Count; i++)
        {
            History.Add(new HistoryItem
            {
                Index = i,
                Text = i == 0 ? $"起点 · {steps[i].Label}" : $"{i} · {steps[i].Label}",
                IsCurrent = i == cur,
                IsFuture = i > cur,
            });
        }
        OnPropertyChanged(nameof(HistoryHeaderText));
        OnPropertyChanged(nameof(UndoTip));
        OnPropertyChanged(nameof(RedoTip));
    }

    // ---------- 导出 ----------
    private void SyncExport(ExportOptions o)
    {
        o.Mode = ExportModeEnum;
        o.TargetWidth = (int)ExportTargetWidth;
        o.TargetHeight = (int)ExportTargetHeight;
        o.KeepAspect = ExportKeepAspect;
        o.Quality = (int)ExportQuality;
        o.TargetBytes = (long)(ExportTargetKb * 1024);
        o.Format = ExportFormat;
        o.Filter = ExportFilterEnum;
        o.IcoBackground = ExportIcoBackground;
        o.IcoBgColor = ExportIcoBgColor;
        o.IcoMaxSize = ExportIcoMaxSize;
        o.BakeAnnotations = ExportBake;
        o.CropRect = null; // MVP 暂不提供裁剪 UI
        // 烘焙当前视图变换：导出即「把旋转 / 翻转后的图另存为新文件」，关闭保存提示才有意义
        o.Rotation = (int)Math.Round(Rotation);
        o.FlipX = FlipX;
        o.FlipY = FlipY;
    }

    public async Task ExportAsync(string outPath)
    {
        if (_current == null) return;
        var opts = new ExportOptions();
        SyncExport(opts);
        await Task.Run(() =>
        {
            try
            {
                ExportService.Export(_current, _engine.Layers, opts, outPath);
                StatusText = $"已导出: {Path.GetFileName(outPath)}";
            }
            catch (Exception ex)
            {
                StatusText = $"导出失败: {ex.Message}";
            }
        });
        // 导出成功后，视图变换已落到文件，未保存标记清除（再关闭不再提示）
        _transformDirty = false;
    }

    public void ResetView() => Fit();
}
