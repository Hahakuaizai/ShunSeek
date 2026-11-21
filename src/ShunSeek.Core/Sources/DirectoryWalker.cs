namespace ShunSeek.Core.Sources;

/// <summary>
/// 兜底遍历数据源：非 NTFS 卷（FAT32/exFAT/U 盘）、无管理员权限的便携模式、
/// 以及只需要索引少数目录（用户目录/指定文件夹）时用它。
/// 迭代式显式栈实现，不用递归（深目录不爆栈）；单个目录无权限/被占用就跳过，
/// 绝不让一个坏目录拖垮整个索引。跳过重解析点（符号链接/联接）防环。
/// 速度预期要说清：这是"能用"的兜底，不是主力——全盘遍历分钟级，
/// 所以默认只喂给它用户目录这类小范围（见开发流程阶段 1 的方案 B）。
/// </summary>
public sealed class DirectoryWalker : IIndexSource
{
    private readonly string[] _roots;
    private readonly string[] _excludePrefixes;
    private readonly bool _includeFiles;

    /// <param name="roots">要遍历的根目录。</param>
    /// <param name="includeFiles">false = 只收目录（AppScanner 复用时会自己转成快捷方式条目）。</param>
    /// <param name="excludeDirs">绕开的目录前缀（缓存/临时目录），按忽略大小写的前缀匹配。</param>
    public DirectoryWalker(IEnumerable<string> roots, bool includeFiles = true, IEnumerable<string>? excludeDirs = null)
    {
        _roots = roots.ToArray();
        _includeFiles = includeFiles;
        _excludePrefixes = excludeDirs?.Where(p => !string.IsNullOrEmpty(p)).ToArray() ?? Array.Empty<string>();
    }

    public string Name => "目录遍历(" + string.Join(",", _roots) + ")";

    private bool IsExcluded(string path)
    {
        foreach (var prefix in _excludePrefixes)
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public IEnumerable<RawEntry> Load(IProgress<int>? progress, CancellationToken ct)
    {
        int produced = 0;
        var stack = new Stack<string>();
        foreach (var root in _roots)
            if (Directory.Exists(root)) stack.Push(root);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(dir);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var path in entries)
            {
                FileAttributes attrs;
                try { attrs = File.GetAttributes(path); }
                catch { continue; } // 枚举间隙文件被删/改名，跳过

                bool isDir = (attrs & FileAttributes.Directory) != 0;
                if ((attrs & FileAttributes.ReparsePoint) != 0 && isDir)
                    continue; // 目录联接/符号链接不跟进，防无限环

                if (isDir)
                {
                    if (IsExcluded(path)) continue; // 缓存/临时目录：整棵子树跳过
                    stack.Push(path);
                    yield return new RawEntry(Path.GetFileName(path), path, EntryCategory.Folder, IsDirectory: true);
                }
                else if (_includeFiles)
                {
                    yield return new RawEntry(
                        Path.GetFileName(path), path,
                        CategoryClassifier.ClassifyByExtension(path));
                }
                if ((++produced & 0xFFFF) == 0) progress?.Report(produced);
            }
        }
        progress?.Report(produced);
    }
}
