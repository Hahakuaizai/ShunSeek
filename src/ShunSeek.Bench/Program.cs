using System.Diagnostics;
using ShunSeek.Core;

// 瞬觅速度基准：合成 200 万条混合条目 → 建平坦索引 → 跑一组代表性查询。
// 目的不是跑分好看，是验证架构假设：MFT 量级的数据（百万级）在内存平坦索引 +
// 并行 Span 扫描下，单次查询能压进几十毫秒。换机器请 Release 配置重跑。
const int EntryCount = 2_000_000;

Console.WriteLine("=== ShunSeek Bench ===");
Console.WriteLine($"条目数: {EntryCount:N0}   线程数: {Environment.ProcessorCount}   OS: {Environment.OSVersion}");

var genSw = Stopwatch.StartNew();
var raw = Generate(EntryCount);
genSw.Stop();
Console.WriteLine($"合成数据: {genSw.ElapsedMilliseconds} ms");

var buildSw = Stopwatch.StartNew();
var store = IndexBuilder.Build(raw);
buildSw.Stop();
Console.WriteLine($"建索引:   {buildSw.ElapsedMilliseconds} ms   条目: {store.Count:N0}");
Console.WriteLine($"索引内存粗估: {store.EstimateMemoryBytes() / 1024.0 / 1024.0:F1} MB");

var engine = new SearchEngine(store);
RunQuery(engine, "报告", null);          // 中文名子串
RunQuery(engine, "合同", null);          // 中文名子串
RunQuery(engine, "report", null);        // 英文子串
RunQuery(engine, "wzry", null);          // 拼音首字母 → 王者荣耀
RunQuery(engine, "mp4", EntryCategory.Video); // 扩展名 + 分类过滤
RunQuery(engine, "2024", null);          // 数字子串（命中面大，压测排序截断）

static void RunQuery(SearchEngine engine, string query, EntryCategory? category)
{
    // 跑 5 次取平均（第一次含 JIT/缓存冷启动，单列）
    var times = new List<double>();
    IReadOnlyList<SearchHit> hits = Array.Empty<SearchHit>();
    for (int i = 0; i < 5; i++)
    {
        var sw = Stopwatch.StartNew();
        hits = engine.Search(query, category, 100);
        sw.Stop();
        times.Add(sw.Elapsed.TotalMilliseconds);
    }
    var label = category.HasValue ? $"{query} [{EntryCategoryNames.DisplayName(category.Value)}]" : query;
    Console.WriteLine($"查询 \"{label}\": 首次 {times[0]:F1} ms, 平均 {times.Skip(1).Average():F1} ms, 返回 {hits.Count} 条");
    foreach (var h in hits.Take(3))
        Console.WriteLine($"    · {engine.Store.NameOf(h.Index)}  [{EntryCategoryNames.DisplayName(engine.Store.CategoryOf(h.Index))}]  {engine.Store.FullPath[h.Index]}");
}

static List<RawEntry> Generate(int count)
{
    var rng = new Random(42);
    string[] zhStems =
    {
        "报告", "合同", "照片", "会议纪要", "项目计划", "毕业论文", "财务报表", "产品需求",
        "测试数据", "学习笔记", "设计稿", "简历", "课件", "备份", "年度总结", "报价单",
    };
    string[] enStems =
    {
        "report", "invoice", "photo", "project-plan", "meeting-notes", "budget",
        "design", "backup", "resume", "dataset", "release-notes", "homework",
    };
    string[] softwares = { "微信", "QQ", "网易云音乐", "王者荣耀", "Steam", "WPS Office", "Photoshop", "Visual Studio Code", "Chrome", "钉钉" };
    string[] docExts = { ".docx", ".pdf", ".xlsx", ".pptx", ".txt", ".md" };
    string[] imgExts = { ".jpg", ".png", ".gif", ".webp" };
    string[] vidExts = { ".mp4", ".mkv", ".mov" };
    string[] audExts = { ".mp3", ".flac", ".wav" };
    string[] arcExts = { ".zip", ".rar", ".7z" };
    string[] codeExts = { ".cs", ".py", ".js", ".json" };
    string[] dirNames = { "工作", "学习", "娱乐", "项目", "资料", "Downloads", "Documents", "Pictures", "Projects", "Archive" };
    string[] drives = { "C:", "D:", "E:" };

    string RandomDirPath()
    {
        int depth = rng.Next(2, 6);
        var parts = new string[depth];
        parts[0] = drives[rng.Next(drives.Length)];
        for (int i = 1; i < depth; i++) parts[i] = dirNames[rng.Next(dirNames.Length)] + rng.Next(1, 20);
        return string.Join("\\", parts);
    }

    var list = new List<RawEntry>(count);
    for (int i = 0; i < count; i++)
    {
        int roll = rng.Next(100);
        string dir = RandomDirPath();
        string name;
        EntryCategory cat;
        bool isDir = false;
        if (roll < 15) // 文件夹 15%
        {
            name = dirNames[rng.Next(dirNames.Length)] + "_" + rng.Next(10000);
            cat = EntryCategory.Folder; isDir = true;
        }
        else if (roll < 20) // 快捷方式 5%
        {
            name = softwares[rng.Next(softwares.Length)] + ".lnk";
            cat = EntryCategory.Shortcut;
        }
        else if (roll < 22) // 软件（注册表来源形态）2%
        {
            name = softwares[rng.Next(softwares.Length)];
            cat = EntryCategory.App;
        }
        else if (roll < 45) // 文档 23%
        {
            name = zhStems[rng.Next(zhStems.Length)] + rng.Next(2018, 2027) + docExts[rng.Next(docExts.Length)];
            cat = EntryCategory.Document;
        }
        else if (roll < 60) // 图片 15%
        {
            name = "IMG_" + rng.Next(100000, 999999) + imgExts[rng.Next(imgExts.Length)];
            cat = EntryCategory.Image;
        }
        else if (roll < 68) // 视频 8%
        {
            name = zhStems[rng.Next(zhStems.Length)] + "_录像" + rng.Next(1000) + vidExts[rng.Next(vidExts.Length)];
            cat = EntryCategory.Video;
        }
        else if (roll < 73) // 音频 5%
        {
            name = "歌曲" + rng.Next(10000) + audExts[rng.Next(audExts.Length)];
            cat = EntryCategory.Audio;
        }
        else if (roll < 78) // 压缩包 5%
        {
            name = enStems[rng.Next(enStems.Length)] + "-backup-" + rng.Next(100) + arcExts[rng.Next(arcExts.Length)];
            cat = EntryCategory.Archive;
        }
        else if (roll < 88) // 代码 10%
        {
            name = enStems[rng.Next(enStems.Length)] + "_" + rng.Next(500) + codeExts[rng.Next(codeExts.Length)];
            cat = EntryCategory.Code;
        }
        else // 英文杂项 12%
        {
            name = enStems[rng.Next(enStems.Length)] + "-" + rng.Next(100000) + ".dat";
            cat = EntryCategory.Other;
        }
        list.Add(new RawEntry(name, dir + "\\" + name, cat, IsDirectory: isDir));
    }
    // 埋几个定点命中，保证拼音查询有确定结果
    list.Add(new RawEntry("王者荣耀.lnk", @"C:\Users\Public\Desktop\王者荣耀.lnk", EntryCategory.Shortcut));
    list.Add(new RawEntry("王者荣耀", @"D:\Games\王者荣耀", EntryCategory.App));
    return list;
}
