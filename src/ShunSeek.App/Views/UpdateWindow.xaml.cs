using System.Windows;
using System.Windows.Input;
using ShunSeek.App.Services;

namespace ShunSeek.App.Views;

/// <summary>重启后的更新说明：一张书页样式的清单，看完点"知道了"或按 Esc 关掉。</summary>
public partial class UpdateWindow : Window
{
    public UpdateWindow(PendingUpdate update)
    {
        InitializeComponent();

        Subtitle.Text = $"{update.Time} · 已应用到 {update.Target}";
        ItemsHost.ItemsSource = update.Items;

        // 没有 git 提交标题时，退回一句能说明规模的话
        FallbackText.Text = update.Items.Count > 0
            ? string.Empty
            : $"本次更新包含 {update.Files} 个文件的改动。";

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };

        Loaded += (_, _) => PositionNearCursor();

        // 弹出与关闭都留一条日志，端到端测试时能看出弹窗到底有没有活下来
        Closed += (_, _) => DevUpdater.Log("更新说明已关闭");
    }

    /// <summary>弹在鼠标所在那块屏的上方居中，和主窗同一个位置习惯。</summary>
    private void PositionNearCursor()
    {
        var area = System.Windows.Forms.Screen.FromPoint(
            System.Windows.Forms.Cursor.Position).WorkingArea;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);

        double left = area.Left / dpi.DpiScaleX;
        double top = area.Top / dpi.DpiScaleY;
        double width = area.Width / dpi.DpiScaleX;
        double height = area.Height / dpi.DpiScaleY;

        Left = left + (width - Width) / 2;
        Top = top + height * 0.16;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>离屏渲染成 PNG（调弹窗样式时用，不依赖屏幕合成）。</summary>
    public void CaptureToPng(string path)
    {
        var root = (FrameworkElement)Content;
        root.UpdateLayout();

        int width = (int)Math.Ceiling(root.ActualWidth);
        int height = (int)Math.Ceiling(root.ActualHeight);
        if (width <= 0 || height <= 0) throw new InvalidOperationException("弹窗还没完成布局");

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(root);

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(path);
        encoder.Save(stream);
    }
}
