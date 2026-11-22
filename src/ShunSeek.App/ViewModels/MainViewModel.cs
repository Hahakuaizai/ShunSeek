using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using ShunSeek.App.Services;
using ShunSeek.Core;

namespace ShunSeek.App;

public sealed class ResultItemViewModel : INotifyPropertyChanged
{
    private ImageSource? _icon;

    private string _name = string.Empty;

    /// <summary>显示名。构造时给文件名，随后可能被 Shell 的本地化名字替换（Desktop → 桌面）。</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            OnPropertyChanged();
        }
    }
    public required string Path { get; init; }
    public required string CategoryLabel { get; init; }

    /// <summary>这一行"具体是什么"：Word 文档 / Python 源码 / 文件夹……而不是笼统的"文档""代码"。</summary>
    public required string TypeLabel { get; init; }

    /// <summary>重要级的标识色（应用/文档为蓝，普通文件灰，日志/缓存更浅）。</summary>
    public required System.Windows.Media.Brush ImportanceBrush { get; init; }

    public required TypeImportance Importance { get; init; }

    /// <summary>这一行是"其他 N 项"的折叠条，不是一个真实条目。</summary>
    public bool IsGroup { get; init; }

    public Visibility NormalVisibility => IsGroup ? Visibility.Collapsed : Visibility.Visible;
    public Visibility GroupVisibility => IsGroup ? Visibility.Visible : Visibility.Collapsed;

    public required EntryCategory Category { get; init; }

    /// <summary>在 SearchIndex 统一下标口径里的位置；"最近使用"列表里的条目为 -1。</summary>
    public int StoreIndex { get; init; }

    public bool IsDirectory { get; init; }

    /// <summary>是否"固定"在首页（右上角会挂一个小别针）。</summary>
    public bool IsPinned { get; init; }

    public Visibility PinVisibility => IsPinned ? Visibility.Visible : Visibility.Collapsed;

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
    private bool _indexReady;
    private bool _expanded;   // 结果里的"其他 N 项"是否已展开
    private Visibility _homeVisibility = Visibility.Visible;
    private Visibility _resultsVisibility = Visibility.Collapsed;
    private string _homeCaption = string.Empty;
    private Visibility _homeCaptionVisibility = Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ResultItemViewModel> Results { get; } = new();

    public IndexService? Service { get; private set; }

    public PinnedStore? Pinned { get; private set; }

    public string Query
    {
        get => _query;
        set
        {
            if (_query == value) return;
            _query = value;
            _expanded = false; // 换关键词就重新折叠
            OnPropertyChanged();
            ScheduleSearch();
        }
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

    /// <summary>首页标题行（"已固定 3 · 最近 5"），只在空查询且有内容时出现。</summary>
    public string HomeCaption
    {
        get => _homeCaption;
        private set { _homeCaption = value; OnPropertyChanged(); }
    }

    public Visibility HomeCaptionVisibility
    {
        get => _homeCaptionVisibility;
        private set { _homeCaptionVisibility = value; OnPropertyChanged(); }
    }

    /// <summary>空查询时显示图标网格（固定 + 最近），有查询时显示详细列表。</summary>
    public Visibility HomeVisibility
    {
        get => _homeVisibility;
        private set { _homeVisibility = value; OnPropertyChanged(); }
    }

    public Visibility ResultsVisibility
    {
        get => _resultsVisibility;
        private set { _resultsVisibility = value; OnPropertyChanged(); }
    }

    /// <summary>状态栏中间：本次查询命中数与耗时。</summary>
    public string ResultText
    {
        get => _resultText;
        private set { _resultText = value; OnPropertyChanged(); }
    }

    /// <summary>索引服务就绪后由 App 调一次。</summary>
    public void Attach(IndexService service, PinnedStore pinned)
    {
        Service = service;
        Pinned = pinned;
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

        // 索引刚建好：如果用户已经输了关键词，立刻重跑一次，不用他再敲一遍
        bool ready = status.Ready && !status.Building;
        if (ready && !_indexReady)
        {
            _indexReady = true;
            if (!string.IsNullOrWhiteSpace(_query)) ScheduleSearch();
        }
        _indexReady = ready;

        // 建索引期间，空状态直接显示进度，别显示"没有匹配的内容"误导人
        if (!ready && Results.Count == 0)
        {
            EmptyHintText = string.IsNullOrEmpty(status.Message) ? "正在建立索引…" : status.Message;
            EmptyHintVisibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// 按类型折叠：High（应用/文档/文件夹）原样保留；其余同 TypeLabel 的条目，
    /// 一组超过 <see cref="FoldThreshold"/> 条就压成一行"N 个 XXX"，放在该组首次出现的位置。
    /// 折叠行本身不可打开，点一下展开。
    /// </summary>
    private List<ResultItemViewModel> FoldByType(List<ResultItemViewModel> items)
    {
        const int FoldThreshold = 4;   // 同类超过这么多条才折叠
        const int AlwaysVisible = 5;   // 前几条无论如何都露出来，不然全是低价值时列表会空

        // 先数一遍每组有多少条，才知道哪些该折
        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item.Importance == TypeImportance.High) continue;
            totals.TryGetValue(item.TypeLabel, out int count);
            totals[item.TypeLabel] = count + 1;
        }

        // 前 AlwaysVisible 条（不分类型）保留可见，剩下的才参与折叠
        var alwaysShow = new HashSet<ResultItemViewModel>(ReferenceEqualityComparer.Instance);
        foreach (var item in items.Where(x => x.Importance != TypeImportance.High).Take(AlwaysVisible))
            alwaysShow.Add(item);

        var folded = new List<ResultItemViewModel>(items.Count);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        int lowTotal = 0;

        foreach (var item in items)
        {
            if (item.Importance == TypeImportance.High)
            {
                folded.Add(item);
                continue;
            }

            if (alwaysShow.Contains(item))
            {
                folded.Add(item);
                continue;
            }

            // 日志/缓存/临时文件不分类别，全部并成一条，免得出现五行"N 个 XXX"
            if (item.Importance == TypeImportance.Low)
            {
                lowTotal++;
                continue;
            }

            if (totals[item.TypeLabel] < FoldThreshold)
            {
                folded.Add(item);
                continue;
            }

            if (emitted.Add(item.TypeLabel))
            {
                int count = totals[item.TypeLabel] - alwaysShow.Count(x => x.TypeLabel == item.TypeLabel);
                if (count <= 0) continue;
                folded.Add(new ResultItemViewModel
                {
                    Name = $"{count} 个{item.TypeLabel} · 点击展开",
                    Path = string.Empty,
                    Category = EntryCategory.Other,
                    CategoryLabel = "折叠",
                    TypeLabel = "折叠",
                    Importance = TypeImportance.Low,
                    ImportanceBrush = ImportanceBrushOf(TypeImportance.Low),
                    IsGroup = true,
                    StoreIndex = -1,
                });
            }
        }

        int lowShown = alwaysShow.Count(x => x.Importance == TypeImportance.Low);
        lowTotal -= lowShown;

        if (lowTotal >= 2)
        {
            folded.Add(new ResultItemViewModel
            {
                Name = $"其他 {lowTotal} 项（日志/缓存/临时文件）· 点击展开",
                Path = string.Empty,
                Category = EntryCategory.Other,
                CategoryLabel = "折叠",
                TypeLabel = "折叠",
                Importance = TypeImportance.Low,
                ImportanceBrush = ImportanceBrushOf(TypeImportance.Low),
                IsGroup = true,
                StoreIndex = -1,
            });
        }
        else if (lowTotal == 1)
        {
            // 只剩一条低价值条目：没必要折叠，直接放出来
            folded.AddRange(items.Where(x => x.Importance == TypeImportance.Low && !alwaysShow.Contains(x)));
        }

        return folded;
    }

    /// <summary>展开被折叠的"其他 N 项"。</summary>
    public void ExpandResults()
    {
        if (_expanded) return;
        _expanded = true;
        ScheduleSearch();
    }

    /// <summary>把任意路径（文件夹/文件）固定到首页——空白处右键菜单用。</summary>
    public void PinPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        Pinned?.Add(path);
        ScheduleSearch();
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

    /// <summary>固定到首页（右键菜单"添加到首页"）。</summary>
    public void Pin(ResultItemViewModel item)
    {
        if (!item.HasPath) return;
        Pinned?.Add(item.Path);
        ScheduleSearch();
    }

    public void Unpin(ResultItemViewModel item)
    {
        if (!item.HasPath) return;
        Pinned?.Remove(item.Path);
        // 同时清掉使用记录：否则它会落进"最近使用"，用户看到的是"挪了一下"而不是"移除"
        _usage?.Forget(item.Path);
        _usage?.Save();
        ScheduleSearch();
    }

    public bool IsPinned(ResultItemViewModel item) => item.HasPath && Pinned?.Contains(item.Path) == true;

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
        string homeCaption = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                var home = await Task.Run(() => BuildHomeItems(_usage, Pinned, token), token);
                items = home.Items;
                resultText = string.Empty;
                homeCaption = home.PinnedCount > 0
                    ? $"已固定 {home.PinnedCount} · 最近使用 {home.RecentCount}"
                    : home.RecentCount > 0 ? $"最近使用 {home.RecentCount}" : string.Empty;
            }
            else
            {
                var hits = await Task.Run(
                    () => engine.Search(query, category, _expanded ? 300 : 100, token), token);
                items = hits.Select(h => FromIndex(snapshot, h.Index)).ToList();
                resultText = $"{hits.Count} 项 · {sw.Elapsed.TotalMilliseconds:F0} ms";

                // 同类型、又不重要的条目叠成一行，点开再看。
                // 应用/文档/文件夹（High）永远不折叠——那是用户真正要找的东西；
                // 其余按"类型"分组，一组里堆了 4 条以上就收起（搜 log 会有一百个 LOG，
                // 搜某个词也会有一堆同名同类的 dll/png，都是一个道理）。
                if (!_expanded) items = FoldByType(items);
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
            bool searching = !string.IsNullOrWhiteSpace(query);
            HomeVisibility = searching ? Visibility.Collapsed : Visibility.Visible;
            ResultsVisibility = searching ? Visibility.Visible : Visibility.Collapsed;

            EmptyHintText = searching
                ? (_engine?.Index.Count == 0 ? "索引还在建，等它跑完再搜" : "没有匹配的内容")
                : "输入关键词开始搜索";
            EmptyHintVisibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HomeCaption = homeCaption;
            HomeCaptionVisibility = homeCaption.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            // 图标另起炉灶：先占位，再逐个替换
            foreach (var item in items) LoadIcon(item);
        });
    }

    /// <summary>
    /// 首页 = 已固定的（用户自己挑出来的文件夹/图片/软件）+ 最近打开过的，最多 12 格。
    /// 固定项排前面，并且打上别针；最近使用里跟固定项重复的不再重复出现。
    /// </summary>
    private (List<ResultItemViewModel> Items, int PinnedCount, int RecentCount) BuildHomeItems(
        UsageStore? usage, PinnedStore? pinned, CancellationToken token)
    {
        // 固定的不设上限：用户自己往首页放多少就是多少。
        // 最近使用仍然只取前 24 条——那是"猜你想用"，不是收藏夹。
        const int recentLimit = 24;
        var items = new List<ResultItemViewModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int pinnedCount = 0;

        foreach (var path in pinned?.Paths ?? Array.Empty<string>())
        {
            token.ThrowIfCancellationRequested();
            if (!seen.Add(path)) continue;
            items.Add(FromPath(path, isPinned: true));
            pinnedCount++;
        }

        int recentCount = 0;
        foreach (var path in usage?.TopPaths(recentLimit) ?? Array.Empty<string>())
        {
            token.ThrowIfCancellationRequested();
            if (recentCount >= recentLimit) break;
            if (!seen.Add(path)) continue;
            items.Add(FromPath(path));
            recentCount++;
        }

        return (items, pinnedCount, recentCount);
    }

    private void LoadIcon(ResultItemViewModel item)
    {
        if (!item.HasPath) return;

        var cached = _icons.TryGet(item.Path, item.Category);
        if (cached != null) item.Icon = cached;

        // 显示名同理：Shell 可能要读文件属性，一律放到后台
        _ = Task.Run(async () =>
        {
            var icon = cached ?? await _icons.GetAsync(item.Path, item.Category).ConfigureAwait(false);
            var name = ShellNames.Resolve(item.Path, item.IsDirectory);

            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (icon != null) item.Icon = icon;
                if (name != null) item.Name = name;
            });
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
        var path = index.FullPathAt(i);
        var isDirectory = cat is EntryCategory.Folder or EntryCategory.InstallDir;
        var (typeLabel, importance) = FileTypeName.Describe(path, cat, isDirectory);
        return new ResultItemViewModel
        {
            Name = index.DisplayNameAt(i),
            Path = path,
            Category = cat,
            CategoryLabel = EntryCategoryNames.DisplayName(cat),
            TypeLabel = typeLabel,
            Importance = importance,
            ImportanceBrush = ImportanceBrushOf(importance),
            StoreIndex = i,
            IsDirectory = isDirectory,
        };
    }

    private static ResultItemViewModel FromPath(string path, bool isPinned = false)
    {
        var isDir = SafeIsDirectory(path);
        var cat = isDir ? EntryCategory.Folder : CategoryClassifier.ClassifyByExtension(path);
        var name = Path.GetFileName(path.TrimEnd('\\'));
        if (string.IsNullOrEmpty(name)) name = path;
        var (typeLabel, importance) = FileTypeName.Describe(path, cat, isDir);
        return new ResultItemViewModel
        {
            Name = name,
            Path = path,
            Category = cat,
            CategoryLabel = EntryCategoryNames.DisplayName(cat),
            TypeLabel = typeLabel,
            Importance = importance,
            ImportanceBrush = ImportanceBrushOf(importance),
            StoreIndex = -1,
            IsDirectory = isDir,
            IsPinned = isPinned,
        };
    }

    /// <summary>重要级标识色：高=木色（和纸质主题同一套），普通=暖灰，低=浅米。</summary>
    private static System.Windows.Media.Brush ImportanceBrushOf(TypeImportance importance)
    {
        var hex = importance switch
        {
            TypeImportance.High => "#A9713F",
            TypeImportance.Normal => "#9C8B77",
            _ => "#CBBBA4",
        };
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static bool SafeIsDirectory(string path)
    {
        try { return Directory.Exists(path); }
        catch { return false; }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
