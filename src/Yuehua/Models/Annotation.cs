using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Yuehua.Models;

/// <summary>
/// 标注基类。所有标注都以「文档坐标」（即当前显示位图像素空间）存储，
/// 渲染时由 ImageView / 导出器统一做坐标变换。
/// 使用 System.Text.Json 源生成（JsonContext）做多态序列化，兼容 NativeAOT。
/// </summary>
[JsonDerivedType(typeof(RectangleAnnotation), "rect")]
[JsonDerivedType(typeof(EllipseAnnotation), "ellipse")]
[JsonDerivedType(typeof(LineAnnotation), "line")]
[JsonDerivedType(typeof(ArrowAnnotation), "arrow")]
[JsonDerivedType(typeof(TextAnnotation), "text")]
[JsonDerivedType(typeof(FreehandAnnotation), "freehand")]
[JsonDerivedType(typeof(MosaicAnnotation), "mosaic")]
[JsonDerivedType(typeof(BlurAnnotation), "blur")]
public abstract class Annotation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int LayerId { get; set; }
    public string Color { get; set; } = "#FFFF0000";
    public double StrokeWidth { get; set; } = 3;

    /// <summary>
    /// 标注的包围盒（派生量，不参与序列化）。
    /// ★ 必须 [JsonIgnore]：宽或高为 0 的退化几何（点一下不拖、水平/垂直直线）
    /// 会让 Avalonia 的 Rect 带出无穷大值，System.Text.Json 直接抛
    /// 「infinity cannot be written as valid JSON」，进而让「历史快照 / 侧车保存」失败。
    /// </summary>
    [JsonIgnore]
    public abstract Rect Bounds { get; }

    public abstract void Draw(DrawingContext dc, Bitmap baseImage, Dictionary<int, Bitmap>? smallBaseCache);

    protected static IBrush BrushFromColor(string c)
        => new SolidColorBrush(Avalonia.Media.Color.Parse(c));

    protected static Pen MakePen(string color, double width)
        => new(BrushFromColor(color), width)
        {
            LineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };

    /// <summary>将任意矩形归一化为正值宽高（拖拽方向任意都安全）。</summary>
    public static void Normalize(ref double x, ref double y, ref double w, ref double h)
    {
        if (w < 0) { x += w; w = -w; }
        if (h < 0) { y += h; h = -h; }
    }
}

/// <summary>
/// 可序列化的二维点。标注里不能直接存 Avalonia.Point：它在 NativeAOT 下没有无参构造函数，
/// System.Text.Json 源生成无法反序列化，会导致含画笔（Freehand）标注的撤销 / 重做 / 侧车读写崩溃。
/// 渲染时再按需转成 Avalonia.Point。
/// </summary>
public sealed class DPoint
{
    public double X { get; set; }
    public double Y { get; set; }
    public DPoint() { }
    public DPoint(double x, double y) { X = x; Y = y; }
}

public class RectangleAnnotation : Annotation
{
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public bool Filled { get; set; }

    public override Rect Bounds => new(X, Y, W, H);

    public override void Draw(DrawingContext dc, Bitmap baseImage, Dictionary<int, Bitmap>? cache)
    {
        var b = Bounds;
        dc.DrawRectangle(Filled ? BrushFromColor(Color) : null, MakePen(Color, StrokeWidth), b);
    }
}

public class EllipseAnnotation : Annotation
{
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public bool Filled { get; set; }

    public override Rect Bounds => new(X, Y, W, H);

    public override void Draw(DrawingContext dc, Bitmap baseImage, Dictionary<int, Bitmap>? cache)
    {
        var b = Bounds;
        dc.DrawEllipse(Filled ? BrushFromColor(Color) : null, MakePen(Color, StrokeWidth),
            new Point(b.Center.X, b.Center.Y), b.Width / 2, b.Height / 2);
    }
}

public class LineAnnotation : Annotation
{
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }

    public override Rect Bounds => new Rect(Math.Min(X1, X2), Math.Min(Y1, Y2),
        Math.Abs(X2 - X1), Math.Abs(Y2 - Y1));

    public override void Draw(DrawingContext dc, Bitmap baseImage, Dictionary<int, Bitmap>? cache)
        => dc.DrawLine(MakePen(Color, StrokeWidth), new Point(X1, Y1), new Point(X2, Y2));
}

public class ArrowAnnotation : Annotation
{
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }

    public override Rect Bounds => new Rect(Math.Min(X1, X2), Math.Min(Y1, Y2),
        Math.Abs(X2 - X1), Math.Abs(Y2 - Y1));

    public override void Draw(DrawingContext dc, Bitmap baseImage, Dictionary<int, Bitmap>? cache)
    {
        var pen = MakePen(Color, StrokeWidth);
        var p1 = new Point(X1, Y1);
        var p2 = new Point(X2, Y2);
        dc.DrawLine(pen, p1, p2);

        var angle = Math.Atan2(Y2 - Y1, X2 - X1);
        var head = 10 + StrokeWidth * 2.5;
        var spread = Math.PI * 150 / 180;
        var a1 = angle + spread;
        var a2 = angle - spread;
        dc.DrawLine(pen, p2, new Point(X2 + head * Math.Cos(a1), Y2 + head * Math.Sin(a1)));
        dc.DrawLine(pen, p2, new Point(X2 + head * Math.Cos(a2), Y2 + head * Math.Sin(a2)));
    }
}

public class TextAnnotation : Annotation
{
    public double X { get; set; }
    public double Y { get; set; }
    public string Text { get; set; } = "文字";
    public double FontSize { get; set; } = 24;

    public override Rect Bounds
    {
        get
        {
            var w = Text.Length * FontSize * 0.6;
            return new Rect(X, Y, Math.Max(10, w), FontSize * 1.3);
        }
    }

    public override void Draw(DrawingContext dc, Bitmap baseImage, Dictionary<int, Bitmap>? cache)
    {
        var ft = new FormattedText(
            Text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Liberation Sans", FontStyle.Normal, FontWeight.Normal),
            FontSize,
            BrushFromColor(Color));
        dc.DrawText(ft, new Point(X, Y));
    }
}

public class FreehandAnnotation : Annotation
{
    public List<DPoint> Points { get; set; } = new();

    public override Rect Bounds
    {
        get
        {
            if (Points.Count == 0) return new Rect(0, 0, 0, 0);
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in Points)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }
    }

    public override void Draw(DrawingContext dc, Bitmap baseImage, Dictionary<int, Bitmap>? cache)
    {
        if (Points.Count < 2) return;
        var geo = new StreamGeometry();
        using (var sgc = geo.Open())
        {
            sgc.BeginFigure(new Point(Points[0].X, Points[0].Y), false);
            for (var i = 1; i < Points.Count; i++)
                sgc.LineTo(new Point(Points[i].X, Points[i].Y));
            sgc.EndFigure(false);
        }
        dc.DrawGeometry(null, MakePen(Color, StrokeWidth), geo);
    }
}

/// <summary>马赛克：将区域内图像做块状像素化处理。硬块由「小图每像素(1×1)放大成色块」实现，无需读像素。</summary>
public class MosaicAnnotation : Annotation
{
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public double Strength { get; set; } = 12; // 1(轻) .. 30(重)

    public override Rect Bounds => new(X, Y, W, H);

    public override void Draw(DrawingContext dc, Bitmap baseImage, Dictionary<int, Bitmap>? cache)
    {
        var b = Bounds;
        var cols = Math.Clamp((int)(Strength * 4), 4, 256);
        var small = Services.SceneRenderer.GetSmallBase(baseImage, cols, cache);
        var sw = small.PixelSize.Width;
        var sh = small.PixelSize.Height;
        if (sw <= 0 || sh <= 0) return;
        var bw = b.Width / sw;
        var bh = b.Height / sh;
        for (var i = 0; i < sw; i++)
        {
            for (var j = 0; j < sh; j++)
            {
                var dest = new Rect(b.X + i * bw, b.Y + j * bh, bw, bh);
                dc.DrawImage(small, new Rect(i, j, 1, 1), dest);
            }
        }
    }
}

/// <summary>模糊遮挡：区域内做平滑模糊（脱敏场景，例如遮受试者编号）。</summary>
public class BlurAnnotation : Annotation
{
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public double Strength { get; set; } = 12;

    public override Rect Bounds => new(X, Y, W, H);

    public override void Draw(DrawingContext dc, Bitmap baseImage, Dictionary<int, Bitmap>? cache)
    {
        var b = Bounds;
        var cols = Math.Clamp((int)(Strength * 4), 4, 256);
        var small = Services.SceneRenderer.GetSmallBase(baseImage, cols, cache);
        dc.DrawImage(small, new Rect(0, 0, small.PixelSize.Width, small.PixelSize.Height), b);
    }
}
