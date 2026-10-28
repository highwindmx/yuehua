namespace Yuehua.Models;

/// <summary>
/// 应用设置（工具条浮动样式 / 显示行为 / 浮动位置）。
/// 位置用可空 double：null 表示「默认位置」（顶栏居中贴顶、工具箱左中、缩略图条贴底），
/// 有值则为相对画布左上角的像素坐标。可空而非 NaN，避免 JSON 往返出现非法数值。
/// </summary>
public class AppSettings
{
    // ---------- 工具条样式 ----------
    /// <summary>顶栏工具条浮于画布之上（可拖动、圆角阴影）。false = 贴顶固定。</summary>
    public bool TopBarFloating { get; set; } = true;
    /// <summary>左侧标注工具箱浮动。</summary>
    public bool ToolboxFloating { get; set; } = true;
    /// <summary>底部缩略图条浮动。</summary>
    public bool ThumbsFloating { get; set; }

    // ---------- 显示行为 ----------
    /// <summary>空闲自动淡出（鼠标静止一段时间后隐藏工具条，动一下即浮现）。false = 常驻。</summary>
    public bool AutoFade { get; set; }
    /// <summary>自动淡出的空闲阈值（秒）。</summary>
    public double FadeDelaySeconds { get; set; } = 2;

    // ---------- 浮动位置 ----------
    public double? TopBarX { get; set; }
    public double? TopBarY { get; set; }
    public double? ToolboxX { get; set; }
    public double? ToolboxY { get; set; }
    public double? ThumbsX { get; set; }
    public double? ThumbsY { get; set; }
}
