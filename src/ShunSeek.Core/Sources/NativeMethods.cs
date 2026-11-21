using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ShunSeek.Core.Sources;

/// <summary>
/// MFT 枚举 / USN 日志所需的 kernel32 互操作与结构体。
/// 数值都对照 winioctl.h：FSCTL_* 用 CTL_CODE(FILE_DEVICE_FILE_SYSTEM=9, func, METHOD_NEITHER=3, FILE_ANY_ACCESS=0)。
/// 这些声明在非 Windows 平台只是"声明"，不会被调用（调用方先做 OS 判断）。
/// </summary>
internal static class NativeMethods
{
    public const uint FSCTL_ENUM_USN_DATA = 0x000900B3;   // func 44
    public const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4; // func 61, METHOD_BUFFERED
    public const uint FSCTL_READ_USN_JOURNAL = 0x000900BB;  // func 46

    public const uint GENERIC_READ = 0x80000000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;

    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    public const int ERROR_HANDLE_EOF = 38;
    // USN 日志相关错误码（winerror.h）：日志被删/没开/被卷满覆盖，都意味着"增量不可信，得重建"
    public const int ERROR_JOURNAL_DELETE_IN_PROGRESS = 1178;
    public const int ERROR_JOURNAL_NOT_ACTIVE = 1179;
    public const int ERROR_JOURNAL_ENTRY_DELETED = 1181;

    /// <summary>NTFS 文件引用号低 48 位是 MFT 记录号，高 16 位是序列号；做 key 时掩掉序列号。</summary>
    public const ulong FileRefMask = 0x0000_FFFF_FFFF_FFFF;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        byte[]? lpInBuffer,
        int nInBufferSize,
        byte[]? lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    /// <summary>FSCTL_ENUM_USN_DATA 的输入（V0 版，24 字节）：从哪个引用号开始枚举、USN 范围。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MftEnumDataV0
    {
        public ulong StartFileReferenceNumber;
        public long LowUsn;
        public long HighUsn;
    }

    /// <summary>FSCTL_QUERY_USN_JOURNAL 的输出（V0，56 字节）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct UsnJournalDataV0
    {
        public ulong UsnJournalID;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    /// <summary>FSCTL_READ_USN_JOURNAL 的输入（V0，40 字节）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ReadUsnJournalDataV0
    {
        public ulong StartUsn;
        public uint ReasonMask;
        public uint ReturnOnlyOnClose;
        public ulong Timeout;       // 单位 100ns；BytesToWaitFor>0 时作为最长等待
        public ulong BytesToWaitFor;
        public ulong UsnJournalID;
    }

    /// <summary>USN_RECORD V2 定长头（60 字节），后面紧跟 FileNameLength 字节的 UTF-16 文件名。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct UsnRecordV2
    {
        public uint RecordLength;
        public ushort MajorVersion;
        public ushort MinorVersion;
        public ulong FileReferenceNumber;
        public ulong ParentFileReferenceNumber;
        public long Usn;
        public long TimeStamp; // FILETIME
        public uint Reason;
        public uint SourceInfo;
        public uint SecurityId;
        public uint FileAttributes;
        public ushort FileNameLength; // 字节数
        public ushort FileNameOffset; // 从记录头算起的字节偏移
    }

    // USN Reason 标志（只列监听关心的）
    public const uint USN_REASON_FILE_CREATE = 0x00000100;
    public const uint USN_REASON_FILE_DELETE = 0x00000200;
    public const uint USN_REASON_RENAME_OLD_NAME = 0x00001000;
    public const uint USN_REASON_RENAME_NEW_NAME = 0x00002000;
    public const uint USN_REASON_BASIC_INFO_CHANGE = 0x00008000;
    public const uint USN_REASON_CLOSE = 0x80000000;
}

/// <summary>从卷句柄打开/校验的公共逻辑。</summary>
internal static class VolumeHandle
{
    public static SafeFileHandle OpenVolume(char driveLetter)
    {
        // 卷路径形如 \\.\C: —— 打开的是卷本身而不是目录，所以能对它发 FSCTL。
        var handle = NativeMethods.CreateFile(
            $@"\\.\{driveLetter}:",
            NativeMethods.GENERIC_READ,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            0,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            // 5 = ERROR_ACCESS_DENIED：读 USN/MFT 需要管理员权限或卷访问权，
            // 这正是 docs/开发流程.md 阶段 1「两档权限方案」要解决的问题。
            throw new UnauthorizedAccessException(
                $"打开卷 {driveLetter}: 失败（Win32 {err}）。读取 NTFS USN 数据需要管理员权限；" +
                "便携版请改用 DirectoryWalker 兜底，或用安装版的服务进程读取。");
        }
        return handle;
    }
}
