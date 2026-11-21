using System.Text;

namespace ShunSeek.Core;

/// <summary>
/// 汉字 → 全拼（无声调、小写），用于"weixin" 也能搜到「微信」。
///
/// 和 PinyinInitials 的关系：
///  - 首字母（wx）用 GB2312 边界字表法，零依赖、极快，索引里只占 1 个字符/汉字；
///  - 全拼（weixin）需要逐字音节表，用 TinyPinyin（纯内存表，MIT），索引里占 3~6 个字符/汉字。
/// 两条通道分开存：先扫首字母（便宜），结果不够才扫全拼（贵），常见输入不吃这个成本。
/// </summary>
public static class PinyinFull
{
    /// <summary>转成全拼串：汉字转音节，英文数字原样小写，其余字符丢掉。</summary>
    public static string Convert(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        // 纯 ASCII 的名字（绝大多数英文文件名）不用进拼音库
        bool hasNonAscii = false;
        foreach (var ch in text)
        {
            if (ch >= 128) { hasNonAscii = true; break; }
        }

        if (!hasNonAscii)
            return Filter(text);

        // 逐字转 + 按字缓存：同一个汉字在几百万条目里会重复几十万次，
        // 缓存之后就不用反复走音节表（整串丢给库的做法每次都有额外开销）。
        var sb = new StringBuilder(text.Length * 2);
        foreach (var ch in text)
        {
            if (ch < 128)
            {
                if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
                else if (ch is >= 'A' and <= 'Z') sb.Append((char)(ch + 32));
                continue;
            }

            sb.Append(SyllableOf(ch));
        }
        return sb.ToString();
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<char, string> Syllables = new();

    private static string SyllableOf(char ch)
        => Syllables.GetOrAdd(ch, static c =>
        {
            var raw = TinyPinyin.PinyinHelper.GetPinyin(c.ToString(), string.Empty);
            return Filter(raw);
        });

    /// <summary>只保留 a-z0-9（拼音库对多音字/生僻字可能吐出声调或原字，统一在这里兜住）。</summary>
    private static string Filter(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (ch is >= 'A' and <= 'Z') sb.Append((char)(ch + 32));
        }
        return sb.ToString();
    }
}
