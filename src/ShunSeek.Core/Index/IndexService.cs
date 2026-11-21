using System.Collections.Concurrent;
using System.Diagnostics;
using ShunSeek.Core.Sources;

namespace ShunSeek.Core;

public enum IndexMode
{
    /// <summary>管理员权限 + NTFS：MFT 直读全盘，USN 日志做增量。</summary>
    Mft,
    /// <summary>普通权限 / 非 NTFS：只索引用户目录等小范围，FileSystemWatcher 做增量。</summary>
    Portable,
}

/// <summary>索引状态。UI 只读这个对象来显示状态栏，不碰内部结构。</summary>
public sealed record IndexStatus(
    bool Ready,
    bool Building,
    IndexMode Mode,
    int EntryCount,
    int DeltaCount,
    int DeadCount,
    string Scope,
    string Message,
    DateTime? LastBuildAt,
    DateTime? LastUpdateAt)
{
    public static readonly IndexStatus Booting =
        new(false, true, IndexMode.Portable, 0, 0, 0, "", "正在准备索引…", null, null);
}

/// <summary>
/// 索引服务：把「建索引 → 增量更新 → 必要时重建」这条链路封成一个对象，UI 只负责显示状态。
///
/// 三条更新通道：
///  1) NTFS + 管理员：UsnWatcher 读 USN 日志（内核级、不丢事件、几乎零开销）；
///  2) 便携模式：DirectoryUpdateWatcher（FileSystemWatcher）盯用户目录这类小范围；
///  3) 兜底：日志被覆盖 / 监听溢出 / 墓碑攒太多 / 目录改名 → 后台整表重建（几秒，可接受）。
///
/// 线程模型：建索引在后台任务里跑；变更先进无锁队列，由一个 300ms 的定时器成批应用，
/// 每次应用都只是「重冻增量表 + 换快照」，所以文件刚建好就能搜到（验收线：1 秒内）。
/// </summary>
public sealed class IndexService : IDisposable
{
    private const int FlushIntervalMs = 300;

    private readonly UsageStore _usage;
    private readonly DeltaIndex _delta = new();
    private readonly ConcurrentQueue<PendingChange> _queue = new();
    private readonly SemaphoreSlim _buildGate = new(1, 1);
    private readonly Timer _flushTimer;
    private readonly CancellationTokenSource _cts = new();

    private readonly object _watchGate = new();
    private readonly List<IDisposable> _watchers = new();

    private readonly object _dirGate = new();
    private readonly Dictionary<ulong, string> _dirPaths = new();

    private readonly object _journalGate = new();
    private readonly Dictionary<char, long> _journalStarts = new();

    private IndexStore _base = IndexStore.Empty;
    private IndexMode _mode = IndexMode.Portable;
    private string _scope = string.Empty;
    private DateTime? _lastBuildAt;
    private DateTime? _lastUpdateAt;
    private int _flushing;
    private int _rebuildScheduled;
    private volatile bool _disposed;
    private volatile IndexStatus _status = IndexStatus.Booting;

    private readonly string _cachePath;
    private int _cacheSaving;
    private DateTime? _cacheBuiltAt;

    public IndexService(UsageStore usage, string? cachePath = null)
    {
        _usage = usage;
        _cachePath = cachePath ?? IndexCache.DefaultPath;
        Engine = new SearchEngine(SearchIndex.Empty, usage);
        _flushTimer = new Timer(_ => Flush(), null, FlushIntervalMs, FlushIntervalMs);
    }

    public SearchEngine Engine { get; }

    public UsageStore Usage => _usage;

    public IndexStatus Status => _status;

    /// <summary>状态变化（进度、增量更新、重建完成、错误）。可能来自后台线程，UI 端自己切回 UI 线程。</summary>
    public event Action<IndexStatus>? StatusChanged;

    /// <summary>
    /// 启动：先尝试吃磁盘缓存（几十万条也就几百毫秒），缓存可用就立刻"就绪"，
    /// 用户双击后马上能搜；缓存太旧/范围对不上才在后台重新建。
    /// </summary>
    public async Task StartAsync()
    {
        if (TryStartFromCache()) return;

        // 没有缓存：第一次启动只能老实扫一遍，扫完顺手把缓存写出来
        await RebuildAsync("建立索引").ConfigureAwait(false);
        SaveCache();
    }

    /// <summary>命中缓存就直接接管索引，返回 false 表示没有可用缓存。</summary>
    private bool TryStartFromCache()
    {
        var cached = IndexCache.TryLoad(_cachePath);
        if (cached == null) return false;

        _base = cached.Store;
        _mode = IndexMode.Portable;
        _scope = cached.Scope;
        _cacheBuiltAt = cached.BuiltAt;
        _lastBuildAt = cached.BuiltAt;
        _lastUpdateAt = cached.BuiltAt;

        Engine.Swap(new SearchIndex(_base, IndexStore.Empty, null, null, null));
        StartWatchers();
        CompactLargeHeap("缓存加载后");

        var age = DateTime.Now - cached.BuiltAt;
        bool stale = age > TimeSpan.FromHours(24);

        Publish(new IndexStatus(
            Ready: true, Building: false, Mode: _mode,
            EntryCount: _base.Count, DeltaCount: 0, DeadCount: 0,
            Scope: _scope,
            Message: $"缓存索引 · {cached.BuiltAt:HH:mm} · {_base.Count:N0} 条" + (stale ? "（后台更新中）" : ""),
            LastBuildAt: _lastBuildAt, LastUpdateAt: _lastUpdateAt));

        // 缓存超过一天：先用着，过一会儿在后台重扫一遍，免得漏掉关机期间的改动
        if (stale) ScheduleRebuild("缓存过期，后台刷新", delaySeconds: 15);
        return true;
    }

    /// <summary>把当前索引写进磁盘缓存（后台线程，不挡 UI）。</summary>
    private void SaveCache()
    {
        if (_base.Count == 0) return;
        if (Interlocked.Exchange(ref _cacheSaving, 1) == 1) return;

        var store = _base;
        var scope = _scope;
        var builtAt = _lastBuildAt ?? DateTime.Now;
        var path = _cachePath;

        _ = Task.Run(() =>
        {
            try
            {
                var sw = Stopwatch.StartNew();
                IndexCache.Save(path, store, scope, builtAt);
                AppLog($"[cache] saved {store.Count:N0} entries in {sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                AppLog("[cache] save failed: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _cacheSaving, 0);
            }
        });
    }

    /// <summary>诊断日志出口，App 会接到自己的日志文件里。</summary>
    public Action<string>? Log { get; set; }

    private void AppLog(string message) => Log?.Invoke(message);

    /// <summary>
    /// 索引是几块几十 MB 的大数组，天生落在大对象堆（LOH）上，LOH 默认不压缩，
    /// 反复重建几次就会留下大量空洞——任务管理器里看着就是"这软件怎么吃几百兆"。
    /// 每次换完大索引手动压一次，内存能立刻还回去。
    /// </summary>
    private void CompactLargeHeap(string when)
    {
        try
        {
            var before = GC.GetTotalMemory(forceFullCollection: false);
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

            long managed = GC.GetTotalMemory(forceFullCollection: false);
            long estimate = _base.EstimateMemoryBytes();
            AppLog($"[mem] {when}: managed={managed / 1024 / 1024}MB " +
                   $"(before={before / 1024 / 1024}MB) index≈{estimate / 1024 / 1024}MB " +
                   $"entries={_base.Count:N0}");
        }
        catch
        {
            // 诊断/优化失败不能影响搜索
        }
    }

    /// <summary>
    /// 整表重建：重新读 MFT / 遍历目录，建好后原子换掉搜索快照。
    /// 重建期间搜索照常可用（用的是旧快照），只是结果里暂时没有最新变化。
    /// </summary>
    public async Task RebuildAsync(string reason = "重建索引")
    {
        if (_disposed) return;
        await _buildGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            Publish(_status with { Building = true, Message = reason + "…" });
            StopWatchers();

            var sw = Stopwatch.StartNew();
            var result = await Task.Run(() => BuildCore(_cts.Token), _cts.Token).ConfigureAwait(false);

            _base = result.Store;
            lock (_dirGate)
            {
                _dirPaths.Clear();
                foreach (var kv in result.DirectoryPaths) _dirPaths[kv.Key] = kv.Value;
            }
            lock (_journalGate)
            {
                _journalStarts.Clear();
                foreach (var kv in result.JournalStarts) _journalStarts[kv.Key] = kv.Value;
            }

            _delta.Clear();
            _mode = result.Mode;
            _scope = result.Scope;
            _lastBuildAt = DateTime.Now;
            _lastUpdateAt = _lastBuildAt;

            Engine.Swap(new SearchIndex(_base, IndexStore.Empty, null, null, null));
            StartWatchers();

            Publish(new IndexStatus(
                Ready: true, Building: false, Mode: _mode,
                EntryCount: _base.Count, DeltaCount: 0, DeadCount: 0,
                Scope: _scope,
                Message: $"{reason}完成 · {sw.Elapsed.TotalSeconds:F1}s · {_base.Count:N0} 条",
                LastBuildAt: _lastBuildAt, LastUpdateAt: _lastUpdateAt));

            SaveCache();
            CompactLargeHeap("重建完成后");
        }
        catch (OperationCanceledException)
        {
            // 退出时取消，正常
        }
        catch (Exception ex)
        {
            Publish(_status with { Building = false, Message = "建索引失败：" + ex.Message });
        }
        finally
        {
            _buildGate.Release();
        }
    }

    /// <summary>手动刷新一次（托盘"立即刷新"）：把队列里攒的变更立刻应用，不必等 300ms。</summary>
    public void Refresh()
    {
        Flush();
        if (_delta.RequiresRebuild) ScheduleRebuild("手动刷新");
    }

    // ───────────────────────────── 建索引 ─────────────────────────────

    private sealed record BuildResult(
        IndexStore Store,
        Dictionary<ulong, string> DirectoryPaths,
        IndexMode Mode,
        string Scope,
        Dictionary<char, long> JournalStarts);

    private BuildResult BuildCore(CancellationToken ct)
    {
        var dirPaths = new Dictionary<ulong, string>();
        var journalStarts = new Dictionary<char, long>();

        bool elevated = Environment.IsPrivilegedProcess && OperatingSystem.IsWindows();
        var ntfsVolumes = FixedVolumes(requireNtfs: true);
        bool useMft = elevated && ntfsVolumes.Count > 0;

        var scope = useMft
            ? string.Join(",", ntfsVolumes.Select(c => c + ":")) + " · MFT 直读全盘"
            : "用户目录 · 便携模式";
        if (!elevated && OperatingSystem.IsWindows()) scope += "（未提权）";

        var progress = new ThrottledProgress(this);

        IEnumerable<RawEntry> Entries()
        {
            if (useMft)
            {
                // 先记下每卷日志写到哪儿了：建索引这几秒里发生的变更，起监听后从这儿补读（重放是幂等的）
                foreach (var v in ntfsVolumes)
                    if (UsnWatcher.TryQueryNextUsn(v, out var usn)) journalStarts[v] = usn;

                foreach (var e in new MftIndexSource(dirPaths, ntfsVolumes.ToArray()).Load(progress, ct))
                    yield return e;
            }
            else
            {
                foreach (var e in new DirectoryWalker(PortableRoots(), excludeDirs: PortableExcludes()).Load(progress, ct))
                    yield return e;
            }

            // 软件/快捷方式/安装目录：任何模式下都要（MFT 只能看到 .lnk 文件，不知道它是"微信"）。
            // 这里不接进度回调：它自己报的是"软件条目数"（几百条），会把总的进度数字顶回去。
            foreach (var e in new AppScanner().Load(null, ct)) yield return e;

            // 非 NTFS 的固定盘（exFAT 数据盘等）用遍历兜底
            foreach (var v in FixedVolumes(requireNtfs: false))
            {
                // FixedVolumes(requireNtfs:false) 含 NTFS 卷，MFT 模式下已经读过，这里只补非 NTFS 的
                if (useMft && ntfsVolumes.Contains(v)) continue;
                foreach (var e in new DirectoryWalker(new[] { v + ":\\" }).Load(null, ct)) yield return e;
            }
        }

        var store = IndexBuilder.Build(Entries(), null, progress, ct, dirPaths);
        return new BuildResult(store, dirPaths, useMft ? IndexMode.Mft : IndexMode.Portable, scope, journalStarts);
    }

    /// <summary>把进度节流成最多每 300ms 一次状态推送（MFT 枚举一秒能报上万次）。</summary>
    private sealed class ThrottledProgress : IProgress<int>
    {
        private readonly IndexService _owner;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private long _last;

        public ThrottledProgress(IndexService owner) => _owner = owner;

        public void Report(int value)
        {
            long now = _sw.ElapsedMilliseconds;
            if (now - _last < 300 && value > 0) return;
            _last = now;
            _owner.Publish(_owner._status with
            {
                Building = true,
                EntryCount = value,
                Message = $"正在建立索引 · {value:N0} 条…",
            });
        }
    }

    private static List<char> FixedVolumes(bool requireNtfs)
    {
        var list = new List<char>();
        if (!OperatingSystem.IsWindows()) return list;
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                bool isNtfs = string.Equals(d.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);
                if (isNtfs == requireNtfs) list.Add(char.ToUpperInvariant(d.Name[0]));
            }
            catch
            {
                // 卷刚拔出/不可访问：跳过
            }
        }
        return list;
    }

    private static string[] PortableRoots()
    {
        var roots = new List<string>();
        void Add(Environment.SpecialFolder folder)
        {
            var p = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) roots.Add(p);
        }
        Add(Environment.SpecialFolder.UserProfile);
        Add(Environment.SpecialFolder.CommonDesktopDirectory);
        Add(Environment.SpecialFolder.CommonDocuments);
        Add(Environment.SpecialFolder.CommonStartMenu);

        // 装在各处的软件本体：这两三个目录实测只有两三万条、几秒钟，
        // 不加的话"搜微信"只能搜到注册表登记的入口，搜不到它目录里的文件。
        Add(Environment.SpecialFolder.ProgramFiles);
        Add(Environment.SpecialFolder.ProgramFilesX86);
        Add(Environment.SpecialFolder.CommonApplicationData);
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>便携模式要绕开的目录：缓存/临时/包数据，几万个小文件但对用户毫无搜索价值。</summary>
    private static string[] PortableExcludes()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var list = new List<string>
        {
            // 微信 4.x 的聊天数据/缓存：几十万个 wx 开头的哈希目录，搜什么都盖住
            Path.Combine(roaming, "Tencent", "xwechat", "radium"),
            Path.Combine(roaming, "Tencent", "WeChat", "All Users"),
            Path.Combine(local, "Temp"),
            Path.Combine(local, "Packages"),
            Path.Combine(local, "Microsoft", "Windows"),
            Path.Combine(local, "CrashDumps"),
            Path.Combine(roaming, "Microsoft", "Windows", "Recent"),
            // 自己的日志/缓存：索引每次落盘都会写这里，不排除的话自己把自己刷爆
            Path.Combine(local, "ShunSeek"),
            // 浏览器/编辑器这些高频写的缓存区（只排缓存，不排它们的配置和书签）
            Path.Combine(local, "Google", "Chrome", "User Data", "Default", "Cache"),
            Path.Combine(local, "Microsoft", "Edge", "User Data", "Default", "Cache"),
            Path.Combine(roaming, "Code", "Cache"),
            Path.Combine(roaming, "Code", "CachedData"),
            Path.Combine(roaming, "Code", "logs"),
        };
        return list.Where(p => !string.IsNullOrEmpty(p)).ToArray();
    }

    // ───────────────────────────── 增量监听 ─────────────────────────────

    private void StartWatchers()
    {
        lock (_watchGate)
        {
            StopWatchersLocked();

            if (_mode == IndexMode.Mft)
            {
                foreach (var v in FixedVolumes(requireNtfs: true))
                {
                    long start = -1;
                    lock (_journalGate) _journalStarts.TryGetValue(v, out start);

                    var watcher = new UsnWatcher(v, start);
                    watcher.Changed += c => _queue.Enqueue(PendingChange.FromUsn(v, c));
                    watcher.Faulted += ex => OnUsnFault(v, ex);
                    try
                    {
                        watcher.Start();
                        _watchers.Add(watcher);
                    }
                    catch (Exception ex)
                    {
                        OnUsnFault(v, ex);
                    }
                }
            }
            else
            {
                var watcher = new DirectoryUpdateWatcher(PortableRoots(), PortableExcludes());
                watcher.Changed += c => _queue.Enqueue(PendingChange.FromFs(c));
                watcher.Start();
                _watchers.Add(watcher);
            }
        }
    }

    private void StopWatchers()
    {
        lock (_watchGate) StopWatchersLocked();
    }

    private void StopWatchersLocked()
    {
        foreach (var w in _watchers)
        {
            try { w.Dispose(); } catch { }
        }
        _watchers.Clear();
    }

    private void OnUsnFault(char drive, Exception ex)
    {
        switch (ex)
        {
            case JournalNotActiveException:
                Publish(_status with { Message = $"{drive}: 未开启 USN 日志，该卷只有全量索引（新文件需手动刷新）" });
                return;
            case JournalWrappedException:
                // 日志被卷满覆盖：中间那段变更丢了，只能重建
                _delta.RequestRebuild();
                ScheduleRebuild($"{drive}: USN 日志已覆盖");
                return;
            default:
                Publish(_status with { Message = $"{drive}: 增量监听中断（{ex.Message}）" });
                return;
        }
    }

    // ───────────────────────────── 增量应用 ─────────────────────────────

    private enum ChangeSource { Usn, Fs }

    private readonly record struct PendingChange(
        ChangeSource Source,
        UsnChangeKind UsnKind,
        FsChangeKind FsKind,
        string Name,
        string Path,
        string? OldPath,
        ulong FileRef,
        ulong ParentFileRef,
        bool IsDirectory)
    {
        public static PendingChange FromUsn(char drive, in UsnChange c) => new(
            ChangeSource.Usn, c.Kind, default, c.Name, string.Empty, null,
            c.FileRef, c.ParentFileRef, c.IsDirectory);

        public static PendingChange FromFs(in FsChange c) => new(
            ChangeSource.Fs, default, c.Kind, System.IO.Path.GetFileName(c.Path), c.Path, c.OldPath,
            0, 0, c.IsDirectory);
    }

    private void Flush()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _flushing, 1) == 1) return; // 上一批还没应用完，下一轮再说
        try
        {
            bool any = false;
            while (_queue.TryDequeue(out var change))
            {
                try { Apply(change); }
                catch { /* 单条变更处理失败不能拖垮整批 */ }
                any = true;
            }
            if (!any) return;

            _lastUpdateAt = DateTime.Now;
            Engine.Swap(_delta.Freeze(_base));

            if (_delta.RequiresRebuild) ScheduleRebuild("索引变化过多");

            Publish(_status with
            {
                Ready = true,
                Building = false,
                EntryCount = _base.Count + _delta.Count,
                DeltaCount = _delta.Count,
                DeadCount = _delta.DeadCount,
                LastUpdateAt = _lastUpdateAt,
                Message = $"增量更新 · {_delta.TotalChanges:N0} 次变更 · {_lastUpdateAt:HH:mm:ss}",
            });
        }
        finally
        {
            Interlocked.Exchange(ref _flushing, 0);
        }
    }

    private void Apply(in PendingChange c)
    {
        if (c.Source == ChangeSource.Usn) ApplyUsn(c);
        else ApplyFs(c);
    }

    private void ApplyUsn(in PendingChange c)
    {
        switch (c.UsnKind)
        {
            case UsnChangeKind.Created:
            case UsnChangeKind.RenamedNewName:
            {
                if (c.IsDirectory)
                {
                    var dirPath = ResolvePath(c.ParentFileRef, c.Name, out bool parentKnown);
                    if (parentKnown) lock (_dirGate) _dirPaths[c.FileRef] = dirPath;
                    if (c.UsnKind == UsnChangeKind.RenamedNewName)
                        ScheduleRebuild("目录改名"); // 子条目路径全变，USN 不会逐个通知
                    if (!parentKnown) { ScheduleRebuild("父目录未登记"); return; }
                    _delta.Upsert(new RawEntry(c.Name, dirPath, EntryCategory.Folder, c.FileRef, c.ParentFileRef, true));
                    return;
                }

                var path = ResolvePath(c.ParentFileRef, c.Name, out bool known);
                if (!known) { ScheduleRebuild("父目录未登记"); return; }
                _delta.Upsert(new RawEntry(
                    c.Name, path, CategoryClassifier.ClassifyByExtension(c.Name), c.FileRef, c.ParentFileRef));
                return;
            }

            case UsnChangeKind.RenamedOldName:
            {
                var oldPath = ResolvePath(c.ParentFileRef, c.Name, out _);
                _delta.Remove(c.FileRef, oldPath);
                if (c.IsDirectory)
                {
                    lock (_dirGate) _dirPaths.Remove(c.FileRef);
                    _delta.RemoveDirectory(oldPath, requestRebuild: true);
                }
                return;
            }

            case UsnChangeKind.Deleted:
            {
                var path = ResolvePath(c.ParentFileRef, c.Name, out _);
                _delta.Remove(c.FileRef, path);
                if (c.IsDirectory)
                {
                    lock (_dirGate) _dirPaths.Remove(c.FileRef);
                    _delta.RemoveDirectory(path, requestRebuild: false);
                }
                return;
            }
        }
    }

    private void ApplyFs(in PendingChange c)
    {
        switch (c.FsKind)
        {
            case FsChangeKind.Created:
            case FsChangeKind.Renamed:
            {
                if (c.FsKind == FsChangeKind.Renamed && c.OldPath != null)
                    RemoveFsPath(c.OldPath, c.IsDirectory, requestRebuild: c.IsDirectory);

                if (c.IsDirectory)
                    _delta.Upsert(new RawEntry(Path.GetFileName(c.Path), c.Path, EntryCategory.Folder, 0, 0, true));
                else
                    _delta.Upsert(new RawEntry(
                        Path.GetFileName(c.Path), c.Path, CategoryClassifier.ClassifyByExtension(c.Path), 0, 0));
                return;
            }

            case FsChangeKind.Deleted:
                RemoveFsPath(c.Path, c.IsDirectory, requestRebuild: false);
                return;

            case FsChangeKind.Overflow:
                _delta.RequestRebuild();
                ScheduleRebuild("文件监听溢出", delaySeconds: 25);
                return;
        }
    }

    private void RemoveFsPath(string path, bool isDirectory, bool requestRebuild)
    {
        _delta.Remove(0, path);
        if (isDirectory) _delta.RemoveDirectory(path, requestRebuild);
    }

    /// <summary>用「父目录引用号 + 文件名」拼全路径；父目录不认识时返回裸名并让上层安排重建。</summary>
    private string ResolvePath(ulong parentRef, string name, out bool parentKnown)
    {
        parentKnown = false;
        if (parentRef == 0) return name;

        string? parent;
        lock (_dirGate) _dirPaths.TryGetValue(parentRef, out parent);
        if (parent == null) return name;

        parentKnown = true;
        return parent.EndsWith('\\') ? parent + name : parent + "\\" + name;
    }

    /// <param name="delaySeconds">
    /// 重建延迟。日志被覆盖/目录改名这种"索引真的错了"给 2 秒就够；
    /// 监听溢出（高负载下很常见）给久一点，别在建完索引的地动山摇里又立刻重建一次。
    /// </param>
    private void ScheduleRebuild(string reason, int delaySeconds = 2)
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _rebuildScheduled, 1) == 1) return;

        // 刚建完不到一分钟的那点溢出，多半是建索引期间的正常抖动，等一等再说
        var delay = TimeSpan.FromSeconds(delaySeconds);
        if (_lastBuildAt is { } built && DateTime.Now - built < TimeSpan.FromSeconds(60))
            delay = TimeSpan.FromSeconds(60);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay).ConfigureAwait(false);
                await RebuildAsync(reason).ConfigureAwait(false);
            }
            catch
            {
                // 重建失败已在 RebuildAsync 里发过状态，这里只保证不抛到线程池
            }
            finally
            {
                Interlocked.Exchange(ref _rebuildScheduled, 0);
            }
        });
    }

    private void Publish(IndexStatus status)
    {
        _status = status;
        try { StatusChanged?.Invoke(status); }
        catch { /* UI 侧异常不影响索引线程 */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        try { _flushTimer.Dispose(); } catch { }
        StopWatchers();
        _cts.Dispose();
        _buildGate.Dispose();
    }
}
