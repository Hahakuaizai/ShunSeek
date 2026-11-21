namespace ShunSeek.Core;

/// <summary>
/// 数据源产出的原始条目，IndexBuilder 把它加工进平坦索引。
/// 两种形态：
///  1) 普通数据源（目录遍历/快捷方式/注册表）：FullPath 有值，Name 是显示名
///     （文件名或软件名），Path 由 IndexBuilder 从 FullPath 拆出父目录。
///  2) MFT 枚举：NTFS 只给文件名 + 文件引用号 + 父目录引用号，FullPath 为 null，
///     IndexBuilder 靠 FileRef/ParentFileRef 在全表里拼出完整路径。
/// </summary>
public readonly record struct RawEntry(
    string Name,
    string? FullPath,
    EntryCategory Category,
    ulong FileRef = 0,
    ulong ParentFileRef = 0,
    bool IsDirectory = false);
