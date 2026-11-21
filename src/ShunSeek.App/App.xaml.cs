using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using ShunSeek.App.Services;
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
        _index = new IndexService(usage);

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
            (_, _) => Dispatcher.BeginInvoke(() => window.ToggleShow()),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        Log($"startup: elevated={Environment.IsPrivilegedProcess} args=[{string.Join(' ', e.Args)}]");
        Log($"hotkey registered={_hotkey.Registered}");

        // 开发便利：带 --show 启动时直接把窗口弹出来（正常是等 Alt+Space/托盘）
        if (e.Args.Contains("--show", StringComparer.OrdinalIgnoreCase))
            Dispatcher.BeginInvoke(() => window.ToggleShow());

        // 索引在后台建：几秒后状态栏会变成"就绪"，期间窗口已经能呼出（空结果 + 进度提示）
        _ = _index.StartAsync();
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
