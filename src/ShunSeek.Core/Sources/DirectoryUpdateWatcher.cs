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
    private readonly List<FileSystemWatcher> _watchers = new();

    public DirectoryUpdateWatcher(IEnumerable<string> roots) => _roots = roots.ToArray();

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

    private void Emit(FsChangeKind kind, string path, string? oldPath)
    {
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
