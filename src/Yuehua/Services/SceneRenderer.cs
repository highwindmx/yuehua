using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Yuehua.Models;

namespace Yuehua.Services;

/// <summary>
/// 场景渲染：把「底图 + 各可见标注层」画进一个已应用坐标变换的 DrawingContext。
/// ImageView（实时预览）与 ExportService（导出烘焙）共用同一套绘制逻辑，保证所见即所得。
/// 坐标约定：所有几何均以「文档坐标」（显示位图像素空间）给出，由调用方通过 matrix 映射到目标画布。
/// </summary>
public static class SceneRenderer
{
    /// <summary>
    /// 生成整图的低分辨率副本（cols×rows），用于马赛克/模糊。
    /// 马赛克通过「把小图每个像素(1×1)放大成硬色块」实现，因此这里无需读像素；
    /// 模糊则直接把整张小图平滑放大到目标区域。结果按 cols 缓存在 smallBaseCache 中。
    /// </summary>
    public static Bitmap GetSmallBase(Bitmap source, int cols, Dictionary<int, Bitmap>? cache)
    {
        if (cache != null && cache.TryGetValue(cols, out var cached))
            return cached;

        // 与 ImageView 的文档坐标约定一致：使用逻辑尺寸 Size（见 ImageView.ComputeMatrix 注释）
        var sw = source.Size.Width;
        var sh = source.Size.Height;
        var scale = (double)cols / sw;
        var w = cols;
        var h = Math.Max(1, (int)(sh * scale));

        var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
        using (var dc = rtb.CreateDrawingContext(true))
        {
            dc.DrawImage(source, new Rect(0, 0, sw, sh), new Rect(0, 0, w, h));
        }

        if (cache != null)
            cache[cols] = rtb;
        return rtb;
    }

    public static void DrawScene(DrawingContext dc, Bitmap baseImage,
        IEnumerable<AnnotationLayer> layers, Matrix transform,
        Dictionary<int, Bitmap>? smallBaseCache = null)
    {
        using (dc.PushTransform(transform))
        {
            var w = baseImage.Size.Width;
            var h = baseImage.Size.Height;
            dc.DrawImage(baseImage, new Rect(0, 0, w, h), new Rect(0, 0, w, h));

            foreach (var layer in layers)
            {
                if (!layer.Visible) continue;
                foreach (var ann in layer.Items)
                    ann.Draw(dc, baseImage, smallBaseCache);
            }
        }
    }
}
