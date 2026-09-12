// 长路径支持(DE-D-16 ①)。
//
// Windows 的传统 `MAX_PATH` 是 260;而 R-11 允许**单名** 240 字节,叠上同步根与目录层级
// 很容易超过 260 —— 到那时"文件明明在服务端、本地却建不出来",用户看到的是同步卡住。
//
// 两条腿一起走:
//   ①进程清单声明 `longPathAware=true`(Windows 10 1607+ 才会给这个进程放宽限制,
//     见 NetDisk.App/app.manifest;检查器里有一条机械断言盯着它别被删掉);
//   ②**路径统一走 `\\?\` 前缀**(本文件)—— 这是唯一不依赖"用户有没有改注册表"的办法。
//
// 一个必须记住的坑:`\\?\` 会**关闭路径规范化** —— 它不解析 `.`、`..`,也不接受正斜杠。
// 所以顺序只能是"先 `Path.GetFullPath` 规范化、再加前缀";反过来(先加前缀再规范化)
// 会得到一个**打不开**的路径,而且报错信息毫无提示性(ERROR_INVALID_NAME)。

namespace NetDisk.SyncEngine.Paths;

/// <summary>长路径工具(所有本地文件操作都应经这里)。</summary>
public static class LongPath
{
    /// <summary>扩展长度前缀(本地卷)。</summary>
    public const string Prefix = @"\\?\";

    /// <summary>扩展长度前缀(UNC 网络路径)。</summary>
    public const string UncPrefix = @"\\?\UNC\";

    /// <summary>传统 MAX_PATH(超过它才**需要**前缀;但我们一律加,免得"有时超有时不超")。</summary>
    public const int LegacyMaxPath = 260;

    /// <summary>是否已经带前缀。</summary>
    public static bool IsExtended(string path)
        => path.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// 转成扩展长度路径。
    ///
    /// **先规范化再加前缀**(见文件头说明):
    ///   - 相对路径、`..`、正斜杠都会被 `Path.GetFullPath` 处理掉;
    ///   - 已经是扩展路径的原样返回(幂等 —— 调用链上多处加前缀也不会变成 `\\?\\\?\`);
    ///   - UNC(`\\server\share`)转成 `\\?\UNC\server\share`。
    /// </summary>
    public static string ToExtended(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("路径不能为空", nameof(path));
        }
        if (IsExtended(path))
        {
            return path;
        }
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // UNC:去掉两个反斜杠再加 `\\?\UNC\`
            return UncPrefix + full[2..];
        }
        return Prefix + full;
    }

    /// <summary>把扩展路径还原成可读形式(日志/UI 展示;**不要**拿它去调文件 API)。</summary>
    public static string ForDisplay(string path)
    {
        if (path.StartsWith(UncPrefix, StringComparison.Ordinal))
        {
            return @"\\" + path[UncPrefix.Length..];
        }
        return path.StartsWith(Prefix, StringComparison.Ordinal) ? path[Prefix.Length..] : path;
    }

    /// <summary>拼接后转扩展路径(组合多个片段时的唯一入口)。</summary>
    public static string Combine(string root, params string[] parts)
    {
        var combined = parts.Length == 0 ? root : Path.Combine(root, Path.Combine(parts));
        return ToExtended(combined);
    }
}
