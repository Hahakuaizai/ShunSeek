using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using ShunSeek.Core;

namespace ShunSeek.App;

public sealed class ResultItemViewModel : INotifyPropertyChanged
{
    private ImageSource? _icon;

    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string CategoryLabel { get; init; }
    public required EntryCategory Category { get; init; }

    /// <summary>在 SearchIndex 统一下标口径里的位置；"最近使用"列表里的条目为 -1。</summary>
    public int StoreIndex { get; init; }

    public bool IsDirectory { get; init; }

    /// <summary>可打开的路径。软件条目偶尔只有名字（注册表没写 InstallLocation），这种行不给打开。</summary>
    public bool HasPath => !string.IsNullOrEmpty(Path);

    /// <summary>系统原版图标（Shell 给的），异步填进来；没到之前显示一块淡占位。</summary>
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value)) return;
            _icon = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PlaceholderVisibility));
            OnPropertyChanged(nameof(IconVisibility));
        }
    }

    public Visibility PlaceholderVisibility => _icon == null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IconVisibility => _icon == null ? Visibility.Collapsed : Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 搜索窗的 ViewModel：接 Core 的 SearchEngine / IndexService，产出一格格"图标 + 名字"的磁贴。
/// 交互约定：输入变化 → 取消上一次查询 → 30ms 防抖 → 后台线程扫描 → 回 UI 线程换结果；
/// 图标另外异步取（专用线程，不抢索引的 CPU），绝不阻塞结果上屏。
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly IconCache _icons = new();
    private SearchEngine? _engine;
    private UsageStore? _usage;
    private CancellationTokenSource? _pending;
    private string _query = string.Empty;
    private EntryCategory? _category;
    private string _statusText = "正在准备索引…";
    private string _resultText = string.Empty;
    private string _emptyHintText = "输入关键词开始搜索";
    private Visibility _emptyHintVisibility = Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ResultItemViewModel> Results { get; } = new();

    public IndexService? Service { get; private set; }

    public string Query
    {
        get => _query;
        set { if (_query != value) { _query = value; OnPropertyChanged(); ScheduleSearch(); } }
    }

    /// <summary>状态栏左侧：索引条数 / 最后更新。</summary>
    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; OnPropertyChanged(); }
    }

    /// <summary>首页只在"什么都没搜"且没有最近记录时给一句提示，不铺一屏东西。</summary>
    public string EmptyHintText
    {
        get => _emptyHintText;
        private set { _emptyHintText = value; OnPropertyChanged(); }
    }

    public Visibility EmptyHintVisibility
    {
        get => _emptyHintVisibility;
        private set { _emptyHintVisibility = value; OnPropertyChanged(); }
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
        Service = service;
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
    }

    /// <summary>状态栏中间那行的临时提示（"路径已不存在"之类）。</summary>
    public void Notify(string message) => ResultText = message;

    /// <summary>类型筛选（顶栏的漏斗按钮）；null = 全部。</summary>
    public void SetCategory(EntryCategory? category)
    {
        _category = category;
        ScheduleSearch();
    }

    public EntryCategory? Category => _category;

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
        var snapshot = engine.Index;
        var sw = Stopwatch.StartNew();

        List<ResultItemViewModel> items;
        string resultText;
        try
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                items = await Task.Run(() => BuildLauncherItems(_usage, token), token);
                resultText = string.Empty;
            }
            else
            {
                var hits = await Task.Run(() => engine.Search(query, category, 100, token), token);
                items = hits.Select(h => FromIndex(snapshot, h.Index)).ToList();
                resultText = $"{hits.Count} 项 · {sw.Elapsed.TotalMilliseconds:F0} ms";
            }
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
            foreach (var item in items) Results.Add(item);
            ResultText = resultText;

            // 首页克制一点：有"最近使用"就只放最近几条，什么都没有就留一句提示，不铺满
            EmptyHintText = string.IsNullOrWhiteSpace(query)
                ? "输入关键词开始搜索"
                : "没有匹配的内容";
            EmptyHintVisibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // 图标另起炉灶：先占位，再逐个替换
            foreach (var item in items) LoadIcon(item);
        });
    }

    /// <summary>
    /// 空查询时只显示"最近打开过的几条"（最多 8 条），不把整个软件列表铺到首页——
    /// 启动器的第一屏应该是干净的，用户想找什么自己输入。
    /// </summary>
    private static List<ResultItemViewModel> BuildLauncherItems(UsageStore? usage, CancellationToken token)
    {
        const int limit = 8;
        var items = new List<ResultItemViewModel>();
        foreach (var path in usage?.TopPaths(limit) ?? Array.Empty<string>())
        {
            token.ThrowIfCancellationRequested();
            items.Add(FromPath(path));
        }
        return items;
    }

    private void LoadIcon(ResultItemViewModel item)
    {
        if (!item.HasPath) return;

        var cached = _icons.TryGet(item.Path, item.Category);
        if (cached != null)
        {
            item.Icon = cached;
            return;
        }

        _ = Task.Run(async () =>
        {
            var icon = await _icons.GetAsync(item.Path, item.Category).ConfigureAwait(false);
            if (icon == null) return;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => item.Icon = icon);
        });
    }

    private static string DescribeIndex(IndexStatus s)
    {
        if (s.Building) return string.IsNullOrEmpty(s.Message) ? "正在建立索引…" : s.Message;

        var text = $"{s.EntryCount:N0} 项";
        if (s.DeltaCount > 0) text += $" · 增量 {s.DeltaCount}";
        if (s.LastUpdateAt is { } t) text += $" · {t:HH:mm} 更新";
        return text;
    }

    private static ResultItemViewModel FromIndex(SearchIndex index, int i)
    {
        var cat = index.CategoryAt(i);
        return new ResultItemViewModel
        {
            Name = index.DisplayNameAt(i),
            Path = index.FullPathAt(i),
            Category = cat,
            CategoryLabel = EntryCategoryNames.DisplayName(cat),
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
            Category = cat,
            CategoryLabel = EntryCategoryNames.DisplayName(cat),
            StoreIndex = -1,
            IsDirectory = isDir,
        };
    }

    private static bool SafeIsDirectory(string path)
    {
        try { return Directory.Exists(path); }
        catch { return false; }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
