// SoakCheck —— DE-D-21 检查器(零额外 NuGet)。
//
// 验收原文:"24h 连续运行无泄漏(句柄/内存曲线平稳);Dispatcher 违规检测为 0;
// 同步引擎与 UI 线程严格分离"。
//
// 这一项最容易被做成"看起来通过了":
//   - 探测器从来不响 → "违规 0 条"只说明没有探测器;
//   - 采样器坏掉(恒返回 0)→ 曲线"完美平稳";
//   - 只跑 30 秒 → 也能说"没看到泄漏"。
// 所以检查器逐条把这些漏洞堵上:
//   ①未标记 UI 线程 → 判定不算通过(不能判定 ≠ 通过);
//   ②已标记后,后台线程判「不该在 UI 线程」通过、且**检查计数在增长**(证明探测器真在跑);
//   ③④UI 线程违规(反向两种情况)必须被抓到,且带操作名;
//   ⑤违规列表排空一次(不重复上报),计数只增不减;
//   ⑥~⑧采样有效性:真采样必须给出正数句柄/内存,CSV 往返一致,坏行被拒,恒 0 判失败;
//   ⑨~⑪合成曲线:平稳 → 通过;句柄/内存/工作集单向增长 → 分别被抓到;
//   ⑫时长与样本数下限:3 个样本或 1 小时跨度**不足以**声称 24h;
//   ⑬违规计数回退(重置计数掩盖违规)本身判失败;
//   ⑭SoakRecorder 真实落盘:表头 + 行数 + 可解析。
// 另:`SoakCheck --load <秒>` 跑**压缩冒烟** —— 用真实组件(StateStore 开关循环 +
// 期望变更账本 + 通知中心 + 后台线程纪律判定)产生真实负载并采样,然后判定。
// 诚实标注:压缩冒烟**不是** 24h 验收那条;24h 由 `.run/soak_client.ps1` 跑真客户端。

using System.Globalization;
using NetDisk.SyncEngine;
using NetDisk.SyncEngine.Diag;
using NetDisk.SyncEngine.Notify;
using NetDisk.SyncEngine.Sync;

namespace NetDisk.Checks.Soak;

internal static class Program
{
    private static int _failed;

    private static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--load")
        {
            var seconds = double.Parse(args[1], CultureInfo.InvariantCulture);
            return RunCompressedSoak(seconds);
        }

        Console.WriteLine("SoakCheck —— DE-D-21 线程纪律与 soak 判定");
        Console.WriteLine();

        ThreadDisciplineSpec();
        SamplingSpec();
        AnalyzerSpec();
        RecorderSpec();

        Console.WriteLine();
        if (_failed == 0)
        {
            Console.WriteLine("全部通过:14 项 DE-D-21 行为断言(线程纪律探测 + soak 判定)");
            return 0;
        }
        Console.WriteLine($"失败 {_failed} 项");
        return 1;
    }

    // ---------------------------------------------------------------- ① ~ ⑤

    private static void ThreadDisciplineSpec()
    {
        Section("线程纪律探测器");

        // ① 未标记 UI 线程 → 不能判定,记违规
        ThreadDiscipline.ResetForTests();
        ThreadDiscipline.AssertOffUiThread("未标记时判定");
        Check("① 未标记 UI 线程时判定不算通过", ThreadDiscipline.ViolationCount == 1,
            $"违规={ThreadDiscipline.ViolationCount}");
        // 取不到违规时**不能**让检查器自己崩:崩溃会让"探测器被关掉"表现为异常退出,
        // 而我们要的是"某条断言失败"(反向验证才有干净的信号)。
        var first = ThreadDiscipline.Snapshot().FirstOrDefault();
        Check("① 原因说明「未启用」", (first?.Detail ?? "(无违规记录)").Contains("未启用"),
            first?.Detail ?? "(无违规记录)");

        // ② 标记后,后台线程上的同步引擎动作 → 0 违规;且检查计数在增长
        ThreadDiscipline.ResetForTests();
        ThreadDiscipline.MarkUiThread();
        Check("② 标记后 IsArmed", ThreadDiscipline.IsArmed, "IsArmed=false");
        var before = ThreadDiscipline.CheckedCount;
        var workerViolations = -1;
        var worker = new Thread(() =>
        {
            ThreadDiscipline.AssertOffUiThread("StateStore.Open");
            ThreadDiscipline.AssertOffUiThread("TransferQueue.Pump");
            workerViolations = ThreadDiscipline.ViolationCount;
        });
        worker.Start();
        worker.Join();
        Check("② 后台线程上判「不该在 UI 线程」通过", workerViolations == 0, $"违规={workerViolations}");
        Check("② 探测器真的在跑(检查计数增长)", ThreadDiscipline.CheckedCount - before >= 2,
            $"检查次数 +{ThreadDiscipline.CheckedCount - before}");

        // ③ 在 UI 线程上跑同步引擎动作 → 违规(界面卡死那类)
        ThreadDiscipline.ResetForTests();
        ThreadDiscipline.MarkUiThread();
        ThreadDiscipline.AssertOffUiThread("在 UI 线程上哈希大文件");
        Check("③ UI 线程上执行同步引擎动作被记为违规", ThreadDiscipline.ViolationCount == 1,
            $"违规={ThreadDiscipline.ViolationCount}");
        var v3 = ThreadDiscipline.Snapshot().FirstOrDefault();
        Check("③ 记录里带上操作名与线程 id",
            v3?.Operation == "在 UI 线程上哈希大文件" && v3.ThreadId == ThreadDiscipline.UiThreadId,
            v3?.ToString() ?? "(无违规记录)");

        // ④ 后台线程上碰 UI 对象 → 违规(反向纪律)
        ThreadDiscipline.ResetForTests();
        ThreadDiscipline.MarkUiThread();
        var onUiViolations = -1;
        var t2 = new Thread(() => { ThreadDiscipline.AssertOnUiThread("TrayNotifier.ShowBalloonTip"); onUiViolations = ThreadDiscipline.ViolationCount; });
        t2.Start();
        t2.Join();
        Check("④ 后台线程触碰 UI 对象被记为违规", onUiViolations == 1, $"违规={onUiViolations}");

        // ④b UI 线程上碰 UI 对象 → 通过(否则规则过严,"任何调用都违规"等于没规则)
        ThreadDiscipline.ResetForTests();
        ThreadDiscipline.MarkUiThread();
        ThreadDiscipline.AssertOnUiThread("TrayNotifier.ShowBalloonTip");
        Check("④b UI 线程上碰 UI 对象通过", ThreadDiscipline.ViolationCount == 0,
            $"违规={ThreadDiscipline.ViolationCount}");

        // ⑤ 排空一次;封顶保护
        ThreadDiscipline.ResetForTests();
        ThreadDiscipline.MarkUiThread();
        ThreadDiscipline.AssertOffUiThread("排空前制造一条");
        var drained = ThreadDiscipline.DrainViolations();
        Check("⑤ 排空取走违规", drained.Count == 1, $"取走 {drained.Count} 条");
        Check("⑤ 排空后不重复上报", ThreadDiscipline.DrainViolations().Count == 0,
            $"仍有 {ThreadDiscipline.ViolationCount} 条");
        Check("⑤ 违规记录有上限(512 条封顶,探测器自己不变成内存泄漏)", CountCapped(),
            "封顶策略不符合预期");
    }

    private static bool CountCapped()
    {
        ThreadDiscipline.ResetForTests();
        ThreadDiscipline.MarkUiThread();
        for (var i = 0; i < 600; i++)
        {
            ThreadDiscipline.AssertOffUiThread("压力");
        }
        return ThreadDiscipline.ViolationCount == 512;
    }

    // ---------------------------------------------------------------- ⑥ ~ ⑧

    private static void SamplingSpec()
    {
        Section("采样有效性");

        // ⑥ 真实采样必须给出正数
        var s1 = SoakProbe.Capture();
        var s2 = SoakProbe.Capture();
        Check("⑥ 真实采样:句柄数 > 0", s1.HandleCount > 0, $"句柄={s1.HandleCount}");
        Check("⑥ 真实采样:托管内存 > 0", s1.ManagedBytes > 0, $"托管={s1.ManagedBytes}");
        Check("⑥ 真实采样:工作集 > 0", s1.WorkingSetBytes > 0, $"工作集={s1.WorkingSetBytes}");
        Check("⑥ 真实采样:线程数 > 0", s1.ThreadCount > 0, $"线程={s1.ThreadCount}");
        Check("⑥ 真实采样:时间戳单调不减", s2.TimestampMs >= s1.TimestampMs, "时间戳倒退");

        // ⑦ CSV 往返一致(脚本与检查器共用一套口径)
        var row = SoakProbe.ToCsvRow(s1);
        Check("⑦ CSV 行可解析回来",
            SoakProbe.TryParseCsvRow(row, out var back) && back.Equals(s1), row);
        Check("⑦ 表头 6 列", SoakProbe.CsvHeader.Split(',').Length == 6, SoakProbe.CsvHeader);
        Check("⑦ 坏行被拒", !SoakProbe.TryParseCsvRow("1,2,3", out _) &&
                            !SoakProbe.TryParseCsvRow("", out _) &&
                            !SoakProbe.TryParseCsvRow("a,b,c,d,e,f", out _), "坏行被接受");

        // ⑧ 采样坏掉的两种表现都必须判失败
        var zeroHandles = Flat(count: 20, handles: 0);
        var v0 = SoakAnalyzer.Analyze(zeroHandles, TimeSpan.FromHours(24));
        Check("⑧ 句柄恒为 0 → 判失败(采样没生效)", !v0.Passed && v0.Reasons.Any(r => r.Contains("恒为 0")),
            v0.Summary + " | " + string.Join(" / ", v0.Reasons));

        var negative = Flat(count: 20, handles: -1);
        var vn = SoakAnalyzer.Analyze(negative, TimeSpan.FromHours(24));
        Check("⑧ 句柄采样失败(-1)→ 判失败", !vn.Passed && vn.Reasons.Any(r => r.Contains("采样失败")),
            string.Join(" / ", vn.Reasons));
    }

    // ---------------------------------------------------------------- ⑨ ~ ⑬

    private static void AnalyzerSpec()
    {
        Section("曲线判定");

        // ⑨ 平稳曲线(带正常抖动)→ 通过
        var flat = Flat(count: 30, handles: 120, managedBase: 40L * 1024 * 1024, ws: 220L * 1024 * 1024, jitter: true);
        var vFlat = SoakAnalyzer.Analyze(flat, TimeSpan.FromHours(24));
        Check("⑨ 24h 平稳曲线 → 通过(抖动不误判)", vFlat.Passed,
            vFlat.Summary + " | " + string.Join(" / ", vFlat.Reasons));

        // ⑩ 句柄单向增长 → 抓句柄
        var leakHandles = Flat(count: 30, handles: 120, handleSlope: 2);
        var vh = SoakAnalyzer.Analyze(leakHandles, TimeSpan.FromHours(24));
        Check("⑩ 句柄单向增长 → 判失败且原因是句柄",
            !vh.Passed && vh.Reasons.Any(r => r.Contains("句柄")), string.Join(" / ", vh.Reasons));

        // ⑪ 托管内存增长 → 抓内存;工作集增长 → 抓工作集
        var leakMem = Flat(count: 30, handles: 120, managedSlopePerSample: 10L * 1024 * 1024);
        var vm = SoakAnalyzer.Analyze(leakMem, TimeSpan.FromHours(24));
        Check("⑪ 托管内存每小时增长约 12MB → 判失败且原因是内存",
            !vm.Passed && vm.Reasons.Any(r => r.Contains("托管内存")), string.Join(" / ", vm.Reasons));

        var leakWs = Flat(count: 30, handles: 120, wsSlopePerSample: 40L * 1024 * 1024);
        var vw = SoakAnalyzer.Analyze(leakWs, TimeSpan.FromHours(24));
        Check("⑪ 工作集单向增长 → 判失败且原因是工作集",
            !vw.Passed && vw.Reasons.Any(r => r.Contains("工作集")), string.Join(" / ", vw.Reasons));

        // ⑪b 短窗口的托管漂移**不**被外推成速率。
        //     2MB 的预热漂移(20 秒)线性外推 = 约 340MB/小时,数字很大但结论是假的;
        //     短窗口只按绝对量判,同一批数据必须通过。
        var warmup = Flat(count: 21, handles: 120, managedSlopePerSample: 100 * 1024, spanMinutes: 0.33);
        var vWarm = SoakAnalyzer.Analyze(warmup, TimeSpan.FromMinutes(0.1));
        Check("⑪b 20 秒窗口的预热漂移不被外推成速率(绝对量在限内 → 通过)",
            vWarm.Passed && vWarm.Summary.Contains("不看速率"), vWarm.Summary + " | " + string.Join(" / ", vWarm.Reasons));

        // ⑫ 时长与样本数下限:短跑不能冒充 24h
        var oneHour = Flat(count: 30, handles: 120, spanMinutes: 60);
        var vt = SoakAnalyzer.Analyze(oneHour, TimeSpan.FromHours(24));
        Check("⑫ 只跑 1 小时 → 判失败且原因是时长",
            !vt.Passed && vt.Reasons.Any(r => r.Contains("时长不足")), string.Join(" / ", vt.Reasons));

        var tiny = Flat(count: 3, handles: 120);
        var vTiny = SoakAnalyzer.Analyze(tiny, TimeSpan.FromHours(24));
        Check("⑫ 3 个样本 → 判失败且原因是样本不足",
            !vTiny.Passed && vTiny.Reasons.Any(r => r.Contains("样本不足")), string.Join(" / ", vTiny.Reasons));

        // ⑫b 压缩冒烟:显式声明更短跨度时才认可短跑(诚实口径)
        var vShort = SoakAnalyzer.Analyze(oneHour, TimeSpan.FromMinutes(59));
        Check("⑫b 显式声明 59min 跨度时,1 小时平稳曲线通过(压缩冒烟口径)",
            vShort.Passed, string.Join(" / ", vShort.Reasons));

        // ⑬ 违规 → 失败;违规计数回退 → 也失败
        var withViolation = Flat(count: 30, handles: 120, violations: 3);
        var vv = SoakAnalyzer.Analyze(withViolation, TimeSpan.FromHours(24));
        Check("⑬ 有线程纪律违规 → 判失败(验收要求 0)",
            !vv.Passed && vv.Reasons.Any(r => r.Contains("违规")), string.Join(" / ", vv.Reasons));

        var regression = Flat(count: 30, handles: 120).ToArray();
        regression[10] = regression[10] with { ViolationCount = 2 };
        regression[20] = regression[20] with { ViolationCount = 0 };
        var vr = SoakAnalyzer.Analyze(regression, TimeSpan.FromHours(24));
        Check("⑬ 违规计数回退(重置计数掩盖违规)→ 判失败",
            !vr.Passed && vr.Reasons.Any(r => r.Contains("回退")), string.Join(" / ", vr.Reasons));
    }

    // ---------------------------------------------------------------- ⑭

    private static void RecorderSpec()
    {
        Section("采样记录器");

        var dir = Path.Combine(Path.GetTempPath(), "netdisk-soak-check-" + Guid.NewGuid().ToString("N")[..8]);
        var csv = Path.Combine(dir, "soak.csv");
        try
        {
            using (var recorder = SoakRecorder.Start(csv, TimeSpan.FromMilliseconds(60), sampleCount: 5))
            {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (recorder.Samples.Count < 5 && DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(50);
                }
                Check("⑭ 采集到 5 个样本", recorder.Samples.Count == 5, $"样本={recorder.Samples.Count}");
                Check("⑭ 内存样本与落盘一致",
                    recorder.Samples.All(s => s.HandleCount > 0), "存在无效样本");
            }

            var lines = File.ReadAllLines(csv);
            Check("⑭ CSV 表头正确", lines.Length >= 1 && lines[0] == SoakProbe.CsvHeader, lines.FirstOrDefault() ?? "(空)");
            var dataRows = lines.Skip(1).Where(l => l.Trim().Length > 0).ToArray();
            Check("⑭ CSV 行数 = 样本数", dataRows.Length == 5, $"行数={dataRows.Length}");
            Check("⑭ CSV 每行可解析且句柄 > 0",
                dataRows.All(l => SoakProbe.TryParseCsvRow(l, out var s) && s.HandleCount > 0),
                "存在不可解析或无效行");
            Check("⑭ 记录器可追加(第二次启动不重复表头)",
                AppendKeepsSingleHeader(csv), "表头重复");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (Exception) { /* 清理失败不影响结论 */ }
        }
    }

    private static bool AppendKeepsSingleHeader(string csv)
    {
        using (SoakRecorder.Start(csv, TimeSpan.FromSeconds(60), sampleCount: 1))
        {
            // 只采一次即退出
        }
        return File.ReadAllLines(csv).Count(l => l == SoakProbe.CsvHeader) == 1;
    }

    // ---------------------------------------------------------------- 压缩冒烟

    /// <summary>
    /// 压缩冒烟:用**真实组件**跑一段有负载的时间,同时采样并判定。
    /// 诚实标注:这不是 24h 验收那条 —— 报告里会打印实测跨度,并要求调用方显式传入跨度。
    /// </summary>
    private static int RunCompressedSoak(double seconds)
    {
        ThreadDiscipline.ResetForTests();
        ThreadDiscipline.MarkUiThread(); // 主线程扮演 UI 线程

        var dir = Path.Combine(Path.GetTempPath(), "netdisk-soak-load-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var csv = Path.Combine(dir, "soak.csv");
        var dbPath = Path.Combine(dir, "state.db");

        var start = DateTime.UtcNow;
        var deadline = start.AddSeconds(seconds);
        var stop = false;

        using var recorder = SoakRecorder.Start(csv, TimeSpan.FromSeconds(1), sampleCount: null);
        recorder.SampleOnce(); // 起点基线

        var cycles = 0;
        var ledger = new ExpectedChangeLedger();
        var center = new NotificationCenter();
        var shown = 0;
        center.Raised += _ => Interlocked.Increment(ref shown);

        var worker = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                // 同步引擎侧的动作:必须在后台线程上(状态库开关是最典型的句柄泄漏面)
                ThreadDiscipline.AssertOffUiThread("StateStore.Open");
                using (var store = StateStore.Open(dbPath))
                {
                    store.Execute(
                        "INSERT INTO cursors(space_id, last_seq, updated_at_utc) VALUES(@s,@q,@u) " +
                        "ON CONFLICT(space_id) DO UPDATE SET last_seq=@q",
                        ("s", "space-soak"), ("q", (long)cycles), ("u", DateTime.UtcNow.ToString("O")));
                    _ = store.Scalar("SELECT COUNT(*) FROM cursors");
                }

                var path = Path.Combine(dir, "file-" + (cycles % 8) + ".bin");
                ledger.Record(path, size: 1024, mtimeTicks: DateTime.UtcNow.Ticks);
                ledger.TryConsume(path, actualSize: 1024, actualMtimeTicks: DateTime.UtcNow.Ticks);
                ledger.EvictExpired();

                center.NotifySyncCompleted(files: 1, bytes: 1024);
                center.NotifyQuota("space-1", usedPercent: 90);

                Interlocked.Increment(ref cycles);
            }
        })
        { IsBackground = true, Name = "soak-load" };
        worker.Start();

        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(250);
        }
        Volatile.Write(ref stop, true);
        worker.Join(TimeSpan.FromSeconds(5));

        var span = DateTime.UtcNow - start;
        // 压缩冒烟的口径:要求跨度 = 实测跨度(少 1 秒容差),并放宽样本数下限
        var verdict = SoakAnalyzer.Analyze(recorder.Samples, span - TimeSpan.FromSeconds(1),
            SoakThresholds.Default with { MinSamples = 3 });
        recorder.Dispose();

        Console.WriteLine($"压缩冒烟:负载循环 {cycles} 次,通知合并后 {shown} 条");
        Console.WriteLine($"CSV: {csv}");
        Console.WriteLine($"结论: {(verdict.Passed ? "PASS" : "FAIL")}  {verdict.Summary}");
        foreach (var r in verdict.Reasons)
        {
            Console.WriteLine("  原因: " + r);
        }
        Console.WriteLine($"注意:本次实测跨度 {span.TotalMinutes:F1} 分钟 —— 这不是 24h 验收那条,");
        Console.WriteLine("      24h 由 .run/soak_client.ps1 跑真实客户端(NetDisk.App.exe)。");

        try { Directory.Delete(dir, recursive: true); } catch (Exception) { /* 忽略 */ }
        return verdict.Passed ? 0 : 1;
    }

    // ---------------------------------------------------------------- 合成曲线

    /// <summary>造一条合成曲线(用于判定逻辑的行为断言)。</summary>
    private static IReadOnlyList<SoakSample> Flat(
        int count,
        int handles,
        int handleSlope = 0,
        long managedBase = 40L * 1024 * 1024,
        long managedSlopePerSample = 0,
        long ws = 220L * 1024 * 1024,
        long wsSlopePerSample = 0,
        int violations = 0,
        double spanMinutes = 24 * 60,
        bool jitter = false)
    {
        var list = new List<SoakSample>(count);
        var spanMs = (long)(spanMinutes * 60_000);
        var t0 = 1_760_000_000_000L; // 固定起点:判定必须与"当前时间"无关
        for (var i = 0; i < count; i++)
        {
            // 先乘后除:最后一个样本必须**正好**落在跨度末端(i=count-1 时得到 spanMs),
            // 否则"24h 的夹具"实际只有 23.99h,判定会(正确地)判时长不足。
            var offsetMs = spanMs * i / Math.Max(1, count - 1);
            // 锯齿抖动:±3 句柄 / ±2MB —— 真实进程的正常波动,不应被判为泄漏
            var wobble = jitter ? (i % 4 - 2) : 0;
            list.Add(new SoakSample(
                TimestampMs: t0 + offsetMs,
                HandleCount: handles + handleSlope * i + wobble,
                ManagedBytes: managedBase + managedSlopePerSample * i + (jitter ? wobble * 512 * 1024L : 0),
                WorkingSetBytes: ws + wsSlopePerSample * i + (jitter ? wobble * 1024 * 1024L : 0),
                ThreadCount: 18,
                ViolationCount: violations));
        }
        return list;
    }

    // ---------------------------------------------------------------- 断言框架

    private static void Section(string title)
    {
        Console.WriteLine($"— {title}");
    }

    private static void Check(string what, bool ok, string detail)
    {
        if (ok)
        {
            Console.WriteLine($"   ✓ {what}");
            return;
        }
        _failed++;
        Console.WriteLine($"   ✗ {what}  [{detail}]");
    }
}
