using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ShunSeek.App.Controls;
using ShunSeek.App.Services;

namespace ShunSeek.App;

/// <summary>
/// 托盘右键菜单：WinForms 的 ContextMenuStrip 是系统灰白样式，和主界面不是一套语言。
/// 这里用 Popup 自绘——连续曲率圆角 + 跟主窗口同一套主题色 + 图标 + 圆角悬停。
/// 颜色全部走 Application 级 DynamicResource，主题切换时托盘菜单也跟着变。
///
/// 为什么用 Popup 而不是一个无边框 Window：Popup 按内容自动测量尺寸，
/// 不会踩到"窗口 SizeToContent 在多显示器不同 DPI 下算错、内容被裁"的坑；
/// 而且 StaysOpen=false 时点菜单外面就自动收起，不用靠 Deactivated 猜。
/// </summary>
public sealed class TrayMenu
{
    private static readonly System.Windows.Media.FontFamily IconFont =
        new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly Popup _popup;
    private System.Windows.Window? _owner;
    private readonly StackPanel _host;
    private readonly LiquidPanel _panel;
    private readonly System.Windows.Media.Effects.DropShadowEffect _shadow;

    private static Brush Res(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as Brush ?? System.Windows.Media.Brushes.Transparent;

    public TrayMenu()
    {
        _host = new StackPanel { Width = 232, Margin = new Thickness(6) };

        _panel = new LiquidPanel
        {
            Radius = 13,
            StrokeThickness = 1,
            Child = _host,
        };
        _panel.SetResourceReference(LiquidPanel.FillProperty, "MenuFill");
        _panel.SetResourceReference(LiquidPanel.StrokeProperty, "MenuStroke");

        var shadow = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 26,
            ShadowDepth = 5,
            Direction = 270,
        };
        _shadow = shadow;
        _panel.Effect = shadow;

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
        _popup.Opened += (_, _) =>
        {
            _panel.Focus();
            // WPF 的 StaysOpen=false 在透明独立 Popup 上收不到外部点击，补一个全局鼠标/键盘监听：
            // 点菜单外任意像素、或按 Esc，都把菜单收起来（点击本身照常传给目标窗口，不吞）。
            var dpi = VisualTreeHelper.GetDpi(_panel);
            GlobalClickWatcher.Attach(
                () =>
                {
                    var origin = _panel.PointToScreen(new System.Windows.Point(0, 0));
                    return new Rect(origin.X, origin.Y,
                        _panel.ActualWidth * dpi.DpiScaleX, _panel.ActualHeight * dpi.DpiScaleY);
                },
                () => _popup.IsOpen = false);
        };
        _popup.Closed += (_, _) => GlobalClickWatcher.Detach();
    }

    /// <summary>加一条可点的菜单项。</summary>
    public TrayMenu AddItem(string glyph, string text, Action action, bool danger = false, string? hint = null)
    {
        var row = CreateRow(glyph, text, danger, hint);
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
        var row = CreateRow(state ? "\uE73E" : string.Empty, text, false, null);
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
        var line = new Border
        {
            Height = 1,
            Margin = new Thickness(10, 5, 10, 5),
        };
        line.SetResourceReference(Border.BackgroundProperty, "HairLine");
        _host.Children.Add(line);
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

        // 投影颜色/浓度跟主题走（Freezable 不支持 DynamicResource，打开时取一次）
        if (System.Windows.Application.Current?.TryFindResource("ShadowColor") is Color sc) _shadow.Color = sc;
        if (System.Windows.Application.Current?.TryFindResource("ShadowOpacity") is double so) _shadow.Opacity = so;

        // 打开前先量一次：拿到真实内容尺寸才能一次摆准，不会先闪一下再跳位
        _panel.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
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
        if (left < area.Left / scale) left = cursor.X / scale;
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

    private static Border CreateRow(string glyph, string text, bool danger, string? hint)
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
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, danger ? "AccentText" : "TextMid");
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var label = new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, danger ? "AccentText" : "TextHi");
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        if (!string.IsNullOrEmpty(hint))
        {
            var hintBlock = new TextBlock
            {
                Text = hint,
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 0, 0),
            };
            hintBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextLow");
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
        row.MouseEnter += (_, _) => row.Background = Res("MenuHover");
        row.MouseLeave += (_, _) => row.Background = System.Windows.Media.Brushes.Transparent;
        row.MouseLeftButtonDown += (_, _) => row.Background = Res("SurfaceSel");
        row.MouseLeftButtonUp += (_, _) => row.Background = Res("MenuHover");
        return row;
    }
}
