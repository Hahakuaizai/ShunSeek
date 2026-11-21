using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ShunSeek.Core;

namespace ShunSeek.App;

/// <summary>
/// 搜索弹窗：热键呼出 → 鼠标所在屏上方居中弹出 → 失焦自动隐藏，DeskBox/Listary 一类启动器的标准行为。
///
/// 结果行现在用"分类色块+字形"占位。换成真实文件图标时必须走异步缓存：
/// 用 IShellItemImageFactory::GetImage（或 SHGetFileInfo）在后台线程按
/// 「扩展名 → 图标」缓存（文件夹/驱动器单独处理），结果先渲染占位、图标好了再替换。
/// 绝不能在搜索或布局路径里同步取图标——Shell 取图标会访问磁盘甚至网络路径，
/// 一次卡顿几十毫秒，搜索的快就白做了。
/// </summary>
public partial class MainWindow
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private long _shownAtTicks;
    private MainViewModel ViewModel => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
        Deactivated += OnDeactivated; // 失焦即收起（点到别处就消失，和 DeskBox 一致）
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>
    /// 失焦收起。但刚 Show 出来的那一瞬间，Activate 还没生效就会收到 Deactivated
    /// （尤其是从后台进程/热键调出来的），这一下必须忽略，否则窗口闪一下又没了。
    /// </summary>
    private void OnDeactivated(object? sender, EventArgs e)
    {
        long now = Environment.TickCount64;
        if (now - _shownAtTicks < 800)
        {
            App.Log($"deactivated ignored (grace {now - _shownAtTicks}ms)");
            return;
        }
        App.Log("deactivated -> hide");
        Hide();
    }

    /// <summary>热键/托盘共用的显隐切换。</summary>
    public void ToggleShow()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        PositionWindow();
        Show();
        _shownAtTicks = Environment.TickCount64;
        Activate();

        // 热键是从别的进程抢焦点，Activate() 经常不生效，再硬顶一次前台
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) SetForegroundWindow(handle);

        SearchBox.Focus();
        SearchBox.SelectAll();
        if (ResultsList.Items.Count > 0) ResultsList.SelectedIndex = 0;
        App.Log($"shown: visible={IsVisible} active={IsActive} handle={handle}");
    }

    /// <summary>在"鼠标所在的那块屏"的上方居中（多显示器 + 不同缩放都按该屏 DPI 换算）。</summary>
    private void PositionWindow()
    {
        var area = System.Windows.Forms.Screen.FromPoint(
            System.Windows.Forms.Cursor.Position).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);

        double left = area.Left / dpi.DpiScaleX;
        double top = area.Top / dpi.DpiScaleY;
        double width = area.Width / dpi.DpiScaleX;
        double height = area.Height / dpi.DpiScaleY;

        Left = left + (width - Width) / 2;
        Top = top + height * 0.16;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.Query = SearchBox.Text;
    }

    private void CategoryChip_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton chip || chip.Tag is not string tag) return;
        // XAML 里 IsChecked="True"（默认"全部"）会在 InitializeComponent 期间就触发本事件，
        // 那会儿 DataContext 还没挂上；这时不用筛选，直接跳过。
        if (DataContext is not MainViewModel vm) return;
        vm.SetCategory(string.IsNullOrEmpty(tag) ? null : Enum.Parse<EntryCategory>(tag));
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Hide();
                e.Handled = true;
                break;
            case Key.Down:
                MoveSelection(+1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.PageDown:
                MoveSelection(+8);
                e.Handled = true;
                break;
            case Key.PageUp:
                MoveSelection(-8);
                e.Handled = true;
                break;
            case Key.Home:
                SelectIndex(0);
                e.Handled = true;
                break;
            case Key.End:
                SelectIndex(ResultsList.Items.Count - 1);
                e.Handled = true;
                break;
            case Key.Enter:
                OpenSelected(locateInExplorer: (Keyboard.Modifiers & ModifierKeys.Control) != 0);
                e.Handled = true;
                break;
            case Key.C when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                CopySelectedPath();
                e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (ResultsList.Items.Count == 0) return;
        SelectIndex(Math.Clamp(ResultsList.SelectedIndex + delta, 0, ResultsList.Items.Count - 1));
    }

    private void SelectIndex(int index)
    {
        if (ResultsList.Items.Count == 0) return;
        ResultsList.SelectedIndex = Math.Clamp(index, 0, ResultsList.Items.Count - 1);
        ResultsList.ScrollIntoView(ResultsList.SelectedItem);
    }

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        => OpenSelected(locateInExplorer: false);

    private void CopySelectedPath()
    {
        var item = SelectedItem();
        if (item is not { HasPath: true }) return;
        try { Clipboard.SetText(item.Path); }
        catch { /* 剪贴板被别的进程占用：忽略 */ }
    }

    private ResultItemViewModel? SelectedItem()
        => ResultsList.SelectedItem as ResultItemViewModel
           ?? ResultsList.Items.Cast<ResultItemViewModel>().FirstOrDefault();

    private void OpenSelected(bool locateInExplorer)
    {
        var item = SelectedItem();
        if (item == null) return;

        if (!item.HasPath)
        {
            // 软件条目偶尔没有记录安装路径：不静默失败，给一行提示
            ViewModel.Notify($"{item.Name} 没有记录可打开的位置");
            return;
        }

        ViewModel.RecordOpened(item);
        try
        {
            if (locateInExplorer)
            {
                // 文件夹定位自己没意义，直接打开；文件才用 /select 高亮
                var args = item.IsDirectory ? $"\"{item.Path}\"" : $"/select,\"{item.Path}\"";
                Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"打开失败: {item.Path} ({ex.Message})");
            ViewModel.Notify(File.Exists(item.Path) || Directory.Exists(item.Path)
                ? "打开失败：" + ex.Message
                : "路径已不存在（可能被移动或删除）");
            return;
        }
        Hide();
    }
}
