using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using ShunSeek.App.Services;
using ShunSeek.Core;

namespace ShunSeek.App;

/// <summary>
/// 搜索窗：热键呼出 → 鼠标所在屏上方居中弹出。普通窗口层级（不置顶），可被其它软件覆盖。
/// 空查询是首页磁贴，有关键词是详细列表；键盘 ↑↓ 走条目，Enter 打开，Ctrl+Enter 定位。
/// 显隐、悬停、选中、筛选展开、主题切换全部带短过渡，不做"啪"式跳变。
/// </summary>
public partial class MainWindow
{
    /// <summary>首页网格的键盘列数（磁贴 170+12 宽，780 窗约 4 列）。</summary>
    private const int Columns = 4;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 2;

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private bool _hiding;
    private bool _closed;
    private bool _exiting;
    private readonly TranslateTransform _rootShift = new();
    private DropShadowEffect? _shellShadow;
    private readonly List<FilterOption> _filterOptions = new();

    /// <summary>键盘/右键都作用于"当前可见"的那个列表。</summary>
    private ListBox ActiveList =>
        ViewModel.ResultsVisibility == Visibility.Visible ? ResultsList : HomeGrid;

    public MainWindow()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;

        _shellShadow = Shell.Effect as DropShadowEffect;
        RootLayer.RenderTransform = _rootShift;

        _filterOptions.AddRange(new[]
        {
            new FilterOption("", "全部"),
            new FilterOption("App", "应用"),
            new FilterOption("Shortcut", "快捷方式"),
            new FilterOption("InstallDir", "安装目录"),
            new FilterOption("Folder", "文件夹"),
            new FilterOption("Document", "文档"),
            new FilterOption("Image", "图片"),
            new FilterOption("Video", "视频"),
            new FilterOption("Audio", "音频"),
            new FilterOption("Archive", "压缩包"),
            new FilterOption("Code", "代码"),
            new FilterOption("Executable", "程序"),
            new FilterOption("Other", "其他"),
        });
        FilterOptions.ItemsSource = _filterOptions;

        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel vm) return;
            vm.Results.CollectionChanged += (_, e) =>
            {
                if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                    ActiveList.BeginAnimation(OpacityProperty,
                        new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(140)));
                Dispatcher.BeginInvoke(() => SelectIndex(0),
                    System.Windows.Threading.DispatcherPriority.Background);
            };
            RefreshFilterUi();
        };

        ThemeManager.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            RefreshThemeUi();
            // 资源整批换掉时做一次极短的淡入：像重新蘸了一次墨，而不是"啪"地换皮
            if (IsVisible)
            {
                RootLayer.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0.72, 1, TimeSpan.FromMilliseconds(170))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    });
            }
        });
        RefreshThemeUi();
        RefreshFilterUi();
    }

    // ───────────────────────────── 主题 ─────────────────────────────

    private void RefreshThemeUi()
    {
        ThemeGlyph.Text = ThemeManager.IsDark ? "\uE706" : "\uE708";
        ThemeButton.ToolTip = ThemeManager.IsDark ? "切换为浅色主题" : "切换为深色主题";

        // 投影是 Freezable，绑在上面的 DynamicResource 不一定跟着换；
        // 直接按当前主题重建一份，浅色下才不会顶着一圈深色重影。
        if (Shell != null && WindowState != WindowState.Maximized)
        {
            var color = System.Windows.Application.Current.TryFindResource("ShadowColor") is Color c
                ? c : Colors.Black;
            var opacity = System.Windows.Application.Current.TryFindResource("ShadowOpacity") is double o
                ? o : 0.55;
            _shellShadow = new DropShadowEffect
            {
                BlurRadius = 26,
                ShadowDepth = 6,
                Direction = 270,
                Color = color,
                Opacity = opacity,
            };
            Shell.Effect = _shellShadow;
        }
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e) => ThemeManager.Toggle();

    // ───────────────────────────── 筛选面板 ─────────────────────────────

    private void FilterButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshFilterUi();
        FilterRoot.Opacity = 0;
        FilterScale.ScaleX = 0.96;
        FilterScale.ScaleY = 0.96;
        FilterPopup.IsOpen = true;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        FilterRoot.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        FilterScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease });
        FilterScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease });
    }

    private void FilterChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string tag }) return;
        ViewModel.SetCategory(ParseTag(tag));
        RefreshFilterUi();
        FilterPopup.IsOpen = false;
        SearchBox.Focus();
    }

    private void FilterReset_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SetCategory(null);
        RefreshFilterUi();
        FilterPopup.IsOpen = false;
        SearchBox.Focus();
    }

    private void RefreshFilterUi()
    {
        if (DataContext is not MainViewModel vm) return;
        var current = vm.Category;
        foreach (var opt in _filterOptions)
            opt.IsChecked = ParseTag(opt.Tag) == current;
        var label = current == null ? "全部类型" : EntryCategoryNames.DisplayName(current.Value);
        FilterDot.Visibility = current == null ? Visibility.Collapsed : Visibility.Visible;
        FilterReset.Visibility = current == null ? Visibility.Collapsed : Visibility.Visible;
        FilterSummary.Text = "当前：" + label;
        FilterButton.ToolTip = "按类型筛选（" + label + "）";
    }

    private static EntryCategory? ParseTag(string? tag) => tag switch
    {
        "App" => EntryCategory.App,
        "Shortcut" => EntryCategory.Shortcut,
        "InstallDir" => EntryCategory.InstallDir,
        "Folder" => EntryCategory.Folder,
        "Document" => EntryCategory.Document,
        "Image" => EntryCategory.Image,
        "Video" => EntryCategory.Video,
        "Audio" => EntryCategory.Audio,
        "Archive" => EntryCategory.Archive,
        "Code" => EntryCategory.Code,
        "Executable" => EntryCategory.Executable,
        "Other" => EntryCategory.Other,
        _ => null,
    };

    // ───────────────────────────── 显隐 ─────────────────────────────

    /// <summary>热键/托盘共用的显隐切换。</summary>
    public void ToggleShow()
    {
        if (_closed) return;
        if (IsVisible) { HideAnimated(); return; }
        ShowWindow();
    }

    /// <summary>把窗口叫出来（已在屏上也照样置前，不隐藏）。</summary>
    public void ShowWindow()
    {
        // 退出流程里可能还有排队的事件回到这里（比如第二个实例的唤醒信号），
        // 窗口已关就不能再 Show，否则抛 InvalidOperationException。
        if (_closed) return;
        _hiding = false;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

        PositionWindow();

        // 先落到透明+微沉，再淡入上浮，避免"啪"地拍在屏幕上
        RootLayer.Opacity = 0;
        _rootShift.Y = 10;
        Show();
        Activate();

        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) SetForegroundWindow(handle);

        SearchBox.Focus();
        SearchBox.SelectAll();
        SelectIndex(0);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        RootLayer.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        _rootShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
    }

    /// <summary>淡出+微沉后再真正 Hide，收起不突兀。</summary>
    private void HideAnimated()
    {
        if (_hiding) return;
        _hiding = true;
        var fade = new DoubleAnimation(RootLayer.Opacity, 0, TimeSpan.FromMilliseconds(110));
        fade.Completed += (_, _) =>
        {
            _hiding = false;
            Hide();
            RootLayer.BeginAnimation(OpacityProperty, null);
            RootLayer.Opacity = 1;
            _rootShift.BeginAnimation(TranslateTransform.YProperty, null);
            _rootShift.Y = 0;
        };
        RootLayer.BeginAnimation(OpacityProperty, fade);
        _rootShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(_rootShift.Y, 6, TimeSpan.FromMilliseconds(110)));
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

    /// <summary>启动参数带关键词时预填并立即搜索（--query 微信）。</summary>
    public void ApplyInitialQuery(string text)
    {
        SearchBox.Text = text;
        SearchBox.CaretIndex = text.Length;
    }

    /// <summary>调试用：把筛选面板展开（--open-filter），配合离屏截图验证。</summary>
    public void OpenFilterForShot() => FilterButton_Click(this, new RoutedEventArgs());

    /// <summary>把筛选面板单独渲染成 PNG（Popup 在自己的窗口里，主窗口截图抓不到它）。</summary>
    public void CaptureFilterToPng(string path)
    {
        FilterRoot.UpdateLayout();
        int width = (int)Math.Ceiling(FilterRoot.ActualWidth);
        int height = (int)Math.Ceiling(FilterRoot.ActualHeight);
        if (width <= 0 || height <= 0) throw new InvalidOperationException("筛选面板还没完成布局");

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(FilterRoot);

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(path);
        encoder.Save(stream);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.Query = SearchBox.Text;
        Dispatcher.BeginInvoke(() => SelectIndex(0), System.Windows.Threading.DispatcherPriority.Background);
    }

    // ───────────────────────────── 顶栏按钮 ─────────────────────────────

    private void CloseButton_Click(object sender, RoutedEventArgs e) => ExitAnimated();

    /// <summary>叉号退出：淡出收尾后直接结束进程，不驻留托盘。</summary>
    private void ExitAnimated()
    {
        if (_hiding) return;
        _hiding = true;
        _exiting = true;
        var fade = new DoubleAnimation(RootLayer.Opacity, 0, TimeSpan.FromMilliseconds(110));
        fade.Completed += (_, _) =>
        {
            _hiding = false;
            System.Windows.Application.Current?.Shutdown();
        };
        RootLayer.BeginAnimation(OpacityProperty, fade);
        _rootShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(_rootShift.Y, 6, TimeSpan.FromMilliseconds(110)));
    }

    /// <summary>把窗口内容渲染成 PNG（离屏验证样式用，不依赖屏幕合成）。</summary>
    public void CaptureToPng(string path)
    {
        var root = RootLayer;
        root.UpdateLayout();

        int width = (int)Math.Ceiling(root.ActualWidth);
        int height = (int)Math.Ceiling(root.ActualHeight);
        if (width <= 0 || height <= 0) throw new InvalidOperationException("窗口还没完成布局");

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(root);

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(path);
        encoder.Save(stream);
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_exiting) return;
        _exiting = true;
        Dispatcher.BeginInvoke(() => System.Windows.Application.Current?.Shutdown());
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        GlobalClickWatcher.Detach();
        base.OnClosed(e);
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>
    /// 拖动窗口：整块面板的空白处都能拖（输入框、按钮、列表里不拖）。
    /// 用 WM_NCLBUTTONDOWN + HTCAPTION 让系统自己接管拖拽，比 DragMove() 稳。
    /// 双击空白处 = 最大化/还原。
    /// </summary>
    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (IsInteractive(source) || IsInsideList(source)) return;

        if (e.ClickCount == 2)
        {
            MaximizeButton_Click(sender, e);
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        ReleaseCapture();
        SendMessage(handle, WM_NCLBUTTONDOWN, HTCAPTION, IntPtr.Zero);
    }

    private static bool IsInsideList(DependencyObject source)
    {
        for (var node = source; node != null; node = VisualTreeHelper.GetParent(node))
            if (node is ListBox or System.Windows.Controls.Primitives.ScrollBar) return true;
        return false;
    }

    private static bool IsInteractive(DependencyObject source)
    {
        for (var node = source; node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.TextBox) return true;
            if (node is FrameworkElement { Name: "SearchBox" }) return true;
        }
        return false;
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized;

        if (MaximizeGlyph != null) MaximizeGlyph.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        if (RestoreGlyph != null) RestoreGlyph.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
        if (MaximizeButton != null) MaximizeButton.ToolTip = maximized ? "还原" : "最大化";

        // 最大化时把外面的投影边收掉、圆角抹平，正文正好铺满可用区域
        if (Shell != null)
        {
            Shell.Radius = maximized ? 0 : 12;
            Shell.Effect = maximized ? null : _shellShadow;
        }
        if (RootLayer != null)
            RootLayer.Margin = maximized ? new Thickness(0) : new Thickness(12);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var vm = DataContext as MainViewModel;
        var menu = new ContextMenu();

        var theme = new MenuItem { Header = ThemeManager.IsDark ? "切换为浅色主题" : "切换为深色主题" };
        theme.Click += (_, _) => ThemeManager.Toggle();
        menu.Items.Add(theme);
        menu.Items.Add(new Separator());

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

        var update = new MenuItem { Header = "检查开发目录改动并更新" };
        update.Click += UpdateMenu_Click;
        menu.Items.Add(update);

        var devRoot = new MenuItem { Header = "更改开发目录…" };
        devRoot.Click += (_, _) => PickDevRoot();
        menu.Items.Add(devRoot);

        menu.Items.Add(new Separator());
        var exit = new MenuItem { Header = "退出" };
        exit.Click += (_, _) => System.Windows.Application.Current?.Shutdown();
        menu.Items.Add(exit);

        menu.PlacementTarget = SettingsButton;
        menu.IsOpen = true;
    }

    // ───────────────────────────── 开发目录更新 ─────────────────────────────

    /// <summary>启动后查一次开发目录有没有比安装目录新的改动，有就亮出页脚的小红标。</summary>
    public void RefreshUpdateHint()
    {
        try
        {
            var check = DevUpdater.Check();
            bool wasVisible = UpdatePill.Visibility == Visibility.Visible;
            UpdatePill.Visibility = check.HasUpdate ? Visibility.Visible : Visibility.Collapsed;
            UpdatePill.ToolTip = check.HasUpdate
                ? $"{check.NewerFiles} 个文件比安装目录新 · 点击编译并重启"
                : null;
            if (!wasVisible && UpdatePill.Visibility == Visibility.Visible)
                UpdatePill.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        }
        catch (Exception ex)
        {
            DevUpdater.Log("检查更新失败：" + ex.Message);
        }
    }

    private void UpdatePill_Click(object sender, RoutedEventArgs e) => _ = RunDevUpdateAsync();

    private void UpdateMenu_Click(object sender, RoutedEventArgs e) => _ = RunDevUpdateAsync();

    /// <summary>命令行 --dev-update 的入口，和点小红标是同一条路。</summary>
    public void StartDevUpdate() => _ = RunDevUpdateAsync();

    private async Task RunDevUpdateAsync()
    {
        var vm = ViewModel;
        var check = DevUpdater.Check();

        if (!check.CanCompare)
        {
            vm.Notify(check.Problem ?? "无法判断是否有更新");
            return;
        }

        if (!check.HasUpdate)
        {
            UpdatePill.Visibility = Visibility.Collapsed;
            vm.Notify("安装目录已经是最新的，没有需要应用的改动");
            return;
        }

        UpdatePill.Visibility = Visibility.Collapsed;
        vm.Notify($"发现 {check.NewerFiles} 个文件有改动，正在编译…");

        var error = await DevUpdater.PublishAsync(DevUpdater.Log);
        if (error != null)
        {
            DevUpdater.Log(error);
            vm.Notify(error);
            return;
        }

        vm.Notify("编译完成，正在替换文件并重启…");
        if (!DevUpdater.LaunchUpdaterAndRestart(DevUpdater.Log))
        {
            vm.Notify("无法启动更新脚本（需要 PowerShell 7 的 pwsh）");
            return;
        }

        await Task.Delay(500);
        System.Windows.Application.Current?.Shutdown();
    }

    private void PickDevRoot()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择 ShunSeek 的开发目录（含 ShunSeek.sln 的文件夹）",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
            SelectedPath = DevUpdater.DevRoot,
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        DevUpdater.SaveDevRoot(dialog.SelectedPath);
        RefreshUpdateHint();
        ViewModel.Notify("开发目录已改为 " + dialog.SelectedPath);
    }

    // ───────────────────────────── 键盘 ─────────────────────────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        int step = ActiveList == HomeGrid ? Columns : 1;
        switch (e.Key)
        {
            case Key.Escape:
                if (FilterPopup.IsOpen) { FilterPopup.IsOpen = false; e.Handled = true; break; }
                if (!string.IsNullOrEmpty(SearchBox.Text)) { SearchBox.Clear(); e.Handled = true; break; }
                HideAnimated();
                e.Handled = true;
                break;
            case Key.Right:
                MoveSelection(+1); e.Handled = true; break;
            case Key.Left:
                MoveSelection(-1); e.Handled = true; break;
            case Key.Down:
                MoveSelection(+step); e.Handled = true; break;
            case Key.Up:
                MoveSelection(-step); e.Handled = true; break;
            case Key.PageDown:
                MoveSelection(+step * 3); e.Handled = true; break;
            case Key.PageUp:
                MoveSelection(-step * 3); e.Handled = true; break;
            case Key.Home:
                SelectIndex(0); e.Handled = true; break;
            case Key.End:
                SelectIndex(ActiveList.Items.Count - 1); e.Handled = true; break;
            case Key.Enter:
                OpenSelected(locateInExplorer: (Keyboard.Modifiers & ModifierKeys.Control) != 0);
                e.Handled = true; break;
            case Key.C when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                CopySelectedPath(); e.Handled = true; break;
        }
    }

    private void MoveSelection(int delta)
    {
        var list = ActiveList;
        if (list.Items.Count == 0) return;
        SelectIndex(Math.Clamp(list.SelectedIndex + delta, 0, list.Items.Count - 1));
    }

    private void SelectIndex(int index)
    {
        var list = ActiveList;
        if (list.Items.Count == 0) return;
        list.SelectedIndex = Math.Clamp(index, 0, list.Items.Count - 1);
        list.ScrollIntoView(list.SelectedItem);
    }

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        => OpenSelected(locateInExplorer: false);

    /// <summary>
    /// 右键分两种：点在条目上给条目菜单；点在空白处给"往首页加东西"的菜单。
    /// </summary>
    private void ResultsList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var list = sender as ListBox ?? ActiveList;
        var hit = list.InputHitTest(e.GetPosition(list)) as DependencyObject;
        while (hit != null && hit is not ListBoxItem) hit = VisualTreeHelper.GetParent(hit);

        if (hit is ListBoxItem container)
        {
            container.IsSelected = true;
            ShowItemMenu(list);
        }
        else
        {
            ShowBlankMenu(list);
        }
        e.Handled = true;
    }

    /// <summary>空白处右键：把任意文件夹/文件固定到首页，以及索引的维护动作。</summary>
    private void ShowBlankMenu(ListBox anchor)
    {
        var vm = ViewModel;
        var menu = new ContextMenu();

        var addFolder = new MenuItem { Header = "添加文件夹到首页…" };
        addFolder.Click += (_, _) => PickAndPinFolder();
        menu.Items.Add(addFolder);

        var addFile = new MenuItem { Header = "添加文件到首页…" };
        addFile.Click += (_, _) => PickAndPinFile();
        menu.Items.Add(addFile);

        menu.Items.Add(new Separator());

        var refresh = new MenuItem { Header = "立即刷新索引" };
        refresh.Click += (_, _) => vm.Service?.Refresh();
        menu.Items.Add(refresh);

        var rebuild = new MenuItem { Header = "重建索引" };
        rebuild.Click += (_, _) => _ = vm.Service?.RebuildAsync("手动重建");
        menu.Items.Add(rebuild);

        menu.PlacementTarget = anchor;
        menu.IsOpen = true;
    }

    private void PickAndPinFolder()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择要固定到首页的文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            ViewModel.PinPath(dialog.SelectedPath);
    }

    private void PickAndPinFile()
    {
        using var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Title = "选择要固定到首页的文件",
            Multiselect = true,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        foreach (var path in dialog.FileNames) ViewModel.PinPath(path);
    }

    /// <summary>点"其他 N 项"那一行：展开被折叠的结果。</summary>
    private void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        if (e.AddedItems[0] is ResultItemViewModel { IsGroup: true })
            ViewModel.ExpandResults();
    }

    private void ShowItemMenu(ListBox anchor)
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

        menu.PlacementTarget = anchor;
        menu.IsOpen = true;
    }

    private void CopySelectedPath()
    {
        if (SelectedItem() is not { HasPath: true } item) return;
        try { Clipboard.SetText(item.Path); }
        catch { /* 剪贴板被别的进程占用：忽略 */ }
    }

    private ResultItemViewModel? SelectedItem()
    {
        var list = ActiveList;
        return list.SelectedItem as ResultItemViewModel
               ?? list.Items.Cast<ResultItemViewModel>().FirstOrDefault();
    }

    private void OpenSelected(bool locateInExplorer)
    {
        var item = SelectedItem();
        if (item == null) return;

        if (item.IsGroup)
        {
            ViewModel.ExpandResults();
            return;
        }

        if (!item.HasPath)
        {
            ViewModel.Notify($"{item.Name} 没有记录可打开的位置");
            return;
        }

        ViewModel.RecordOpened(item);
        try
        {
            if (locateInExplorer)
            {
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
        HideAnimated();
    }
}

/// <summary>筛选面板里的一枚芯片。</summary>
public sealed class FilterOption : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isChecked;

    public FilterOption(string tag, string label)
    {
        Tag = tag;
        Label = label;
    }

    public string Tag { get; }
    public string Label { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
