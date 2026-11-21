namespace ShunSeek.Core;

/// <summary>
/// 统一的索引数据源接口：MFT 枚举、目录遍历、开始菜单/注册表、（UI 兜底用的）演示数据
/// 都实现它，IndexBuilder 只管汇总，不关心条目从哪来。
/// Load 允许慢（扫盘/读注册表），实现里应响应 ct 取消、并通过 progress 报告已产出条数。
/// </summary>
public interface IIndexSource
{
    /// <summary>数据源名字，进日志和进度提示，如 "MFT C:"、"开始菜单"。</summary>
    string Name { get; }

    IEnumerable<RawEntry> Load(IProgress<int>? progress, CancellationToken ct);
}
