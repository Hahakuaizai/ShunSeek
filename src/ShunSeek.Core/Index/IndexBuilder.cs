namespace ShunSeek.Core;

/// <summary>
/// 把若干数据源汇总成一个 IndexStore：
///  1) 先把全部 RawEntry 攒成表，建 FileRef → 下标 的映射；
///  2) 给没有 FullPath 的条目（来自 MFT）沿 ParentFileRef 链拼出完整路径；
///  3) 分类兜底：数据源给了 Other 且不是文件夹时，按扩展名再分一次；
///  4) 顺手把「目录引用号 → 目录路径」表填出来——增量更新靠它给新建文件拼路径；
///  5) 灌进 IndexStore.Builder 并 Freeze。
/// 建索引允许花几秒（MFT 直读本身就是秒级），搜索热路径不在这里。
/// </summary>
public static class IndexBuilder
{
    /// <param name="volumeRoots">MFT 条目拼路径用的卷根，如 ['C'] 对应 "C:\"。key 是卷号字符。</param>
    /// <param name="directoryPaths">
    /// 输出：目录的文件引用号 → 完整路径。增量监听（UsnWatcher）拿到新记录时只有父目录引用号，
    /// 靠这张表才能拼出全路径。只收目录（数量是文件数的几十分之一），几 MB 量级。
    /// </param>
    public static IndexStore Build(
        IEnumerable<RawEntry> entries,
        IReadOnlyDictionary<char, string>? volumeRoots = null,
        IProgress<int>? progress = null,
        CancellationToken ct = default,
        IDictionary<ulong, string>? directoryPaths = null)
    {
        var list = new List<RawEntry>();
        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            list.Add(e);
            if ((list.Count & 0xFFFF) == 0) progress?.Report(list.Count);
        }
        progress?.Report(list.Count);

        // 软件类条目（注册表/开始菜单来的，FileRef 恒为 0）的路径集合：
        // MFT 也会枚举到同一个 .lnk，那个名字还带 .lnk 后缀，会跟"软件"条目重复成两行。
        // 这里记下软件条目的路径，MFT 侧的 .lnk 一律让位（保留信息更全的那条）。
        var softwarePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in list)
        {
            if (e.FileRef == 0 && !string.IsNullOrEmpty(e.FullPath) &&
                e.Category is EntryCategory.App or EntryCategory.Shortcut or EntryCategory.InstallDir)
                softwarePaths.Add(e.FullPath!);
        }

        // FileRef → 下标（只有 MFT 条目带引用号）
        var refToIndex = new Dictionary<ulong, int>();
        for (int i = 0; i < list.Count; i++)
            if (list[i].FileRef != 0) refToIndex[list[i].FileRef] = i;

        var builder = new IndexStore.Builder();
        int skipped = 0;
        for (int i = 0; i < list.Count; i++)
        {
            var e = list[i];

            string fullPath = e.FullPath ?? ResolveMftPath(i, list, refToIndex, volumeRoots);

            // MFT 侧的 .lnk 与「软件」条目重复：跳过 MFT 那条
            if (e.FileRef != 0 && e.Category == EntryCategory.Shortcut && softwarePaths.Contains(fullPath))
            {
                skipped++;
                continue;
            }

            var category = e.Category;
            if (category == EntryCategory.Other && !e.IsDirectory)
                category = CategoryClassifier.ClassifyByExtension(e.Name);
            if (e.IsDirectory && category == EntryCategory.Other)
                category = EntryCategory.Folder;

            if (e.IsDirectory && e.FileRef != 0 && directoryPaths != null)
                directoryPaths[e.FileRef] = fullPath;

            int parentIndex = -1;
            if (e.ParentFileRef != 0 && refToIndex.TryGetValue(e.ParentFileRef, out var p))
                parentIndex = p;

            builder.Add(e.Name, fullPath, category, e.FileRef, parentIndex);
        }

        if (skipped > 0) progress?.Report(list.Count);
        return builder.Freeze();
    }

    /// <summary>
    /// 沿父引用链拼路径：从条目自身往上收集名字段，反转后用 '\' 连接，再冠上卷根。
    /// 父链断裂（父条目不在表里，多见于卷根目录的父 = 卷本身）时就停在已收集的部分。
    /// 防环：最多走 64 层（真实路径深度远小于此）。
    /// </summary>
    private static string ResolveMftPath(
        int index,
        List<RawEntry> list,
        Dictionary<ulong, int> refToIndex,
        IReadOnlyDictionary<char, string>? volumeRoots)
    {
        var parts = new List<string>();
        int cur = index;
        for (int depth = 0; depth < 64; depth++)
        {
            var e = list[cur];
            parts.Add(e.Name);
            if (e.ParentFileRef == 0 || e.ParentFileRef == e.FileRef) break;
            if (!refToIndex.TryGetValue(e.ParentFileRef, out var parent)) break;
            cur = parent;
        }
        parts.Reverse();
        var joined = string.Join("\\", parts);
        // 卷根：MFT 条目不带盘符，由数据源在 RawEntry 之外通过 volumeRoots 告知；
        // 找不到对应卷时退而用第一项。调用方按卷分别 Build 再合并时可精确对应。
        if (volumeRoots != null && volumeRoots.Count > 0)
        {
            foreach (var kv in volumeRoots) return kv.Value + joined;
        }
        return joined;
    }
}
