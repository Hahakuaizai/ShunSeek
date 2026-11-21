namespace ShunSeek.Core;

/// <summary>
/// 平坦内存索引（建好后只读，可被多线程并发扫描）。
///
/// 为什么不用 List&lt;FileItem&gt; 对象数组：200 万个对象散落在堆上，扫描时缓存命中率差、
/// GC 压力大。这里把「搜索热路径真正要碰的数据」压成连续数组：
///  - 所有小写文件名拼进一个 char 池（NameArena），每条目只记 Offset/Length；
///    扫描 = 在连续内存上做 Span.IndexOf（内部向量化），且天然可按条目区间分片并行。
///  - 拼音首字母同样拼进 InitialsArena。
///  - Category 1 字节、ParentRef 4 字节、FileRef 8 字节，都是并行数组。
/// 冷数据（原始大小写名、全路径）放 string[]，只在命中后组装结果时才碰。
/// 200 万条目量级：热数据约 130~160MB 以内（视平均名长），验收线见 docs/开发流程.md。
/// </summary>
public sealed class IndexStore
{
    /// <summary>空索引（启动时、重建前、增量表为空时都用它，省得到处判 null）。</summary>
    public static readonly IndexStore Empty = new();

    public int Count { get; private init; }

    // —— 热数据（扫描时逐条访问）——
    public char[] NameArena { get; private init; } = Array.Empty<char>();
    public int[] NameOffset { get; private init; } = Array.Empty<int>();
    public int[] NameLength { get; private init; } = Array.Empty<int>();
    public char[] InitialsArena { get; private init; } = Array.Empty<char>();
    public char[] PinyinArena { get; private init; } = Array.Empty<char>();
    public int[] InitialsOffset { get; private init; } = Array.Empty<int>();
    public int[] InitialsLength { get; private init; } = Array.Empty<int>();
    public int[] PinyinOffset { get; private init; } = Array.Empty<int>();
    public int[] PinyinLength { get; private init; } = Array.Empty<int>();
    public byte[] Category { get; private init; } = Array.Empty<byte>();
    public int[] ParentRef { get; private init; } = Array.Empty<int>();   // 父条目下标，-1 表示无
    public ulong[] FileRef { get; private init; } = Array.Empty<ulong>(); // NTFS 文件引用号，无则 0

    // —— 冷数据（命中后才访问）——
    public string[] DisplayName { get; private init; } = Array.Empty<string>();
    public string[] FullPath { get; private init; } = Array.Empty<string>();

    /// <summary>给缓存恢复用：直接从现成数组装配一个只读索引，不做任何转换。</summary>
    internal static IndexStore FromArrays(
        char[] nameArena, int[] nameOffset, int[] nameLength,
        char[] initialsArena, int[] initialsOffset, int[] initialsLength,
        char[] pinyinArena, int[] pinyinOffset, int[] pinyinLength,
        byte[] category, int[] parentRef, ulong[] fileRef,
        string[] displayName, string[] fullPath)
        => new()
        {
            Count = displayName.Length,
            NameArena = nameArena, NameOffset = nameOffset, NameLength = nameLength,
            InitialsArena = initialsArena, InitialsOffset = initialsOffset, InitialsLength = initialsLength,
            PinyinArena = pinyinArena, PinyinOffset = pinyinOffset, PinyinLength = pinyinLength,
            Category = category, ParentRef = parentRef, FileRef = fileRef,
            DisplayName = displayName, FullPath = fullPath,
        };

    public ReadOnlySpan<char> LowerName(int i) => NameArena.AsSpan(NameOffset[i], NameLength[i]);
    public ReadOnlySpan<char> Initials(int i) => InitialsArena.AsSpan(InitialsOffset[i], InitialsLength[i]);
    public ReadOnlySpan<char> FullPinyin(int i) => PinyinArena.AsSpan(PinyinOffset[i], PinyinLength[i]);
    public EntryCategory CategoryOf(int i) => (EntryCategory)Category[i];

    /// <summary>索引内存粗估（字节），给 Bench 和诊断页用：数组实际占用 + string 估算。</summary>
    public long EstimateMemoryBytes()
    {
        long bytes = 0;
        bytes += (long)NameArena.Length * 2 + (long)InitialsArena.Length * 2 + (long)PinyinArena.Length * 2;
        bytes += (long)Count * (4 + 4 + 4 + 4 + 4 + 4 + 1 + 4 + 8); // 各并行数组
        for (int i = 0; i < Count; i++)
            bytes += 24 + (DisplayName[i].Length + FullPath[i].Length) * 2L + 16; // string 对象头粗估
        return bytes;
    }

    /// <summary>构建器：数据源条目先攒在这里，最后 Freeze 成上面的连续数组。</summary>
    public sealed class Builder
    {
        private readonly List<char> _nameArena = new();
        private readonly List<char> _initialsArena = new();
        private readonly List<char> _pinyinArena = new();
        private readonly List<int> _nameOff = new(), _nameLen = new(), _initOff = new(), _initLen = new();
        private readonly List<int> _pinyinOff = new(), _pinyinLen = new();
        private readonly List<byte> _cat = new();
        private readonly List<int> _parent = new();
        private readonly List<ulong> _refs = new();
        private readonly List<string> _display = new(), _path = new();

        public int Count => _display.Count;

        /// <param name="parentIndex">父条目下标，IndexBuilder 解析完父链后回填，未知传 -1。</param>
        public void Add(string displayName, string fullPath, EntryCategory category, ulong fileRef, int parentIndex)
        {
            var lower = displayName.ToLowerInvariant();
            _nameOff.Add(_nameArena.Count);
            _nameLen.Add(lower.Length);
            _nameArena.AddRange(lower);

            var initials = PinyinInitials.Convert(displayName);
            _initOff.Add(_initialsArena.Count);
            _initLen.Add(initials.Length);
            _initialsArena.AddRange(initials);

            // 全拼（weixin）：只在名字里有汉字时才真正去查表
            var pinyin = PinyinFull.Convert(displayName);
            _pinyinOff.Add(_pinyinArena.Count);
            _pinyinLen.Add(pinyin.Length);
            _pinyinArena.AddRange(pinyin);

            _cat.Add((byte)category);
            _parent.Add(parentIndex);
            _refs.Add(fileRef);
            _display.Add(displayName);
            _path.Add(fullPath);
        }

        public IndexStore Freeze() => new()
        {
            Count = Count,
            NameArena = _nameArena.ToArray(),
            NameOffset = _nameOff.ToArray(),
            NameLength = _nameLen.ToArray(),
            InitialsArena = _initialsArena.ToArray(),
            InitialsOffset = _initOff.ToArray(),
            InitialsLength = _initLen.ToArray(),
            PinyinArena = _pinyinArena.ToArray(),
            PinyinOffset = _pinyinOff.ToArray(),
            PinyinLength = _pinyinLen.ToArray(),
            Category = _cat.ToArray(),
            ParentRef = _parent.ToArray(),
            FileRef = _refs.ToArray(),
            DisplayName = _display.ToArray(),
            FullPath = _path.ToArray(),
        };
    }
}
