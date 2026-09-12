// 冲突副本命名(DE-D-12 ②)—— **服务端 namepolicy.ConflictSuffix 的逐字节镜像**。
//
// 为什么客户端要自己算这个名字:冲突副本是客户端发起的一次写操作(本地改名 / 远端改名 +
// 上传),它得先把名字定下来。而**权威在服务端**(namepolicy):如果两边算法漂移,用户会看到
// "客户端说这个名字行、服务端 400",而且只在特定长度/特定字符的名字上出现
// (超长中文名、带组合符的名字、表情符号跨截断边界…)。
//
// 所以这里不是"再写一遍"而是**镜像**:
//   - 同一批输入 + 同一批期望输出由共享夹具 `desktop/testdata/conflict-cases.json` 锁住,
//     夹具**由服务端实现生成**(NETDISK_EMIT_CONFLICT_FIXTURE=1),客户端只能对齐它;
//   - 算法顺序也与服务端逐字对应:240 字节预算 → 给「后缀 + 扩展名」留位置 →
//     主体在 **UTF-8 码点边界**截断 → **NFC 重归一** → 拼后缀 → 再兜底(截断后可能落在
//     组合符中间,使 NFC 后长度变化)。
//
// 确定性:同一 (name, timestamp, maxBytes) 必然得到同一个名字 —— 所以冲突重试是幂等的,
// 不会每次重试都在用户目录里造一个新副本(那是"冲突副本爆炸"的经典成因)。

using System.Globalization;
using System.Text;

namespace NetDisk.ClientCore;

/// <summary>冲突副本命名(与服务端 namepolicy 同源)。</summary>
public static class ConflictNaming
{
    /// <summary>与服务端一致的名字上限(UTF-8 字节)。</summary>
    public const int DefaultMaxBytes = 240;

    /// <summary>时间戳格式(全 ASCII 安全,与服务端约定一致)。</summary>
    public const string TimestampFormat = "yyyyMMdd'T'HHmmss";

    /// <summary>把时刻格式化成冲突后缀里用的时间戳(UTC)。</summary>
    public static string Timestamp(DateTimeOffset at)
        => at.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// 生成确定性冲突名:<c>主体_conflict_时间戳.扩展名</c>,总长 ≤ <paramref name="maxBytes"/>。
    /// </summary>
    public static string Suggest(string name, string timestamp, int maxBytes = DefaultMaxBytes)
    {
        if (maxBytes <= 0)
        {
            maxBytes = DefaultMaxBytes;
        }
        var suffix = "_conflict_" + timestamp;

        // 扩展名单独保留,避免把 .xlsx 截掉;`i > 0` 让 `.gitignore` 这类点开头的名字
        // 不被当成"只有扩展名"
        var ext = "";
        var body = name;
        var i = name.LastIndexOf('.');
        if (i > 0)
        {
            body = name[..i];
            ext = name[i..];
        }

        // 「后缀 + 扩展名」已超限 → 退化为"截断主体 + 后缀"
        var budget = maxBytes - ByteCount(suffix) - ByteCount(ext);
        if (budget < 1)
        {
            ext = "";
            budget = maxBytes - ByteCount(suffix);
        }
        if (budget < 1)
        {
            // 极端情况:后缀本身超限,直接截断后缀
            var s = Normalize(name + suffix);
            return ByteCount(s) > maxBytes ? TruncateUtf8(s, maxBytes) : s;
        }

        var result = Normalize(TruncateUtf8(body, budget) + suffix + ext);
        // 截断后可能落在组合符中间,再归一并做长度兜底(与服务端同款循环)
        while (ByteCount(result) > maxBytes)
        {
            result = Normalize(TruncateUtf8(result, ByteCount(result) - 1));
        }
        return result;
    }

    /// <summary>NFC 归一(与服务端 <c>namepolicy.Normalize</c> 同款)。</summary>
    public static string Normalize(string name) => name.Normalize(NormalizationForm.FormC);

    /// <summary>UTF-8 字节数。</summary>
    public static int ByteCount(string s) => Encoding.UTF8.GetByteCount(s);

    /// <summary>在 UTF-8 **码点边界**截断到 ≤ n 字节(不会把一个汉字/表情切成半个)。</summary>
    private static string TruncateUtf8(string s, int n)
    {
        if (n <= 0)
        {
            return "";
        }
        var bytes = Encoding.UTF8.GetBytes(s);
        if (bytes.Length <= n)
        {
            return s;
        }
        // 从 n 往前退到码点起始字节(UTF-8 续字节形如 10xxxxxx)
        var end = n;
        while (end > 0 && (bytes[end] & 0xC0) == 0x80)
        {
            end--;
        }
        return Encoding.UTF8.GetString(bytes, 0, end);
    }
}
