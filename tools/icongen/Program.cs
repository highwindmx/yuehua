// 阅画 App 图标生成器
// 设计：圆角矩形「墨蓝」底 + 楷体「画」字（宣纸米白）。
// 输出：src/Yuehua/Assets/yuehua.ico（多尺寸）与 yuehua.png（512，窗口/Avalonia 资源用）。
//
// 关于「四角透明」：.ico 容器里若以 BMP 存储小尺寸帧（16/24/32/48），会丢失 alpha 通道，
// 在资源管理器 / 任务栏里表现为「四角不透明」。强制把每个帧写成 PNG（PNG-in-ICO 支持 alpha）
// 即可让所有尺寸都保留圆角外面的透明像素。
//
// 用法： dotnet run --project tools/icongen -c Release
using System;
using System.IO;
using ImageMagick;
using ImageMagick.Drawing;

internal static class Program
{
    // 楷体（系统自带）。若不存在则回退到华文楷体 / 微软雅黑。
    private static readonly string[] KaiFonts =
    {
        @"C:\Windows\Fonts\simkai.ttf",
        @"C:\Windows\Fonts\STKAITI.TTF",
        @"C:\Windows\Fonts\msyh.ttc",
    };

    private static string PickFont()
    {
        foreach (var f in KaiFonts)
            if (File.Exists(f)) return f;
        throw new FileNotFoundException("未找到可用的中文字体（simkai.ttf / STKAITI.TTF / msyh.ttc）");
    }

    private static int Main(string[] args)
    {
        var font = PickFont();
        Console.WriteLine($"字体: {font}");

        var outDir = args.Length > 0
            ? args[0]
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "src", "Yuehua", "Assets"));
        Directory.CreateDirectory(outDir);

        const int master = 512;
        using var masterImg = Render(master, font);

        var pngPath = Path.Combine(outDir, "yuehua.png");
        masterImg.Write(pngPath);
        Console.WriteLine($"写出 {pngPath}");

        Analyze(pngPath);

        // ---- ICO：每个帧强制 PNG 编码，保留圆角外面的透明像素 ----
        var icoPath = Path.Combine(outDir, "yuehua.ico");
        using var coll = new MagickImageCollection();
        foreach (var s in new[] { 256, 128, 96, 64, 48, 32, 24, 16 })
        {
            var img = Render(s, font);
            ForceTransparentCorners(img);            // 兜底：极端小尺寸下圆角可能退化，强清四角
            img.Format = MagickFormat.Png;          // 关键：PNG-in-ICO 才带 alpha
            img.FilterType = FilterType.Lanczos;
            coll.Add(img);
        }
        coll.Write(icoPath, MagickFormat.Ico);
        Console.WriteLine($"写出 {icoPath}（{new FileInfo(icoPath).Length / 1024.0:F1} KB, {coll.Count} 个尺寸）");

        // ---- 量测：确认 ICO 每个尺寸的四个角都是透明的 ----
        VerifyIco(icoPath);
        return 0;
    }

    /// <summary>读回写出的 ICO，确认每个尺寸的四个角 alpha 均为 0（圆角外透明）。</summary>
    private static void VerifyIco(string icoPath)
    {
        using var back = new MagickImageCollection(icoPath);
        var ok = true;
        foreach (var f in back)
        {
            var w = (int)f.Width; var h = (int)f.Height;
            var corners = new[] { (0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1) };
            var alpha = f.HasAlpha ? "有 alpha" : "无 alpha(!)";
            var allClear = true;
            using (var px = f.GetPixels())
            {
                foreach (var (x, y) in corners)
                {
                    var p = px.GetPixel(x, y);
                    // PNG-in-ICO 各帧是 RGBA；取最后一个通道（alpha）
                    if (p.GetChannel(3) != 0) { allClear = false; break; }
                }
            }
            if (!allClear) ok = false;
            Console.WriteLine($"  ICO 帧 {w}x{h}: {alpha}，四角透明 = {allClear}");
        }
        Console.WriteLine(ok ? "校验通过：所有 ICO 帧四角均透明" : "校验失败：存在不透明角的 ICO 帧");
    }

    /// <summary>客观量测：字（米白像素）在画布中的包围盒与质心，用于校验居中。</summary>
    private static void Analyze(string path)
    {
        using var img = new MagickImage(path);
        var w = (int)img.Width;
        var minX = int.MaxValue; var maxX = -1; var minY = int.MaxValue; var maxY = -1;
        double sx = 0, sy = 0, n = 0;
        using (var px = img.GetPixels())
        {
            foreach (var p in px)
            {
                var r = p[0]; var g = p[1]; var b = p[2];
                if (r > 165 && g > 155 && b > 130)          // 米白字
                {
                    if (p.X < minX) minX = p.X;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.Y > maxY) maxY = p.Y;
                    sx += p.X; sy += p.Y; n++;
                }
            }
        }
        if (n == 0) { Console.WriteLine("量测：未找到字形像素"); return; }
        Console.WriteLine($"量测：画布 {w}, 字包围盒 x:[{minX},{maxX}] y:[{minY},{maxY}]");
        Console.WriteLine($"      左留白 {minX} / 右留白 {w - 1 - maxX}（差 {minX - (w - 1 - maxX)}）");
        Console.WriteLine($"      上留白 {minY} / 下留白 {(int)img.Height - 1 - maxY}（差 {minY - ((int)img.Height - 1 - maxY)}）");
        Console.WriteLine($"      墨迹质心 ({sx / n:F1}, {sy / n:F1})，画布中心 ({w / 2.0:F1}, {img.Height / 2.0:F1})");
    }

    private static MagickImage Render(int size, string font)
    {
        var u = (uint)size;

        // ---- 1. 渐变底（墨蓝 → 近墨黑，方向由 gradient 伪图决定，不做旋转以免产生杂色）----
        var rs = new MagickReadSettings { Width = (uint)size, Height = (uint)size };
        using var grad = new MagickImage("gradient:#406FA6-#1E3352", rs);

        // ---- 2. 圆角遮罩（矩形象限圆角，四角之外完全透明）----
        // 关键：遮罩底色必须是「透明」，白色圆角矩形内部才是可见区；若用 Black（不透明黑），
        // CopyAlpha 会把整张图变成不透明，四角也就不再透明（资源管理器/任务栏里看着像方块）。
        var radius = size * 0.22;
        var inset = size * 0.015;
        using var mask = new MagickImage(MagickColors.Transparent, u, u);
        new Drawables()
            .FillColor(MagickColors.White)
            .RoundRectangle(inset, inset, size - inset, size - inset, radius, radius)
            .Draw(mask);
        grad.Composite(mask, CompositeOperator.CopyAlpha);

        // ---- 3. 内描边（提升小尺寸时的轮廓可辨度）----
        new Drawables()
            .StrokeColor(new MagickColor("#4B74AC"))
            .StrokeWidth(Math.Max(1.0, size * 0.009))
            .FillColor(MagickColors.Transparent)
            .RoundRectangle(inset, inset, size - inset, size - inset, radius, radius)
            .Draw(grad);

        // ---- 4. 楷体「画」字：单独渲染 → 去透明边 → 缩放到目标占比 → 严格居中合成 ----
        using var text = new MagickImage(MagickColors.Transparent, u, u);
        new Drawables()
            .Font(font)
            .FontPointSize(size * 0.72)
            .FillColor(new MagickColor("#F7F1E3"))
            .TextAlignment(TextAlignment.Center)
            .Text(size / 2.0, size * 0.80, "画")
            .Draw(text);
        text.Trim();                                   // 裁到字形外接框，消除字体度量带来的偏移

        var target = size * 0.54;
        var k = Math.Min(target / text.Width, target / text.Height);
        text.FilterType = FilterType.Lanczos;
        text.Resize((uint)Math.Max(1, text.Width * k), (uint)Math.Max(1, text.Height * k));

        // 按「墨迹质心」而非包围盒对齐：楷体「画」的笔画分布不对称，
        // 包围盒居中会让字看起来偏一侧，质心居中视觉才平衡。
        double cx = text.Width / 2.0, cy = text.Height / 2.0, wsum = 0, sx = 0, sy = 0;
        using (var px = text.GetPixels())
        {
            foreach (var p in px)
            {
                var a = p[3];                            // Q8：RGBA，通道 3 为 alpha
                if (a == 0) continue;
                sx += p.X * a; sy += p.Y * a; wsum += a;
            }
        }
        if (wsum > 0) { cx = sx / wsum; cy = sy / wsum; }

        // 包围盒居中（上下留白均等）与质心居中（视觉重量平衡）各取一半：
        // 楷体「画」下部笔画密集，纯质心居中会让字偏低，纯包围盒居中则偏上。
        var boxX = (size - text.Width) / 2.0;
        var boxY = (size - text.Height) / 2.0;
        var gravX = size / 2.0 - cx;
        var gravY = size / 2.0 - cy;

        grad.Composite(text,
            (int)Math.Round((boxX + gravX) / 2.0),
            (int)Math.Round((boxY + gravY) / 2.0),
            CompositeOperator.Over);

        var sharp = (MagickImage)grad.Clone();
        sharp.Format = MagickFormat.Png32;
        return sharp;
    }

    /// <summary>把四个角的 2×2 像素强制设为完全透明，作为极小尺寸（如 16px）圆角退化的兜底。</summary>
    private static void ForceTransparentCorners(MagickImage img)
    {
        var w = (int)img.Width;
        var h = (int)img.Height;
        var zero = new byte[] { 0, 0, 0, 0 };   // Q8：RGBA，全 0 = 透明
        using var px = img.GetPixels();
        for (var i = 0; i < 2 && i < w; i++)
            for (var j = 0; j < 2 && j < h; j++)
            {
                px.SetPixel(i, j, zero);
                px.SetPixel(w - 1 - i, j, zero);
                px.SetPixel(i, h - 1 - j, zero);
                px.SetPixel(w - 1 - i, h - 1 - j, zero);
            }
    }
}
