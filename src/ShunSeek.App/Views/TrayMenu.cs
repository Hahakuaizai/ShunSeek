using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ShunSeek.App.Controls;

namespace ShunSeek.App;

/// <summary>
/// 托盘右键菜单：WinForms 的 ContextMenuStrip 是系统灰白样式，和主界面不是一套语言。
/// 这里用 Popup 自绘——连续曲率圆角 + 同一套玻璃配色 + 图标 + 圆角悬停。
///
/// 为什么用 Popup 而不是一个无边框 Window：Popup 按内容自动测量尺寸，
/// 不会踩到"窗口 SizeToContent 在多显示器不同 DPI 下算错、内容被裁"的坑；
/// 而且 StaysOpen=false 时点菜单外面就自动收起，不用靠 Deactivated 猜。
/// </summary>
public sealed class TrayMenu
{
    private static readonly System.Windows.Media.FontFamily IconFont =
        new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private static readonly Brush IconBrush = new SolidColorBrush(Color.FromRgb(0x5A, 0x63, 0x72));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(0x1D, 0x23, 0x2B));
    private static readonly Brush HintBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xAF));
    private static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
    private static readonly Brush HoverBrush = new SolidColorBrush(Color.FromArgb(0x14, 0x1D, 0x23, 0x2B));
    private static readonly Brush PressedBrush = new SolidColorBrush(Color.FromArgb(0x24, 0x1D, 0x23, 0x2B));

    private readonly Popup _popup;
    private System.Windows.Window? _owner;
    private readonly StackPanel _host;
    private readonly LiquidPanel _panel;

    public TrayMenu()
    {
        _host = new StackPanel { Width = 232, Margin = new Thickness(6) };

        _panel = new LiquidPanel
        {
            Radius = 13,
            StrokeThickness = 1,
            Child = _host,
            Fill = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(0xFC, 0xFD, 0xFE, 0xFF), 0),
                    new GradientStop(Color.FromArgb(0xF8, 0xF4, 0xF7, 0xFA), 0.55),
                    new GradientStop(Color.FromArgb(0xF5, 0xEC, 0xF0, 0xF6), 1),
                },
                new Point(0, 0), new Point(1, 1)),
            Stroke = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF), 0),
                    new GradientStop(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF), 0.35),
                    new GradientStop(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF), 1),
                },
                new Point(0, 0), new Point(0.7, 1)),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 26,
                ShadowDepth = 5,
                Direction = 270,
                Opacity = 0.18,
                Color = Color.FromRgb(0x1D, 0x23, 0x2B),
            },
        };

        _popup = new Popup
        {
            Child = _panel,
            AllowsTransparency = true,
            StaysOpen = false,
            Placement = PlacementMode.Absolute,
            PopupAnimation = PopupAnimation.Fade,
        };

        // Esc 收起（Popup 自己拿焦点，主窗口隐藏时也能用）
        _panel.Focusable = true;
        _panel.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) _popup.IsOpen = false;
        };
        _popup.Opened += (_, _) => _panel.Focus();
    }

    /// <summary>加一条可点的菜单项。</summary>
    public TrayMenu AddItem(string glyph, string text, Action action, bool danger = false, string? hint = null)
    {
        var row = CreateRow(glyph, text, danger ? DangerBrush : TextBrush, danger ? DangerBrush : IconBrush, hint);
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _popup.IsOpen = false;
            action();
        };
        _host.Children.Add(row);
        return this;
    }

    /// <summary>加一条开关项（勾在左边）。</summary>
    public TrayMenu AddToggle(string text, bool isChecked, Action<bool> onToggle)
    {
        bool state = isChecked;
        var row = CreateRow(state ? "\uE73E" : string.Empty, text, TextBrush, IconBrush, null);
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            state = !state;
            _popup.IsOpen = false;
            onToggle(state);
        };
        _host.Children.Add(row);
        return this;
    }

    public TrayMenu AddSeparator()
    {
        _host.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(10, 5, 10, 5),
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0x1D, 0x23, 0x2B)),
        });
        return this;
    }

    /// <summary>
    /// 在光标处弹出。默认出现在光标左上方（和 Windows 托盘菜单一致），
    /// 上面/左边放不下就翻到另一侧。
    /// </summary>
    public void ShowAtCursor(System.Windows.Window? owner)
    {
        _owner = owner;
        var cursor = System.Windows.Forms.Cursor.Position;

        // 打开前先量一次：拿到真实内容尺寸才能一次摆准，不会先闪一下再跳位
        _panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _panel.DesiredSize;

        var screen = System.Windows.Forms.Screen.FromPoint(cursor);
        var area = screen.WorkingArea;
        // DPI：主窗口可能还没显示过，GetDpi 拿不到就退回系统 DPI
        double scale;
        try
        {
            scale = owner != null
                ? VisualTreeHelper.GetDpi(owner).DpiScaleX
                : VisualTreeHelper.GetDpi(_panel).DpiScaleX;
        }
        catch { scale = 1; }
        if (scale <= 0) scale = 1;

        double left = cursor.X / scale - size.Width;
        double top = cursor.Y / scale - size.Height;

        if (top < area.Top / scale) top = cursor.Y / scale;
        if (left < area.Left / scale) left = area.Left / scale;
        if (left + size.Width > area.Right / scale) left = area.Right / scale - size.Width;
        if (top + size.Height > area.Bottom / scale) top = area.Bottom / scale - size.Height;

        // 关键：不能把 PlacementTarget 设成主窗口。主窗口在托盘模式下是隐藏的
        // （Visibility=Hidden 只是建了句柄），Popup 挂在不可见目标上就永远不渲染。
        // Absolute + 无目标 = 以屏幕左上角为原点，坐标是 DIP。
        _popup.PlacementTarget = null;
        _popup.Placement = PlacementMode.Absolute;
        _popup.HorizontalOffset = left;
        _popup.VerticalOffset = top;
        _popup.IsOpen = true;

        App.Log($"[tray-menu] shown at {left:F0},{top:F0} size {size.Width:F0}x{size.Height:F0} items={_host.Children.Count}");
    }

    private static Border CreateRow(string glyph, string text, Brush foreground, Brush glyphBrush, string? hint)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = IconFont,
            FontSize = 12.5,
            Foreground = glyphBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var label = new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            Foreground = foreground,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        if (!string.IsNullOrEmpty(hint))
        {
            var hintBlock = new TextBlock
            {
                Text = hint,
                FontSize = 10.5,
                Foreground = HintBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 0, 0),
            };
            Grid.SetColumn(hintBlock, 2);
            grid.Children.Add(hintBlock);
        }

        var row = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 7, 10, 7),
            Background = System.Windows.Media.Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = grid,
        };
        row.MouseEnter += (_, _) => row.Background = HoverBrush;
        row.MouseLeave += (_, _) => row.Background = System.Windows.Media.Brushes.Transparent;
        row.MouseLeftButtonDown += (_, _) => row.Background = PressedBrush;
        row.MouseLeftButtonUp += (_, _) => row.Background = HoverBrush;
        return row;
    }
}
