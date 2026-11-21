namespace ShunSeek.Core;

/// <summary>
/// 索引条目的分类。用 byte 存储以便放进平坦索引的并行数组里（每条目 1 字节）。
/// 顺序即 UI 分类筛选条的大致顺序，不要随意重排数值（会写进将来的索引缓存文件）。
/// </summary>
public enum EntryCategory : byte
{
    Other = 0,
    App = 1,         // 已安装软件（开始菜单/注册表 Uninstall 枚举到）
    Shortcut = 2,    // 快捷方式 .lnk
    InstallDir = 3,  // 软件安装目录（注册表 InstallLocation）
    Folder = 4,      // 文件夹
    Document = 5,    // 文档
    Image = 6,       // 图片
    Video = 7,       // 视频
    Audio = 8,       // 音频
    Archive = 9,     // 压缩包
    Code = 10,       // 代码/文本工程文件
    Executable = 11, // 普通 .exe（非软件枚举来源的散落可执行文件）
}

public static class EntryCategoryNames
{
    /// <summary>分类的中文显示名，UI 标签与筛选条共用。</summary>
    public static string DisplayName(EntryCategory c) => c switch
    {
        EntryCategory.App => "应用",
        EntryCategory.Shortcut => "快捷方式",
        EntryCategory.InstallDir => "安装目录",
        EntryCategory.Folder => "文件夹",
        EntryCategory.Document => "文档",
        EntryCategory.Image => "图片",
        EntryCategory.Video => "视频",
        EntryCategory.Audio => "音频",
        EntryCategory.Archive => "压缩包",
        EntryCategory.Code => "代码",
        EntryCategory.Executable => "程序",
        _ => "其他",
    };
}
