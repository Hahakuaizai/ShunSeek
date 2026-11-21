namespace ShunSeek.Core;

/// <summary>
/// 一次搜索看到的完整索引快照 = 基础索引（建好即冻结） + 增量索引（启动后新出现的条目） + 墓碑（已删除的条目）。
///
/// 为什么分两块：
///  - 全量索引是百万级、建一次要几秒，绝不能为了"新建了一个文件"去重建它；
///  - 增量表只有几十到几千条，重建一次是微秒级，所以每次增量更新只需要重冻增量表；
///  - 删除不需要从基础索引里挖洞（数组没法挖洞），而是记一块"墓碑"，扫描命中后一查就跳过。
///    墓碑攒多了（或目录改名导致整棵子树的路径都失效）就整表重建，几秒钟的事。
///
/// 这个对象构造好之后就不再变化，可以放心被多个搜索线程并发读；更新时整体换一个新快照
/// （SearchEngine.Swap），正在跑的旧查询继续用旧快照跑完，天然没有锁竞争。
/// </summary>
public sealed class SearchIndex
{
    public static readonly SearchIndex Empty =
        new(IndexStore.Empty, IndexStore.Empty, null, null, null);

    public IndexStore Base { get; }
    public IndexStore Delta { get; }

    private readonly HashSet<ulong>? _deadRefs;    // 被删除/改名的文件引用号（只作用于基础索引）
    private readonly HashSet<string>? _deadPaths;  // 被删除的精确路径（非 NTFS 兜底模式用）
    private readonly string[]? _deadDirPrefixes;   // 被删除/改名的目录前缀（含结尾 '\'），整棵子树失效

    public SearchIndex(
        IndexStore baseStore,
        IndexStore deltaStore,
        HashSet<ulong>? deadRefs,
        HashSet<string>? deadPaths,
        string[]? deadDirPrefixes)
    {
        Base = baseStore;
        Delta = deltaStore;
        _deadRefs = deadRefs;
        _deadPaths = deadPaths;
        _deadDirPrefixes = deadDirPrefixes;
    }

    /// <summary>总条目数（含增量；墓碑只在结果里过滤，不计入这个数）。</summary>
    public int Count => Base.Count + Delta.Count;

    public int DeadCount =>
        (_deadRefs?.Count ?? 0) + (_deadPaths?.Count ?? 0) + (_deadDirPrefixes?.Length ?? 0);

    // —— 统一下标口径：0 .. Base.Count-1 是基础索引，之后是增量索引。SearchHit.Index 用的就是这套口径。——
    private bool IsBase(int index) => index < Base.Count;

    public string DisplayNameAt(int index) =>
        IsBase(index) ? Base.DisplayName[index] : Delta.DisplayName[index - Base.Count];

    public string FullPathAt(int index) =>
        IsBase(index) ? Base.FullPath[index] : Delta.FullPath[index - Base.Count];

    public EntryCategory CategoryAt(int index) =>
        IsBase(index) ? Base.CategoryOf(index) : Delta.CategoryOf(index - Base.Count);

    /// <summary>
    /// 搜索。与老版本 SearchEngine.Search 参数一致，额外接一个「频次加分」委托
    /// （只在命中后调用，不进扫描循环），这样基础与增量两部分能共用同一套打分。
    /// </summary>
    public IReadOnlyList<SearchHit> Search(
        string query,
        EntryCategory? category,
        int limit,
        Func<string, float>? boost,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0 || Count == 0)
            return Array.Empty<SearchHit>();

        var q = query.Trim().ToLowerInvariant();
        bool initialsEligible = IsAsciiLettersOrDigits(q);
        byte? catByte = category.HasValue ? (byte)category.Value : null;

        int partitions = Math.Max(1, Environment.ProcessorCount);
        var locals = new List<SearchHit>[partitions];

        // ① 基础索引：按核数分片并行扫
        int baseCount = Base.Count;
        if (baseCount > 0)
        {
            var options = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = partitions };
            try
            {
                Parallel.For(0, partitions, options, p =>
                {
                    int start = (int)((long)baseCount * p / partitions);
                    int end = (int)((long)baseCount * (p + 1) / partitions);
                    var store = Base;
                    var qSpan = q.AsSpan(); // Span 不能跨 lambda 捕获，在分区内部取
                    var local = new List<SearchHit>(256);
                    for (int i = start; i < end; i++)
                    {
                        if (catByte.HasValue && store.Category[i] != catByte.Value) continue;

                        int matchAt = store.LowerName(i).IndexOf(qSpan, StringComparison.Ordinal);
                        bool viaInitials = false;
                        if (matchAt < 0 && initialsEligible)
                        {
                            matchAt = store.Initials(i).IndexOf(qSpan, StringComparison.Ordinal);
                            viaInitials = matchAt >= 0;
                        }
                        if (matchAt < 0) continue;
                        if (IsBaseDead(i)) continue; // 墓碑：已删除的条目，命中也不返回

                        local.Add(new SearchHit(i, Score(store, i, matchAt, viaInitials, boost), matchAt, viaInitials));

                        // 防爆：某分区命中过多（如搜 "a"）时先排序截断，避免内存与排序成本失控
                        if (local.Count >= 16384)
                        {
                            local.Sort(static (a, b) => b.Score.CompareTo(a.Score));
                            local.RemoveRange(4096, local.Count - 4096);
                        }
                    }
                    locals[p] = local;
                });
            }
            catch (OperationCanceledException)
            {
                return Array.Empty<SearchHit>(); // 被新查询取代，UI 丢弃这次结果即可
            }
        }

        // ② 增量索引：通常只有几百条，单线程扫完即可（并进 0 号分区的结果里）
        int deltaCount = Delta.Count;
        if (deltaCount > 0)
        {
            var store = Delta;
            var qSpan = q.AsSpan();
            var local = locals[0] ??= new List<SearchHit>(64);
            for (int i = 0; i < deltaCount; i++)
            {
                if (catByte.HasValue && store.Category[i] != catByte.Value) continue;
                int matchAt = store.LowerName(i).IndexOf(qSpan, StringComparison.Ordinal);
                bool viaInitials = false;
                if (matchAt < 0 && initialsEligible)
                {
                    matchAt = store.Initials(i).IndexOf(qSpan, StringComparison.Ordinal);
                    viaInitials = matchAt >= 0;
                }
                if (matchAt < 0) continue;
                local.Add(new SearchHit(
                    baseCount + i, Score(store, i, matchAt, viaInitials, boost), matchAt, viaInitials));
            }
        }

        int total = 0;
        foreach (var l in locals) if (l != null) total += l.Count;
        if (total == 0) return Array.Empty<SearchHit>();

        var all = new List<SearchHit>(total);
        foreach (var l in locals) if (l != null) all.AddRange(l);
        all.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        if (all.Count > limit) all.RemoveRange(limit, all.Count - limit);
        return all;
    }

    /// <summary>基础索引条目的墓碑判定。只有命中之后才走这里，所以热路径不受影响。</summary>
    private bool IsBaseDead(int i)
    {
        var refs = _deadRefs;
        if (refs != null && refs.Count > 0)
        {
            ulong r = Base.FileRef[i];
            if (r != 0 && refs.Contains(r)) return true;
        }

        var paths = _deadPaths;
        if (paths != null && paths.Count > 0 && paths.Contains(Base.FullPath[i])) return true;

        var dirs = _deadDirPrefixes;
        if (dirs != null && dirs.Length > 0)
        {
            var path = Base.FullPath[i];
            foreach (var d in dirs)
                if (path.StartsWith(d, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static float Score(IndexStore store, int index, int matchAt, bool viaInitials, Func<string, float>? boost)
    {
        var name = store.LowerName(index);
        float score;
        if (viaInitials)
        {
            score = matchAt == 0 ? 80f : 50f;
        }
        else if (matchAt == 0)
        {
            score = 100f;
        }
        else
        {
            // 词首命中：命中点前一个字符是分隔符（空格、-、_、·、( 等）或前一字是汉字后一字起词
            char prev = name[matchAt - 1];
            bool wordStart = prev is ' ' or '-' or '_' or '·' or '(' or '（' or '.'
                || (prev >= 0x4E00 && prev <= 0x9FFF && !IsCjk(name[matchAt]));
            score = wordStart ? 70f : 40f;
        }
        score -= matchAt * 0.1f;      // 越靠前越好
        score -= name.Length * 0.01f; // 同等命中下短名优先（"QQ" 本体压过 "QQ音乐"）

        score += store.CategoryOf(index) switch
        {
            EntryCategory.App => 15f,
            EntryCategory.Shortcut => 15f,
            EntryCategory.InstallDir => 5f,
            EntryCategory.Folder => 5f,
            _ => 0f,
        };

        if (boost != null)
            score += boost(store.FullPath[index]);

        return score;
    }

    private static bool IsCjk(char c) => c is >= (char)0x4E00 and <= (char)0x9FFF;

    private static bool IsAsciiLettersOrDigits(string s)
    {
        foreach (var c in s)
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))) return false;
        return s.Length > 0;
    }
}
