using System.Text;

namespace ShunSeek.Core;

/// <summary>
/// 汉字 → 拼音首字母，用经典的「GB2312 一级字边界字表」法：
/// GB2312 一级汉字（3755 个常用字）恰好按拼音排序，每个声母段的第一个字就是边界字。
/// 把汉字编码成 GB2312 双字节码（0xB0A1~0xD7F9），落在哪个边界段里，首字母就是哪个。
/// 例：「王者荣耀」→ 王(CD F5)∈w 段、者(D5 DF)∈z 段、荣(C8 D9)∈r 段、耀(D2 AB)∈y 段 → "wzry"。
///
/// 已知局限（可接受，注释留档）：
///  - 多音字只取一级字表里的段归属，如「重」归 z 段（zhong），搜 "cy" 找不到「重庆」，
///    靠全名搜索兜底；阶段 2 可引入多音字词典修正高频词。
///  - 二级字（生僻字）不在 GB2312 一级区，直接跳过，不产生首字母。
///  - 英文/数字原样保留并转小写，其余符号丢弃，所以 "QQ音乐" → "qqyy"（乐取 y 段）。
/// </summary>
public static class PinyinInitials
{
    // 各声母段边界字的 GB2312 码（(高字节<<8)|低字节），与 Letters 一一对应，升序。
    private static readonly int[] SecValues =
    {
        0xB0A1, 0xB0C5, 0xB2C1, 0xB4EE, 0xB6EA, 0xB7A2, 0xB8C1, 0xB9FE, 0xBBF7, 0xBFA6,
        0xC0AC, 0xC2E8, 0xC4C3, 0xC5B6, 0xC5BE, 0xC6DA, 0xC8BB, 0xC8F6, 0xCBFA, 0xCDDA,
        0xCEF4, 0xD1B9, 0xD4D1,
    };

    private static readonly char[] Letters =
    {
        'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'j', 'k',
        'l', 'm', 'n', 'o', 'p', 'q', 'r', 's', 't', 'w',
        'x', 'y', 'z',
    };

    private static readonly Encoding Gb2312 = CreateEncoding();

    private static Encoding CreateEncoding()
    {
        // .NET Core/5+ 默认不带 GB2312，必须注册代码页提供程序（包 System.Text.Encoding.CodePages）。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding("GB2312");
    }

    /// <summary>把任意显示名转成拼音首字母串（全小写，只含 a-z0-9）。</summary>
    public static string Convert(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch < 128)
            {
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
                continue;
            }
            var initial = InitialOf(ch);
            if (initial != '\0') sb.Append(initial);
        }
        return sb.ToString();
    }

    /// <summary>单个汉字的拼音首字母；非一级汉字返回 '\0'。</summary>
    public static char InitialOf(char ch)
    {
        Span<byte> bytes = stackalloc byte[2];
        int written;
        try
        {
            written = Gb2312.GetBytes(new ReadOnlySpan<char>(in ch), bytes);
        }
        catch (EncoderFallbackException)
        {
            return '\0';
        }
        if (written != 2) return '\0'; // 编码不出双字节 → 不是 GB2312 汉字
        int code = (bytes[0] << 8) | bytes[1];
        if (code < SecValues[0]) return '\0';
        // 二分找最后一个 ≤ code 的边界
        int lo = 0, hi = SecValues.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (SecValues[mid] <= code) lo = mid; else hi = mid - 1;
        }
        return Letters[lo];
    }
}
