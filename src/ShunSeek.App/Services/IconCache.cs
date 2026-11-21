using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ShunSeek.Core;

namespace ShunSeek.App;

/// <summary>
/// 文件/文件夹/程序图标缓存：向 Shell 要真实图标，后台取、按 key 缓存，UI 只读结果。
///
/// 两条红线（见 docs/开发流程.md 阶段 4）：
///  1) 取图标绝不在 UI/布局线程上同步做——Shell 可能去访问磁盘甚至网络路径，一次几十毫秒；
///  2) 结果先渲染占位（分类色块+首字），图标到了再替换，不能为了等图标把列表卡住。
///
/// 为什么不用线程池：索引在后台建表时会把池线程全占满，图标请求排队能等到几百毫秒甚至几秒
/// （用户看到的就是"一片灰块"）。所以这里自己开两个专职线程取图标；
/// 优先级用 Normal 而不是 BelowNormal——实测改低之后，建索引期间图标会被饿住不出现。
/// 每个线程还要显式 CoInitialize：Shell 的 SHGetImageList 是 COM，不初始化会偶发失败。
///
/// 缓存 key 的粒度是刻意选的：
///  - 文件夹/程序/快捷方式：按完整路径（每个都不一样）；
///  - 普通文件：按扩展名（10 万个 .jpg 只需要取一次图标）。
/// 用 Shell 的"超大图标"（SHIL_JUMBO，256px 图源）缩到 56px 显示才不糊。
/// </summary>
public sealed class IconCache : IDisposable
{
    private readonly ConcurrentDictionary<string, BitmapSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BitmapSource?>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingCollection<Request> _queue = new(new ConcurrentQueue<Request>(), 4096);

    /// <summary>失败次数：失败不写死缓存，允许重试（启动瞬间 Shell 忙，第一次经常取不到）。</summary>
    private readonly ConcurrentDictionary<string, int> _failures = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxAttempts = 3;
    private readonly List<Thread> _workers = new();
    private bool _disposed;

    private readonly record struct Request(string Key, string Path, EntryCategory Category);

    public IconCache(int workers = 2)
    {
        for (int i = 0; i < workers; i++)
        {
            var thread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.Normal,
                Name = $"ShunSeek.IconWorker{i}",
            };
            thread.Start();
            _workers.Add(thread);
        }
    }

    /// <summary>同步查缓存（命中就直接用，不命中返回 null 走异步）。</summary>
    public BitmapSource? TryGet(string path, EntryCategory category)
        => _cache.TryGetValue(KeyFor(path, category), out var icon) ? icon : null;

    /// <summary>取图标；同一个 key 并发请求只真正执行一次。</summary>
    public Task<BitmapSource?> GetAsync(string path, EntryCategory category)
    {
        var key = KeyFor(path, category);
        if (_cache.TryGetValue(key, out var cached))
        {
            // 缓存里是"取不到"：只要没超过重试次数就再试一次，
            // 否则一次启动瞬间的失败会让这个图标永久变成灰块。
            if (cached != null || _failures.GetOrAdd(key, 0) >= MaxAttempts)
                return Task.FromResult(cached);
            _cache.TryRemove(key, out _);
        }

        var mine = new TaskCompletionSource<BitmapSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var actual = _pending.GetOrAdd(key, mine);

        // 只有"第一个"排队的请求真正入队，后来的一律等同一个 TCS，避免同一张图被取 N 次
        if (ReferenceEquals(actual, mine))
        {
            try { _queue.Add(new Request(key, path, category)); }
            catch (InvalidOperationException) { _pending.TryRemove(key, out _); mine.TrySetResult(null); }
        }
        return actual.Task;
    }

    private void WorkerLoop()
    {
        // Shell 的图标列表是 COM 对象：线程必须先初始化 STA
        CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
        try
        {
            foreach (var request in _queue.GetConsumingEnumerable())
            {
                BitmapSource? icon = null;
                try { icon = Extract(request.Path, request.Category); }
                catch { /* 单个图标失败不影响其他 */ }

                if (icon != null)
                {
                    _cache[request.Key] = icon;
                    _failures.TryRemove(request.Key, out _);
                }
                else
                {
                    // 失败只记次数，不把 null 焊死在缓存里——超过上限才彻底放弃
                    int attempts = _failures.AddOrUpdate(request.Key, 1, (_, n) => n + 1);
                    if (attempts >= MaxAttempts) _cache[request.Key] = null;
                }

                if (_pending.TryRemove(request.Key, out var tcs)) tcs.TrySetResult(icon);
            }
        }
        finally
        {
            CoUninitialize();
        }
    }

    private static string KeyFor(string path, EntryCategory category) => category switch
    {
        EntryCategory.Folder or EntryCategory.InstallDir => "dir:" + path,
        EntryCategory.App or EntryCategory.Shortcut or EntryCategory.Executable => "exe:" + path,
        _ => "ext:" + (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant(),
    };

    private static BitmapSource? Extract(string path, EntryCategory category)
    {
        if (!OperatingSystem.IsWindows()) return null;

        bool isDirectory = category is EntryCategory.Folder or EntryCategory.InstallDir;
        bool exists = isDirectory ? Directory.Exists(path) : File.Exists(path);

        // ① 路径真实存在：走 IShellItemImageFactory，拿到的是这一项自己的图标（64px，清晰）
        if (exists && !string.IsNullOrEmpty(path))
        {
            var image = FromShellItem(path);
            if (image != null) return image;
        }

        // ①.5 路径存在但 ShellItem 失败：直接拿路径问 SHGetFileInfo（exe 关联图标）
        if (exists && !string.IsNullOrEmpty(path))
        {
            var direct = new SHFILEINFO();
            var directResult = SHGetFileInfo(path, 0, ref direct,
                (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);
            if (directResult != IntPtr.Zero && direct.hIcon != IntPtr.Zero) return FromHIcon(direct.hIcon);
        }

        // ② 没有真实路径（注册表里的软件只登记了名字）或上面失败：按类型/扩展名要通用图标，
        //    绝不画色块——用户要的是系统原版图标，通用图标也是原版。
        string query;
        if (isDirectory) query = "folder";
        else
        {
            var ext = Path.GetExtension(path) ?? string.Empty;
            query = ext.Length > 0 ? "file" + ext : "file";
        }

        var info = new SHFILEINFO();
        uint flags = SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES;
        var attributes = (uint)(isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL);
        var result = SHGetFileInfo(query, attributes, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        if (result != IntPtr.Zero && info.hIcon != IntPtr.Zero) return FromHIcon(info.hIcon);

        // ③ 最后的兜底：连通用图标都拿不到，就借一枚文件夹图标
        if (isDirectory) return null;
        var folderInfo = new SHFILEINFO();
        var folderResult = SHGetFileInfo("folder", FILE_ATTRIBUTE_DIRECTORY, ref folderInfo,
            (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);
        return folderResult != IntPtr.Zero && folderInfo.hIcon != IntPtr.Zero ? FromHIcon(folderInfo.hIcon) : null;
    }

    /// <summary>
    /// IShellItemImageFactory::GetImage —— 按路径直接拿这一项自己的图标（64px）。
    /// 比"SHGetFileInfo 取索引 → 再去图像列表里按索引找"可靠得多：大小图标列表的索引并不总是对得上。
    /// </summary>
    private static BitmapSource? FromShellItem(string path)
    {
        object? item = null;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item);
            if (item is not IShellItemImageFactory factory) return null;
            if (factory.GetImage(new SIZE { cx = 64, cy = 64 }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out var hBitmap) != 0
                || hBitmap == IntPtr.Zero) return null;
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (item != null) Marshal.ReleaseComObject(item);
        }
    }

    /// <summary>按系统图像列表里的下标取 256px 大图标。</summary>
    private static BitmapSource? FromSystemImageList(int iconIndex)
    {
        IImageList? list = null;
        try
        {
            var iid = typeof(IImageList).GUID;
            if (SHGetImageList(SHIL_JUMBO, ref iid, out list) == 0 && list != null &&
                list.GetIcon(iconIndex, ILD_TRANSPARENT, out var hIcon) == 0 && hIcon != IntPtr.Zero)
            {
                return FromHIcon(hIcon);
            }
        }
        catch
        {
            // 系统图像列表拿不到（老系统/被安全软件拦），往下走 32px 兜底
        }
        finally
        {
            if (list != null) Marshal.ReleaseComObject(list);
        }
        return null;
    }

    private static BitmapSource FromHIcon(IntPtr hIcon)
    {
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze(); // 冻结后可以跨线程直接绑给 UI
            return source;
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _queue.CompleteAdding(); } catch { }
        _queue.Dispose();
    }

    // ─────────────────────────── Shell 互操作 ───────────────────────────

    private const uint SHGFI_SYSICONINDEX = 0x00004000;
    private const uint SHGFI_ICON = 0x00000100;
    private const uint SHGFI_LARGEICON = 0x00000000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x00000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const int SHIL_JUMBO = 0x4;   // 256px，Win7+
    private const int ILD_TRANSPARENT = 0x1;
    private const int SIIGBF_BIGGERSIZEOK = 0x1;
    private const int SIIGBF_ICONONLY = 0x4;
    private const uint COINIT_APARTMENTTHREADED = 0x2;

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

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

    [DllImport("shell32.dll")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    /// <summary>
    /// 系统图像列表。vtable 顺序必须与 shobjidl.h 里的 IImageList 完全一致，
    /// 少一个方法后面的全部错位——所以这里把 GetIcon 之前的成员老老实实列全。
    /// </summary>
    [ComImport]
    [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, ref int pi);
        [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, ref int pi);
        [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
        [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
        [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, ref int pi);
        [PreserveSig] int Draw(IntPtr pimldp);
        [PreserveSig] int Remove(int i);
        [PreserveSig] int GetIcon(int i, int flags, out IntPtr picon);
    }
}
