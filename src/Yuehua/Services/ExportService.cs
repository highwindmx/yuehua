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

        // ICO 走专用分支：强制 ≤256 多分辨率 + 正方形透明画布（ICO 目录项宽高仅 1 字节，
        // 0 表示 256，故任一边不得超 256；正方形是 Windows 显示约定，避免非正方形被拉伸）。
        if (opt.Format == "ico")
        {
            ExportIco(loaded, layers, opt, outPath);
            return;
        }

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

    /// <summary>
    /// 导出 ICO：把合成好的底图（含标注与视图变换）生成一组标准分辨率
    /// （16/24/32/48/64/72/96/128/256）的正方形图标，写入单个 .ico。
    /// ICO 目录项宽高仅 1 字节（0 表示 256），故任一边不得超 256。
    /// 背景：透明 = 等比居中贴到真·透明正方形画布（保留 alpha，适合带透明通道的图）；
    /// 白/黑/自定义 = 裁切铺满正方形、再把透明区域压平到指定底色（默认透明，不再意外显黑）。
    /// 每帧以 PNG 嵌入、手动组装 ICO 容器：绕开 Magick ICO 集合写出对 ≤255 小帧采用「调色板 BMP + 1bit 掩码」
    /// 而把主色误标为透明的怪象（实测会让 16×16 出现大量杂散透明）。Windows Vista+ 原生支持 PNG-ICO。
    /// </summary>
    private static void ExportIco(LoadedImage loaded, List<AnnotationLayer> layers, ExportOptions opt, string outPath)
    {
        int sw = loaded.OriginalWidth, sh = loaded.OriginalHeight;
        if (sw <= 0 || sh <= 0) throw new InvalidOperationException("无法读取原图尺寸");

        // 全分辨率合成（含标注 + 视图变换烘焙），再统一缩到工作边长，
        // 避免逐尺寸全分辨率克隆吃内存。
        using var baseImg = CompositeAt(loaded, layers, opt, sw, sh);
        ApplyViewTransform(baseImg, opt);

        int srcMax = Math.Max(sw, sh);
        // ICO 尺寸由专属选项 IcoMaxSize 决定（对话框里的下拉），不再误用栅格导出的「宽/高」。
        int maxDim = Math.Min(256, Math.Max(16, opt.IcoMaxSize));
        int workMax = Math.Min(maxDim, srcMax); // 永不放大原图
        double rb = Math.Min((double)workMax / sw, (double)workMax / sh);
        baseImg.Resize((uint)Math.Max(1, (int)Math.Round(sw * rb)),
                       (uint)Math.Max(1, (int)Math.Round(sh * rb)));

        var std = new[] { 16, 24, 32, 48, 64, 72, 96, 128,256 };
        var sizes = std.Where(s => s <= maxDim && s <= srcMax).ToList();
        if (maxDim <= srcMax && !sizes.Contains(maxDim)) sizes.Add(maxDim);
        if (sizes.Count == 0) sizes.Add(workMax);
        sizes = sizes.Distinct().OrderBy(s => s).ToList();

        // 透明 → 真·透明(Contain 式)；其余 → 铺满(Cover)并压平到指定底色
        MagickColor? flat = opt.IcoBackground switch
        {
            IcoBackgroundMode.White => MagickColors.White,
            IcoBackgroundMode.Black => MagickColors.Black,
            IcoBackgroundMode.Custom => SafeColor(opt.IcoBgColor, MagickColors.White),
            _ => null,
        };
        bool transparent = flat == null;

        var frames = new List<(int w, int h, byte[] png)>();
        foreach (var s in sizes)
        {
            MagickImage frame;
            var piece = (MagickImage)baseImg.Clone();
            if (transparent)
            {
                // 适应：等比缩放到 ≤s，居中贴到透明正方形画布
                double r = Math.Min((double)s / piece.Width, (double)s / piece.Height);
                int tw = Math.Max(1, (int)Math.Round(piece.Width * r));
                int th = Math.Max(1, (int)Math.Round(piece.Height * r));
                piece.Resize((uint)tw, (uint)th);
                var icon = new MagickImage(MagickColors.Transparent, (uint)s, (uint)s);
                // 必须显式 Over：Magick.NET 的 Composite(image, Gravity) 重载默认并非 Over，
                // 直接用会把源图整个丢掉，只留下全透明画布（导出成一张空白图标）。
                icon.Composite(piece, Gravity.Center, CompositeOperator.Over);
                piece.Dispose(); // piece 仅作合成源
                frame = icon;
            }
            else
            {
                // 铺满：按较大边放大到 ≥s，再居中裁切正中 s×s，然后压平到指定底色
                // （不再用 Alpha(Off) 把透明区域变成黑，而是显式填白/黑/自定义色）
                double scale = Math.Max((double)s / piece.Width, (double)s / piece.Height);
                piece.Resize((uint)Math.Max(1, (int)Math.Round(piece.Width * scale)),
                             (uint)Math.Max(1, (int)Math.Round(piece.Height * scale)));
                piece.Crop((uint)s, (uint)s, Gravity.Center);
                var canvas = new MagickImage(flat!, (uint)s, (uint)s);
                // 同上：显式 Over，否则只剩纯底色、源图丢失
                canvas.Composite(piece, Gravity.Center, CompositeOperator.Over);
                piece.Dispose();
                frame = canvas;
            }

            using var ms = new MemoryStream();
            // 强制 RGBA 8 位 PNG：Magick 默认会按「最小体积」把少色图量化成 1 位黑白调色板，
            // 导致彩色图标在某些查看器里显黑 / 打不开。color-type=6 直接指定真彩+alpha。
            frame.Settings.SetDefine("png:color-type", "6");
            frame.Depth = 8;
            frame.Write(ms, MagickFormat.Png); // 各帧以 PNG 嵌入（见方法注释）
            frames.Add((s, s, ms.ToArray()));
            frame.Dispose(); // frames 仅存字节，源图释放
        }

        WriteIcoFromPngFrames(frames, outPath);
    }

    /// <summary>把十六进制颜色（#RGB / #RRGGBB）解析为 MagickColor；非法则回退 fallback。</summary>
    private static MagickColor SafeColor(string? hex, MagickColor fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return new MagickColor(hex!); }
        catch { return fallback; }
    }

    /// <summary>
    /// 把一组 PNG 帧字节手动组装成 ICO 文件（每帧以 PNG 形式嵌入；Windows Vista+ 支持）。
    /// 不依赖 Magick 的 ICO 集合写出，从而避开小帧被编码成带透明掩码的调色板 BMP 的怪象。
    /// </summary>
    private static void WriteIcoFromPngFrames(List<(int w, int h, byte[] png)> frames, string outPath)
    {
        if (!outPath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
            outPath += ".ico";
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((ushort)0);                 // reserved
        bw.Write((ushort)1);                 // image type = icon
        bw.Write((ushort)frames.Count);
        int offset = 6 + 16 * frames.Count;
        foreach (var f in frames)
        {
            bw.Write((byte)(f.w >= 256 ? 0 : (byte)f.w));   // 0 表示 256
            bw.Write((byte)(f.h >= 256 ? 0 : (byte)f.h));
            bw.Write((byte)0);                // colorCount
            bw.Write((byte)0);                // reserved
            bw.Write((ushort)1);              // planes
            bw.Write((ushort)32);             // bitCount（PNG 含 alpha）
            bw.Write((uint)f.png.Length);     // bytesInRes
            bw.Write((uint)offset);           // imageOffset
            offset += f.png.Length;
        }
        foreach (var f in frames) bw.Write(f.png);
        File.WriteAllBytes(outPath, ms.ToArray());
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
        "ico" => MagickFormat.Icon,
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
