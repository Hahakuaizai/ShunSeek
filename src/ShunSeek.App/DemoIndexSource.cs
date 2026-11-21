using ShunSeek.Core;

namespace ShunSeek.App;

/// <summary>
/// 演示数据源：几十条仿真条目，让 UI 在真实索引（MFT 等）接上之前就能完整跑通
/// 搜索/筛选/打开的逻辑。真实装配时在 App.OnStartup 里换成
/// MftIndexSource + AppScanner + DirectoryWalker 的组合即可，UI 不用改。
/// </summary>
public sealed class DemoIndexSource : IIndexSource
{
    public string Name => "演示数据";

    public IEnumerable<RawEntry> Load(IProgress<int>? progress, CancellationToken ct)
    {
        var items = new (string Name, string Path, EntryCategory Cat)[]
        {
            ("微信", @"C:\Program Files\Tencent\WeChat\WeChat.exe", EntryCategory.App),
            ("微信", @"C:\Program Files\Tencent\WeChat", EntryCategory.InstallDir),
            ("微信.lnk", @"C:\Users\demo\Desktop\微信.lnk", EntryCategory.Shortcut),
            ("网易云音乐", @"D:\Apps\NetEase\CloudMusic\cloudmusic.exe", EntryCategory.App),
            ("网易云音乐.lnk", @"C:\Users\demo\Desktop\网易云音乐.lnk", EntryCategory.Shortcut),
            ("王者荣耀.lnk", @"C:\Users\demo\Desktop\王者荣耀.lnk", EntryCategory.Shortcut),
            ("Visual Studio Code", @"C:\Users\demo\AppData\Local\Programs\Microsoft VS Code\Code.exe", EntryCategory.App),
            ("WPS Office", @"C:\Program Files\Kingsoft\WPS Office\ksolaunch.exe", EntryCategory.App),
            ("Steam", @"D:\Games\Steam\steam.exe", EntryCategory.App),
            ("Photoshop", @"C:\Program Files\Adobe\Photoshop\Photoshop.exe", EntryCategory.App),
            ("毕业论文终稿.docx", @"D:\学习\毕业论文\毕业论文终稿.docx", EntryCategory.Document),
            ("毕业论文答辩.pptx", @"D:\学习\毕业论文\毕业论文答辩.pptx", EntryCategory.Document),
            ("2026年度报告.pdf", @"D:\工作\报告\2026年度报告.pdf", EntryCategory.Document),
            ("财务报表2026.xlsx", @"D:\工作\财务\财务报表2026.xlsx", EntryCategory.Document),
            ("会议纪要-11月.md", @"D:\工作\会议\会议纪要-11月.md", EntryCategory.Document),
            ("课程表.txt", @"C:\Users\demo\Documents\课程表.txt", EntryCategory.Document),
            ("IMG_20261101_120433.jpg", @"C:\Users\demo\Pictures\IMG_20261101_120433.jpg", EntryCategory.Image),
            ("毕业合影.png", @"C:\Users\demo\Pictures\毕业合影.png", EntryCategory.Image),
            ("桌面壁纸.webp", @"C:\Users\demo\Pictures\桌面壁纸.webp", EntryCategory.Image),
            ("屏幕截图 2026-11-20.png", @"C:\Users\demo\Pictures\Screenshots\屏幕截图 2026-11-20.png", EntryCategory.Image),
            ("旅行录像.mp4", @"D:\视频\旅行录像.mp4", EntryCategory.Video),
            ("课程录屏-第3章.mp4", @"D:\视频\课程\课程录屏-第3章.mp4", EntryCategory.Video),
            ("电影.mkv", @"D:\视频\电影.mkv", EntryCategory.Video),
            ("周杰伦-晴天.mp3", @"D:\音乐\周杰伦-晴天.mp3", EntryCategory.Audio),
            ("播客第12期.mp3", @"D:\音乐\播客\播客第12期.mp3", EntryCategory.Audio),
            ("无损合集.flac", @"D:\音乐\无损合集.flac", EntryCategory.Audio),
            ("照片备份.zip", @"E:\备份\照片备份.zip", EntryCategory.Archive),
            ("课程资料.rar", @"E:\备份\课程资料.rar", EntryCategory.Archive),
            ("main.py", @"D:\Projects\demo\main.py", EntryCategory.Code),
            ("app.js", @"D:\Projects\course-vault\src\app.js", EntryCategory.Code),
            ("config.json", @"D:\Projects\demo\config.json", EntryCategory.Code),
            ("学习", @"D:\学习", EntryCategory.Folder),
            ("工作", @"D:\工作", EntryCategory.Folder),
            ("视频", @"D:\视频", EntryCategory.Folder),
            ("Downloads", @"C:\Users\demo\Downloads", EntryCategory.Folder),
            ("setup.exe", @"C:\Users\demo\Downloads\setup.exe", EntryCategory.Executable),
        };
        foreach (var (name, path, cat) in items)
        {
            ct.ThrowIfCancellationRequested();
            yield return new RawEntry(name, path, cat, IsDirectory: cat is EntryCategory.Folder or EntryCategory.InstallDir);
        }
        progress?.Report(items.Length);
    }
}
