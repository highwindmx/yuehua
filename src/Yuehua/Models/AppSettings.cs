namespace Yuehua.Models;

/// <summary>
/// 应用设置（工具条浮动样式 / 显示行为 / 浮动位置）。
/// 位置用可空 double：null 表示「默认位置」（顶栏居中贴顶、工具箱左中、缩略图条贴底），
/// 有值则为相对画布左上角的像素坐标。可空而非 NaN，避免 JSON 往返出现非法数值。
/// </summary>
public class AppSettings
{
    // ---------- 工具条样式 ----------
    /// <summary>左侧标注工具箱浮动。</summary>
    public bool ToolboxFloating { get; set; } = true;
    /// <summary>底部缩略图条浮动。</summary>
    public bool ThumbsFloating { get; set; }

    // ---------- 显示行为 ----------
    /// <summary>浮动工具条的显现方式：常驻 / 空闲淡出 / 悬停上缘（默认，最不遮挡画面）。</summary>
    public ToolbarRevealMode RevealMode { get; set; } = ToolbarRevealMode.IdleFade;

    /// <summary>
    /// 旧版「空闲自动淡出」开关，仅为从旧设置文件迁移保留：
    /// 读到非 null 说明是旧文件，迁移成 <see cref="RevealMode"/> 后置回 null，之后不再参与。
    /// </summary>
    public bool? AutoFade { get; set; }

    /// <summary>空闲自动淡出的空闲阈值（秒）。</summary>
    public double FadeDelaySeconds { get; set; } = 2;

    // ---------- 浮动位置 ----------
    public double? ToolboxX { get; set; }
    public double? ToolboxY { get; set; }
    public double? ThumbsX { get; set; }
    public double? ThumbsY { get; set; }
}
