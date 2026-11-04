using Avalonia;

namespace Yuehua.Models;

public enum ExportMode
{
    /// <summary>指定像素尺寸（宽高）重采样导出，可选插值算法。</summary>
    Pixel,
    /// <summary>指定目标文件体积，自动调整质量/尺寸命中。</summary>
    FileSize,
}

public enum InterpolationFilter
{
    Nearest,
    Bilinear,
    Bicubic,
    Lanczos,
}

/// <summary>ICO 背景：透明=真·透明（等比居中贴到透明正方形画布）；白/黑/自定义=铺满正方形并把
/// 透明区域压平到该底色（不再是意外黑底）。</summary>
public enum IcoBackgroundMode
{
    /// <summary>真透明：等比缩放居中，四周留透明边（适合带透明通道的图）。</summary>
    Transparent,
    /// <summary>白色底：铺满正方形，透明区域填白。</summary>
    White,
    /// <summary>黑色底：铺满正方形，透明区域填黑。</summary>
    Black,
    /// <summary>自定义底色：铺满正方形，透明区域填 IcoBgColor。</summary>
    Custom,
}

/// <summary>导出参数。格式用字符串以避免 Model 层直接依赖 ImageMagick（利于 NativeAOT）。</summary>
public class ExportOptions
{
    public ExportMode Mode { get; set; } = ExportMode.Pixel;
    public int TargetWidth { get; set; } = 1920;
    public int TargetHeight { get; set; } = 1080;
    /// <summary>保持原图宽高比（Pixel 模式生效）。</summary>
    public bool KeepAspect { get; set; } = true;
    public InterpolationFilter Filter { get; set; } = InterpolationFilter.Lanczos;
    public int Quality { get; set; } = 90;
    /// <summary>目标文件体积（字节），FileSize 模式生效。</summary>
    public long TargetBytes { get; set; } = 500 * 1024;
    /// <summary>导出格式：jpeg / png / webp / bmp / tiff。</summary>
    public string Format { get; set; } = "jpeg";
    /// <summary>是否将标注烙进导出图；false 导出纯净原图。</summary>
    public bool BakeAnnotations { get; set; } = true;
    /// <summary>裁剪区域（文档坐标）；为 null 表示整图。</summary>
    public Rect? CropRect { get; set; }

    /// <summary>导出时烘焙的视图旋转角度（度，90 的倍数）；0 表示不旋转。关闭保存提示用。</summary>
    public int Rotation { get; set; }
    /// <summary>导出时烘焙的水平翻转。</summary>
    public bool FlipX { get; set; }
    /// <summary>导出时烘焙的垂直翻转。</summary>
    public bool FlipY { get; set; }
    /// <summary>ICO 背景（仅 ico 格式生效）：透明 / 白 / 黑 / 自定义。</summary>
    public IcoBackgroundMode IcoBackground { get; set; } = IcoBackgroundMode.Transparent;
    /// <summary>ICO 自定义底色（IcoBackground==Custom 时生效），十六进制如 #RRGGBB。</summary>
    public string IcoBgColor { get; set; } = "#FFFFFF";
    /// <summary>ICO 最大边长（仅 ico 生效，16–256）。导出会生成所有 ≤ 该值的标准尺寸帧。</summary>
    public int IcoMaxSize { get; set; } = 256;
}
