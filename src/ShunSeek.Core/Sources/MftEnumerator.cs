using System.Runtime.InteropServices;
using System.Text;

namespace ShunSeek.Core.Sources;

/// <summary>MFT 枚举出的一条原始记录：只有名字和引用号，路径靠父链拼（见 MftIndexSource）。</summary>
public readonly record struct MftRecord(
string Name,
ulong FileRef,
ulong ParentFileRef,
bool IsDirectory,
uint FileAttributes);

/// <summary>
/// 用 FSCTL_ENUM_USN_DATA 直读 NTFS 卷的 MFT 枚举全部文件/目录。
///
///
/// 普通遍历（FindFirstFile 递归）要逐目录发几十万次系统调用，还要等磁盘寻道；
/// 而 FSCTL_ENUM_USN_DATA 让文件系统把 USN 日志里现成的记录成批吐出来——
/// 一次 DeviceIoControl 返回 1MB 的记录数组，几百万文件也就几十次调用。
/// Everything、WinShun、WizTree 的"几秒扫完全盘"都是这个原理，不是它们遍历得快，
/// 是根本没遍历。这也是瞬觅不需要"先加载数据库半天"的原因：每次冷启动重读 MFT
/// 本来就只要几秒，索引直接放内存，连落盘缓存都可以是可选优化。
///
/// 权限：打开卷并读 USN 需要管理员权限（或卷访问权），无权限时 OpenVolume 抛
/// UnauthorizedAccessException，调用方按 docs 里的两档方案降级到 DirectoryWalker。
/// </summary>
public static class MftEnumerator
{
private const int BufferSize = 1024 * 1024; // 1MB 一批，与 Everything 的量级一致

public static IEnumerable<MftRecord> EnumerateVolume(char driveLetter, CancellationToken ct = default)
{
if (!OperatingSystem.IsWindows())
throw new PlatformNotSupportedException("MFT 枚举只支持 Windows/NTFS。");

using var handle = VolumeHandle.OpenVolume(driveLetter);
var buffer = new byte[BufferSize];
var inBuffer = new byte[Marshal.SizeOf<NativeMethods.MftEnumDataV0>()];

ulong startRef = 0;
while (true)
{
ct.ThrowIfCancellationRequested();

var req = new NativeMethods.MftEnumDataV0
{
StartFileReferenceNumber = startRef,
LowUsn = 0,
HighUsn = long.MaxValue,
};
MemoryMarshal.Write(inBuffer, in req);

if (!NativeMethods.DeviceIoControl(handle, NativeMethods.FSCTL_ENUM_USN_DATA,
inBuffer, inBuffer.Length, buffer, buffer.Length, out int returned, IntPtr.Zero))
{
int err = Marshal.GetLastWin32Error();
if (err == NativeMethods.ERROR_HANDLE_EOF) yield break; // 枚举完毕
throw new System.ComponentModel.Win32Exception(err, "FSCTL_ENUM_USN_DATA 失败");
}
if (returned <= 8) yield break; // 只剩续读引用号，没有记录了

// 输出布局：[8 字节下一起始引用号][USN_RECORD 变长记录...]
startRef = MemoryMarshal.Read<ulong>(buffer);
int offset = 8;
while (offset + 60 <= returned) // 60 = USN_RECORD_V2 定长头大小
{
var rec = MemoryMarshal.Read<NativeMethods.UsnRecordV2>(buffer.AsSpan(offset));
if (rec.RecordLength < 60) yield break; // 记录损坏，防御性退出
var name = Encoding.Unicode.GetString(buffer, offset + rec.FileNameOffset, rec.FileNameLength);
yield return new MftRecord(
name,
rec.FileReferenceNumber & NativeMethods.FileRefMask,
rec.ParentFileReferenceNumber & NativeMethods.FileRefMask,
(rec.FileAttributes & NativeMethods.FILE_ATTRIBUTE_DIRECTORY)!= 0,
rec.FileAttributes);
offset += (int)rec.RecordLength;
}
if (startRef == 0) yield break;
}
}
}

/// <summary>
/// 把 MftEnumerator 适配成 IIndexSource：按卷枚举 → 在本卷内建 引用号→记录 映射 →
/// 沿父链拼出完整路径后再产出 RawEntry（IndexBuilder 拿到的就是成品条目）。
/// 按卷处理的原因：不同卷的引用号空间互相独立，父链绝不能跨卷拼。
/// </summary>
public sealed class MftIndexSource: IIndexSource
{
private readonly char[] _drives;
private readonly IDictionary<ulong, string>? _directoryPaths;

/// <param name="directoryPaths">输出：目录引用号 → 路径，增量监听拼路径时要用（见 IndexBuilder）。</param>
public MftIndexSource(IDictionary<ulong, string>? directoryPaths, params char[] drives)
{
_drives = drives;
_directoryPaths = directoryPaths;
}

public string Name => "MFT 直读(" + string.Join(",", _drives) + ")";

public IEnumerable<RawEntry> Load(IProgress<int>? progress, CancellationToken ct)
{
foreach (var drive in _drives)
{
ct.ThrowIfCancellationRequested();
// 第一遍：整卷记录先落地成表（几百万条 record struct 约几十 MB，可接受；
// 若以后要压内存，可改成只存 ref→(name,parent) 的紧凑数组）。
var records = new List<MftRecord>();
foreach (var r in MftEnumerator.EnumerateVolume(drive, ct)) records.Add(r);

var byRef = new Dictionary<ulong, int>(records.Count);
for (int i = 0; i < records.Count; i++) byRef[records[i].FileRef] = i;

string root = drive + ":\\";
int produced = 0;
for (int i = 0; i < records.Count; i++)
{
var r = records[i];
// 跳过 NTFS 元文件（$MFT、$LogFile 等，名字以 $ 开头且父是卷根的系统记录）
if (r.Name.StartsWith('$') && r.FileRef < 16) continue;
var path = ResolvePath(i, records, byRef, root);
// 目录表：增量监听拿到新建文件时只有父目录引用号，靠它拼全路径
if (r.IsDirectory && _directoryPaths != null) _directoryPaths[r.FileRef] = path;
var category = r.IsDirectory
? EntryCategory.Folder
: CategoryClassifier.ClassifyByExtension(r.Name);
yield return new RawEntry(r.Name, path, category, r.FileRef, r.ParentFileRef, r.IsDirectory);
if ((++produced & 0xFFFF) == 0) progress?.Report(produced);
}
progress?.Report(produced);
}
}

private static string ResolvePath(int index, List<MftRecord> records, Dictionary<ulong, int> byRef, string root)
{
// 卷根目录自身的引用号是 5，名字为 "."，它的父指向自己——到它为止。
var parts = new List<string>();
int cur = index;
for (int depth = 0; depth < 64; depth++)
{
var r = records[cur];
if (r.FileRef == 5) break; // 到卷根了，根名不进路径
parts.Add(r.Name);
if (r.ParentFileRef == r.FileRef ||!byRef.TryGetValue(r.ParentFileRef, out var parent)) break;
cur = parent;
}
parts.Reverse();
return parts.Count == 0? root: root + string.Join("\\", parts);
}
}
