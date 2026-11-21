using Microsoft.Win32;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace ShunSeek.App;

/// <summary>
/// 系统托盘图标 + 右键菜单。搜索窗平时隐藏，托盘是它的"常驻入口"和退出出口；
/// 索引的"立即刷新 / 重建"也放在这里——索引出问题时用户不用去翻设置。
/// 图标先用系统默认应用图标占位；正式图标（.ico 资源）在阶段 5 打包时替换。
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIconManager(Action onToggle, Action onRefresh, Action onRebuild, Action onRestartElevated, Action onExit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示搜索 (Alt+Space)", null, (_, _) => onToggle());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("立即刷新索引", null, (_, _) => onRefresh());
        menu.Items.Add("重建索引", null, (_, _) => onRebuild());
        // 便携模式只索引用户目录；要全盘（MFT 直读，几秒建完）得提权跑
        menu.Items.Add("以管理员身份重启（全盘索引）", null, (_, _) => onRestartElevated());
        menu.Items.Add(new ToolStripSeparator());

        var autoStart = new ToolStripMenuItem("开机自启")
        {
            CheckOnClick = true,
            Checked = StartupRegistration.IsEnabled(),
        };
        autoStart.CheckedChanged += (_, _) => StartupRegistration.Set(autoStart.Checked);
        menu.Items.Add(autoStart);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => onExit());

        _icon = new NotifyIcon
        {
            Text = "瞬觅 ShunSeek",
            Icon = System.Drawing.SystemIcons.Application, // TODO(阶段5): 换成正式 .ico
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => onToggle();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}

/// <summary>开机自启：HKCU\...\Run 一项，不需要管理员，卸载时删同一项即可。</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ShunSeek";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            var value = key?.GetValue(ValueName) as string;
            if (string.IsNullOrEmpty(value)) return false;
            // 注册的是老路径（程序被移动过）也算没开，让用户重新点一次即可修正
            return value.Contains(ExecutableCommand(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key == null) return;
            if (enabled) key.SetValue(ValueName, ExecutableCommand());
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // 注册表写不进去（组策略限制）：静默失败，不打扰用户
        }
    }

    /// <summary>注册表里要写的命令行。dotnet run 调试时进程是 dotnet.exe，得补上 dll 路径。</summary>
    private static string ExecutableCommand()
    {
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe) &&
            !Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return $"\"{exe}\"";
        }

        var dll = Assembly.GetEntryAssembly()?.Location;
        return string.IsNullOrEmpty(exe) || string.IsNullOrEmpty(dll) ? string.Empty : $"\"{exe}\" \"{dll}\"";
    }
}
