using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using ShunSeek.App.Services;
using ShunSeek.App.Views;
using ShunSeek.Core;

namespace ShunSeek.App;

/// <summary>
/// 程序入口：单实例 → 建索引（后台）→ 主窗 + 托盘 + 全局热键。
/// 注意本类写全限定 System.Windows.Application：项目同时开了 WinForms（托盘要用），
/// 裸写 Application 会和 System.Windows.Forms.Application 二义。
/// </summary>
public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutex = "ShunSeek.App.SingleInstance";
    private const string ShowEventName = "ShunSeek.App.ShowWindow";

    private Mutex? _singleInstance;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private TrayIconManager? _tray;
    private HotkeyManager? _hotkey;
    private IndexService? _index;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 主题调色板要先于任何窗口铺进 Application 资源（XAML 全走 DynamicResource）
        ThemeManager.Initialize();

        // 单实例：第二个进程把已有实例的窗口叫出来，然后自己退出（否则会两套索引/两套热键打架）
        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceMutex, out bool createdNew);
        if (!createdNew)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowEventName, out var existing))
                {
                    existing.Set();
                    existing.Dispose();
                }
            }
            catch { /* 老实例刚好在退出：忽略，直接走 */ }
            Shutdown();
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown; // 关窗≠退出，退出走托盘菜单
        InstallCrashLogger();

        // 索引服务：负责建索引 + 增量监听 + 必要时重建。UI 只订阅状态，不关心内部怎么干。
        var usagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ShunSeek", "usage.json");
        var usage = UsageStore.Load(usagePath);
        var pinned = PinnedStore.Load(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ShunSeek", "pinned.json"));
        _index = new IndexService(usage) { Log = Log };

        var viewModel = new MainViewModel();
        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;

        viewModel.Attach(_index, pinned);
        _index.StatusChanged += viewModel.ApplyStatus;

        _tray = new TrayIconManager(
            onToggle: () => window.ToggleShow(),
            onRefresh: () => _index.Refresh(),
            onRebuild: () => _ = _index.RebuildAsync("手动重建"),
            onRestartElevated: RestartElevated,
            onExit: () => Shutdown());

        // 热键要挂在窗口句柄上，可窗口是"热键呼出才显示"的——句柄不创建就永远挂不上。
        // 所以先 EnsureHandle() 把 HWND 预创建出来（不 Show、不上屏），再注册热键。
        new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        _hotkey = new HotkeyManager();
        _hotkey.Attach(window, window.ToggleShow);

        // 第二个实例按下热键/双击托盘时的"叫醒"通道
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(
            _showEvent,
            (_, _) => Dispatcher.BeginInvoke(() => window.ShowWindow()),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        Log($"startup: elevated={Environment.IsPrivilegedProcess} args=[{string.Join(' ', e.Args)}]");
        Log($"hotkey registered={_hotkey.Registered}");

        // 开发/命令行便利：--show 直接弹窗，--query 关键词 预填并立刻搜一次
        string? initialQuery = null;
        for (int i = 0; i < e.Args.Length; i++)
        {
            if (string.Equals(e.Args[i], "--query", StringComparison.OrdinalIgnoreCase) && i + 1 < e.Args.Length)
                initialQuery = e.Args[i + 1];
        }

        // 调试：--theme light|dark 临时覆盖主题（不落盘），--open-filter 展开筛选面板
        int themeIndex = Array.FindIndex(e.Args, a => string.Equals(a, "--theme", StringComparison.OrdinalIgnoreCase));
        if (themeIndex >= 0 && themeIndex + 1 < e.Args.Length)
            ThemeManager.Apply(string.Equals(e.Args[themeIndex + 1], "light", StringComparison.OrdinalIgnoreCase)
                ? AppTheme.Light : AppTheme.Dark, animate: false, persist: false);
        bool openFilter = e.Args.Contains("--open-filter", StringComparer.OrdinalIgnoreCase);

        // 双击图标/直接运行 = 就是要用，直接把窗口弹出来（以前只有 --show 才弹，
        // 结果双击后只在托盘里躺着，像没启动一样）。开机自启带 --tray，安静地待在托盘。
        bool silent = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase)
                   || e.Args.Contains("--silent", StringComparer.OrdinalIgnoreCase);

        if (!silent)
        {
            Dispatcher.BeginInvoke(() =>
            {
                window.ShowWindow();
                if (initialQuery != null) window.ApplyInitialQuery(initialQuery);
                if (openFilter) window.OpenFilterForShot();
            });
        }

        // 开发用：--dev-update 走一遍"编译→替换→重启"，省得每次都点页脚的小红标
        if (e.Args.Contains("--dev-update", StringComparer.OrdinalIgnoreCase))
            Dispatcher.BeginInvoke(() => window.StartDevUpdate());

        // 开发用：--update-preview [png] 只弹更新说明；带 png 路径就离屏渲染后退出
        int previewIndex = Array.FindIndex(e.Args, a =>
            string.Equals(a, "--update-preview", StringComparison.OrdinalIgnoreCase));
        if (previewIndex >= 0)
        {
            silent = true;
            var shotPath = previewIndex + 1 < e.Args.Length && e.Args[previewIndex + 1].EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                ? e.Args[previewIndex + 1]
                : null;

            Dispatcher.BeginInvoke(() =>
            {
                var sample = new PendingUpdate
                {
                    Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                    Target = DevUpdater.TargetRoot,
                    Files = 3,
                    Items = new List<string>
                    {
                        "界面全部重绘为墨本深色主题",
                        "右键菜单改为深色，跟窗口同一套墨色",
                        "书眉副标题加分隔点",
                    },
                };

                var previewWindow = new Views.UpdateWindow(sample);
                if (shotPath != null)
                {
                    previewWindow.Left = -6000;
                    previewWindow.Top = -6000;
                    previewWindow.ShowActivated = false;
                }
                previewWindow.Show();

                if (shotPath != null)
                {
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                    int ticks = 0;
                    timer.Tick += (_, _) =>
                    {
                        ticks++;
                        try
                        {
                            previewWindow.CaptureToPng(shotPath);
                            timer.Stop();
                            DevUpdater.Log("[shot] update window saved " + shotPath);
                            Shutdown();
                        }
                        catch (Exception ex)
                        {
                            if (ticks >= 6)
                            {
                                timer.Stop();
                                DevUpdater.Log("[shot] update window failed: " + ex.Message);
                                Shutdown();
                            }
                        }
                    };
                    timer.Start();
                }
                else
                {
                    previewWindow.Activate();
                }
            });
        }

        // 开发/截图用：--shot <png> 把界面渲染成图片后退出。
        // 窗口放在屏幕外且不抢焦点，用户看不到——改样式时用它验证。
        int shotIndex = Array.FindIndex(e.Args, a => string.Equals(a, "--shot", StringComparison.OrdinalIgnoreCase));
        if (shotIndex >= 0 && shotIndex + 1 < e.Args.Length)
        {
            var shotPath = e.Args[shotIndex + 1];
            Dispatcher.BeginInvoke(new Action(() =>
            {
                window.Left = -6000;
                window.Top = -6000;
                window.ShowActivated = false;
                window.Show();
                if (initialQuery != null) window.ApplyInitialQuery(initialQuery);
                if (openFilter) window.OpenFilterForShot();
                _ = _index?.StartAsync();

                // 索引没就绪的话截出来是"正在建立索引"，先等它就绪（缓存加载约 1 秒）
                int ticks = 0;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                timer.Tick += (_, _) =>
                {
                    ticks++;
                    bool ready = _index?.Status.Ready == true || ticks >= 16;
                    if (!ready) return;

                    timer.Stop();
                    try
                    {
                        window.CaptureToPng(shotPath);
                        if (openFilter)
                            window.CaptureFilterToPng(
                                System.IO.Path.Combine(
                                    System.IO.Path.GetDirectoryName(shotPath) ?? "",
                                    System.IO.Path.GetFileNameWithoutExtension(shotPath) + "_filter.png"));
                        Log($"[shot] saved {shotPath}");
                    }
                    catch (Exception ex)
                    {
                        Log("[shot] failed: " + ex);
                    }
                    Shutdown();
                };
                timer.Start();
            }));
            return;
        }


        // 开发/截图用：直接弹出托盘菜单（没法用脚本点托盘图标）
        if (e.Args.Contains("--tray-menu", StringComparer.OrdinalIgnoreCase))
            Dispatcher.BeginInvoke(() => _tray.ShowMenu());

        // 索引在后台建：几秒后状态栏会变成"就绪"，期间窗口已经能呼出（空结果 + 进度提示）
        _ = _index.StartAsync();

        // 更新过的进程会留一份待展示的更新说明：先弹说明，再顺手查一眼开发目录是否有更新的改动
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var pending = DevUpdater.TakePendingUpdate();
            if (pending != null)
            {
                try
                {
                    window.ShowWindow();
                    var updateWindow = new UpdateWindow(pending);
                    updateWindow.Show();
                    updateWindow.Activate();
                    DevUpdater.Log("更新说明已弹出");
                }
                catch (Exception ex)
                {
                    DevUpdater.Log("弹出更新说明失败：" + ex);
                }
            }

            var hintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            hintTimer.Tick += (_, _) =>
            {
                hintTimer.Stop();
                window.RefreshUpdateHint();
            };
            hintTimer.Start();
        }), DispatcherPriority.ApplicationIdle);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkey?.Dispose();
        _index?.Dispose();
        _tray?.Dispose();
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// 以管理员身份重启：MFT 直读全盘要读卷句柄，普通权限打不开。
    /// 提权后启动的是同一个 exe（UAC 会弹确认框），索引范围从"用户目录"升级成"全盘"。
    /// </summary>
    internal void RestartElevated()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
            System.Diagnostics.Process.Start(psi);
            Shutdown(); // UAC 被取消会抛异常（下面兜住），那就保持现状继续用便携模式
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("提权重启失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 全局异常落盘（%LocalAppData%\ShunSeek\logs）。未签名的独立小工具最怕"双击没反应"，
    /// 有一条能回传的崩溃日志，比什么都强。
    /// </summary>
    private void InstallCrashLogger()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog("Dispatcher", args.Exception);
            args.Handled = true; // 不让一个 UI 异常把整个搜索窗带走
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrashLog("AppDomain", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog("Task", args.Exception);
            args.SetObserved();
        };
    }

    /// <summary>
    /// 轻量诊断日志（同一个 logs 目录，文件名 shunseek.log）。
    /// 只记启动/热键/索引就绪这种关键节点，出问题时用户直接把这一份发过来就行。
    /// </summary>
    internal static void Log(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ShunSeek", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "shunseek.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响功能
        }
    }

    private static void WriteCrashLog(string source, Exception? ex)
    {
        if (ex == null) return;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ShunSeek", "logs");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(file, $"[{DateTime.Now:O}] {source}\n{ex}\n");
        }
        catch
        {
            // 记日志本身失败就别再抛了
        }
    }
}
