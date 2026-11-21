using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ShunSeek.App.Controls;

/// <summary>
/// 液态玻璃面板：连续曲率圆角（squircle / 超椭圆）+ 玻璃填充 + 边缘高光。
///
/// 为什么不用 Border 的 CornerRadius：那是正圆弧，圆弧在切点处曲率突变，看起来"方中带硬"。
/// 连续曲率的做法是把角做成**超椭圆**：|x|^n + |y|^n = r^n（n≈4），
/// 曲率从直边的 0 平滑过渡到中点最大，观感才是苹果那种"柔而不软"的角。
/// 这里按参数方程采样成折线（每角 14 段，1x 下看不出棱），再裁切子元素。
///
/// 内外圆角：只用**一个** Radius，同心关系就是「内角 = 外角 − 内缩距离」，
/// 不做椭圆的双半径——那会让角在纵横方向比例不一致，正是上一版看着不对的原因。
/// </summary>
public sealed class LiquidPanel : Decorator
{
    private const int CornerSegments = 14; // 每个角的采样段数

    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(LiquidPanel),
        new FrameworkPropertyMetadata(24.0, FrameworkPropertyMetadataOptions.AffectsRender, OnShapeChanged));

    /// <summary>超椭圆指数：2 = 正圆角，4~5 = 典型 squircle 观感。</summary>
    public static readonly DependencyProperty ExponentProperty = DependencyProperty.Register(
        nameof(Exponent), typeof(double), typeof(LiquidPanel),
        new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsRender, OnShapeChanged));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(LiquidPanel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(LiquidPanel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(LiquidPanel),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Radius
    {
        get => (double)GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    public double Exponent
    {
        get => (double)GetValue(ExponentProperty);
        set => SetValue(ExponentProperty, value);
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

    private static void OnShapeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (LiquidPanel)d;
        panel._geometry = null;
        if (panel.Child != null) panel.Child.Clip = panel.BuildGeometry(panel.RenderSize);
    }

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

    protected override void OnVisualChildrenChanged(DependencyObject? visualAdded, DependencyObject? visualRemoved)
    {
        base.OnVisualChildrenChanged(visualAdded, visualRemoved);
        if (Child != null) Child.Clip = BuildGeometry(RenderSize);
    }

    /// <summary>
    /// 超椭圆圆角矩形：四条直边 + 四段采样出来的角。
    /// 参数方程 x = cx + r·cos(t)^(2/n)，y = cy − r·sin(t)^(2/n)，
    /// t 从 0 到 π/2，正好从切点走到切点；n 越大越"方"，n=4 是常见的 squircle 手感。
    /// </summary>
    private Geometry? BuildGeometry(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0) return null;

        double w = size.Width;
        double h = size.Height;
        double r = Math.Min(Radius, Math.Min(w, h) / 2);
        double n = Math.Max(2.0, Exponent);
        double power = 2.0 / n;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(r, 0), isFilled: true, isClosed: true);

            // 顺时针一圈。每个角都从"刚走过的那条边的切点"出发，
            // t 从 0 走到 π/2 正好抵达下一条边的切点——方向写反会自交，角上就会出现缺口。
            ctx.LineTo(new Point(w - r, 0), true, false);
            for (int i = 1; i <= CornerSegments; i++)
            {
                double t = Math.PI / 2 * i / CornerSegments;
                ctx.LineTo(new Point((w - r) + r * Math.Pow(Math.Sin(t), power),
                                     r - r * Math.Pow(Math.Cos(t), power)), true, false); // 右上
            }

            ctx.LineTo(new Point(w, h - r), true, false);
            for (int i = 1; i <= CornerSegments; i++)
            {
                double t = Math.PI / 2 * i / CornerSegments;
                ctx.LineTo(new Point((w - r) + r * Math.Pow(Math.Cos(t), power),
                                     (h - r) + r * Math.Pow(Math.Sin(t), power)), true, false); // 右下
            }

            ctx.LineTo(new Point(r, h), true, false);
            for (int i = 1; i <= CornerSegments; i++)
            {
                double t = Math.PI / 2 * i / CornerSegments;
                ctx.LineTo(new Point(r - r * Math.Pow(Math.Sin(t), power),
                                     (h - r) + r * Math.Pow(Math.Cos(t), power)), true, false); // 左下
            }

            ctx.LineTo(new Point(0, r), true, false);
            for (int i = 1; i <= CornerSegments; i++)
            {
                double t = Math.PI / 2 * i / CornerSegments;
                ctx.LineTo(new Point(r - r * Math.Pow(Math.Cos(t), power),
                                     r - r * Math.Pow(Math.Sin(t), power)), true, false); // 左上
            }
        }
        geometry.Freeze();
        return geometry;
    }

}
