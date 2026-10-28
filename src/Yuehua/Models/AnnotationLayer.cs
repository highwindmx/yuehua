using System.Text.Json.Serialization;

namespace Yuehua.Models;

/// <summary>一个标注图层，包含若干标注形状。可独立开关、决定是否烙进导出图。</summary>
public class AnnotationLayer
{
    public int Id { get; set; }
    public string Name { get; set; } = "标注层";
    public bool Visible { get; set; } = true;
    /// <summary>导出时是否把本层标注烙进图像。false 则导出纯净原图。</summary>
    public bool BakeIntoExport { get; set; } = true;
    public List<Annotation> Items { get; set; } = new();
}

/// <summary>侧车文件（同目录 .yuehua.json）的持久化结构，记录标注以便下次重载继续编辑。</summary>
public class AnnotationSidecar
{
    public string ImagePath { get; set; } = "";
    public List<AnnotationLayer> Layers { get; set; } = new();
}
