// ============================================================================
// I18nCheck —— 桌面端多语言(P3 运行态验证 + P6 检查器雏形)。
//
// 设计:与 tests/ 下其它 *Check 一致 —— 零额外 NuGet,控制台自证,任一断言失败
// 打印 FAIL 并以非零码退出(CI 里 build 之后跑一遍即可把回归拦住)。
//
// 为什么这些断言值得写死:
//   ① 键集一致性:中文表/英文表必须同键。漏一个键不会崩(有回退),但会让某一种
//      语言的用户看到"另一半没翻" —— 这是静默的错,只有机器比对才抓得住。
//   ② 即时切换:LocalizationService 靠 INotifyPropertyChanged 抬"空串"让绑定 XAML
//      全量刷新。漏掉这一行,切换语言后界面不更新,得重启才生效 —— 违背 P0 承诺。
//   ③ 持久化:language 要能写进 client.json、重载后保持(测试用临时路径,不碰真实配置)。
//   ④ 回退链:未知 key → 回 key 本身(便于发现漏键);未知 code → 默认中文。
//   ⑤ 复合串 Format:六参的 Sync.Summary 中英各验一遍,防 {0}..{5} 语序/参数错位。
// ============================================================================

using System.ComponentModel;
using NetDisk.App.Localization;
using NetDisk.SyncEngine.Host;

namespace NetDisk.Checks.I18n;

internal static class Program
{
    private static int _fail;

    private static void Check(bool cond, string name)
    {
        if (cond)
        {
            Console.WriteLine($"  PASS  {name}");
        }
        else
        {
            _fail++;
            Console.WriteLine($"  FAIL  {name}");
        }
    }

    private static void ExpectEq(string actual, string expected, string name)
    {
        if (string.Equals(actual, expected, StringComparison.Ordinal))
        {
            Console.WriteLine($"  PASS  {name}");
        }
        else
        {
            _fail++;
            Console.WriteLine($"  FAIL  {name}: 期望=[{expected}] 实际=[{actual}]");
        }
    }

    public static int Main()
    {
        Console.WriteLine("== I18nCheck: 语言键集 / 即时切换 / 持久化 / 回退 / 格式化 ==");

        Loc.Init("zh");

        // ---------- ① 键集一致 ----------
        Console.WriteLine("[1] 中文/英文键集一致");
        var zh = Locale.TableFor("zh");
        var en = Locale.TableFor("en");
        var zhOnly = zh.Keys.Except(en.Keys).ToList();
        var enOnly = en.Keys.Except(zh.Keys).ToList();
        Check(zhOnly.Count == 0 && enOnly.Count == 0,
            $"键集完全一致 (zh={zh.Count} en={en.Count}; zh-only={zhOnly.Count} en-only={enOnly.Count})");
        if (zhOnly.Count > 0) Console.WriteLine($"    zh 独有: {string.Join(", ", zhOnly.Take(8))}");
        if (enOnly.Count > 0) Console.WriteLine($"    en 独有: {string.Join(", ", enOnly.Take(8))}");

        // 逐键 {n} 占位符数量一致(防语序/参数错位)
        Console.WriteLine("[2] 逐键占位符 {n} 数量一致");
        var phMismatch = zh.Keys
            .Where(k => !Placeholders(zh[k]).SequenceEqual(Placeholders(en[k])))
            .ToList();
        Check(phMismatch.Count == 0, $"占位符一致 (不一致={phMismatch.Count}; 如 {phMismatch.FirstOrDefault()})");
        foreach (var k in phMismatch.Take(5))
        {
            Console.WriteLine($"    {k}: zh=[{zh[k]}]  en=[{en[k]}]");
        }

        // ---------- ② 非格式化文本不含 {n} 字面量(会被 XAML 绑定当占位符) ----------
        Console.WriteLine("[3] 纯绑定文本不残留 {0} 展开式");
        Check(!zh["Shares.Footer"].Contains("{0}") && !en["Shares.Footer"].Contains("{0}"),
            "Shares.Footer 已用字面量 {token} 而非 {0}");

        // ---------- ③ 即时切换 + PropertyChanged 全局刷新 ----------
        Console.WriteLine("[4] 切语言即时生效(抬空串=全局刷新)");
        string? notified = null;
        PropertyChangedEventHandler handler = (_, e) => notified = e.PropertyName;
        Loc.Instance.PropertyChanged += handler;
        Loc.Instance.Code = "en";
        Loc.Instance.PropertyChanged -= handler;
        Check(notified == "", "切换 Code 抬起了空 PropertyName(触发全部绑定刷新)");
        ExpectEq(Loc.T("Main.Settings"), "Settings", "en 下 Main.Settings=Settings");
        Loc.Instance.Code = "zh";
        ExpectEq(Loc.T("Main.Settings"), "设置", "切回 zh 后 Main.Settings=设置");
        Check(Loc.Current == "zh", $"切回后 Current 应为 zh,实际={Loc.Current}");

        // ---------- ④ 回退链 ----------
        Console.WriteLine("[5] 回退链(Normalize / 未知 key)");
        ExpectEq(LocalizationService.Normalize(null), "zh", "Normalize(null)→zh");
        ExpectEq(LocalizationService.Normalize(""), "zh", "Normalize(\"\")→zh");
        ExpectEq(LocalizationService.Normalize("fr"), "zh", "Normalize(fr)→zh");
        ExpectEq(LocalizationService.Normalize("en"), "en", "Normalize(en)→en");
        ExpectEq(LocalizationService.Normalize("en-US"), "en", "Normalize(en-US)→en");
        ExpectEq(Loc.T("No.Such.Key"), "No.Such.Key", "未知 key 回退 key 本身");
        Loc.Init("en");
        ExpectEq(Loc.T("App.WindowTitle"), "NetDisk Desktop", "en 已知 key 正常取英文");
        Loc.Init("zh");

        // ---------- ⑤ 复合串 Format ----------
        Console.WriteLine("[6] 复合串 Format(六参,中英)");
        Loc.Init("zh");
        var zhFmt = Loc.F("Sync.Summary", 10, 5, 2, 1, 0, 1);
        Check(zhFmt.Contains("共 10 个文件") && zhFmt.Contains("已同步 5") && zhFmt.Contains("冲突 0"),
            $"zh Sync.Summary 正确: {zhFmt}");
        Loc.Init("en");
        var enFmt = Loc.F("Sync.Summary", 10, 5, 2, 1, 0, 1);
        Check(enFmt.StartsWith("10 files:") && enFmt.Contains("synced 5") && enFmt.Contains("conflict 0"),
            $"en Sync.Summary 正确: {enFmt}");
        // Format 占位符不匹配时不崩、回原文
        Loc.Init("zh");
        ExpectEq(Loc.F("Sync.NotStarted"), "尚未开始同步", "无占位符 key 用 F() 不崩且回原文");
        Loc.Init("zh");

        // ---------- ⑥ 持久化(language 写 client.json + 重载保持) ----------
        Console.WriteLine("[7] 持久化(临时路径,不碰真实配置)");
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "netdisk_i18n_check_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var cfg = ClientConfig.Load(tmp);
            cfg.Language = "en";
            cfg.Save();
            var reloaded = ClientConfig.Load(tmp);
            ExpectEq(reloaded.Language, "en", "Save→Load 后 language 保持 en");
            // 默认(空)应归一化为 zh
            cfg.Language = "";
            cfg.Save();
            reloaded = ClientConfig.Load(tmp);
            ExpectEq(reloaded.Language, "", "空 language 原样持久化(由 Normalize 在装配侧回退 zh)");
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            try { if (File.Exists(tmp + ".tmp")) File.Delete(tmp + ".tmp"); } catch { }
        }

        // ---------- ⑦ 界面无空白键:XAML Binding 键引用静态校验 ----------
        Console.WriteLine("[8] XAML Binding 键引用全部存在(界面无空白键)");
        var appRoot = FindAppProjectRoot();
        Check(appRoot != null, "定位 src/NetDisk.App 工程目录");
        int xamlCount = 0, refCount = 0;
        var missing = new System.Collections.Generic.SortedSet<string>();
        var xamlRegex = new System.Text.RegularExpressions.Regex(
            @"\{Binding\s*\[([A-Za-z0-9_.]+)\]\s*,",
            System.Text.RegularExpressions.RegexOptions.Compiled);
        if (appRoot != null)
        {
            foreach (var f in System.IO.Directory.EnumerateFiles(appRoot, "*.xaml", System.IO.SearchOption.AllDirectories))
            {
                xamlCount++;
                var text = System.IO.File.ReadAllText(f);
                foreach (System.Text.RegularExpressions.Match m in xamlRegex.Matches(text))
                {
                    refCount++;
                    var key = m.Groups[1].Value;
                    if (!zh.ContainsKey(key) || !en.ContainsKey(key)) missing.Add(key);
                }
            }
        }
        Check(refCount > 0, $"扫描到 {refCount} 处 Binding 键引用(xaml 文件={xamlCount})");
        Check(missing.Count == 0, $"XAML 引用的键全部存在(缺失={missing.Count})");
        foreach (var k in missing.Take(8)) Console.WriteLine($"    缺失键: {k}");

        Console.WriteLine();
        if (_fail == 0)
        {
            Console.WriteLine("I18NCHECK: ALL PASS");
            return 0;
        }
        Console.WriteLine($"I18NCHECK: {_fail} FAILED");
        return 1;
    }

    private static List<int> Placeholders(string s)
    {
        var list = new List<int>();
        for (var m = System.Text.RegularExpressions.Regex.Match(s, @"\{(\d+)\}"); m.Success; m = m.NextMatch())
        {
            list.Add(int.Parse(m.Groups[1].Value));
        }
        list.Sort();
        return list;
    }

    // 从检查器输出的 bin 目录向上定位到包含 src/NetDisk.App 的工程根(仓库根)
    private static string? FindAppProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "NetDisk.App")))
                return Path.Combine(dir.FullName, "src", "NetDisk.App");
            dir = dir.Parent;
        }
        return null;
    }
}
