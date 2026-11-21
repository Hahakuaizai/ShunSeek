using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ShunSeek.App;

/// <summary>
/// 取"给人看的名字"：Windows 里一个东西的显示名和文件名经常不是一回事——
///  - 文件夹：Desktop 显示成"桌面"、Downloads 显示成"下载"（Shell 的本地化名称）；
///  - 快捷方式：微信.lnk 显示成"微信"（不该带扩展名）；
///  - 程序：Weixin.exe 的文件描述是"微信"（版本信息里写着）。
/// 索引里只存路径，所以这些名字在渲染时才问 Shell 要，按路径缓存，绝不放到 UI 线程上做。
/// </summary>
public static class ShellNames
{
    private const uint SHGFI_DISPLAYNAME = 0x00000200;

    private static readonly ConcurrentDictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>拿不到合适名字时返回 null，调用方继续用文件名。</summary>
    public static string? Resolve(string path, bool isDirectory)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (Cache.TryGetValue(path, out var cached)) return cached;

        string? name = null;
        try
        {
            var ext = Path.GetExtension(path);

            if (isDirectory)
            {
                name = ShellDisplayName(path);
            }
            else if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                // 快捷方式：Shell 显示名天然不带 .lnk，而且是用户起的名字
                name = ShellDisplayName(path) ?? Path.GetFileNameWithoutExtension(path);
            }
            else if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                // 程序：优先文件描述（"微信"），没有就保留文件名
                var description = FileDescription(path);
                if (!string.IsNullOrWhiteSpace(description)) name = description;
            }

            // 和文件名一样就没必要替换，省得白白触发一次属性通知
            var leaf = Path.GetFileName(path);
            if (string.Equals(name, leaf, StringComparison.Ordinal)) name = null;
            if (string.IsNullOrWhiteSpace(name)) name = null;
        }
        catch
        {
            name = null;
        }

        Cache[path] = name;
        return name;
    }

    private static string? ShellDisplayName(string path)
    {
        var info = new SHFILEINFO();
        var result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_DISPLAYNAME);
        if (result == IntPtr.Zero) return null;
        var text = info.szDisplayName?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string? FileDescription(string path)
    {
        if (!File.Exists(path)) return null;
        var info = FileVersionInfo.GetVersionInfo(path);
        return info.FileDescription?.Trim();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);
}
