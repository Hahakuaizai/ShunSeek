using Microsoft.Win32;

namespace ShunSeek.Core.Sources;

/// <summary>
/// 「软件」类数据源：让搜索不止搜文件，还能搜到装过的软件和它们的安装目录。
/// 三路来源（都只是读公开位置，不需要管理员）：
///  1) 开始菜单（当前用户 + 全部用户）与桌面上的 .lnk → Shortcut 条目；
///     其中指向程序的快捷方式同时是用户心里的"软件"，排序时按 Shortcut 加权即可。
///  2) 注册表 Uninstall 键（HKLM 64/32 位视图 + HKCU）：DisplayName → App 条目，
///     InstallLocation → InstallDir 条目（"搜到软件 → 直达它的安装文件夹"是高频需求）。
///     App 条目的可打开路径优先取 DisplayIcon 里的 exe，其次取安装目录——
///     这样回车就能启动/打开，而不是搜到一行点不动的东西。
///  3) UWP/商店应用不在注册表 Uninstall 里，正确来源是 shell:AppsFolder 枚举，
///     需要 COM 互操作，阶段 3 后续接入；此处先留 TODO 注释，不假装已实现。
/// </summary>
public sealed class AppScanner : IIndexSource
{
    public string Name => "软件与快捷方式";

    public IEnumerable<RawEntry> Load(IProgress<int>? progress, CancellationToken ct)
    {
        int produced = 0;
        foreach (var e in ScanShortcuts()) { ct.ThrowIfCancellationRequested(); yield return e; produced++; }
        foreach (var e in ScanInstalledApps()) { ct.ThrowIfCancellationRequested(); yield return e; produced++; }
        progress?.Report(produced);
    }

    /// <summary>开始菜单 + 桌面的快捷方式（和散落的 .exe 入口）。</summary>
    public IEnumerable<RawEntry> ScanShortcuts()
    {
        var dirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };
        var walker = new DirectoryWalker(dirs.Where(d => !string.IsNullOrEmpty(d)));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in walker.Load(null, CancellationToken.None))
        {
            if (e.IsDirectory) continue;
            var ext = (Path.GetExtension(e.FullPath) ?? string.Empty).ToLowerInvariant();
            if (ext == ".lnk")
            {
                // 显示名去掉 .lnk 后缀，"微信.lnk" 搜出来应显示「微信」
                var name = Path.GetFileNameWithoutExtension(e.FullPath!);
                if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;
                yield return new RawEntry(name, e.FullPath, EntryCategory.Shortcut);
            }
            else if (ext == ".exe")
            {
                yield return new RawEntry(e.Name, e.FullPath, EntryCategory.App);
            }
            // 开始菜单里偶尔有 .url（网页快捷方式）等，其余类型不收，避免噪音
        }
    }

    /// <summary>注册表 Uninstall 枚举已安装软件 + 安装目录。非 Windows 直接返回空。</summary>
    public IEnumerable<RawEntry> ScanInstalledApps()
    {
        if (!OperatingSystem.IsWindows()) yield break;

        const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        const string uninstallWow = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (hive, sub) in new[]
        {
            (Registry.LocalMachine, uninstall),
            (Registry.LocalMachine, uninstallWow),
            (Registry.CurrentUser, uninstall),
        })
        {
            RegistryKey? root = null;
            try { root = hive.OpenSubKey(sub, writable: false); }
            catch { /* 无权限读这个 hive 就跳过这一路 */ }
            if (root == null) continue;

            using (root)
            {
                string[] subKeys;
                try { subKeys = root.GetSubKeyNames(); }
                catch { continue; }

                foreach (var keyName in subKeys)
                {
                    RegistryKey? key = null;
                    try { key = root.OpenSubKey(keyName, writable: false); }
                    catch { }
                    if (key == null) continue;

                    using (key)
                    {
                        var displayName = (TryGetValue(key, "DisplayName") as string)?.Trim();
                        if (string.IsNullOrWhiteSpace(displayName)) continue;
                        // 系统组件/更新补丁带 SystemComponent=1，是噪音，跳过
                        if (TryGetValue(key, "SystemComponent") is int sc && sc == 1) continue;
                        // 64/32 位视图 + HKCU 会重复登记同一个软件
                        if (!seen.Add(displayName!)) continue;

                        var location = NormalizePath(TryGetValue(key, "InstallLocation") as string);
                        bool hasLocation = location != null && Directory.Exists(location);

                        // App 条目：优先用 DisplayIcon 里的 exe（回车直接启动），没有就用安装目录
                        var exe = ResolveExecutable(TryGetValue(key, "DisplayIcon") as string);
                        var appPath = exe ?? (hasLocation ? location! : string.Empty);

                        yield return new RawEntry(displayName!, appPath, EntryCategory.App);

                        if (hasLocation)
                        {
                            var leaf = Path.GetFileName(location!.TrimEnd('\\'));
                            if (!string.IsNullOrEmpty(leaf))
                                yield return new RawEntry(leaf, location, EntryCategory.InstallDir, IsDirectory: true);
                        }
                    }
                }
            }
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static object? TryGetValue(RegistryKey key, string name)
    {
        try { return key.GetValue(name); }
        catch { return null; } // 权限/损坏的值：当没有
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim().Trim('"').TrimEnd('\\');
        return p.Length == 0 ? null : p;
    }

    /// <summary>
    /// DisplayIcon 形如 `"C:\Program Files\App\app.exe",0` 或 `C:\…\icon.ico`。
    /// 只有指向真实存在的 .exe 才拿来当打开路径，否则返回 null（图标文件不能启动）。
    /// </summary>
    private static string? ResolveExecutable(string? displayIcon)
    {
        var p = NormalizePath(displayIcon);
        if (p == null) return null;

        int comma = p.LastIndexOf(',');
        if (comma > 2) p = p[..comma].Trim().Trim('"'); // 去掉 ",0" 这种图标索引
        if (!p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;

        try { return File.Exists(p) ? p : null; }
        catch { return null; }
    }

    // TODO(阶段3) UWP 应用接入点：
    //   用 Shell 的 AppsFolder（shell:AppsFolder）枚举：通过 IShellFolder 绑定
    //   "::{4234d49b-0244-4df3-b780-3893943456e1}" 或 FOLDERID_AppsFolder，
    //   逐项取 SFGAO + 显示名 + ParsingName（即 AppUserModelID），
    //   启动方式是 explorer.exe shell:AppsFolder\<AppUserModelID>。
    //   条目类别记为 App，FullPath 存 "shell:AppsFolder\..." 伪路径，打开逻辑在 UI 层特判。
    //
    // TODO(阶段3) .lnk 目标解析接入点：
    //   用 IShellLinkW/IPersistFile 加载 .lnk 取 GetPath，可把"目标 exe 名"作为
    //   条目的附加可搜文本（需要给 RawEntry/IndexStore 加 Alias 字段，阶段 2/3 再扩）。
}
