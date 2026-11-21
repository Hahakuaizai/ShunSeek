using System.IO;
using System.Text.Json;

namespace ShunSeek.App.Services;

/// <summary>
/// 首页固定项：用户挑出来的文件夹/图片/软件，固定在搜索结果前面。
/// 存成一个很小的 JSON（%LocalAppData%\ShunSeek\pinned.json），读失败一律当空表，
/// 绝不因为它坏了就起不来。
/// </summary>
public sealed class PinnedStore
{
    private readonly List<string> _paths = new();
    private readonly string? _filePath;
    private readonly object _gate = new();

    private PinnedStore(string? filePath) => _filePath = filePath;

    public static PinnedStore Load(string? filePath)
    {
        var store = new PinnedStore(filePath);
        if (filePath == null || !File.Exists(filePath)) return store;
        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(filePath));
            if (list != null)
                foreach (var path in list)
                    if (!string.IsNullOrWhiteSpace(path)) store._paths.Add(path);
        }
        catch
        {
            // 文件坏了：当空表，不影响启动
        }
        return store;
    }

    public IReadOnlyList<string> Paths
    {
        get { lock (_gate) return _paths.ToArray(); }
    }

    public bool Contains(string path)
    {
        lock (_gate) return _paths.Contains(path, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>固定一项；已经在里面就挪到最前面（最近固定的排前面）。</summary>
    public void Add(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (_gate)
        {
            _paths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            _paths.Insert(0, path);
        }
        Save();
    }

    public void Remove(string path)
    {
        lock (_gate) _paths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    private void Save()
    {
        if (_filePath == null) return;
        try
        {
            List<string> snapshot;
            lock (_gate) snapshot = new List<string>(_paths);
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(snapshot));
        }
        catch
        {
            // 落盘失败（只读盘/权限）：内存里已经生效，下次再说
        }
    }
}
