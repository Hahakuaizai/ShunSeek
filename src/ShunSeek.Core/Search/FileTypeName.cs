namespace ShunSeek.Core;

/// <summary>条目的重要级：界面上用一个小圆点区分，别把所有东西一视同仁。</summary>
public enum TypeImportance
{
    /// <summary>人真正会去点开的东西：应用、快捷方式、文档、文件夹。</summary>
    High = 0,
    /// <summary>普通文件：图片、视频、音频、压缩包、代码。</summary>
    Normal = 1,
    /// <summary>多半是中间产物/日志/缓存：临时文件、日志、备份、索引库。</summary>
    Low = 2,
}

/// <summary>
/// 把"这是什么"说清楚：不要笼统地写"代码""图片"，而是 .py → Python 源码、
/// .docx → Word 文档、.zip → ZIP 压缩包。扩展名认不出来时退回分类名。
/// 顺便给一个重要级，界面上用来区分"该不该先看它"。
/// </summary>
public static class FileTypeName
{
    private static readonly Dictionary<string, (string Label, TypeImportance Importance)> Known =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // 文档
            [".doc"] = ("Word 文档", TypeImportance.High),
            [".docx"] = ("Word 文档", TypeImportance.High),
            [".pdf"] = ("PDF 文档", TypeImportance.High),
            [".xls"] = ("Excel 表格", TypeImportance.High),
            [".xlsx"] = ("Excel 表格", TypeImportance.High),
            [".csv"] = ("CSV 表格", TypeImportance.High),
            [".ppt"] = ("PPT 演示", TypeImportance.High),
            [".pptx"] = ("PPT 演示", TypeImportance.High),
            [".md"] = ("Markdown", TypeImportance.High),
            [".markdown"] = ("Markdown", TypeImportance.High),
            [".txt"] = ("文本", TypeImportance.High),
            [".rtf"] = ("富文本", TypeImportance.Normal),
            [".epub"] = ("电子书", TypeImportance.Normal),
            [".wps"] = ("WPS 文档", TypeImportance.High),
            [".et"] = ("WPS 表格", TypeImportance.High),
            [".dps"] = ("WPS 演示", TypeImportance.High),

            // 图片
            [".jpg"] = ("JPEG 图片", TypeImportance.Normal),
            [".jpeg"] = ("JPEG 图片", TypeImportance.Normal),
            [".png"] = ("PNG 图片", TypeImportance.Normal),
            [".gif"] = ("GIF 动图", TypeImportance.Normal),
            [".webp"] = ("WebP 图片", TypeImportance.Normal),
            [".bmp"] = ("BMP 图片", TypeImportance.Normal),
            [".svg"] = ("SVG 矢量图", TypeImportance.Normal),
            [".ico"] = ("图标", TypeImportance.Normal),
            [".psd"] = ("PSD 源文件", TypeImportance.Normal),
            [".ai"] = ("Illustrator 源文件", TypeImportance.Normal),
            [".heic"] = ("HEIC 图片", TypeImportance.Normal),
            [".raw"] = ("相机原始图", TypeImportance.Normal),

            // 视频 / 音频
            [".mp4"] = ("MP4 视频", TypeImportance.Normal),
            [".mkv"] = ("MKV 视频", TypeImportance.Normal),
            [".avi"] = ("AVI 视频", TypeImportance.Normal),
            [".mov"] = ("MOV 视频", TypeImportance.Normal),
            [".flv"] = ("FLV 视频", TypeImportance.Normal),
            [".mp3"] = ("MP3 音频", TypeImportance.Normal),
            [".flac"] = ("FLAC 无损", TypeImportance.Normal),
            [".wav"] = ("WAV 音频", TypeImportance.Normal),
            [".m4a"] = ("M4A 音频", TypeImportance.Normal),

            // 压缩包 / 磁盘镜像
            [".zip"] = ("ZIP 压缩包", TypeImportance.Normal),
            [".rar"] = ("RAR 压缩包", TypeImportance.Normal),
            [".7z"] = ("7Z 压缩包", TypeImportance.Normal),
            [".tar"] = ("TAR 归档", TypeImportance.Normal),
            [".iso"] = ("光盘镜像", TypeImportance.Normal),

            // 代码 / 配置
            [".cs"] = ("C# 源码", TypeImportance.Normal),
            [".py"] = ("Python 源码", TypeImportance.Normal),
            [".js"] = ("JavaScript", TypeImportance.Normal),
            [".ts"] = ("TypeScript", TypeImportance.Normal),
            [".java"] = ("Java 源码", TypeImportance.Normal),
            [".cpp"] = ("C++ 源码", TypeImportance.Normal),
            [".c"] = ("C 源码", TypeImportance.Normal),
            [".h"] = ("C 头文件", TypeImportance.Normal),
            [".go"] = ("Go 源码", TypeImportance.Normal),
            [".rs"] = ("Rust 源码", TypeImportance.Normal),
            [".html"] = ("网页", TypeImportance.Normal),
            [".css"] = ("样式表", TypeImportance.Normal),
            [".json"] = ("JSON 配置", TypeImportance.Normal),
            [".xml"] = ("XML 配置", TypeImportance.Normal),
            [".yml"] = ("YAML 配置", TypeImportance.Normal),
            [".yaml"] = ("YAML 配置", TypeImportance.Normal),
            [".ini"] = ("配置", TypeImportance.Normal),
            [".sln"] = ("VS 解决方案", TypeImportance.Normal),
            [".csproj"] = ("C# 工程", TypeImportance.Normal),

            // 可执行 / 脚本
            [".exe"] = ("可执行文件", TypeImportance.High),
            [".msi"] = ("安装包", TypeImportance.High),
            [".bat"] = ("批处理脚本", TypeImportance.Normal),
            [".ps1"] = ("PowerShell 脚本", TypeImportance.Normal),
            [".sh"] = ("Shell 脚本", TypeImportance.Normal),

            // 低价值：中间产物、日志、缓存
            [".log"] = ("日志", TypeImportance.Low),
            [".tmp"] = ("临时文件", TypeImportance.Low),
            [".bak"] = ("备份文件", TypeImportance.Low),
            [".cache"] = ("缓存文件", TypeImportance.Low),
            [".dmp"] = ("转储文件", TypeImportance.Low),
            [".pdb"] = ("调试符号", TypeImportance.Low),
            [".obj"] = ("编译中间产物", TypeImportance.Low),
        };

    /// <summary>识别条目"是什么"，并给出重要级。</summary>
    public static (string Label, TypeImportance Importance) Describe(string path, EntryCategory category, bool isDirectory)
    {
        if (isDirectory || category is EntryCategory.Folder or EntryCategory.InstallDir)
            return category == EntryCategory.InstallDir ? ("安装目录", TypeImportance.High) : ("文件夹", TypeImportance.High);

        if (category is EntryCategory.App) return ("应用", TypeImportance.High);
        if (category is EntryCategory.Shortcut) return ("快捷方式", TypeImportance.High);

        var ext = System.IO.Path.GetExtension(path);
        if (!string.IsNullOrEmpty(ext) && Known.TryGetValue(ext, out var known)) return known;

        // 没有扩展名、但名字本身就在喊"我是垃圾"：LOG / tmp / cache / debug …
        var fileName = System.IO.Path.GetFileName(path);
        if (NoiseNames.Contains(fileName)) return ("日志/缓存", TypeImportance.Low);

        // 放在缓存/日志目录里的，一并不重要（父目录已经说明它是什么了）
        if (IsUnderNoiseDirectory(path)) return ("缓存/日志文件", TypeImportance.Low);

        // 认不出来的扩展名：说清楚"是什么类别的什么文件"，别只给分类名
        var fallback = string.IsNullOrEmpty(ext)
            ? "无扩展名文件"
            : ext.TrimStart('.').ToUpperInvariant() + " 文件";

        var importance = category switch
        {
            EntryCategory.Document => TypeImportance.High,
            EntryCategory.Image or EntryCategory.Video or EntryCategory.Audio
                or EntryCategory.Archive or EntryCategory.Code or EntryCategory.Executable => TypeImportance.Normal,
            _ => TypeImportance.Normal,
        };
        return (fallback, importance);
    }

    /// <summary>这些文件名（不带扩展名）基本就是日志/缓存/临时产物。</summary>
    private static readonly HashSet<string> NoiseNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "log", "logs", "tmp", "temp", "cache", "caches", "debug", "debug.log",
        "trace", "output", "console", "stdout", "stderr", "crash", "dump",
        "thumbs.db", "desktop.ini", ".ds_store", "lock", "index", "leveldb",
    };

    /// <summary>路径里出现过这些目录名（作为一段），就认为它在缓存/日志区。</summary>
    private static bool IsUnderNoiseDirectory(string path)
    {
        ReadOnlySpan<char> rest = path;
        while (true)
        {
            int slash = rest.LastIndexOfAny('\\', '/');
            if (slash <= 0) return false;
            rest = rest[..slash];
            int start = rest.LastIndexOfAny('\\', '/');
            var segment = start < 0 ? rest : rest[(start + 1)..];

            if (NoiseNames.Contains(segment.ToString())) return true;
            // 目录名里带 cache/log 字样的也一并算（WebView2\EBWebView\Default\LOG 这种）
            if (segment.Contains("cache", StringComparison.OrdinalIgnoreCase) ||
                segment.Contains("log", StringComparison.OrdinalIgnoreCase))
                return true;

            if (start < 0) return false;
        }
    }
}
