using System.Text.Json;

namespace ShunSeek.Core;

/// <summary>
/// 使用频次存储（Frecency 的一半：Frequency + Recency）。
/// 用户每打开一个结果就 RecordUse 一次，搜索排序时 GetBoost 给常打开的条目加分。
/// 存成一个小 JSON 文件（如 %LocalAppData%\ShunSeek\usage.json），
/// 加载失败/文件损坏一律当空表处理，绝不因为它拖垮主流程。
/// </summary>
public sealed class UsageStore
{
    public sealed class Info
    {
        public int Count { get; set; }
        public DateTime LastUsedUtc { get; set; }
    }

    private readonly Dictionary<string, Info> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly string? _filePath;

    private UsageStore(string? filePath) => _filePath = filePath;

    public static UsageStore Load(string? filePath)
    {
        var store = new UsageStore(filePath);
        if (filePath == null || !File.Exists(filePath)) return store;
        try
        {
            var json = File.ReadAllText(filePath);
            var data = JsonSerializer.Deserialize<Dictionary<string, Info>>(json);
            if (data != null)
                foreach (var kv in data) store._map[kv.Key] = kv.Value;
        }
        catch
        {
            // 损坏就当空表：频次只是排序加分，丢了不影响搜索本身
        }
        return store;
    }

    /// <summary>记录一次打开。内存里先累加，落盘由调用方在空闲时调 Save（避免每次打开都写盘）。</summary>
    public void RecordUse(string path)
    {
        lock (_lock)
        {
            if (!_map.TryGetValue(path, out var info))
            {
                info = new Info();
                _map[path] = info;
            }
            info.Count++;
            info.LastUsedUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// 排序加分：频次部分封顶（防老条目霸榜），最近用过再给一点新鲜度加分。
    /// 返回值刻意保持在个位数到十几分量级，只影响同档命中之间的顺序。
    /// </summary>
    public float GetBoost(string path)
    {
        lock (_lock)
        {
            if (!_map.TryGetValue(path, out var info)) return 0f;
            float boost = Math.Min(info.Count, 50) * 0.2f;
            if ((DateTime.UtcNow - info.LastUsedUtc).TotalDays < 7) boost += 2f;
            return boost;
        }
    }

    /// <summary>最近打开过的前 n 条路径（空查询时展示"最近使用"，比罗列磁盘前 100 条有用得多）。</summary>
    public IReadOnlyList<string> TopPaths(int n)
    {
        if (n <= 0) return Array.Empty<string>();
        lock (_lock)
        {
            return _map
                .OrderByDescending(kv => kv.Value.LastUsedUtc)
                .ThenByDescending(kv => kv.Value.Count)
                .Take(n)
                .Select(kv => kv.Key)
                .ToList();
        }
    }

    public void Save()
    {
        if (_filePath == null) return;
        try
        {
            Dictionary<string, Info> snapshot;
            lock (_lock) snapshot = new Dictionary<string, Info>(_map, StringComparer.OrdinalIgnoreCase);
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(snapshot));
        }
        catch
        {
            // 落盘失败（磁盘满/无权限）静默放弃，下次再存
        }
    }
}
