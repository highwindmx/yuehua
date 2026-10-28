using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Yuehua.Models;
using Yuehua.Services;
using Yuehua.ViewModels;

namespace Yuehua.Views;

/// <summary>
/// 图片画布：渲染「底图 + 各标注层 + 进行中的标注」，并把视图变换（缩放/平移/旋转/翻转）应用于坐标。
/// 所有几何以「文档坐标」（显示位图像素空间）存储，由本控件统一映射到屏幕坐标。
/// </summary>
public class ImageView : Control
{
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<ImageView, Bitmap?>(nameof(Source));

    public static readonly StyledProperty<IEnumerable<AnnotationLayer>?> LayersProperty =
        AvaloniaProperty.Register<ImageView, IEnumerable<AnnotationLayer>?>(nameof(Layers));

    public static readonly StyledProperty<Annotation?> InProgressProperty =
        AvaloniaProperty.Register<ImageView, Annotation?>(nameof(InProgress));

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<ImageView, double>(nameof(Zoom), 1.0);

    public static readonly StyledProperty<double> OffsetXProperty =
        AvaloniaProperty.Register<ImageView, double>(nameof(OffsetX));

    public static readonly StyledProperty<double> OffsetYProperty =
        AvaloniaProperty.Register<ImageView, double>(nameof(OffsetY));

    public static readonly StyledProperty<double> RotationProperty =
        AvaloniaProperty.Register<ImageView, double>(nameof(Rotation));

    public static readonly StyledProperty<bool> FlipXProperty =
        AvaloniaProperty.Register<ImageView, bool>(nameof(FlipX));

    public static readonly StyledProperty<bool> FlipYProperty =
        AvaloniaProperty.Register<ImageView, bool>(nameof(FlipY));

    public static readonly StyledProperty<ScaleMode> ViewScaleModeProperty =
        AvaloniaProperty.Register<ImageView, ScaleMode>(nameof(ViewScaleMode), ScaleMode.Uniform);

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<ImageView, IBrush?>(nameof(Background), Brushes.Black);

    /// <summary>当前「文档坐标 → 屏幕坐标」的真实缩放比（含适应窗口的 fit 系数），供状态栏显示百分比。</summary>
    public static readonly StyledProperty<double> ActualScaleProperty =
        AvaloniaProperty.Register<ImageView, double>(nameof(ActualScale), 1.0);

    public Bitmap? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public IEnumerable<AnnotationLayer>? Layers { get => GetValue(LayersProperty); set => SetValue(LayersProperty, value); }
    public Annotation? InProgress { get => GetValue(InProgressProperty); set => SetValue(InProgressProperty, value); }
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public double OffsetX { get => GetValue(OffsetXProperty); set => SetValue(OffsetXProperty, value); }
    public double OffsetY { get => GetValue(OffsetYProperty); set => SetValue(OffsetYProperty, value); }
    public double Rotation { get => GetValue(RotationProperty); set => SetValue(RotationProperty, value); }
    public bool FlipX { get => GetValue(FlipXProperty); set => SetValue(FlipXProperty, value); }
    public bool FlipY { get => GetValue(FlipYProperty); set => SetValue(FlipYProperty, value); }
    public ScaleMode ViewScaleMode { get => GetValue(ViewScaleModeProperty); set => SetValue(ViewScaleModeProperty, value); }
    public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public double ActualScale { get => GetValue(ActualScaleProperty); set => SetValue(ActualScaleProperty, value); }

    // 自定义绘制控件在布局完成前就可能触发首帧渲染，此时 Bounds 往往是 (0,0) 或一个
    // 过渡尺寸，会把图像错误地钉在角落。这里缓存「最终布局尺寸」（Arrange 是布局的最后一步，
    // 其 finalSize 即为控件真实尺寸），所有居中数学都以它为准，确保图像稳定居中。
    private double _arrangedW, _arrangedH;

    // 取「已排布尺寸」为准；极端情况下（Arrange 尚未发生）回退到 Bounds，避免画到 (0,0)。
    private double CanvasW => _arrangedW > 0 ? _arrangedW : Bounds.Width;
    private double CanvasH => _arrangedH > 0 ? _arrangedH : Bounds.Height;

    /// <summary>把文档坐标映射到屏幕坐标的矩阵（供指针交互做坐标反算）。</summary>
    public Matrix GetDisplayMatrix() => ComputeMatrix(CanvasW, CanvasH);

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        if (Math.Abs(_arrangedW - finalSize.Width) > 0.5 || Math.Abs(_arrangedH - finalSize.Height) > 0.5)
        {
            _arrangedW = finalSize.Width;
            _arrangedH = finalSize.Height;
            SyncActualScale(ComputeBaseScale(_arrangedW, _arrangedH)); // 状态栏百分比先跟上
            InvalidateVisual();   // 尺寸变化后强制重绘，使用正确的画布尺寸居中
        }
        return result;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == SourceProperty) _smallCache.Clear();
        if (e.Property == SourceProperty || e.Property == LayersProperty || e.Property == InProgressProperty
            || e.Property == ZoomProperty || e.Property == OffsetXProperty || e.Property == OffsetYProperty
            || e.Property == RotationProperty || e.Property == FlipXProperty || e.Property == FlipYProperty
            || e.Property == ViewScaleModeProperty || e.Property == BackgroundProperty)
        {
            InvalidateVisual();
        }
    }

    private readonly Dictionary<int, Bitmap> _smallCache = new();

    /// <summary>「文档 1 像素 → 屏幕 1 点」的缩放比（适应窗口时为 fit × Zoom，1:1 时为 Zoom）。</summary>
    private double ComputeBaseScale(double cw, double ch)
    {
        if (Source == null) return Zoom;
        // 文档坐标必须用位图的「逻辑尺寸」Size，而非 PixelSize：
        // Avalonia 的 DrawImage 目标矩形是逻辑坐标，而图片元数据里的 DPI 常非 96
        // （JPEG 多为 72），若混用 PixelSize 会导致底图被放大约 1.33 倍并偏出中心。
        var iw = Source.Size.Width;
        var ih = Source.Size.Height;
        if (ViewScaleMode == ScaleMode.Actual) return Zoom;

        var fit = Math.Min(cw / iw, ch / ih);
        if (!double.IsFinite(fit) || fit <= 0) fit = 1;
        return fit * Zoom;
    }

    private void SyncActualScale(double baseScale)
    {
        // 值未变时不会触发属性变更，因此不会造成 Render 重入循环
        if (Math.Abs(ActualScale - baseScale) > 1e-6) SetValue(ActualScaleProperty, baseScale);
    }

    private Matrix ComputeMatrix(double cw, double ch)
    {
        if (Source == null) return Matrix.Identity;
        var iw = Source.Size.Width;
        var ih = Source.Size.Height;
        var baseScale = ComputeBaseScale(cw, ch);

        var sx = baseScale * (FlipX ? -1 : 1);
        var sy = baseScale * (FlipY ? -1 : 1);

        // ★ Avalonia 的矩阵是行向量约定：p * (A * B) == (p * A) * B，即「左乘先执行」。
        // 因此下面的书写顺序就是实际执行顺序：先把图像中心移到原点 → 缩放/翻转 → 旋转
        // → 最后平移到「画布中心 + 用户平移量」。
        // 之前写成 T(中心) * R * S * T(-w/2,-h/2)，等效于先平移到画布中心再缩放，
        // 只有在缩放恰好为 1（1:1）时才是居中的，其它比例会整体甩到右下（s>1）或左上（s<1）。
        var m = Matrix.CreateTranslation(-iw / 2, -ih / 2);
        m *= Matrix.CreateScale(sx, sy);
        m *= Matrix.CreateRotation(Rotation * Math.PI / 180);
        m *= Matrix.CreateTranslation(cw / 2 + OffsetX, ch / 2 + OffsetY);

        SyncActualScale(baseScale);
        return m;
    }

    public override void Render(DrawingContext context)
    {
        var cw = CanvasW;
        var ch = CanvasH;
        context.DrawRectangle(Background ?? Brushes.Black, null, new Rect(0, 0, cw, ch));
        if (cw <= 0 || ch <= 0) return;
        if (Source == null) return;

        var m = ComputeMatrix(cw, ch);
        SceneRenderer.DrawScene(context, Source, Layers ?? Enumerable.Empty<AnnotationLayer>(), m, _smallCache);

        if (InProgress != null)
        {
            using (context.PushTransform(m))
                InProgress.Draw(context, Source, _smallCache);
        }
    }
}
