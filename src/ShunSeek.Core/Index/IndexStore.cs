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
    // 只存全路径：显示名就是它的文件名部分，取的时候算一次就行（每次搜索只算前一两百条），
    // 没必要为 44 万条各存一份重复的短名字符串。
    public string[] FullPath { get; private init; } = Array.Empty<string>();

    /// <summary>
    /// 显示名与文件名不一致的条目（微信 → E:\Weixin\Weixin.exe、微信输入法 → wetype_server.exe…）。
    /// 只给这几千条应用/快捷方式存一份，普通文件一律用文件名，不占内存。
    /// </summary>
    public IReadOnlyDictionary<int, string> FriendlyNames { get; private init; } =
        new Dictionary<int, string>();

    /// <summary>给缓存恢复用：直接从现成数组装配一个只读索引，不做任何转换。</summary>
    internal static IndexStore FromArrays(
        char[] nameArena, int[] nameOffset, int[] nameLength,
        char[] initialsArena, int[] initialsOffset, int[] initialsLength,
        char[] pinyinArena, int[] pinyinOffset, int[] pinyinLength,
        byte[] category, int[] parentRef, ulong[] fileRef,
        string[] fullPath,
        IReadOnlyDictionary<int, string>? friendlyNames = null)
        => new()
        {
            Count = fullPath.Length,
            NameArena = nameArena, NameOffset = nameOffset, NameLength = nameLength,
            InitialsArena = initialsArena, InitialsOffset = initialsOffset, InitialsLength = initialsLength,
            PinyinArena = pinyinArena, PinyinOffset = pinyinOffset, PinyinLength = pinyinLength,
            Category = category, ParentRef = parentRef, FileRef = fileRef,
            FullPath = fullPath,
            FriendlyNames = friendlyNames ?? new Dictionary<int, string>(),
        };

    public ReadOnlySpan<char> LowerName(int i) => NameArena.AsSpan(NameOffset[i], NameLength[i]);

    // 首字母/全拼为空 = 和名字本身完全一样（纯英文名），直接复用名字那份内存，
    // 不用再存一遍。纯英文的条目占绝大多数，这一条省掉近三分之二的字符池。
    public ReadOnlySpan<char> Initials(int i) => InitialsLength[i] == 0
        ? LowerName(i)
        : InitialsArena.AsSpan(InitialsOffset[i], InitialsLength[i]);

    public ReadOnlySpan<char> FullPinyin(int i) => PinyinLength[i] == 0
        ? LowerName(i)
        : PinyinArena.AsSpan(PinyinOffset[i], PinyinLength[i]);

    /// <summary>显示名：优先用注册表/快捷方式给的友好名，否则取文件名。</summary>
    public string NameOf(int i) => FriendlyNames.TryGetValue(i, out var friendly)
        ? friendly
        : System.IO.Path.GetFileName(FullPath[i]);
    public EntryCategory CategoryOf(int i) => (EntryCategory)Category[i];

    /// <summary>索引内存粗估（字节），给 Bench 和诊断页用：数组实际占用 + string 估算。</summary>
    public long EstimateMemoryBytes()
    {
        long bytes = 0;
        bytes += (long)NameArena.Length * 2 + (long)InitialsArena.Length * 2 + (long)PinyinArena.Length * 2;
        bytes += (long)Count * (4 + 4 + 4 + 4 + 4 + 4 + 1 + 4 + 8); // 各并行数组
        for (int i = 0; i < Count; i++)
            bytes += 24 + FullPath[i].Length * 2L + 16; // string 对象头粗估
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
        private readonly List<string> _path = new();
        private readonly Dictionary<int, string> _friendly = new();

        public int Count => _path.Count;

        /// <param name="parentIndex">父条目下标，IndexBuilder 解析完父链后回填，未知传 -1。</param>
        public void Add(string displayName, string fullPath, EntryCategory category, ulong fileRef, int parentIndex)
        {
            var lower = displayName.ToLowerInvariant();
            _nameOff.Add(_nameArena.Count);
            _nameLen.Add(lower.Length);
            _nameArena.AddRange(lower);

            // 首字母 / 全拼：和名字一样（纯英文名）就记长度 0，表示"复用名字"，不再存一遍
            var initials = PinyinInitials.Convert(displayName);
            bool initialsSame = initials.Equals(lower, StringComparison.Ordinal);
            _initOff.Add(initialsSame ? 0 : _initialsArena.Count);
            _initLen.Add(initialsSame ? 0 : initials.Length);
            if (!initialsSame) _initialsArena.AddRange(initials);

            // 全拼（weixin）：只在名字里有汉字时才真正去查表
            var pinyin = PinyinFull.Convert(displayName);
            bool pinyinSame = pinyin.Equals(lower, StringComparison.Ordinal);
            _pinyinOff.Add(pinyinSame ? 0 : _pinyinArena.Count);
            _pinyinLen.Add(pinyinSame ? 0 : pinyin.Length);
            if (!pinyinSame) _pinyinArena.AddRange(pinyin);

            _cat.Add((byte)category);
            _parent.Add(parentIndex);
            _refs.Add(fileRef);
            _path.Add(fullPath);

            // 名字和文件名不一样才有必要单独记（应用注册表名、快捷方式名）
            var leaf = System.IO.Path.GetFileName(fullPath);
            if (!string.Equals(displayName, leaf, StringComparison.Ordinal))
                _friendly[_path.Count - 1] = displayName;
        }

        public IndexStore Freeze()
        {
            var store = new IndexStore
            {
                Count = Count,
                NameArena = CopyChars(_nameArena),
                NameOffset = CopyInts(_nameOff),
                NameLength = CopyInts(_nameLen),
                InitialsArena = CopyChars(_initialsArena),
                InitialsOffset = CopyInts(_initOff),
                InitialsLength = CopyInts(_initLen),
                PinyinArena = CopyChars(_pinyinArena),
                PinyinOffset = CopyInts(_pinyinOff),
                PinyinLength = CopyInts(_pinyinLen),
                Category = CopyBytes(_cat),
                ParentRef = CopyInts(_parent),
                FileRef = CopyUlongs(_refs),
                FullPath = _path.ToArray(),
                FriendlyNames = new Dictionary<int, string>(_friendly),
            };

            // 构建期的 List 全是几 MB~几十 MB 的底层数组，Freeze 之后就没用了。
            // 不主动清空的话，它们要等下一次大对象堆压缩才还回去——任务管理器里
            // 看到的就是"刚建完索引，私有内存还是六七百兆"。
            ReleaseBuffers();
            return store;
        }

        /// <summary>清掉构建期的缓冲，让内存立刻可回收。</summary>
        private void ReleaseBuffers()
        {
            _nameArena.Clear(); _nameArena.TrimExcess();
            _initialsArena.Clear(); _initialsArena.TrimExcess();
            _pinyinArena.Clear(); _pinyinArena.TrimExcess();
            _nameOff.Clear(); _nameOff.TrimExcess();
            _nameLen.Clear(); _nameLen.TrimExcess();
            _initOff.Clear(); _initOff.TrimExcess();
            _initLen.Clear(); _initLen.TrimExcess();
            _pinyinOff.Clear(); _pinyinOff.TrimExcess();
            _pinyinLen.Clear(); _pinyinLen.TrimExcess();
            _cat.Clear(); _cat.TrimExcess();
            _parent.Clear(); _parent.TrimExcess();
            _refs.Clear(); _refs.TrimExcess();
            _path.Clear(); _path.TrimExcess();
            _friendly.Clear();
        }

        // ToArray() 在 List 内部会先 EnsureCapacity(count)（容量可能已经是 2 倍），
        // 多拷一份临时大数组；这里按精确长度 new + CopyTo，峰值只多一份目标数组。
        private static char[] CopyChars(List<char> source)
        {
            var result = new char[source.Count];
            source.CopyTo(result);
            return result;
        }

        private static int[] CopyInts(List<int> source)
        {
            var result = new int[source.Count];
            source.CopyTo(result);
            return result;
        }

        private static byte[] CopyBytes(List<byte> source)
        {
            var result = new byte[source.Count];
            source.CopyTo(result);
            return result;
        }

        private static ulong[] CopyUlongs(List<ulong> source)
        {
            var result = new ulong[source.Count];
            source.CopyTo(result);
            return result;
        }
    }
}
