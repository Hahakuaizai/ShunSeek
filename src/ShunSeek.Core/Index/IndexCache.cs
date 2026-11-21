using System.Runtime.InteropServices;
using System.Text;

namespace ShunSeek.Core;

/// <summary>
/// 索引磁盘缓存：把建好的平坦索引原样存一份，下次启动直接内存映射回来，
/// 不用再花一两分钟扫全盘。
///
/// 为什么存的是"已经算好的数组"而不是原始路径列表：拼音首字母、全拼、小写名这些
/// 转换是建索引里最费 CPU 的部分（四十多万条），存成品意味着启动时几乎是纯 IO。
/// 缓存里带 scope 和建表时间：路径对不上（换了机器/改了索引范围）或放太久（>24h）
/// 就当作不可信，后台重新建一次。
/// </summary>
public static class IndexCache
{
    private const int Magic = 0x53534958;   // "SSIX"
    private const int Version = 2;

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ShunSeek", "index.cache");

    public sealed record CachedIndex(IndexStore Store, string Scope, DateTime BuiltAt);

    /// <summary>读缓存。文件不存在、版本不对、内容截断——一律返回 null，调用方照常重建。</summary>
    public static CachedIndex? TryLoad(string path)
    {
        if (!System.IO.File.Exists(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            if (reader.ReadInt32() != Magic) return null;
            if (reader.ReadInt32() != Version) return null;

            var scope = reader.ReadString();
            var builtAt = new DateTime(reader.ReadInt64(), DateTimeKind.Local);
            int count = reader.ReadInt32();
            if (count < 0) return null;

            var nameArena = ReadChars(reader);
            var nameOffset = ReadInts(reader);
            var nameLength = ReadInts(reader);
            var initialsArena = ReadChars(reader);
            var initialsOffset = ReadInts(reader);
            var initialsLength = ReadInts(reader);
            var pinyinArena = ReadChars(reader);
            var pinyinOffset = ReadInts(reader);
            var pinyinLength = ReadInts(reader);
            var category = ReadBytes(reader);
            var parentRef = ReadInts(reader);
            var fileRef = ReadUlongs(reader);
            var fullPath = ReadStrings(reader, count);

            // 长度自洽检查：截断的缓存宁可丢掉也不要拿半截索引去搜
            if (nameOffset.Length != count || nameLength.Length != count ||
                category.Length != count || fullPath.Length != count)
                return null;

            var store = IndexStore.FromArrays(
                nameArena, nameOffset, nameLength,
                initialsArena, initialsOffset, initialsLength,
                pinyinArena, pinyinOffset, pinyinLength,
                category, parentRef, fileRef, fullPath);

            return new CachedIndex(store, scope, builtAt);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>写缓存：先写临时文件再原子替换，写到一半被杀不会留下坏文件。</summary>
    public static void Save(string path, IndexStore store, string scope, DateTime builtAt)
    {
        if (store.Count == 0) return;

        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);

        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write,
                   FileShare.None, 1 << 20, FileOptions.SequentialScan))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(scope);
            writer.Write(builtAt.Ticks);
            writer.Write(store.Count);

            WriteChars(writer, store.NameArena);
            WriteInts(writer, store.NameOffset);
            WriteInts(writer, store.NameLength);
            WriteChars(writer, store.InitialsArena);
            WriteInts(writer, store.InitialsOffset);
            WriteInts(writer, store.InitialsLength);
            WriteChars(writer, store.PinyinArena);
            WriteInts(writer, store.PinyinOffset);
            WriteInts(writer, store.PinyinLength);
            WriteBytes(writer, store.Category);
            WriteInts(writer, store.ParentRef);
            WriteUlongs(writer, store.FileRef);
            WriteStrings(writer, store.FullPath);
        }

        System.IO.File.Move(temp, path, overwrite: true);
    }

    // ── 数组的批量读写：整块交给 BinaryWriter，不要一条一条 Write ──

    private static void WriteChars(BinaryWriter w, char[] values)
    {
        w.Write(values.Length);
        w.Write(MemoryMarshal.AsBytes(values.AsSpan()));
    }

    /// <summary>
    /// 直接读进目标数组，不经过中间的 byte[]。
    /// 143MB 的缓存如果先 ReadBytes 再 ToArray，瞬间会多占一整份（大对象堆还不好回收）。
    /// </summary>
    private static void ReadExact(BinaryReader r, Span<byte> buffer)
    {
        var stream = r.BaseStream;
        while (buffer.Length > 0)
        {
            int read = stream.Read(buffer);
            if (read <= 0) throw new EndOfStreamException();
            buffer = buffer[read..];
        }
    }

    private static char[] ReadChars(BinaryReader r)
    {
        int length = r.ReadInt32();
        var result = new char[length];
        ReadExact(r, MemoryMarshal.AsBytes(result.AsSpan()));
        return result;
    }

    private static void WriteInts(BinaryWriter w, int[] values)
    {
        w.Write(values.Length);
        w.Write(MemoryMarshal.AsBytes(values.AsSpan()));
    }

    private static int[] ReadInts(BinaryReader r)
    {
        int length = r.ReadInt32();
        var result = new int[length];
        ReadExact(r, MemoryMarshal.AsBytes(result.AsSpan()));
        return result;
    }

    private static void WriteUlongs(BinaryWriter w, ulong[] values)
    {
        w.Write(values.Length);
        w.Write(MemoryMarshal.AsBytes(values.AsSpan()));
    }

    private static ulong[] ReadUlongs(BinaryReader r)
    {
        int length = r.ReadInt32();
        var result = new ulong[length];
        ReadExact(r, MemoryMarshal.AsBytes(result.AsSpan()));
        return result;
    }

    private static void WriteBytes(BinaryWriter w, byte[] values)
    {
        w.Write(values.Length);
        w.Write(values);
    }

    private static byte[] ReadBytes(BinaryReader r)
    {
        int length = r.ReadInt32();
        var result = new byte[length];
        ReadExact(r, result);
        return result;
    }

    private static void WriteStrings(BinaryWriter w, string[] values)
    {
        w.Write(values.Length);
        foreach (var value in values) w.Write(value);
    }

    private static string[] ReadStrings(BinaryReader r, int expected)
    {
        int length = r.ReadInt32();
        if (length != expected) throw new EndOfStreamException();
        var result = new string[length];
        for (int i = 0; i < length; i++) result[i] = r.ReadString();
        return result;
    }
}
