namespace ShunSeek.Core;

/// <summary>
/// 增量索引表：索引建好之后，文件系统里新发生的变化先攒在这里，搜索时和基础索引合并。
///
/// 取舍（对应 docs/开发流程.md 阶段 3 的"增量表"路线）：
///  - 整表重建要几秒，而增量表只有几十~几千条，重冻一次是微秒级，所以更新的代价几乎为零；
///  - 删除记墓碑而不是从数组里抠（基础索引是冻结的连续数组，抠不动也不该抠）；
///  - 墓碑攒到阈值、或者目录被改名（子树的路径全变了）时置 RequiresRebuild，
///    由 IndexService 安排一次整表重建——几秒钟，用户基本无感。
///
/// 线程安全：所有方法内部加锁；对外的搜索快照（Freeze）是拷贝出来的不可变集合，
/// 所以搜索线程和更新线程完全解耦。
/// </summary>
public sealed class DeltaIndex
{
    /// <summary>墓碑条目的上限：超过就整表重建，避免墓碑集合无限长大。</summary>
    private const int MaxDeadEntries = 20_000;

    /// <summary>失效目录前缀的上限：超过就整表重建（目录删除/改名是低频事件，正常远达不到）。</summary>
    private const int MaxDeadDirPrefixes = 32;

    private readonly object _gate = new();

    private readonly Dictionary<ulong, RawEntry> _addByRef = new();                                   // NTFS 来源：按引用号去重
    private readonly Dictionary<string, RawEntry> _addByPath = new(StringComparer.OrdinalIgnoreCase); // 兜底来源：按路径去重
    private readonly HashSet<ulong> _deadRefs = new();
    private readonly HashSet<string> _deadPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _deadDirPrefixes = new(StringComparer.OrdinalIgnoreCase);

    private IndexStore _frozen = IndexStore.Empty;
    private bool _dirty;

    /// <summary>累计收到过多少条变更（超过阈值时上层可以考虑重建，日志/状态栏也用它）。</summary>
    public int TotalChanges { get; private set; }

    /// <summary>当前增量条目数。</summary>
    public int Count
    {
        get { lock (_gate) return _addByRef.Count + _addByPath.Count; }
    }

    /// <summary>墓碑条目数（引用号 + 精确路径）。</summary>
    public int DeadCount
    {
        get { lock (_gate) return _deadRefs.Count + _deadPaths.Count; }
    }

    /// <summary>是否需要一次整表重建（墓碑太多 / 目录改名 / 监听端明确要求）。</summary>
    public bool RequiresRebuild
    {
        get
        {
            lock (_gate)
                return _forceRebuild
                    || _deadRefs.Count + _deadPaths.Count > MaxDeadEntries
                    || _deadDirPrefixes.Count > MaxDeadDirPrefixes;
        }
    }

    private bool _forceRebuild;

    /// <summary>标记"这份增量已经不可信，请重建"。例如 USN 日志被卷满覆盖、Watcher 缓冲区溢出。</summary>
    public void RequestRebuild()
    {
        lock (_gate) _forceRebuild = true;
    }

    /// <summary>新增或更新一条条目（新建文件、改名后的新名字、软件安装目录变化都走这里）。</summary>
    public void Upsert(RawEntry entry)
    {
        lock (_gate)
        {
            if (entry.FileRef != 0) _addByRef[entry.FileRef] = entry;
            else if (!string.IsNullOrEmpty(entry.FullPath)) _addByPath[entry.FullPath] = entry;
            _dirty = true;
            TotalChanges++;
        }
    }

    /// <summary>
    /// 删除一条条目。NTFS 模式用引用号（文件被删/改名前），兜底模式用路径。
    /// 注意：这里只加墓碑，不会尝试从已冻结的基础索引里挖洞。
    /// </summary>
    public void Remove(ulong fileRef, string? fullPath)
    {
        lock (_gate)
        {
            if (fileRef != 0)
            {
                _addByRef.Remove(fileRef);   // 我们自己加的增量条目要真删掉
                _deadRefs.Add(fileRef);      // 基础索引里的同引用号条目靠墓碑隐藏
            }
            if (!string.IsNullOrEmpty(fullPath))
            {
                _addByPath.Remove(fullPath);
                _deadPaths.Add(fullPath);
            }
            _dirty = true;
            TotalChanges++;
        }
    }

    /// <summary>
    /// 目录被删除或改名：它下面所有条目的路径都失效了，记一个前缀墓碑。
    /// 删除不需要重建（文件本来就没了吗，重建也搜不到），但**改名必须重建**：
    /// 子条目的引用号还在、路径全变了，而 USN/Watcher 不会逐个通知子文件，
    /// 靠增量补不回来，只能整表重建（几秒钟）。
    /// </summary>
    public void RemoveDirectory(string dirPath, bool requestRebuild)
    {
        if (string.IsNullOrEmpty(dirPath)) return;
        lock (_gate)
        {
            _deadDirPrefixes.Add(EnsureTrailingSlash(dirPath));
            _deadPaths.Add(dirPath);
            if (requestRebuild) _forceRebuild = true;
            _dirty = true;
            TotalChanges++;
        }
    }

    /// <summary>把当前增量冻成搜索快照；基础索引没变时只是把墓碑集合拷一份，成本可以忽略。</summary>
    public SearchIndex Freeze(IndexStore baseStore)
    {
        lock (_gate)
        {
            if (_dirty)
            {
                _frozen = BuildDeltaStore();
                _dirty = false;
            }

            return new SearchIndex(
                baseStore,
                _frozen,
                _deadRefs.Count > 0 ? new HashSet<ulong>(_deadRefs) : null,
                _deadPaths.Count > 0 ? new HashSet<string>(_deadPaths, StringComparer.OrdinalIgnoreCase) : null,
                _deadDirPrefixes.Count > 0 ? _deadDirPrefixes.ToArray() : null);
        }
    }

    /// <summary>整表重建后调用：基础索引已经包含最新状态，增量全部清空。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _addByRef.Clear();
            _addByPath.Clear();
            _deadRefs.Clear();
            _deadPaths.Clear();
            _deadDirPrefixes.Clear();
            _frozen = IndexStore.Empty;
            _dirty = false;
            _forceRebuild = false;
            TotalChanges = 0;
        }
    }

    private IndexStore BuildDeltaStore()
    {
        int capacity = _addByRef.Count + _addByPath.Count;
        if (capacity == 0) return IndexStore.Empty;

        var builder = new IndexStore.Builder();
        foreach (var e in _addByRef.Values) AddTo(builder, e);
        foreach (var e in _addByPath.Values) AddTo(builder, e);
        return builder.Freeze();
    }

    private static void AddTo(IndexStore.Builder builder, RawEntry e)
    {
        var category = e.Category;
        // 与 IndexBuilder 保持一致的分类兜底：数据源给不出类别时按扩展名/目录属性再判一次
        if (category == EntryCategory.Other)
            category = e.IsDirectory ? EntryCategory.Folder : CategoryClassifier.ClassifyByExtension(e.Name);

        builder.Add(e.Name, e.FullPath ?? string.Empty, category, e.FileRef, -1);
    }

    private static string EnsureTrailingSlash(string path)
    {
        var p = path.Replace('/', '\\');
        return p.EndsWith('\\') ? p : p + "\\";
    }
}
