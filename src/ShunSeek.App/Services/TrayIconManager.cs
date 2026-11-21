using Microsoft.Win32;
using System.IO;
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

        var appIcon = LoadAppIcon();
        App.Log("tray icon: " + (appIcon == null ? "fallback(系统默认)" : "loaded(" + appIcon.Width + "px)"));

        _icon = new NotifyIcon
        {
            Text = "瞬觅 ShunSeek", // 注意 NotifyIcon.Text 上限 63 字符
            Icon = appIcon ?? System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => onToggle();

        ShowTrayHintOnce();
    }

    /// <summary>
    /// Win11 会把新程序的小图标默认收进 ^ 折叠区，用户会以为"没有托盘图标"。
    /// 第一次运行时冒个气泡说一声，之后就不打扰了（用一个小文件记状态）。
    /// </summary>
    private void ShowTrayHintOnce()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShunSeek");
            var flag = Path.Combine(dir, "tray-hint-shown");
            if (File.Exists(flag)) return;

            Directory.CreateDirectory(dir);
            File.WriteAllText(flag, DateTime.Now.ToString("O"));
            _icon.ShowBalloonTip(5000, "瞬觅已在托盘",
                "Win11 可能把它收进了 ^ 折叠区，拖出来固定一下就好。Alt+Space 呼出搜索。",
                ToolTipIcon.None);
        }
        catch
        {
            // 提示失败无所谓，不影响功能
        }
    }

    /// <summary>
    /// 取应用自己的图标：先用打进程序集的托管资源（开发期也有），
    /// 取不到再退回 exe 关联图标，最后才是系统默认——不能因为图标失败让托盘空着。
    /// </summary>
    private static System.Drawing.Icon? LoadAppIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/app.ico"))?.Stream;
            if (resource != null)
            {
                using (resource) return new System.Drawing.Icon(resource, 32, 32);
            }
        }
        catch
        {
            // 资源没打进去（比如换了个构建方式）：往下走
        }

        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe)) return System.Drawing.Icon.ExtractAssociatedIcon(exe);
        }
        catch
        {
        }

        return null;
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

        // 单文件发布时 Assembly.Location 恒为空串，入口 dll 用基目录拼（只有调试期才走这条分支）
        var dll = Path.Combine(AppContext.BaseDirectory, "ShunSeek.dll");
        return string.IsNullOrEmpty(exe) ? string.Empty : $"\"{exe}\" \"{dll}\"";
    }
}
