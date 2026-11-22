using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace ShunSeek.App.Services;

/// <summary>
/// 托盘菜单的"点外面就收起来"。
///
/// WPF Popup 的 StaysOpen=false 依赖鼠标捕获，但在 AllowsTransparency 的独立分层窗口上，
/// 点击别的应用/桌面时根本收不到消息——菜单就赖在屏幕上，只能点自己的条目才消失。
/// 这里装一个低级鼠标钩子：点在任何不属于菜单的像素上，先把菜单关掉，再把这次点击原样放行
/// （不吞点击，点哪还是哪）。Esc 用低级键盘钩子一并处理。
///
/// 钩子必须装在 UI 线程（消息泵所在线程），回调里只做一次矩形判断，重活丢回 Dispatcher。
/// </summary>
internal static class GlobalClickWatcher
{
    private const int WH_MOUSE_LL = 14;
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_KEYDOWN = 0x0100;
    private const int VK_ESCAPE = 0x1B;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT Pt;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    private static HookProc? _mouseProc;
    private static HookProc? _keyProc;
    private static IntPtr _mouseHook;
    private static IntPtr _keyHook;
    private static Func<Rect>? _bounds;
    private static Action? _dismiss;
    private static Dispatcher? _dispatcher;
    private static DispatcherTimer? _watchdog;
    private static IntPtr _baselineForeground;

    public static bool IsAttached => _mouseHook != IntPtr.Zero || _keyHook != IntPtr.Zero;

    /// <summary>
    /// bounds 返回菜单在屏幕上的物理像素矩形；dismiss 在点击外部/Esc 时调用。
    /// </summary>
    public static void Attach(Func<Rect> bounds, Action dismiss)
    {
        Detach();
        _bounds = bounds;
        _dismiss = dismiss;
        _dispatcher = Dispatcher.CurrentDispatcher;
        // 打开托盘菜单时前台通常还是别的应用（菜单不抢焦点），所以只认"前台变了"这个信号
        _baselineForeground = GetForegroundWindow();

        _mouseProc = MouseProc;
        _keyProc = KeyProc;
        var module = GetModuleHandle(null);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
        _keyHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyProc, module, 0);

        // 钩子被系统摘掉（超时）时的兜底：前台窗口超过 0.5 秒不是自己，就收起来。
        // 只在菜单显示期间跑，代价可以忽略。
        _watchdog = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _watchdog.Tick += (_, _) =>
        {
            if (_dismiss == null) return;
            var hwnd = GetForegroundWindow();
            if (hwnd != IntPtr.Zero && hwnd != _baselineForeground && !IsOurs(hwnd))
            {
                // 前台换了人：用户切走了（Alt+Tab / 点任务栏 / 点别的应用）
                var cb = _dismiss;
                if (cb != null) _dispatcher?.BeginInvoke(new Action(cb));
            }
        };
        _watchdog.Start();

        App.Log($"[dismisser] attached mouse={_mouseHook != IntPtr.Zero} key={_keyHook != IntPtr.Zero}");
    }

    public static void Detach()
    {
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        if (_keyHook != IntPtr.Zero) UnhookWindowsHookEx(_keyHook);
        _mouseHook = IntPtr.Zero;
        _keyHook = IntPtr.Zero;
        _mouseProc = null;
        _keyProc = null;
        _watchdog?.Stop();
        _watchdog = null;
        _bounds = null;
        _dismiss = null;
        _baselineForeground = IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>前台窗口是不是本进程的（菜单弹窗本身也是本进程的窗口）。</summary>
    private static bool IsOurs(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == Environment.ProcessId;
    }

    private static IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
            {
                try
                {
                    var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    var bounds = _bounds?.Invoke();
                    if (bounds is { } rect &&
                        !rect.Contains(new Point(info.Pt.X, info.Pt.Y)))
                    {
                        var cb = _dismiss;
                        if (cb != null) _dispatcher?.BeginInvoke(new Action(cb));
                    }
                }
                catch { /* 钩子回调里绝不抛异常，否则会被系统摘掉 */ }
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private static IntPtr KeyProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam.ToInt32() == WM_KEYDOWN)
        {
            try
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (info.VkCode == VK_ESCAPE)
                {
                    var cb = _dismiss;
                    if (cb != null) _dispatcher?.BeginInvoke(new Action(cb));
                }
            }
            catch { }
        }
        return CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }
}
