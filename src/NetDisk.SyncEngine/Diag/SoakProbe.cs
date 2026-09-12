// soak 采样与判定(DE-D-21)。
//
// 验收原文:"24h 连续运行无泄漏(句柄/内存曲线平稳)"。
//
// 这一项**没法靠"跑一遍看起来没事"来交付**,因为泄漏的特征恰恰是"一次运行看不出"。
// 所以拆成三件可验证的事:
//
//	① **采样必须有效**:句柄数来自内核(`GetProcessHandleCount`),托管内存来自
//	   `GC.GetTotalMemory`,`工作集` 来自进程对象。采样失败/恒为 0 必须判失败 ——
//	   否则一个坏掉的采样器会让曲线"完美平稳"。
//	② **判定必须能对合成数据判失败**:窗口中位数比较(句柄/工作集)+ 线性回归
//	   斜率(托管内存),注入"每轮 +1 句柄"必须被抓到。
//	③ **时长必须被计入判定**:3 个样本也能"曲线平稳",但那不叫 soak。判定里必须
//	   带"实测跨度 ≥ 要求跨度",24h 就是 24h,压缩冒烟必须显式声明更短的跨度
//	   (诚实:压缩跑过的不是 24h 验收那条)。
//
// 曲线为什么用"中位数 + 窗口"而不是首末两个点:句柄与内存都是**锯齿**的
// (GC、连接池、临时文件),单点比较会把正常抖动当成泄漏;而泄漏是**持续单向**
// 的,所以比较前后段的中位数并给一个固定的松弛量,既不会被抖动误伤,又能抓住
// 单向增长。

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace NetDisk.SyncEngine.Diag;

/// <summary>一次采样。</summary>
/// <param name="TimestampMs">Unix 毫秒时间戳。</param>
/// <param name="HandleCount">进程句柄数(内核口径;-1 = 采样失败)。</param>
/// <param name="ManagedBytes">托管堆字节数。</param>
/// <param name="WorkingSetBytes">工作集字节数。</param>
/// <param name="ThreadCount">线程数。</param>
/// <param name="ViolationCount">累计线程纪律违规数。</param>
public readonly record struct SoakSample(
    long TimestampMs,
    int HandleCount,
    long ManagedBytes,
    long WorkingSetBytes,
    int ThreadCount,
    int ViolationCount);

/// <summary>判定阈值。</summary>
/// <param name="MinSamples">最少样本数(样本太少无法谈"平稳")。</param>
/// <param name="MaxHandleGrowth">前后段句柄中位数之差的上限。</param>
/// <param name="MaxManagedGrowthBytes">前后段托管内存中位数之差的上限(绝对量,任何跨度都适用)。</param>
/// <param name="MaxManagedBytesPerHour">托管内存线性增长的每小时上限(仅在跨度 ≥ <see cref="MinSpanForRate"/> 时评估)。</param>
/// <param name="MaxWorkingSetGrowthBytes">前后段工作集中位数之差的上限。</param>
public sealed record SoakThresholds(
    int MinSamples = 12,
    int MaxHandleGrowth = 32,
    long MaxManagedGrowthBytes = 64L * 1024 * 1024,
    long MaxManagedBytesPerHour = 8L * 1024 * 1024,
    long MaxWorkingSetGrowthBytes = 256L * 1024 * 1024)
{
    /// <summary>默认阈值(24h 客户端 soak)。</summary>
    public static SoakThresholds Default { get; } = new();

    /// <summary>
    /// "每小时速率"这类判据能被信任的最小跨度。
    ///
    /// 为什么需要它:压缩冒烟(20 秒)里托管堆有约 2MB 的**预热**漂移(GC 代际扩容、
    /// 连接池、JIT),线性回归把它外推成"340 MB/小时" —— 数字很大,结论却是假的。
    /// 速率只有在**时间足够长**时才是速率;短窗口只能用**绝对量**判,
    /// 否则判据自己就在骗人(这正是"曲线看起来会说话"的典型陷阱)。
    /// </summary>
    public static TimeSpan MinSpanForRate { get; } = TimeSpan.FromHours(1);
}

/// <summary>判定结论。</summary>
/// <param name="Passed">是否通过。</param>
/// <param name="Reasons">不通过的逐条原因(通过时为空)。</param>
/// <param name="Summary">一行摘要(含实测数字,便于贴进验收记录)。</param>
public sealed record SoakVerdict(bool Passed, IReadOnlyList<string> Reasons, string Summary);

/// <summary>进程指标采样器。</summary>
public static class SoakProbe
{
    private static readonly Process Self = Process.GetCurrentProcess();

    /// <summary>CSV 表头。</summary>
    public const string CsvHeader = "timestamp_ms,handles,managed_bytes,working_set_bytes,threads,violations";

    /// <summary>取一次真实采样。采样失败时句柄数为 -1(判定会因此失败,而不是静默通过)。</summary>
    public static SoakSample Capture()
    {
        var handles = -1;
        try
        {
            if (GetProcessHandleCount(GetCurrentProcess(), out var n))
            {
                handles = (int)n;
            }
        }
        catch (Exception)
        {
            handles = -1;
        }

        long managed;
        try
        {
            managed = GC.GetTotalMemory(forceFullCollection: false);
        }
        catch (Exception)
        {
            managed = -1;
        }

        long workingSet = -1;
        int threads = -1;
        try
        {
            Self.Refresh();
            workingSet = Self.WorkingSet64;
            threads = Self.Threads.Count;
        }
        catch (Exception)
        {
            // 保留 -1:采不到工作集就不该声称"工作集平稳"
        }

        return new SoakSample(
            TimestampMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            HandleCount: handles,
            ManagedBytes: managed,
            WorkingSetBytes: workingSet,
            ThreadCount: threads,
            ViolationCount: ThreadDiscipline.ViolationCount);
    }

    /// <summary>把采样写成一行 CSV(不变文化,便于脚本解析)。</summary>
    public static string ToCsvRow(SoakSample s) => string.Join(',',
        s.TimestampMs.ToString(CultureInfo.InvariantCulture),
        s.HandleCount.ToString(CultureInfo.InvariantCulture),
        s.ManagedBytes.ToString(CultureInfo.InvariantCulture),
        s.WorkingSetBytes.ToString(CultureInfo.InvariantCulture),
        s.ThreadCount.ToString(CultureInfo.InvariantCulture),
        s.ViolationCount.ToString(CultureInfo.InvariantCulture));

    /// <summary>解析 CS V 行(检查器与脚本共用同一套口径)。</summary>
    public static bool TryParseCsvRow(string line, out SoakSample sample)
    {
        sample = default;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }
        var parts = line.Trim().Split(',');
        if (parts.Length != 6)
        {
            return false;
        }
        if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) ||
            !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ||
            !long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) ||
            !int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ||
            !int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
        {
            return false;
        }
        sample = new SoakSample(ts, h, m, w, t, v);
        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessHandleCount(IntPtr process, out uint handleCount);
}

/// <summary>soak 结论判定(纯函数,可用合成数据断言)。</summary>
public static class SoakAnalyzer
{
    /// <summary>
    /// 判定一组采样。
    /// </summary>
    /// <param name="samples">按时间升序的采样。</param>
    /// <param name="requiredSpan">要求的连续运行跨度(默认 24h)。</param>
    /// <param name="thresholds">阈值,空则用默认。</param>
    public static SoakVerdict Analyze(
        IReadOnlyList<SoakSample> samples,
        TimeSpan? requiredSpan = null,
        SoakThresholds? thresholds = null)
    {
        var t = thresholds ?? SoakThresholds.Default;
        var span = requiredSpan ?? TimeSpan.FromHours(24);
        var reasons = new List<string>();

        if (samples is null || samples.Count < t.MinSamples)
        {
            var got = samples?.Count ?? 0;
            return new SoakVerdict(false,
                new[] { $"样本不足:只有 {got} 个,判「平稳」至少需要 {t.MinSamples} 个(曲线平稳与否不能用一两个点证明)" },
                $"样本 {got}/{t.MinSamples},未判定");
        }

        var first = samples[0];
        var last = samples[^1];

        // ---- ① 采样必须有效 ----
        var handleMin = int.MaxValue;
        foreach (var s in samples)
        {
            if (s.HandleCount < handleMin)
            {
                handleMin = s.HandleCount;
            }
        }
        if (handleMin < 0)
        {
            reasons.Add("句柄采样失败(GetProcessHandleCount 返回失败):采不到就无法证明「句柄不泄漏」");
        }
        else if (handleMin == 0)
        {
            reasons.Add("句柄数恒为 0:采样没有生效(一个坏掉的采样器会让曲线看起来完美平稳)");
        }
        foreach (var s in samples)
        {
            if (s.ManagedBytes < 0)
            {
                reasons.Add("托管内存采样失败(GC.GetTotalMemory 抛异常)");
                break;
            }
        }
        foreach (var s in samples)
        {
            if (s.WorkingSetBytes <= 0)
            {
                reasons.Add("工作集采样失败:为空值,不能声称「工作集平稳」");
                break;
            }
        }

        // ---- ② 时长必须够 ----
        var actualSpan = TimeSpan.FromMilliseconds(Math.Max(0, last.TimestampMs - first.TimestampMs));
        if (actualSpan < span)
        {
            reasons.Add(
                $"连续运行时长不足:要求 {FormatSpan(span)},实测 {FormatSpan(actualSpan)}(压缩冒烟跑过的不是 24h 验收那条)");
        }

        // ---- ③ 违规计数:必须为 0,且只能增 ----
        if (last.ViolationCount != 0)
        {
            reasons.Add($"线程纪律违规 {last.ViolationCount} 条(验收要求为 0)");
        }
        for (var i = 1; i < samples.Count; i++)
        {
            if (samples[i].ViolationCount < samples[i - 1].ViolationCount)
            {
                reasons.Add("违规计数出现回退:计数被重置过(重置计数即可掩盖违规,故本身视为违规)");
                break;
            }
        }

        // ---- ④ 句柄:前后段中位数之差 ----
        var third = Math.Max(1, samples.Count / 3);
        var headHandles = Median(samples.Take(third).Select(s => (double)s.HandleCount));
        var tailHandles = Median(samples.Skip(samples.Count - third).Select(s => (double)s.HandleCount));
        var handleGrowth = tailHandles - headHandles;

        // ---- ⑤ 托管内存:绝对量(任何跨度)+ 速率(仅长跨度) ----
        var headManaged = Median(samples.Take(third).Select(s => (double)s.ManagedBytes));
        var tailManaged = Median(samples.Skip(samples.Count - third).Select(s => (double)s.ManagedBytes));
        var managedGrowth = tailManaged - headManaged;
        var managedSlopePerHour = SlopePerHour(samples.Select(s => ((double)s.TimestampMs, (double)s.ManagedBytes)).ToList());
        var rateIsMeaningful = actualSpan >= SoakThresholds.MinSpanForRate;

        // ---- ⑥ 工作集:前后段中位数之差 ----
        var headWs = Median(samples.Take(third).Select(s => (double)s.WorkingSetBytes));
        var tailWs = Median(samples.Skip(samples.Count - third).Select(s => (double)s.WorkingSetBytes));
        var wsGrowth = tailWs - headWs;

        if (handleGrowth > t.MaxHandleGrowth)
        {
            reasons.Add($"句柄泄漏:前段中位数 {headHandles:F0} → 后段 {tailHandles:F0}(+{handleGrowth:F0},上限 +{t.MaxHandleGrowth})");
        }
        if (managedGrowth > t.MaxManagedGrowthBytes)
        {
            reasons.Add($"托管内存增长:前段中位数 {headManaged / 1024.0 / 1024.0:F1} MB → 后段 {tailManaged / 1024.0 / 1024.0:F1} MB(上限 +{t.MaxManagedGrowthBytes / 1024.0 / 1024.0:F0} MB)");
        }
        if (rateIsMeaningful && managedSlopePerHour > t.MaxManagedBytesPerHour)
        {
            reasons.Add($"托管内存持续增长:{managedSlopePerHour / 1024.0 / 1024.0:F2} MB/小时(上限 {t.MaxManagedBytesPerHour / 1024.0 / 1024.0:F0} MB/小时)");
        }
        if (wsGrowth > t.MaxWorkingSetGrowthBytes)
        {
            reasons.Add($"工作集增长:前段中位数 {headWs / 1024.0 / 1024.0:F1} MB → 后段 {tailWs / 1024.0 / 1024.0:F1} MB(上限 +{t.MaxWorkingSetGrowthBytes / 1024.0 / 1024.0:F1} MB)");
        }

        var summary = string.Join("; ", new[]
        {
            $"样本 {samples.Count}",
            $"跨度 {FormatSpan(actualSpan)}",
            $"句柄 {headHandles:F0}→{tailHandles:F0}({handleGrowth:+0;-0;0})",
            $"托管 {headManaged / 1024.0 / 1024.0:F1}→{tailManaged / 1024.0 / 1024.0:F1} MB" +
                (rateIsMeaningful ? $"({managedSlopePerHour / 1024.0 / 1024.0:+0.00;-0.00;0.00} MB/h)" : "(跨度不足 1h,不看速率)"),
            $"工作集 {headWs / 1024.0 / 1024.0:F1}→{tailWs / 1024.0 / 1024.0:F1} MB",
            $"违规 {last.ViolationCount}",
        });

        return new SoakVerdict(reasons.Count == 0, reasons, summary);
    }

    private static string FormatSpan(TimeSpan span) =>
        span.TotalHours >= 1
            ? $"{span.TotalHours:F1}h"
            : $"{span.TotalMinutes:F1}min";

    private static double Median(IEnumerable<double> values)
    {
        var a = values.ToArray();
        if (a.Length == 0)
        {
            return 0;
        }
        Array.Sort(a);
        return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2.0;
    }

    /// <summary>最小二乘斜率,换算成"每小时"。</summary>
    private static double SlopePerHour(IReadOnlyList<(double X, double Y)> points)
    {
        if (points.Count < 2)
        {
            return 0;
        }
        double mx = 0, my = 0;
        foreach (var (x, y) in points)
        {
            mx += x;
            my += y;
        }
        mx /= points.Count;
        my /= points.Count;

        double num = 0, den = 0;
        foreach (var (x, y) in points)
        {
            num += (x - mx) * (y - my);
            den += (x - mx) * (x - mx);
        }
        if (den == 0)
        {
            return 0;
        }
        var perMs = num / den;
        return perMs * 3_600_000.0;
    }
}

/// <summary>soak 采样记录器:后台线程按固定间隔采样并追加 CSV。</summary>
public sealed class SoakRecorder : IDisposable
{
    private readonly string _csvPath;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<SoakSample> _samples = new();
    private readonly object _gate = new();
    private readonly Thread _worker;
    private bool _disposed;

    private SoakRecorder(string csvPath, TimeSpan interval, int? sampleCount)
    {
        _csvPath = csvPath;
        _interval = interval;
        _worker = new Thread(() => Loop(sampleCount))
        {
            IsBackground = true, // 采样线程不许阻止进程退出
            Name = "netdisk-soak",
        };
    }

    /// <summary>已采集的样本(只读快照)。</summary>
    public IReadOnlyList<SoakSample> Samples
    {
        get { lock (_gate) { return _samples.ToArray(); } }
    }

    /// <summary>CSV 落盘路径。</summary>
    public string CsvPath => _csvPath;

    /// <summary>
    /// 按环境变量启动:<c>NETDISK_SOAK=1</c> 打开,<c>NETDISK_SOAK_CSV</c> 指定落盘路径,
    /// <c>NETDISK_SOAK_SECONDS</c> 指定间隔(默认 30s)。未打开时返回 null。
    /// </summary>
    public static SoakRecorder? StartFromEnvironment()
    {
        var on = Environment.GetEnvironmentVariable("NETDISK_SOAK");
        if (!string.Equals(on, "1", StringComparison.Ordinal) &&
            !string.Equals(on, "true", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var csv = Environment.GetEnvironmentVariable("NETDISK_SOAK_CSV");
        if (string.IsNullOrWhiteSpace(csv))
        {
            csv = Path.Combine(Path.GetTempPath(), "netdisk-soak.csv");
        }

        var seconds = 30.0;
        if (double.TryParse(Environment.GetEnvironmentVariable("NETDISK_SOAK_SECONDS"),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
        {
            seconds = parsed;
        }

        // 立刻采一次:soak 的起点基线不能等第一个间隔过去才有
        return Start(csv, TimeSpan.FromSeconds(seconds), sampleCount: null, sampleImmediately: true);
    }

    /// <summary>启动记录器。</summary>
    public static SoakRecorder Start(string csvPath, TimeSpan interval, int? sampleCount, bool sampleImmediately = true)
    {
        var r = new SoakRecorder(csvPath, interval, sampleCount);
        if (sampleImmediately)
        {
            r.SampleOnce();
        }
        r._worker.Start();
        return r;
    }

    private void Loop(int? sampleCount)
    {
        try
        {
            var taken = Samples.Count;
            while (!_cts.IsCancellationRequested)
            {
                if (sampleCount.HasValue && taken >= sampleCount.Value)
                {
                    return;
                }
                if (_cts.Token.WaitHandle.WaitOne(_interval))
                {
                    return;
                }
                SampleOnce();
                taken++;
            }
        }
        catch (Exception)
        {
            // 采样线程绝不把异常抛给宿主:soak 是"观察",观察本身不能影响被测进程
        }
    }

    /// <summary>采一次并追加落盘。</summary>
    public SoakSample SampleOnce()
    {
        var s = SoakProbe.Capture();
        lock (_gate)
        {
            _samples.Add(s);
        }
        try
        {
            var dir = Path.GetDirectoryName(_csvPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var needHeader = !File.Exists(_csvPath) || new FileInfo(_csvPath).Length == 0;
            using var w = new StreamWriter(_csvPath, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (needHeader)
            {
                w.WriteLine(SoakProbe.CsvHeader);
            }
            w.WriteLine(SoakProbe.ToCsvRow(s));
            w.Flush(); // 每次落盘:进程若崩,已采到的数据仍可用
        }
        catch (Exception)
        {
            // 落盘失败不影响采样(样本仍在内存里,判定用内存那份)
        }
        return s;
    }

    /// <summary>用已采样本做判定。</summary>
    public SoakVerdict Verdict(TimeSpan requiredSpan, SoakThresholds? thresholds = null) =>
        SoakAnalyzer.Analyze(Samples, requiredSpan, thresholds);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            _cts.Cancel();
            _worker.Join(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // 退出路径不抛
        }
        _cts.Dispose();
    }
}
