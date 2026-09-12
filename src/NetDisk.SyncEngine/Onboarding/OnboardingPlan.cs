// 首次运行向导的「计划」与落骨架(DE-D-15 / 7.6 / 7.2 T2-6)。
//
// 向导给用户**三选**(7.2 T2-6):
//   ①**仅同步目录结构** —— 只读浏览模式:本地只落**目录骨架** + 一份 `_未下载说明.txt`,
//     文件在客户端文件树里以云朵图标 +「下载」按钮按需落地;
//   ②**限定子树** —— 只同步勾选的空间/子目录(防"新设备一次灌整库");
//   ③**带宽与并发上限** —— 首次可把并发降到 1(弱网/机械盘上「一次灌整库」会把机器拖死)。
//
// 这一层里有两条**不能含糊**的设计:
//
// **A. 结构模式绝不「冒充」可双击的同步目录(7.6 / V2.17 P1-6)**:骨架里**只允许有目录**
//    和那一份说明文件。绝不创建"与远端同名的 0 字节文件" —— 那种占位文件会让用户双击
//    打开一个空文档、以为文件坏了/被清空了("用户以为文件丢了"是这一条要防的事)。
//    真正的「双击即取用」是二期 CFAPI 占位文件的职责(7.7 S-4),一期**不提前认领**。
//
// **B. 落骨架必须幂等且不破坏本地既有内容**:用户可能第二次进向导、或从「全量」切到
//    "仅结构";此时本地已经有真实文件。落骨架只能**建目录 + 写说明文件**,
//    绝不能删除/覆盖/改名任何既有文件(那不是"切模式",那是数据丢失)。

using System.Text;
using NetDisk.ClientCore;
using NetDisk.SyncEngine.Transfer;

namespace NetDisk.SyncEngine.Onboarding;

/// <summary>首次同步的三选(7.2 T2-6)。</summary>
public enum FirstSyncChoice
{
    /// <summary>仅同步目录结构 = 只读浏览模式(按需下载)。</summary>
    StructureOnly,

    /// <summary>限定子树:只同步勾选的子树。</summary>
    SelectedSubtrees,

    /// <summary>全量,但限制带宽与并发(首次可降并发到 1)。</summary>
    FullWithThrottle,
}

/// <summary>远端树里的一条(容量预估与骨架都用它)。</summary>
public sealed record RemoteEntry(string RelativePath, bool IsDirectory, long Size, int Depth);

/// <summary>容量预估结果(向导展示;防灌满系统盘)。</summary>
public sealed record CapacityEstimate(long TotalBytes, int FileCount, int DirectoryCount)
{
    /// <summary>给用户看的一句话(带 GB 换算,避免"一串数字")。</summary>
    public string Summary =>
        $"约 {TotalBytes / 1024.0 / 1024.0 / 1024.0:0.##} GB /{FileCount} 个文件 /{DirectoryCount} 个目录";
}

/// <summary>向导产出的计划。</summary>
public sealed record OnboardingPlan
{
    public required FirstSyncChoice Choice { get; init; }

    /// <summary>本地同步根。</summary>
    public required string RootPath { get; init; }

    /// <summary>限定的子树(仅 <see cref="FirstSyncChoice.SelectedSubtrees"/> 用)。</summary>
    public IReadOnlyList<string> Subtrees { get; init; } = Array.Empty<string>();

    /// <summary>传输节流(并发与上下行限速),直接交给 <see cref="TransferQueue"/>。</summary>
    public TransferQueueOptions Transfer { get; init; } = new();

    public CapacityEstimate? Estimate { get; init; }

    public PathBudgetReport? PathBudget { get; init; }

    /// <summary>是否为只读浏览模式(客户端据此显示云朵图标 + 托盘常驻提示)。</summary>
    public bool IsReadOnlyBrowse => Choice == FirstSyncChoice.StructureOnly;

    /// <summary>校验:不通过时返回可读的原因(向导当场拦下,别等到同步跑到一半)。</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(RootPath))
        {
            problems.Add("同步根不能为空");
        }
        if (Transfer.MaxConcurrency < 1)
        {
            problems.Add("并发上限必须 ≥1");
        }
        if (Transfer.RateLimit.UploadBytesPerSecond < 0 || Transfer.RateLimit.DownloadBytesPerSecond < 0)
        {
            problems.Add("限速不能为负(0 = 不限速)");
        }
        if (Choice == FirstSyncChoice.SelectedSubtrees && Subtrees.Count == 0)
        {
            // 选了「限定子树」却一个都没勾:此时若放行,用户会得到一个「什么都不同步」的客户端,
            // 而界面上一切正常 —— 必须当场拦下。
            problems.Add("选择了限定子树,但没有勾选任何子树");
        }
        if (PathBudget is { Ok: false })
        {
            problems.Add(PathBudget.Summary + (PathBudget.Suggestion is null ? "" : $" → {PathBudget.Suggestion}"));
        }
        return problems;
    }
}

/// <summary>向导(纯逻辑 + 可注入的远端树来源,便于把三选与预估都跑出来)。</summary>
public sealed class OnboardingWizard
{
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<RemoteEntry>>> _listTree;

    public OnboardingWizard(Func<string, CancellationToken, Task<IReadOnlyList<RemoteEntry>>> listTree)
    {
        _listTree = listTree ?? throw new ArgumentNullException(nameof(listTree));
    }

    /// <summary>
    /// 生成计划:按三选计算**范围**、**容量预估**与**路径预算**。
    /// </summary>
    public async Task<OnboardingPlan> BuildAsync(
        FirstSyncChoice choice,
        string rootPath,
        IReadOnlyList<string>? subtrees = null,
        TransferQueueOptions? transfer = null,
        CancellationToken ct = default)
    {
        var tree = await _listTree(rootPath, ct).ConfigureAwait(false);

        // 范围过滤:限定子树只统计/落地所选子树(前缀匹配,含子树自身)
        var selected = subtrees ?? Array.Empty<string>();
        var inScope = choice == FirstSyncChoice.SelectedSubtrees
            ? tree.Where(e => selected.Any(s => e.RelativePath.Equals(s, StringComparison.OrdinalIgnoreCase)
                                                || e.RelativePath.StartsWith(s + "/", StringComparison.OrdinalIgnoreCase)
                                                || e.RelativePath.StartsWith(s + "\\", StringComparison.OrdinalIgnoreCase)))
                  .ToList()
            : tree.ToList();

        // 容量预估**按所选范围**:限定子树时若按全库报,用户会以为"选了 5GB 却要下 200GB"
        var estimate = new CapacityEstimate(
            inScope.Where(e => !e.IsDirectory).Sum(e => e.Size),
            inScope.Count(e => !e.IsDirectory),
            inScope.Count(e => e.IsDirectory));

        // 路径预算按**留下的最长样本**算(全库最长即可,不必全量排序)
        var probes = inScope
            .OrderByDescending(e => Encoding.UTF8.GetByteCount(e.RelativePath))
            .Take(50)
            .Select(e => new PathProbe(e.RelativePath, e.Depth));

        var budget = PathBudget.Check(rootPath, probes);

        return new OnboardingPlan
        {
            Choice = choice,
            RootPath = rootPath,
            Subtrees = selected,
            Transfer = transfer ?? DefaultTransferFor(choice),
            Estimate = estimate,
            PathBudget = budget,
        };
    }

    /// <summary>
    /// 三选各自的默认节流:首次「全量」默认把并发降到 **1**(7.2 T2-6 的原话),
    /// 因为新设备第一次灌库时网络与磁盘都是瓶颈,并发 3 只会让两边都更慢、
    /// 还把用户机器拖住(而「仅结构/限定子树」的量本来就小,保持默认 3)。
    /// </summary>
    public static TransferQueueOptions DefaultTransferFor(FirstSyncChoice choice)
        => choice == FirstSyncChoice.FullWithThrottle
            ? new TransferQueueOptions { MaxConcurrency = 1 }
            : new TransferQueueOptions();
}

/// <summary>落骨架(仅结构模式;幂等且不碰既有内容)。</summary>
public sealed class SkeletonBuilder
{
    /// <summary>说明文件名(与 7.6 文案一致,写死成常量以免各处拼错)。</summary>
    public const string MarkerFileName = "_未下载说明.txt";

    /// <summary>说明文件内容(讲清"为什么目录里有东西、文件在哪、怎么拿")。</summary>
    public const string MarkerContent =
        "此目录由网盘客户端以「仅同步目录结构」模式创建,用于只读浏览。\n" +
        "此模式不落地文件内容:文件请在本客户端的文件树中点击「下载」按需获取。\n" +
        "因此这里只有目录骨架,不会出现与远端同名的空文件(那会被误当成「文件损坏」)。\n";

    /// <summary>
    /// 按远端树落目录骨架 + 一份说明文件。
    ///
    /// 返回实际创建的目录数与是否写入了说明文件(便于向导展示"本次创建了什么")。
    /// </summary>
    public (int CreatedDirectories, bool WroteMarker) Apply(OnboardingPlan plan, IReadOnlyList<RemoteEntry> tree)
    {
        if (!plan.IsReadOnlyBrowse)
        {
            // 非结构模式:只确保同步根本身存在,其余内容由同步引擎按需落盘
            Directory.CreateDirectory(plan.RootPath);
            return (0, false);
        }

        var created = 0;
        foreach (var entry in tree.Where(e => e.IsDirectory))
        {
            var full = CombineUnderRoot(plan.RootPath, entry.RelativePath);
            // \\?\(DE-D-16):骨架里的深层目录同样可能超 MAX_PATH
            var extended = Paths.LongPath.ToExtended(full);
            if (!Directory.Exists(extended))
            {
                Directory.CreateDirectory(extended);
                created++;
            }
        }
        Directory.CreateDirectory(plan.RootPath);

        // **只写这一个说明文件**:绝不创建与远端同名的 0 字节占位文件(见文件头 A 条)
        var markerPath = Path.Combine(plan.RootPath, MarkerFileName);
        var content = MarkerContent;
        if (!File.Exists(markerPath) || File.ReadAllText(markerPath) != content)
        {
            File.WriteAllText(markerPath, content, new UTF8Encoding(false));
            return (created, true);
        }
        return (created, false);
    }

    /// <summary>把相对路径挂到同步根下(避免用 Path.Combine 时的根吞并问题)。</summary>
    private static string CombineUnderRoot(string root, string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var path = root;
        foreach (var p in parts)
        {
            path = Path.Combine(path, p);
        }
        return path;
    }
}
