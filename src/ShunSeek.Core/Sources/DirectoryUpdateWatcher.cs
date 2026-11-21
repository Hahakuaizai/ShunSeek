namespace ShunSeek.Core.Sources;

public enum FsChangeKind { Created, Deleted, Renamed, Overflow }

/// <summary>一条来自 FileSystemWatcher 的变更。路径是现成的，不需要靠引用号拼。</summary>
public readonly record struct FsChange(FsChangeKind Kind, string Path, string? OldPath, bool IsDirectory);

/// <summary>
/// 兜底增量：没有管理员权限（便携模式）或卷不是 NTFS 时，用 FileSystemWatcher 盯住索引范围。
///
/// 定位要说清：这是"能用"的兜底，不是主力。
///  - FileSystemWatcher 会丢事件（高负载、缓冲区溢出、深层移动），丢了就报 Error，
///    上层收到 Overflow 直接整表重建——宁可多花几秒，也不能让索引悄悄变错；
///  - 全盘范围不要用它（几十万个目录的监听会把系统拖慢），只盯用户目录/开始菜单这类小范围。
/// </summary>
public sealed class DirectoryUpdateWatcher : IDisposable
{
    private readonly string[] _roots;
    private readonly string[] _excluded;
    private readonly List<FileSystemWatcher> _watchers = new();

    /// <param name="excludedDirs">
    /// 不监听（也不上报）的目录：临时目录、缓存、日志这些地方每秒都在变，
    /// 全收下来的话队列会被垃圾变更灌满，攒到阈值就触发整表重建——
    /// 表现就是状态栏一直"索引变化过多…"、搜索期间索引反复重建。
    /// </param>
    public DirectoryUpdateWatcher(IEnumerable<string> roots, IEnumerable<string>? excludedDirs = null)
    {
        _roots = roots.ToArray();
        _excluded = (excludedDirs ?? Array.Empty<string>()).ToArray();
    }

    public event Action<FsChange>? Changed;
    public event Action<Exception>? Faulted;

    public void Start()
    {
        foreach (var root in _roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                var w = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                };
                w.Created += (_, e) => Emit(FsChangeKind.Created, e.FullPath, null);
                w.Deleted += (_, e) => Emit(FsChangeKind.Deleted, e.FullPath, null);
                w.Renamed += (_, e) => Emit(FsChangeKind.Renamed, e.FullPath, e.OldFullPath);
                w.Error += (_, e) =>
                {
                    // 缓冲区溢出/网络盘断线：增量不可信了，交给上层重建
                    Changed?.Invoke(new FsChange(FsChangeKind.Overflow, root, null, true));
                    Faulted?.Invoke(e.GetException());
                };
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex)
            {
                Faulted?.Invoke(ex); // 单个根目录盯不住不影响其他根目录
            }
        }
    }

    /// <summary>路径是否落在排除目录里（前缀匹配，忽略大小写）。</summary>
    private bool IsExcluded(string path)
    {
        foreach (var dir in _excluded)
        {
            if (dir.Length == 0) continue;
            if (path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private void Emit(FsChangeKind kind, string path, string? oldPath)
    {
        // 垃圾目录里的变更连队都别进
        if (IsExcluded(path) || (oldPath != null && IsExcluded(oldPath))) return;

        try
        {
            // Watcher 不告诉我们是文件还是目录：路径还在就直接问文件系统；
            // 删除事件路径已经没了，退而用"叶子名有没有扩展名"猜（目录绝大多数没有点）。
            bool isDir;
            if (kind == FsChangeKind.Deleted)
                isDir = !Path.GetFileName(path).Contains('.');
            else if (Directory.Exists(path))
                isDir = true;
            else
                isDir = false;

            Changed?.Invoke(new FsChange(kind, path, oldPath, isDir));
        }
        catch
        {
            // 竞态：事件到手时文件已经又被删/改了。忽略这一条，不影响其他条目
        }
    }

    public void Stop()
    {
        foreach (var w in _watchers)
        {
            try { w.EnableRaisingEvents = false; } catch { }
            w.Dispose();
        }
        _watchers.Clear();
    }

    public void Dispose() => Stop();
}
