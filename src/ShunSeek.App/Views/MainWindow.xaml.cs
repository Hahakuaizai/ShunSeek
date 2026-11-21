using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ShunSeek.Core;

namespace ShunSeek.App;

/// <summary>
/// 搜索弹窗：热键呼出 → 鼠标所在屏上方居中弹出 → 失焦自动隐藏，DeskBox/Listary 一类启动器的标准行为。
/// 内容是"图标网格"（对齐 Win11 开始菜单的观感），键盘 ↑↓←→ 在格子里走，Enter 打开。
/// </summary>
public partial class MainWindow
{
    /// <summary>网格列数（ItemWidth 152 + 边距，700 宽正好 4 列）。键盘上下移动按这个数跳。</summary>
    private const int Columns = 4;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private long _shownAtTicks;
    private MainViewModel ViewModel => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
        Deactivated += OnDeactivated; // 失焦即收起（点到别处就消失，和 DeskBox 一致）
        PreviewKeyDown += OnPreviewKeyDown;
        ViewModel_ResultsChanged();
    }

    private void ViewModel_ResultsChanged()
    {
        if (DataContext is MainViewModel vm) vm.Results.CollectionChanged += (_, _) => SelectIndex(0);
    }

    /// <summary>
    /// 失焦收起。但刚 Show 出来的那一瞬间，Activate 还没生效就会收到 Deactivated
    /// （尤其是从后台进程/热键调出来的），这一下必须忽略，否则窗口闪一下又没了。
    /// </summary>
    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (Environment.TickCount64 - _shownAtTicks < 800) return;
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
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) SetForegroundWindow(handle);

        SearchBox.Focus();
        SearchBox.SelectAll();
        SelectIndex(0);
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
        Top = top + height * 0.14;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.Query = SearchBox.Text;
    }

    // ───────────────────────────── 顶栏按钮 ─────────────────────────────

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void FilterButton_Click(object sender, RoutedEventArgs e)
    {
        if (FilterButton.ContextMenu is { } menu)
        {
            menu.PlacementTarget = FilterButton;
            menu.IsOpen = true;
        }
    }

    private void FilterMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem clicked || clicked.Tag is not string tag) return;
        if (DataContext is not MainViewModel vm) return;

        // 菜单是多级的（应用程序/文件 下面还有子项），勾选状态要整棵树一起清
        foreach (var item in AllMenuItems(FilterButton.ContextMenu.Items))
            item.IsChecked = ReferenceEquals(item, clicked);

        var category = string.IsNullOrEmpty(tag) ? (EntryCategory?)null : Enum.Parse<EntryCategory>(tag);
        vm.SetCategory(category);
        FilterButton.ToolTip = category == null ? "按类型筛选" : $"按类型筛选：{EntryCategoryNames.DisplayName(category.Value)}";
        SearchBox.Focus();
    }

    private static IEnumerable<MenuItem> AllMenuItems(ItemCollection items)
    {
        foreach (var entry in items)
        {
            if (entry is not MenuItem item) continue;
            yield return item;
            foreach (var child in AllMenuItems(item.Items)) yield return child;
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var vm = DataContext as MainViewModel;
        var menu = new ContextMenu();

        var refresh = new MenuItem { Header = "立即刷新索引" };
        refresh.Click += (_, _) => vm?.Service?.Refresh();
        menu.Items.Add(refresh);

        var rebuild = new MenuItem { Header = "重建索引" };
        rebuild.Click += (_, _) => _ = vm?.Service?.RebuildAsync("手动重建");
        menu.Items.Add(rebuild);

        var elevated = new MenuItem { Header = "以管理员身份重启（全盘索引）" };
        elevated.Click += (_, _) => (System.Windows.Application.Current as App)?.RestartElevated();
        menu.Items.Add(elevated);

        menu.Items.Add(new Separator());
        var exit = new MenuItem { Header = "退出" };
        exit.Click += (_, _) => System.Windows.Application.Current?.Shutdown();
        menu.Items.Add(exit);

        menu.PlacementTarget = SettingsButton;
        menu.IsOpen = true;
    }

    // ───────────────────────────── 键盘 ─────────────────────────────

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Hide();
                e.Handled = true;
                break;
            case Key.Right:
                MoveSelection(+1);
                e.Handled = true;
                break;
            case Key.Left:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Down:
                MoveSelection(+Columns);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-Columns);
                e.Handled = true;
                break;
            case Key.PageDown:
                MoveSelection(+Columns * 3);
                e.Handled = true;
                break;
            case Key.PageUp:
                MoveSelection(-Columns * 3);
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

    private void ResultsList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => OpenSelected(locateInExplorer: false);

    /// <summary>右键：选中被点的那一格，再弹"打开 / 定位 / 复制路径 / 添加到首页"。</summary>
    private void ResultsList_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var hit = ResultsList.InputHitTest(e.GetPosition(ResultsList)) as DependencyObject;
        while (hit != null && hit is not ListBoxItem) hit = VisualTreeHelper.GetParent(hit);
        if (hit is ListBoxItem container) container.IsSelected = true;

        ShowItemMenu();
        e.Handled = true;
    }

    private void ShowItemMenu()
    {
        var item = SelectedItem();
        if (item == null) return;

        var vm = ViewModel;
        var menu = new ContextMenu();

        var open = new MenuItem { Header = "打开" };
        open.Click += (_, _) => OpenSelected(locateInExplorer: false);
        menu.Items.Add(open);

        var locate = new MenuItem { Header = "打开所在位置" };
        locate.Click += (_, _) => OpenSelected(locateInExplorer: true);
        menu.Items.Add(locate);

        var copy = new MenuItem { Header = "复制路径" };
        copy.Click += (_, _) => CopySelectedPath();
        menu.Items.Add(copy);

        menu.Items.Add(new Separator());

        if (vm.IsPinned(item))
        {
            var unpin = new MenuItem { Header = "从首页移除" };
            unpin.Click += (_, _) => vm.Unpin(item);
            menu.Items.Add(unpin);
        }
        else
        {
            var pin = new MenuItem { Header = "添加到首页" };
            pin.Click += (_, _) => vm.Pin(item);
            menu.Items.Add(pin);
        }

        menu.PlacementTarget = ResultsList;
        menu.IsOpen = true;
    }

    private void CopySelectedPath()
    {
        if (SelectedItem() is not { HasPath: true } item) return;
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
