// 本地名字兜底断言(DE-D-16 ②)。
//
// 场景:服务端有一个名字,客户端要把它落到本地磁盘。而"服务端能收的名字"与
// "Windows 能建的文件名"**不是同一集合** —— Windows 另有非法字符(`<>:"/\|?*` 与控制字符)、
// 保留设备名(`CON`、`PRN`、`COM1`…)、不能以点或空格结尾、单段长度上限 255 字符。
//
// 关键纪律:**断言失败就报错上报,禁止静默改名**。
// 为什么不许"顺手把 `a?b.txt` 改成 `a_b.txt`":
//   - 本地名字一旦与远端不同,下一次同步就会把它当成"本地新增"再传一份到服务端,
//     于是服务端出现两个长得几乎一样的文件,而用户完全不知道哪个是哪个;
//   - 用户对本地文件做的修改会挂在这个"改名后"的文件上,而它在远端没有对应项 ——
//     冲突解决、重命名识别(DE-D-11)全部失去依据;
//   - 更糟的是它**不会报错**:用户只看到"文件多了一个",没有任何线索指向"客户端改了名字"。
// 所以这里只做两件事:**检查**与**抛出可上报的错误**。没有 sanitize,没有 rename,
// 没有"自动修复"(检查器用反射断言这一点:本类型不存在任何改名/清洗能力)。

using System.Text;
using NetDisk.ClientCore;

namespace NetDisk.SyncEngine.Paths;

/// <summary>本地名字不可用的问题描述(可直接上报/展示)。</summary>
public sealed record LocalNameProblem(string Name, string Reason, IReadOnlyList<char> InvalidChars);

/// <summary>本地名字断言失败(调用方必须**上报**,不得静默改名后继续)。</summary>
public sealed class LocalNameException : Exception
{
    public LocalNameException(LocalNameProblem problem)
        : base($"本地文件名不可用:{problem.Reason}(名字:{problem.Name})。" +
               "请重命名后重试;客户端不会自动改名(自动改名会让本地与远端名字不一致)。")
    {
        Problem = problem;
    }

    public LocalNameProblem Problem { get; }
}

/// <summary>本地名字兜底断言。</summary>
public static class LocalNameGuard
{
    /// <summary>NTFS 单段长度上限(字符)。与"240 字节的 namepolicy 上限"是**两回事**,两者都要过。</summary>
    public const int MaxComponentChars = 255;

    /// <summary>Windows 保留设备名(不区分大小写;带扩展名也一样被拒,如 `CON.txt`)。</summary>
    private static readonly string[] ReservedDeviceNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// 检查名字能否在本地建成文件。
    /// </summary>
    public static bool IsCreatable(string? name, out LocalNameProblem? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(name))
        {
            problem = new LocalNameProblem(name ?? "", "名字为空", Array.Empty<char>());
            return false;
        }
        if (name is "." or "..")
        {
            problem = new LocalNameProblem(name, "名字是相对目录记号", Array.Empty<char>());
            return false;
        }

        // ① 平台非法字符(`Path.GetInvalidFileNameChars` 是**兜底** —— 它随平台变化,
        //    所以不能只靠它:下面还有保留名/结尾点空格/长度三条 Windows 专属规则)
        var invalid = name.Where(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0).ToArray();
        if (invalid.Length > 0)
        {
            problem = new LocalNameProblem(name, "含本地文件系统不允许的字符", invalid);
            return false;
        }

        // ② 显式拦路径分隔符:`GetInvalidFileNameChars` 在不同平台对 `/` 的处理不一致,
        //    而"名字里带分隔符"在网盘语义里一定是错的(它会被当成目录层级)
        if (name.Contains('/') || name.Contains('\\'))
        {
            problem = new LocalNameProblem(name, "名字里含路径分隔符", new[] { '/', '\\' });
            return false;
        }

        // ③ Windows 不允许以点或空格结尾(`a.` 实际会被系统静默截断成 `a` ——
        //    那正是"客户端以为建了 a. 、实际是 a"的静默不一致)
        if (name.EndsWith(' ') || name.EndsWith('.'))
        {
            problem = new LocalNameProblem(name, "名字不能以点或空格结尾", Array.Empty<char>());
            return false;
        }

        // ④ 保留设备名(带扩展名也算:`CON.txt` 在 Windows 上同样打不开)
        var stem = name.Split('.')[0];
        if (ReservedDeviceNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
        {
            problem = new LocalNameProblem(name, "与 Windows 保留设备名冲突", Array.Empty<char>());
            return false;
        }

        // ⑤ 单段长度(NTFS 255 字符;注意是**字符**不是字节)
        if (name.Length > MaxComponentChars)
        {
            problem = new LocalNameProblem(name, $"名字超过 {MaxComponentChars} 个字符", Array.Empty<char>());
            return false;
        }

        // ⑥ 与服务端共享的名字规则(保留名/尾点空格/路径分隔符/240 字节上限…)——
        //    客户端要在**上传/落地之前**就与服务端同口径,否则"传上去才被拒"
        if (!NameRules.IsValidName(name))
        {
            problem = new LocalNameProblem(name,
                $"不符合与服务端共享的名字规则(UTF-8 长度 {Encoding.UTF8.GetByteCount(name)} 字节)", Array.Empty<char>());
            return false;
        }

        return true;
    }

    /// <summary>
    /// 断言名字可用;**不可用就抛 <see cref="LocalNameException"/> 让调用方上报**。
    /// 刻意不提供"返回一个改好的名字"的重载:那会被顺手用成静默改名。
    /// </summary>
    public static void EnsureCreatable(string? name)
    {
        if (!IsCreatable(name, out var problem))
        {
            throw new LocalNameException(problem!);
        }
    }
}
