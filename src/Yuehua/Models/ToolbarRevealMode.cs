namespace Yuehua.Models;

/// <summary>
/// 浮动工具条（标注工具箱 / 缩略图条）的显现方式。
/// 顶栏已并入标题栏固定显示，不再参与隐藏；此枚举只作用于仍可浮动的卡片。
/// <see cref="EdgeHover"/> 仅为兼容旧设置文件保留，新版本不再可选，读到该值回落为 <see cref="IdleFade"/>。
/// </summary>
public enum ToolbarRevealMode
{
    /// <summary>常驻：浮动卡片一直显示。</summary>
    Always = 0,

    /// <summary>空闲淡出：鼠标静止一段时间后淡出，动一下即恢复。</summary>
    IdleFade = 1,

    /// <summary>【已废弃】原顶栏「悬停上缘」隐藏方式；保留仅为兼容旧设置，新版本回落为 IdleFade。</summary>
    EdgeHover = 2,
}
