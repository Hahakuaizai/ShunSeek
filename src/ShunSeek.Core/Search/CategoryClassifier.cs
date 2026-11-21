namespace ShunSeek.Core;

/// <summary>
/// 按扩展名把文件分到类别。查表用 HashSet（扩展名带点、小写），
/// 索引构建时每条目只查一次，搜索时不再调用，所以不需要极端优化。
/// 文件夹与 .lnk / .exe 的判定不在这里：调用方根据 IsDirectory 与来源决定
/// （同一个 .exe，从开始菜单扫到的是 App，散落在下载目录里的只是 Executable）。
/// </summary>
public static class CategoryClassifier
{
    private static readonly HashSet<string> Image = new(StringComparer.Ordinal)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".svg", ".ico",
        ".tif", ".tiff", ".heic", ".heif", ".avif", ".raw", ".psd", ".ai",
    };

    private static readonly HashSet<string> Video = new(StringComparer.Ordinal)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v",
        ".mpg", ".mpeg", ".3gp", ".ts", ".rm", ".rmvb", ".vob", ".swf",
    };

    private static readonly HashSet<string> Audio = new(StringComparer.Ordinal)
    {
        ".mp3", ".flac", ".wav", ".ogg", ".m4a", ".aac", ".wma", ".ape",
        ".opus", ".aiff", ".mid", ".midi",
    };

    private static readonly HashSet<string> Document = new(StringComparer.Ordinal)
    {
        ".doc", ".docx", ".pdf", ".txt", ".md", ".markdown", ".rtf", ".odt",
        ".xls", ".xlsx", ".csv", ".tsv", ".ods",
        ".ppt", ".pptx", ".odp", ".epub", ".mobi", ".chm",
        ".wps", ".et", ".dps", // WPS 系列
    };

    private static readonly HashSet<string> Archive = new(StringComparer.Ordinal)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz",
        ".iso", ".img", ".cab", ".jar", ".war",
    };

    private static readonly HashSet<string> Code = new(StringComparer.Ordinal)
    {
        ".cs", ".py", ".js", ".ts", ".jsx", ".tsx", ".java", ".cpp", ".cc",
        ".c", ".h", ".hpp", ".go", ".rs", ".php", ".rb", ".swift", ".kt",
        ".json", ".xml", ".yml", ".yaml", ".toml", ".ini", ".cfg",
        ".html", ".htm", ".css", ".scss", ".vue", ".sql", ".sh", ".bat", ".ps1",
        ".sln", ".csproj", ".vcxproj",
    };

    /// <summary>根据文件名/路径的扩展名分类；无法识别返回 Other。name 可以是纯文件名也可以是全路径。</summary>
    public static EntryCategory ClassifyByExtension(string name)
    {
        var ext = Path.GetExtension(name);
        if (string.IsNullOrEmpty(ext)) return EntryCategory.Other;
        ext = ext.ToLowerInvariant();
        if (Image.Contains(ext)) return EntryCategory.Image;
        if (Video.Contains(ext)) return EntryCategory.Video;
        if (Audio.Contains(ext)) return EntryCategory.Audio;
        if (Document.Contains(ext)) return EntryCategory.Document;
        if (Archive.Contains(ext)) return EntryCategory.Archive;
        if (Code.Contains(ext)) return EntryCategory.Code;
        if (ext == ".lnk") return EntryCategory.Shortcut;
        if (ext == ".exe") return EntryCategory.Executable;
        return EntryCategory.Other;
    }
}
