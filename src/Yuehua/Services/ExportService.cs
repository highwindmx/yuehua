using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ImageMagick;
using Yuehua.Models;

namespace Yuehua.Services;

/// <summary>
/// 导出服务：把「底图 + 标注」烘焙成目标文件。
/// 像素尺寸模式 → 指定宽高重采样；文件体积模式 → 对质量(有损)或尺寸(无损)做二分命中。
/// 底层绘制复用 SceneRenderer（所见即所得），Magick.NET 负责格式/质量编码与体积控制。
/// </summary>
public static class ExportService
{
    public static void Export(LoadedImage loaded, IEnumerable<AnnotationLayer> allLayers,
        ExportOptions opt, string outPath)
    {
        var layers = opt.BakeAnnotations
            ? allLayers.Where(l => l.Visible && l.BakeIntoExport).ToList()
            : new List<AnnotationLayer>();

        var fmt = MapFormat(opt.Format);
        // 文档坐标 = 显示位图的逻辑尺寸（与 ImageView / SceneRenderer 保持同一套坐标约定）
        var dispSize = loaded.Display.Size;
        double docW = dispSize.Width, docH = dispSize.Height;
        if (opt.CropRect is { } cr) { docW = cr.Width; docH = cr.Height; }

        if (opt.Mode == ExportMode.Pixel)
        {
            int w, h;
            if (opt.KeepAspect)
            {
                var r = Math.Min((double)opt.TargetWidth / docW, (double)opt.TargetHeight / docH);
                if (!double.IsFinite(r) || r <= 0) r = 1;
                w = Math.Max(1, (int)(docW * r));
                h = Math.Max(1, (int)(docH * r));
            }
            else
            {
                w = Math.Max(1, opt.TargetWidth);
                h = Math.Max(1, opt.TargetHeight);
            }
            using var mag = CompositeAt(loaded, layers, opt, w, h);
            mag.Format = fmt;
            if (IsLossy(opt.Format)) mag.Quality = (uint)opt.Quality;
            ApplyViewTransform(mag, opt);
            mag.Write(outPath);
            return;
        }

        // FileSize 模式
        if (IsLossy(opt.Format))
        {
            const int cap = 4000;
            var rr = Math.Min(1.0, (double)cap / Math.Max(docW, docH));
            var w = Math.Max(1, (int)(docW * rr));
            var h = Math.Max(1, (int)(docH * rr));
            using var mag = CompositeAt(loaded, layers, opt, w, h);
            mag.Format = fmt;
            var lo = 1; var hi = 100; var best = opt.Quality;
            for (var i = 0; i < 20 && lo <= hi; i++)
            {
                var mid = (lo + hi) / 2;
                mag.Quality = (uint)mid;
                var len = Measure(mag, fmt);
                if (len > opt.TargetBytes) lo = mid + 1;
                else { best = mid; hi = mid - 1; }
            }
            mag.Quality = (uint)best;
            ApplyViewTransform(mag, opt);
            mag.Write(outPath);
        }
        else
        {
            // 无损格式：二分缩放尺寸命中目标体积（先压到上限，避免对超大图反复全尺寸编码）
            var rr = Math.Min(1.0, (double)3000 / Math.Max(docW, docH));
            var loS = 0.08 * rr; var hiS = rr;
            var bestW = Math.Max(1, (int)(docW * rr)); var bestH = Math.Max(1, (int)(docH * rr));
            for (var i = 0; i < 12; i++)
            {
                var mid = (loS + hiS) / 2;
                var w = Math.Max(1, (int)(docW * mid));
                var h = Math.Max(1, (int)(docH * mid));
                using var mag = CompositeAt(loaded, layers, opt, w, h);
                mag.Format = fmt;
                var len = Measure(mag, fmt);
                if (len > opt.TargetBytes) hiS = mid;
                else { bestW = w; bestH = h; loS = mid; }
            }
            using var outMag = CompositeAt(loaded, layers, opt, bestW, bestH);
            outMag.Format = fmt;
            ApplyViewTransform(outMag, opt);
            outMag.Write(outPath);
        }
    }

    /// <summary>
    /// 把视图变换（翻转 → 旋转）烘焙进已合成好的位图，顺序与 ImageView 显示一致
    /// （先翻转后旋转：显示矩阵 p' = T(中心)·R·S(翻转)·T(-中心)·p）。
    /// </summary>
    private static void ApplyViewTransform(MagickImage mag, ExportOptions opt)
    {
        if (opt.FlipX) mag.Flop();
        if (opt.FlipY) mag.Flip();
        if (opt.Rotation != 0) mag.Rotate(opt.Rotation);
    }

    /// <summary>
    /// 把底图(原始分辨率)与标注合成到一张 w×h 的位图，返回 PNG 编码的 MagickImage。
    /// 关键：底图用「原始分辨率」绘制（原始→输出矩阵），标注用「显示坐标→输出矩阵」绘制，
    /// 二者经比例因子 f(原始/显示)对齐，从而导出图是原始清晰度而非被显示降采样拖垮。
    /// </summary>
    private static MagickImage CompositeAt(LoadedImage loaded, IEnumerable<AnnotationLayer> layers,
        ExportOptions opt, int w, int h)
    {
        // 底图：原始分辨率位图
        var origBytes = loaded.Full.ToByteArray(MagickFormat.Png);
        var origBmp = new Bitmap(new MemoryStream(origBytes));
        // 标注马赛克/模糊的小图参考用显示位图（与显示坐标一致）
        var dispBmp = loaded.Display;

        // 比例因子同样基于逻辑尺寸，避免图片 DPI 非 96 时标注与底图错位
        double f = origBmp.Size.Width / dispBmp.Size.Width; // 原始/显示 比例
        double crX = 0, crY = 0, crW = dispBmp.Size.Width, crH = dispBmp.Size.Height;
        if (opt.CropRect is { } cr)
        {
            crX = cr.X; crY = cr.Y; crW = cr.Width; crH = cr.Height;
        }

        double sOutBase = (double)w / (crW * f); // 原始→输出
        double sOutAnn = (double)w / crW;        // 显示→输出
        var mBase = Matrix.CreateScale(sOutBase, sOutBase)
                    * Matrix.CreateTranslation(-crX * f * sOutBase, -crY * f * sOutBase);
        var mAnn = Matrix.CreateScale(sOutAnn, sOutAnn)
                   * Matrix.CreateTranslation(-crX * sOutAnn, -crY * sOutAnn);

        var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
        using (var dc = rtb.CreateDrawingContext(true))
        {
            using (dc.PushTransform(mBase))
                dc.DrawImage(origBmp, new Rect(0, 0, origBmp.Size.Width, origBmp.Size.Height),
                                     new Rect(0, 0, origBmp.Size.Width, origBmp.Size.Height));

            foreach (var layer in layers)
            {
                if (!layer.Visible) continue;
                foreach (var ann in layer.Items)
                {
                    using (dc.PushTransform(mAnn))
                        ann.Draw(dc, dispBmp, null);
                }
            }
        }

        origBmp.Dispose();
        using var ms = new MemoryStream();
        rtb.Save(ms, new PngBitmapEncoderOptions());
        ms.Position = 0;
        // 无可用渲染后端（如无头环境）时 Save 会得到 0 字节，直接失败而不是让下游解码挂起
        if (ms.Length == 0)
            throw new InvalidOperationException("标注合成失败：渲染后端返回空位图");
        return new MagickImage(ms);
    }

    private static long Measure(MagickImage mag, MagickFormat fmt)
    {
        using var ms = new MemoryStream();
        mag.Format = fmt;
        mag.Write(ms);
        return ms.Length;
    }

    private static MagickFormat MapFormat(string f) => f.ToLowerInvariant() switch
    {
        "png" => MagickFormat.Png,
        "jpeg" or "jpg" => MagickFormat.Jpeg,
        "webp" => MagickFormat.WebP,
        "bmp" => MagickFormat.Bmp,
        "tiff" or "tif" => MagickFormat.Tiff,
        _ => MagickFormat.Jpeg,
    };

    private static bool IsLossy(string f) => f is "jpeg" or "jpg" or "webp";

    private static FilterType MapFilter(InterpolationFilter f) => f switch
    {
        InterpolationFilter.Nearest => FilterType.Point,
        InterpolationFilter.Bilinear => FilterType.Triangle,
        InterpolationFilter.Bicubic => FilterType.Cubic,
        InterpolationFilter.Lanczos => FilterType.Lanczos,
        _ => FilterType.Lanczos,
    };
}
