using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ShunSeek.App.Controls;

/// <summary>
/// 液态玻璃面板：连续曲率圆角 + 玻璃填充 + 边缘高光。
///
/// 为什么不用 Border：WPF 的 CornerRadius 只能画正圆弧（rx == ry），
/// 而"连续曲率"（squircle / 超椭圆）观感来自**椭圆角**——垂直半径比水平半径大，
/// 角部过渡更柔和、更饱满。这里自己用 StreamGeometry 拼椭圆形圆角并裁切子元素。
///
/// 填充与描边都做成可绑定的 Brush，方便外面用 LinearGradientBrush 堆玻璃渐变和边缘高光。
/// </summary>
public sealed class LiquidPanel : Decorator
{
    public static readonly DependencyProperty RadiusXProperty = DependencyProperty.Register(
        nameof(RadiusX), typeof(double), typeof(LiquidPanel),
        new FrameworkPropertyMetadata(28.0, FrameworkPropertyMetadataOptions.AffectsRender, OnGeometryChanged));

    public static readonly DependencyProperty RadiusYProperty = DependencyProperty.Register(
        nameof(RadiusY), typeof(double), typeof(LiquidPanel),
        new FrameworkPropertyMetadata(42.0, FrameworkPropertyMetadataOptions.AffectsRender, OnGeometryChanged));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(LiquidPanel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(LiquidPanel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(LiquidPanel),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>水平方向的圆角半径。</summary>
    public double RadiusX
    {
        get => (double)GetValue(RadiusXProperty);
        set => SetValue(RadiusXProperty, value);
    }

    /// <summary>垂直方向的圆角半径（比 RadiusX 大，才有连续曲率的饱满感）。</summary>
    public double RadiusY
    {
        get => (double)GetValue(RadiusYProperty);
        set => SetValue(RadiusYProperty, value);
    }

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>边缘高光（沿圆角走的 1px 渐变描边，玻璃的"厚度"）。</summary>
    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    private Geometry? _geometry;

    private static void OnGeometryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((LiquidPanel)d)._geometry = null;

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _geometry = null;
        if (Child != null) Child.Clip = BuildGeometry(sizeInfo.NewSize);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var geometry = _geometry ??= BuildGeometry(RenderSize);
        if (geometry == null) return;

        if (Fill != null) dc.DrawGeometry(Fill, null, geometry);
        if (Stroke != null && StrokeThickness > 0)
        {
            var pen = new System.Windows.Media.Pen(Stroke, StrokeThickness);
            pen.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var size = base.ArrangeOverride(arrangeSize);
        if (Child != null) Child.Clip = BuildGeometry(size);
        return size;
    }

    /// <summary>椭圆角矩形：四段 ArcTo，Size 的 x/y 半径分开给，就是连续曲率的观感来源。</summary>
    private Geometry? BuildGeometry(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0) return null;

        double rx = Math.Min(RadiusX, size.Width / 2);
        double ry = Math.Min(RadiusY, size.Height / 2);
        double w = size.Width;
        double h = size.Height;
        var arcSize = new Size(rx, ry);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(rx, 0), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(w - rx, 0), true, false);
            ctx.ArcTo(new Point(w, ry), arcSize, 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(w, h - ry), true, false);
            ctx.ArcTo(new Point(w - rx, h), arcSize, 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(rx, h), true, false);
            ctx.ArcTo(new Point(0, h - ry), arcSize, 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(0, ry), true, false);
            ctx.ArcTo(new Point(rx, 0), arcSize, 0, false, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    protected override void OnVisualChildrenChanged(DependencyObject? visualAdded, DependencyObject? visualRemoved)
    {
        base.OnVisualChildrenChanged(visualAdded, visualRemoved);
        if (Child != null) Child.Clip = BuildGeometry(RenderSize);
    }
}
