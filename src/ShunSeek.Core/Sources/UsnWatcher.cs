using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ShunSeek.Core.Sources;

public enum UsnChangeKind { Created, Deleted, RenamedOldName, RenamedNewName, Other }

/// <summary>一条 USN 增量变更。注意：USN 只给文件名和父目录引用号，完整路径要靠索引里的目录表拼。</summary>
public readonly record struct UsnChange(
    UsnChangeKind Kind,
    string Name,
    ulong FileRef,
    ulong ParentFileRef,
    bool IsDirectory,
    uint Reason);

/// <summary>USN 日志被卷满覆盖了（起点太旧），只能回退到整卷重建。</summary>
public sealed class JournalWrappedException : Exception
{
    public JournalWrappedException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>该卷没开 USN 日志（多见于非系统盘），这台卷只能放弃增量。</summary>
public sealed class JournalNotActiveException : Exception
{
    public JournalNotActiveException(string message) : base(message) { }
}

/// <summary>
/// USN 日志增量监听：索引建好之后，靠它实时跟进文件的增删改名，不再全量重扫。
///
/// 工作方式：先 FSCTL_QUERY_USN_JOURNAL 拿到日志 ID 与 NextUsn 作为起点，
/// 再循环 FSCTL_READ_USN_JOURNAL 阻塞读新记录（BytesToWaitFor&gt;0 + Timeout 让调用
/// 在无新记录时挂起等待，而不是忙轮询）。读到的记录逐条回调 Changed。
///
/// 落地策略（见 DeltaIndex）：变更交给增量表，累计多了或日志被覆盖时整卷重建。
/// 改名是 OldName/NewName 成对事件，按 FileRef 配对处理成"改路径"，不要当成一删一增。
/// </summary>
public sealed class UsnWatcher : IDisposable
{
    private readonly char _drive;
    private readonly long _requestedStartUsn; // -1 = 从"现在"开始
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <param name="drive">卷号字符，如 'C'。</param>
    public UsnWatcher(char drive, long startUsn = -1)
    {
        _drive = drive;
        _requestedStartUsn = startUsn;
    }

    public char Drive => _drive;

    /// <summary>每条变更触发一次。回调在监听线程上，注意不要阻塞（会拖慢后续读取）。</summary>
    public event Action<UsnChange>? Changed;

    /// <summary>监听线程挂了（日志被覆盖 / 卷被卸载 / 权限被收回）。上层据此决定重建或降级。</summary>
    public event Action<Exception>? Faulted;

    public void Start()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("USN 监听只支持 Windows/NTFS。");
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunLoop(_cts.Token), _cts.Token);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* 退出阶段忽略 */ }
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* 退出阶段忽略 */ }
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();

    /// <summary>
    /// 查一下某卷的 USN 日志当前写到哪儿了。建索引前记下这个位置，建完从这里开始读，
    /// 就能把"建索引那几秒里发生的变更"补回来（重放变更本身是幂等的）。
    /// </summary>
    public static bool TryQueryNextUsn(char drive, out long nextUsn)
    {
        nextUsn = 0;
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var handle = VolumeHandle.OpenVolume(drive);
            nextUsn = QueryJournal(handle).NextUsn;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void RunLoop(CancellationToken ct)
    {
        try
        {
            using var handle = VolumeHandle.OpenVolume(_drive);
            var journal = QueryJournal(handle);
            long nextUsn = _requestedStartUsn;
            // 起点比日志里还老 → 这段历史已经被覆盖，只能整表重建
            if (nextUsn < 0 || nextUsn < journal.FirstUsn) nextUsn = journal.NextUsn;

            var inBuffer = new byte[Marshal.SizeOf<NativeMethods.ReadUsnJournalDataV0>()];
            var outBuffer = new byte[256 * 1024];

            uint reasonMask = NativeMethods.USN_REASON_FILE_CREATE
                | NativeMethods.USN_REASON_FILE_DELETE
                | NativeMethods.USN_REASON_RENAME_OLD_NAME
                | NativeMethods.USN_REASON_RENAME_NEW_NAME;

            while (!ct.IsCancellationRequested)
            {
                var req = new NativeMethods.ReadUsnJournalDataV0
                {
                    StartUsn = (ulong)nextUsn,
                    ReasonMask = reasonMask,
                    ReturnOnlyOnClose = 0,
                    Timeout = 1_000_000, // 100ms（100ns 单位）：无新记录时最多等这么久，好响应取消
                    BytesToWaitFor = 1,  // >0 表示"有至少 1 字节新数据再返回"的等待模式
                    UsnJournalID = journal.UsnJournalID,
                };
                MemoryMarshal.Write(inBuffer, in req);

                if (!NativeMethods.DeviceIoControl(handle, NativeMethods.FSCTL_READ_USN_JOURNAL,
                        inBuffer, inBuffer.Length, outBuffer, outBuffer.Length, out int returned, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == NativeMethods.ERROR_JOURNAL_ENTRY_DELETED
                        || err == NativeMethods.ERROR_JOURNAL_DELETE_IN_PROGRESS
                        || err == NativeMethods.ERROR_JOURNAL_NOT_ACTIVE)
                    {
                        throw new JournalWrappedException(
                            $"{_drive}: 的 USN 日志已失效（Win32 {err}），需要整卷重建索引。",
                            new Win32Exception(err));
                    }
                    throw new Win32Exception(err, "FSCTL_READ_USN_JOURNAL 失败");
                }

                if (returned >= 8)
                {
                    // 输出布局：[8 字节 NextUsn][USN_RECORD...]；只返回 8 字节表示这轮没新记录
                    nextUsn = MemoryMarshal.Read<long>(outBuffer);
                }

                int offset = 8;
                while (offset + 60 <= returned)
                {
                    var rec = MemoryMarshal.Read<NativeMethods.UsnRecordV2>(outBuffer.AsSpan(offset));
                    if (rec.RecordLength < 60) break;

                    var name = Encoding.Unicode.GetString(outBuffer, offset + rec.FileNameOffset, rec.FileNameLength);
                    var change = new UsnChange(
                        Classify(rec.Reason),
                        name,
                        rec.FileReferenceNumber & NativeMethods.FileRefMask,
                        rec.ParentFileReferenceNumber & NativeMethods.FileRefMask,
                        (rec.FileAttributes & NativeMethods.FILE_ATTRIBUTE_DIRECTORY) != 0,
                        rec.Reason);

                    try { Changed?.Invoke(change); }
                    catch { /* 上层处理出错不能拖垮监听循环 */ }

                    offset += (int)rec.RecordLength;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (JournalWrappedException ex)
        {
            Faulted?.Invoke(ex);
        }
        catch (Exception ex)
        {
            Faulted?.Invoke(ex);
        }
    }

    private static UsnChangeKind Classify(uint reason)
    {
        // 同一条记录可能带多个原因（创建后立刻删除、改名后关闭…），按"最终状态"优先：
        // 删除 > 新名字 > 旧名字 > 创建
        if ((reason & NativeMethods.USN_REASON_FILE_DELETE) != 0) return UsnChangeKind.Deleted;
        if ((reason & NativeMethods.USN_REASON_RENAME_NEW_NAME) != 0) return UsnChangeKind.RenamedNewName;
        if ((reason & NativeMethods.USN_REASON_RENAME_OLD_NAME) != 0) return UsnChangeKind.RenamedOldName;
        if ((reason & NativeMethods.USN_REASON_FILE_CREATE) != 0) return UsnChangeKind.Created;
        return UsnChangeKind.Other;
    }

    private static NativeMethods.UsnJournalDataV0 QueryJournal(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        var outBuffer = new byte[Marshal.SizeOf<NativeMethods.UsnJournalDataV0>()];
        if (!NativeMethods.DeviceIoControl(handle, NativeMethods.FSCTL_QUERY_USN_JOURNAL,
                null, 0, outBuffer, outBuffer.Length, out _, IntPtr.Zero))
        {
            int err = Marshal.GetLastWin32Error();
            if (err == NativeMethods.ERROR_JOURNAL_NOT_ACTIVE)
                throw new JournalNotActiveException("该卷没有开启 USN 变更日志（fsutil usn createjournal 可开启）。");
            throw new Win32Exception(err, "FSCTL_QUERY_USN_JOURNAL 失败（该卷可能未开启 USN 日志）");
        }
        return MemoryMarshal.Read<NativeMethods.UsnJournalDataV0>(outBuffer);
    }
}
