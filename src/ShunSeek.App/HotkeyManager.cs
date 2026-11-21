using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ShunSeek.App;

/// <summary>
/// 全局热键 Alt+Space 呼出/隐藏搜索窗（RegisterHotKey 系统级注册，
/// 任何前台应用下都生效，这正是启动器类工具的标准做法）。
/// 注意：Alt+Space 同时是窗口系统菜单的默认快捷键，RegisterHotKey 注册后
/// 一般由本程序优先截获；若与个别软件冲突，阶段 4 在设置里提供改键
/// （备选 Ctrl+Shift+Space / 双击 Ctrl，DeskBox 用的是双击 Ctrl 一类方案）。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int HotkeyId = 0x5353; // 'SS'
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint VkSpace = 0x20;

    /// <summary>RegisterHotKey 是否成功（失败多半是热键被别的程序占了）。</summary>
    public bool Registered { get; private set; }

    private IntPtr _hwnd;
    private HwndSource? _source;
    private Action? _onHotkey;

    public void Attach(Window window, Action onHotkey)
    {
        _onHotkey = onHotkey;
        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        Registered = RegisterHotKey(_hwnd, HotkeyId, ModAlt, VkSpace);
        if (!Registered)
        {
            // 注册失败多半是被别的程序占用了：不弹窗打扰用户，托盘菜单仍可呼出。
            App.Log($"Alt+Space 热键注册失败（Win32 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}）");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            _onHotkey?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) UnregisterHotKey(_hwnd, HotkeyId);
        _source?.RemoveHook(WndProc);
    }
}
