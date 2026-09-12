// NetDisk.ClientCore 的占位实现(DE-D-01 骨架;DE-D-02 补齐实体)。
//
// 这一版只做两件事,目的是**让纪律有东西可检查**,而不是先写一堆空壳:
//   1. `NameRules` 声明"名字/路径规则与服务端同源"的入口(DE-D-02 ③ 会用它
//      把 server 侧 namepolicy 的那批用例在双端各跑一遍,断言结论一致);
//   2. `Cursor` 表达增量同步游标的最小形状(6.9 的"双态同一游标")。
//
// 刻意**不**在这里写文件系统/网络/Windows 相关的东西 —— 那正是本工程被禁止的
// (见 csproj 里的三条硬纪律)。

namespace NetDisk.ClientCore;

/// <summary>
/// 名字与路径规则(与服务端 namepolicy 同源)。
///
/// 为什么要有"两端同一批用例":客户端必须在**上传之前**就拒绝非法名
/// (否则用户体验是"传到 99% 才报错"),而它一旦与服务端规则漂移,
/// 用户就会遇到"客户端说没问题、服务端拒绝"这类无法解释的失败。
/// 规则的**唯一权威是服务端**,客户端这份是它的镜像,靠同一批用例锁住。
/// </summary>
public static class NameRules
{
    /// <summary>单个文件名的 UTF-8 字节上限(6.7 规则 5,与服务端默认一致)。</summary>
    public const int MaxNameBytes = 240;

    /// <summary>累计路径字节上限(R-11)。</summary>
    public const int MaxPathBytes = 240;

    /// <summary>目录深度硬上限(6.4)。</summary>
    public const int MaxDepth = 31;

    /// <summary>Windows 保留名(`CON`/`PRN`/`NUL` 等)不得作为文件名。</summary>
    private static readonly string[] ReservedNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>UTF-8 字节数(不是字符数:中文名按字节计)。</summary>
    public static int Utf8Length(string s) => System.Text.Encoding.UTF8.GetByteCount(s);

    /// <summary>
    /// 名字是否可用于上传(空/超长/含非法字符/保留名/首尾点空格 → 否)。
    /// </summary>
    /// <remarks>
    /// **必须先 NFC 归一再判**:服务端 namepolicy 的顺序就是"归一再校验",而同一串文字
    /// 用组合符写(分解形式)会比归一后**多占字节** —— 例如 80 个 `e` + 组合尖音符,
    /// 分解形式 244 字节、NFC 之后只有 164 字节。客户端不归一就会把服务端接受的名字判成
    /// 非法(用户看到"这个名字不能用",而服务端其实收得下),反之某些边界名字则会漏判。
    ///
    /// 这里的坑在于**它不会报错**:`string.Normalize` 在 `InvariantGlobalization=true` 时
    /// 是空操作(不加载 ICU),整条规则看起来"实现了归一"而实际什么也没做 ——
    /// 所以 Directory.Build.props 里必须把 InvariantGlobalization 关掉(DE-D-12 的
    /// 双端夹具把这个静默失效抓了出来)。
    /// </remarks>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var normalized = Normalize(name);
        if (Utf8Length(normalized) > MaxNameBytes) return false;
        // 服务端拒绝的字符集(6.7):路径分隔符、控制字符、Windows 非法字符
        foreach (var ch in normalized)
        {
            if (ch < 0x20 || ch is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
            {
                return false;
            }
        }
        if (normalized.EndsWith(' ') || normalized.EndsWith('.')) return false;
        var stem = normalized.Split('.')[0];
        return !ReservedNames.Contains(stem, System.StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>NFC 归一(与服务端 <c>namepolicy.Normalize</c> 同款,也是冲突命名的前置)。</summary>
    public static string Normalize(string name) => name.Normalize(System.Text.NormalizationForm.FormC);

    /// <summary>深度是否超限(根目录为 0 层)。</summary>
    public static bool IsDepthAllowed(int depth) => depth >= 0 && depth <= MaxDepth;
}
