namespace ShunSeek.Core;

/// <summary>一条搜索命中。Index 是 SearchIndex 统一口径下的下标，组装显示信息时再去取冷数据。</summary>
public readonly record struct SearchHit(int Index, float Score, int MatchStart, bool ViaInitials);

/// <summary>
/// 搜索引擎：对当前索引快照做并行子串扫描。
///
/// 做法（和 Everything 的"快"不是一回事，它快在索引；我们快在扫描本身）：
///  - 把条目区间按核数切成段，Parallel.For 并行扫；每段在连续的 char 池上用
///    ReadOnlySpan&lt;char&gt;.IndexOf（运行时内部向量化）找子串，200 万条目一遍几十毫秒量级。
///  - 查询是纯 ASCII 字母时才启用第二通道「拼音首字母」匹配（"wzry" 命中「王者荣耀」）；
///    含汉字/符号的查询只走名字通道，避免无谓开销。
///  - 打分：前缀命中 100 / 词首命中 70 / 普通子串 40，减去命中位置与名长的微小惩罚；
///    App/Shortcut 等"人找软件"的类别给固定加权；最后叠加 UsageStore 的频次加分。
///    拼音通道命中按 0.8 折算，排在同档名字命中之后。
///  - 新输入到来时用 CancellationToken 取消旧查询，UI 永远只呈现最新一次的结果。
///
/// 索引更新（增量表 / 整表重建）通过 Swap 换一个不可变快照进来：
/// 正在跑的查询继续用旧快照跑完，新查询立刻看到新快照，不需要任何锁。
/// </summary>
public sealed class SearchEngine
{
    private volatile SearchIndex _index;
    private readonly Func<string, float>? _boost;

    public SearchEngine(IndexStore store, UsageStore? usage = null)
        : this(new SearchIndex(store, IndexStore.Empty, null, null, null), usage)
    {
    }

    public SearchEngine(SearchIndex index, UsageStore? usage = null)
    {
        _index = index;
        if (usage != null) _boost = usage.GetBoost;
    }

    /// <summary>当前快照（查询期间保持一致，不会被中途换掉）。</summary>
    public SearchIndex Index => _index;

    /// <summary>基础索引（兼容老调用点：Bench 与 UI 直接用它的 DisplayName/FullPath）。</summary>
    public IndexStore Store => _index.Base;

    /// <summary>换上一个新快照（增量更新或整表重建后调用）。</summary>
    public void Swap(SearchIndex index) => _index = index;

    /// <param name="category">null = 全部分类；否则只扫该类别（筛选在扫描内层第一步做，几乎零成本）。</param>
    /// <param name="limit">最多返回条数。UI 传 100 即可：多取无用，排序成本也涨。</param>
    public IReadOnlyList<SearchHit> Search(string query, EntryCategory? category, int limit, CancellationToken ct = default)
        => _index.Search(query, category, limit, _boost, ct);
}
