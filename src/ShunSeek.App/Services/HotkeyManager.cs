using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ShunSeek.App;

/// <summary>
/// 全局热键呼出/隐藏搜索窗（RegisterHotKey 系统级注册，任何前台应用下都生效）。
///
/// 之前 MainWindow 和这里各注册了一次 Alt+Space，第二个必然吃到 Win32 1409，
/// 日志里永远是"注册失败"——其实功能是好的，但看着像坏了。现在只在这里注册一次。
///
/// 候选按优先级试：Ctrl+Alt+Space 是本机唯一空闲的"空格系"组合
/// （Alt+Space 与 Ctrl+Shift+Space 都被别的软件占了，实测 1409）；
/// 再退到 Ctrl+Shift+Q / Alt+Q。全部失败也不崩，托盘和双击图标仍可呼出。
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
    private const uint ModCtrl = 0x0002;
    private const uint ModShift = 0x0004;

    private static readonly (uint Mods, uint Vk, string Label)[] Candidates =
    {
        (ModCtrl | ModAlt, 0x20, "Ctrl+Alt+Space"), // Space = 0x20
        (ModCtrl | ModShift, 0x51, "Ctrl+Shift+Q"),  // Q = 0x51
        (ModAlt, 0x51, "Alt+Q"),
    };

    /// <summary>RegisterHotKey 是否成功（全部候选都被占用时为 false）。</summary>
    public bool Registered { get; private set; }

    /// <summary>实际注册上的组合，用于托盘菜单和气泡提示。</summary>
    public string HotkeyLabel { get; private set; } = Candidates[0].Label;

    private IntPtr _hwnd;
    private HwndSource? _source;
    private Action? _onHotkey;

    public void Attach(Window window, Action onHotkey)
    {
        _onHotkey = onHotkey;
        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);

        if (_hwnd == IntPtr.Zero)
        {
            App.Log("[hotkey] window handle is zero, skip registration");
            return;
        }

        foreach (var (mods, vk, label) in Candidates)
        {
            if (RegisterHotKey(_hwnd, HotkeyId, mods, vk))
            {
                Registered = true;
                HotkeyLabel = label;
                App.Log($"[hotkey] registered {label}");
                return;
            }
            App.Log($"[hotkey] {label} unavailable (Win32 {Marshal.GetLastWin32Error()})");
        }

        Registered = false;
        App.Log("[hotkey] all candidates unavailable; tray icon / double-click still opens the window");
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
        if (_hwnd != IntPtr.Zero && Registered) UnregisterHotKey(_hwnd, HotkeyId);
        _source?.RemoveHook(WndProc);
    }
}
