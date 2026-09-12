// 同步根与路径预算预检(DE-D-15;与 6.4 的服务端双上限同源)。
//
// 为什么这件事必须在**首次向导**里做,而不是等同步跑到一半报错:
// 服务端对"单个名字 ≤240 字节"和"完整路径 ≤240 字节 + 深度 ≤31"两头都卡(6.4)。
// 如果客户端把同步根放到一个已经很深的目录(例如 `D:\用户\文档\公司\项目\2026\...\网盘`),
// 那么"本地能建、服务端拒收"的文件会**同步到一半才失败** —— 此时本地已经落了一批文件、
// 服务端只收了一半,用户面对的是"两边都不完整"的不一致状态,而修法只有改根目录重来。
// 向导里花一秒钟算一次最长路径,就能把这件事变成"当场提示换根"(V2.17 P1-4)。
//
// 所以这里算的是**服务端口径**的预算(相对路径 240 字节 / 深度 31),而不是 Windows 的
// MAX_PATH —— 后者是另一件事(DE-D-16 的 `\\?\` 前缀),两者都要过,但失败的后果不同:
// 服务端拒收 = 数据不一致(严重);Windows 路径过长 = 本地打不开(可用 `\\?\` 兜)。

using System.Text;

namespace NetDisk.ClientCore;

/// <summary>一条待检查的相对路径(相对同步根)。</summary>
public sealed record PathProbe(string RelativePath, int Depth);

/// <summary>路径预算报告。</summary>
public sealed record PathBudgetReport(
    bool Ok,
    int LongestPathBytes,
    string LongestPath,
    int DeepestDepth,
    IReadOnlyList<string> Problems,
    string? Suggestion)
{
    /// <summary>给用户的一句话结论(向导文案)。</summary>
    public string Summary => Ok
        ? $"路径预算正常(最长 {LongestPathBytes} 字节 / 最深 {DeepestDepth} 层)"
        : string.Join(";", Problems);
}

/// <summary>路径预算检查器(纯函数)。</summary>
public static class PathBudget
{
    /// <summary>与服务端 6.4 同源:单个名字与完整路径都按 240 字节计。</summary>
    public const int MaxPathBytes = 240;

    /// <summary>与服务端 6.4 同源:目录深度上限(根为 0 层)。</summary>
    public const int MaxDepth = 31;

    /// <summary>
    /// 预检:<paramref name="rootPath"/> 作同步根时,这批相对路径能不能过。
    /// </summary>
    /// <param name="rootPath">
    /// 本地同步根(只用于提示"换个更浅的根")。**不参与字节预算** ——
    /// 服务端看到的是空间内的相对路径,本地根多深与服务端无关
    /// (但本地根太深会让 Windows 侧路径过长,那是 DE-D-16 的事,这里一并给提示)。
    /// </param>
    /// <param name="probes">远程树里待检查的相对路径(通常抽样最长的那些)。</param>
    public static PathBudgetReport Check(string rootPath, IEnumerable<PathProbe> probes)
    {
        var problems = new List<string>();
        var longest = "";
        var longestBytes = 0;
        var deepest = 0;
        var any = false;

        foreach (var p in probes)
        {
            any = true;
            var bytes = Encoding.UTF8.GetByteCount(p.RelativePath);
            if (bytes > longestBytes)
            {
                longestBytes = bytes;
                longest = p.RelativePath;
            }
            deepest = Math.Max(deepest, p.Depth);
        }

        if (!any)
        {
            // 没有样本不该被当成"通过":它可能意味着"还没连上/还没拉到清单" —— 那就不该放行
            return new PathBudgetReport(false, 0, "", 0,
                new[] { "没有可检查的路径(尚未拉到远程清单?)" }, null);
        }

        if (longestBytes > MaxPathBytes)
        {
            problems.Add($"最长路径 {longestBytes} 字节超过上限 {MaxPathBytes}");
        }
        if (deepest > MaxDepth)
        {
            problems.Add($"目录深度 {deepest} 层超过上限 {MaxDepth}");
        }

        // Windows 侧提示(不阻塞):MAX_PATH=260,同步根长度 + 相对路径长度接近它时建议挪根
        var windowsHint = TryWindowsHint(rootPath, longestBytes);

        if (problems.Count > 0)
        {
            var suggestion = "把同步根换到更浅的位置(例如盘符根附近,如 D:\\NetDisk)";
            if (windowsHint is not null)
            {
                suggestion += ";" + windowsHint;
            }
            return new PathBudgetReport(false, longestBytes, longest, deepest, problems, suggestion);
        }

        return new PathBudgetReport(true, longestBytes, longest, deepest,
            Array.Empty<string>(), windowsHint);
    }

    private static string? TryWindowsHint(string rootPath, int longestRelativeBytes)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }
        var rootBytes = Encoding.UTF8.GetByteCount(rootPath.TrimEnd('\\'));
        // 260 是传统 MAX_PATH;留 12 字节余量给 `\` 与结尾
        if (rootBytes + longestRelativeBytes + 12 > 260)
        {
            return $"本地根较深(根 {rootBytes} 字节 + 最长相对路径 {longestRelativeBytes} 字节接近 MAX_PATH 260)," +
                   "建议换更浅的根";
        }
        return null;
    }
}
