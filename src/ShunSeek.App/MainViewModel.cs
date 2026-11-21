using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using ShunSeek.Core;

namespace ShunSeek.App;

public sealed class ResultItemViewModel
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string CategoryLabel { get; init; }

    /// <summary>图标位的字形（分类首字）与底色：不用外部图标资源，永远能渲染；
    /// 真实文件图标走异步提取缓存（见 MainWindow 注释的接入点），提取到后再替换。</summary>
    public required string TileText { get; init; }
    public required System.Windows.Media.Brush TileBackground { get; init; }

    /// <summary>在 SearchIndex 统一下标口径里的位置；"最近使用"列表里的条目为 -1。</summary>
    public int StoreIndex { get; init; }

    /// <summary>可打开的路径。软件条目可能只有名字没有路径（注册表没写 InstallLocation），这种行不给打开。</summary>
    public bool HasPath => !string.IsNullOrEmpty(Path);

    public bool IsDirectory { get; init; }
}

/// <summary>
/// 搜索窗的 ViewModel：接 Core 的 SearchEngine / IndexService。
/// 交互约定：输入变化 → 取消上一次查询 → 30ms 防抖（主要挡中文输入法连续上屏的中间态，
/// 完整的 IME composing 保护见开发流程阶段 2）→ 后台线程扫描 → 回 UI 线程换结果。
/// 状态栏显示索引范围/条数/增量情况，索引在后台更新时这里会跟着变。
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private SearchEngine? _engine;
    private UsageStore? _usage;
    private CancellationTokenSource? _pending;
    private string _query = string.Empty;
    private EntryCategory? _category;
    private string _statusText = "正在准备索引…";
    private string _resultText = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ResultItemViewModel> Results { get; } = new();

    public string Query
    {
        get => _query;
        set { if (_query != value) { _query = value; OnPropertyChanged(); ScheduleSearch(); } }
    }

    /// <summary>状态栏左侧：索引范围 / 条数 / 最后更新。</summary>
    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; OnPropertyChanged(); }
    }

    /// <summary>状态栏中间：本次查询命中数与耗时。</summary>
    public string ResultText
    {
        get => _resultText;
        private set { _resultText = value; OnPropertyChanged(); }
    }

    /// <summary>索引服务就绪后由 App 调一次。</summary>
    public void Attach(IndexService service)
    {
        _engine = service.Engine;
        _usage = service.Usage;
        ApplyStatus(service.Status);
        ScheduleSearch();
    }

    /// <summary>索引状态变化（可能来自后台线程）：切回 UI 线程再更新绑定。</summary>
    public void ApplyStatus(IndexStatus status)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => ApplyStatus(status));
            return;
        }

        StatusText = DescribeIndex(status);

        // 索引刚建好/刚更新完：如果现在没有输入，把"最近使用"或新条目刷出来
        if (!string.IsNullOrEmpty(_query))
        {
            // 查询结果里的条目可能变了（增量/重建），重查一次保证点开的是有效条目
            ScheduleSearch();
        }
    }

    /// <summary>状态栏中间那行的临时提示（"路径已不存在"之类）。</summary>
    public void Notify(string message) => ResultText = message;

    /// <summary>分类筛选条点选时调；null = 全部。</summary>
    public void SetCategory(EntryCategory? category)
    {
        _category = category;
        ScheduleSearch();
    }

    /// <summary>用户打开了某个结果：记一次频次（影响以后排序），并顺手落盘。</summary>
    public void RecordOpened(ResultItemViewModel item)
    {
        if (!item.HasPath) return;
        _usage?.RecordUse(item.Path);
        _usage?.Save();
    }

    private void ScheduleSearch()
    {
        _pending?.Cancel();
        _pending?.Dispose();
        var cts = new CancellationTokenSource();
        _pending = cts;
        _ = RunSearchAsync(cts.Token);
    }

    private async Task RunSearchAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(30, token); // 防抖：连续输入只查最后一次
        }
        catch (TaskCanceledException) { return; }

        var engine = _engine;
        if (engine == null) return;

        var query = _query;
        var category = _category;
        var sw = Stopwatch.StartNew();
        IReadOnlyList<SearchHit> hits = Array.Empty<SearchHit>();
        var snapshot = engine.Index;
        try
        {
            if (!string.IsNullOrWhiteSpace(query))
                hits = await Task.Run(() => engine.Search(query, category, 100, token), token);
        }
        catch (OperationCanceledException) { return; }
        sw.Stop();

        if (token.IsCancellationRequested) return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        await dispatcher.InvokeAsync(() =>
        {
            if (token.IsCancellationRequested) return;
            Results.Clear();

            if (string.IsNullOrWhiteSpace(query))
            {
                // 空查询：优先"最近使用"（真用过的东西），没有再列索引前 100 条
                int recent = 0;
                foreach (var path in _usage?.TopPaths(12) ?? Array.Empty<string>())
                {
                    Results.Add(FromPath(path));
                    recent++;
                }
                if (recent == 0)
                {
                    var idx = engine.Index;
                    int n = Math.Min(idx.Count, 100);
                    for (int i = 0; i < n; i++) Results.Add(FromIndex(idx, i));
                }
                ResultText = string.Empty;
            }
            else
            {
                foreach (var hit in hits) Results.Add(FromIndex(snapshot, hit.Index));
                ResultText = $"{hits.Count} 条结果 · {sw.Elapsed.TotalMilliseconds:F0} ms";
            }
        });
    }

    private static string DescribeIndex(IndexStatus s)
    {
        if (s.Building) return string.IsNullOrEmpty(s.Message) ? "正在建立索引…" : s.Message;

        var scope = string.IsNullOrEmpty(s.Scope) ? "索引" : s.Scope;
        var text = $"{scope} · {s.EntryCount:N0} 条";
        if (s.DeltaCount > 0) text += $" · 增量 {s.DeltaCount}";
        if (s.LastUpdateAt is { } t) text += $" · {t:HH:mm:ss} 更新";
        if (!s.Ready && !string.IsNullOrEmpty(s.Message)) text += " · " + s.Message;
        return text;
    }

    private static ResultItemViewModel FromIndex(SearchIndex index, int i)
    {
        var cat = index.CategoryAt(i);
        var name = index.DisplayNameAt(i);
        return new ResultItemViewModel
        {
            Name = name,
            Path = index.FullPathAt(i),
            CategoryLabel = EntryCategoryNames.DisplayName(cat),
            TileText = TileTextOf(cat, name),
            TileBackground = TileBrushOf(cat),
            StoreIndex = i,
            IsDirectory = cat is EntryCategory.Folder or EntryCategory.InstallDir,
        };
    }

    private static ResultItemViewModel FromPath(string path)
    {
        var isDir = SafeIsDirectory(path);
        var cat = isDir ? EntryCategory.Folder : CategoryClassifier.ClassifyByExtension(path);
        var name = Path.GetFileName(path.TrimEnd('\\'));
        if (string.IsNullOrEmpty(name)) name = path;
        return new ResultItemViewModel
        {
            Name = name,
            Path = path,
            CategoryLabel = EntryCategoryNames.DisplayName(cat),
            TileText = TileTextOf(cat, name),
            TileBackground = TileBrushOf(cat),
            StoreIndex = -1,
            IsDirectory = isDir,
        };
    }

    private static bool SafeIsDirectory(string path)
    {
        try { return Directory.Exists(path); }
        catch { return false; }
    }

    private static string TileTextOf(EntryCategory cat, string name) => cat switch
    {
        EntryCategory.App => "软",
        EntryCategory.Shortcut => "捷",
        EntryCategory.InstallDir => "录",
        EntryCategory.Folder => "夹",
        EntryCategory.Document => "文",
        EntryCategory.Image => "图",
        EntryCategory.Video => "视",
        EntryCategory.Audio => "音",
        EntryCategory.Archive => "压",
        EntryCategory.Code => "码",
        EntryCategory.Executable => "程",
        _ => string.IsNullOrEmpty(name) ? "?" : name[..1],
    };

    private static System.Windows.Media.Brush TileBrushOf(EntryCategory cat)
    {
        var hex = cat switch
        {
            EntryCategory.App => "#2563EB",
            EntryCategory.Shortcut => "#7C3AED",
            EntryCategory.InstallDir => "#475569",
            EntryCategory.Folder => "#D97706",
            EntryCategory.Document => "#0EA5E9",
            EntryCategory.Image => "#059669",
            EntryCategory.Video => "#DC2626",
            EntryCategory.Audio => "#DB2777",
            EntryCategory.Archive => "#78716C",
            EntryCategory.Code => "#0D9488",
            EntryCategory.Executable => "#4F46E5",
            _ => "#6B7280",
        };
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
