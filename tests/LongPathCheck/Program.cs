// DE-D-16 行为检查器:长路径 + 本地名字兜底断言。
//
// 用法:`dotnet run --project desktop/tests/LongPathCheck -c Release`
//
// 最要紧的一条是 ⑫:**禁止静默改名**。把 `a?b.txt` 顺手改成 `a_b.txt` 不会报错,
// 但会让本地名字与远端不一致 —— 下一次同步会把它当成"本地新增"再传一份,
// 用户对本地文件的修改也就脱离了远端对应项(冲突解决与改名识别全部失去依据)。
// 所以既断言"不可用的名字要抛错",也用反射断言"这个类型根本没有改名能力"。

using System.Xml.Linq;
using NetDisk.SyncEngine.Paths;

// C# 字面量里的反斜杠很烦,统一用常量,免得每处都数转义
const string Ext = "\\\\?\\";        // 扩展前缀(本地卷)
const string Unc = "\\\\?\\UNC\\";   // 扩展前缀(UNC)

var checks = new List<(string Name, Func<Task> Run)>
{
    ("① 清单声明 longPathAware=true(机械断言)", CheckManifestAsync),
    ("② 加前缀 + 幂等", CheckPrefixAsync),
    ("③ 先规范化再加前缀(.. 与正斜杠先被处理掉)", CheckNormalizeFirstAsync),
    ("④ UNC 路径转扩展 UNC 前缀", CheckUncAsync),
    ("⑤ 展示用路径可还原(去掉前缀)", CheckDisplayAsync),
    ("⑥ 真实 IO:超过 MAX_PATH 的路径能建/能读", CheckRealLongPathIoAsync),
    ("⑦ 非法字符被拦下并列出具体字符", CheckInvalidCharsAsync),
    ("⑧ Windows 保留设备名被拦下(CON / CON.txt)", CheckReservedNamesAsync),
    ("⑨ 尾点/尾空格/相对记号被拦下", CheckTrailingAsync),
    ("⑩ 单段超长被拦下,正常中文名放行", CheckLengthAsync),
    ("⑪ 与服务端共享名字规则同口径(超 240 字节拒)", CheckSharedRulesAsync),
    ("⑫ 禁止静默改名:无可用的改名/清洗 API + 错误带原名", CheckNoSilentRenameAsync),
    ("⑬ 扫描器与骨架构建器确实走了扩展路径(机械断言)", CheckCallSitesAsync),
};

var failed = 0;
foreach (var (name, run) in checks)
{
    try
    {
        await run();
        Console.WriteLine($"✓ {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"✗ {name}: {ex.Message}");
    }
}

Console.WriteLine();
if (failed == 0)
{
    Console.WriteLine($"全部通过:{checks.Count} 项 DE-D-16 行为断言(长路径 + 本地名字兜底)");
    return 0;
}
Console.Error.WriteLine($"{failed}/{checks.Count} 项失败");
return 1;

// ---------------------------------------------------------------- 检查

static Task CheckManifestAsync()
{
    var path = Locate("desktop/src/NetDisk.App/app.manifest");
    var doc = XDocument.Load(path);
    XNamespace ws = "http://schemas.microsoft.com/SMI/2016/WindowsSettings";

    var node = doc.Descendants(ws + "longPathAware").FirstOrDefault();
    Assert(node is not null,
        "app.manifest 必须声明 longPathAware(Windows 10 1607+ 才给这个进程放宽 MAX_PATH;删掉它长路径就会开始失败)");
    Assert(string.Equals(node!.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase),
        "longPathAware 必须为 true,实际 " + node.Value);

    var dpi = doc.Descendants(ws + "dpiAwareness").FirstOrDefault();
    Assert(dpi is not null && dpi.Value.Contains("permonitorv2", StringComparison.OrdinalIgnoreCase),
        "dpiAwareness=permonitorv2 不能被删(高分屏下界面会变糊)");
    return Task.CompletedTask;
}

static Task CheckPrefixAsync()
{
    var ext = LongPath.ToExtended(@"C:\sync\a.txt");
    Assert(ext == Ext + @"C:\sync\a.txt", "应加上扩展前缀,实际 " + ext);
    Assert(LongPath.IsExtended(ext), "IsExtended 应为 true");

    var again = LongPath.ToExtended(ext);
    Assert(again == ext, "重复加前缀必须幂等(调用链上多处加前缀不该叠成两个前缀),实际 " + again);
    return Task.CompletedTask;
}

static Task CheckNormalizeFirstAsync()
{
    // 扩展前缀会**关闭**路径规范化:前缀 + `..` 是打不开的(它不解析 ..)。
    // 所以顺序只能是"先 GetFullPath 再前缀" —— 这条断言就是钉住顺序。
    var ext = LongPath.ToExtended(@"C:\sync\docs\..\a.txt");
    Assert(!ext.Contains("..", StringComparison.Ordinal),
        "必须先规范化再加前缀(否则得到打不开的路径),实际 " + ext);
    Assert(ext == Ext + @"C:\sync\a.txt", "规范化结果不对:" + ext);

    var mixed = LongPath.ToExtended("C:/sync/sub/../b.txt");
    Assert(mixed == Ext + @"C:\sync\b.txt", "正斜杠也应被规范化,实际 " + mixed);
    return Task.CompletedTask;
}

static Task CheckUncAsync()
{
    var ext = LongPath.ToExtended(@"\\server\share\dir\f.txt");
    Assert(ext == Unc + @"server\share\dir\f.txt", "UNC 应转成扩展 UNC 前缀,实际 " + ext);
    Assert(LongPath.ForDisplay(ext) == @"\\server\share\dir\f.txt", "展示时应还原成普通 UNC");
    return Task.CompletedTask;
}

static Task CheckDisplayAsync()
{
    var ext = LongPath.ToExtended(@"C:\sync\a.txt");
    Assert(LongPath.ForDisplay(ext) == @"C:\sync\a.txt", "展示用路径应去掉前缀(状态库/UI 存可读形式)");
    Assert(LongPath.ForDisplay(@"C:\plain\b.txt") == @"C:\plain\b.txt", "没有前缀时原样返回");
    return Task.CompletedTask;
}

static Task CheckRealLongPathIoAsync()
{
    // 真实 IO:构造一条**远超过 260** 的路径,经 LongPath 建目录、写文件、读回来。
    // 这是"长路径真的能用"的唯一硬证据(只断言字符串前缀是不够的)。
    var root = Path.Combine(Path.GetTempPath(), "netdisk-longpath-check", Guid.NewGuid().ToString("N"));
    var deep = root;
    for (var i = 0; i < 10; i++)
    {
        deep = Path.Combine(deep, new string((char)('a' + i), 40)); // 每段 40 字符 × 10 段
    }
    var file = Path.Combine(deep, "deep.txt");

    try
    {
        Assert(file.Length > LongPath.LegacyMaxPath,
            "测试路径必须真的超过 MAX_PATH 才有意义,实际 " + file.Length + " 字符");

        var extendedDir = LongPath.ToExtended(deep);
        Directory.CreateDirectory(extendedDir);
        var extendedFile = LongPath.ToExtended(file);
        File.WriteAllText(extendedFile, "长路径内容");
        Assert(File.ReadAllText(extendedFile) == "长路径内容", "长路径文件应能读回同样内容");
        Assert(Directory.Exists(extendedDir), "长路径目录应存在");
    }
    finally
    {
        try
        {
            Directory.Delete(LongPath.ToExtended(root), recursive: true); // 清理也得用扩展形式
        }
        catch (IOException) { }
    }
    return Task.CompletedTask;
}

static Task CheckInvalidCharsAsync()
{
    Assert(!LocalNameGuard.IsCreatable("a?b.txt", out var p1), "含 ? 必须被拦下");
    Assert(p1!.InvalidChars.Contains('?'), "应列出具体非法字符,实际 [" + string.Join(",", p1.InvalidChars) + "]");
    Assert(p1.Reason.Contains("不允许的字符"), "原因应可读,实际 " + p1.Reason);

    foreach (var bad in new[] { "a<b.txt", "a>b.txt", "a:b.txt", "a|b.txt", "a*b.txt", "a\"b.txt", "a/b.txt", "a\\b.txt" })
    {
        Assert(!LocalNameGuard.IsCreatable(bad, out _), "「" + bad + "」必须被拦下");
    }
    Assert(LocalNameGuard.IsCreatable("正常文件名.txt", out _), "正常名字应当放行");
    return Task.CompletedTask;
}

static Task CheckReservedNamesAsync()
{
    foreach (var bad in new[] { "CON", "con", "CON.txt", "PRN.doc", "NUL", "COM1", "lpt9.bin" })
    {
        Assert(!LocalNameGuard.IsCreatable(bad, out var p), "保留设备名「" + bad + "」必须被拦下");
        Assert(p!.Reason.Contains("保留设备名"), "原因应说明是保留名,实际 " + p.Reason);
    }
    Assert(LocalNameGuard.IsCreatable("CONSOLE.txt", out _), "CONSOLE 不是保留名,应放行");
    return Task.CompletedTask;
}

static Task CheckTrailingAsync()
{
    Assert(!LocalNameGuard.IsCreatable("a.", out var p1), "以点结尾必须被拦下");
    Assert(p1!.Reason.Contains("结尾"), "原因应可读,实际 " + p1.Reason);
    Assert(!LocalNameGuard.IsCreatable("a.txt ", out _), "以空格结尾必须被拦下");
    Assert(!LocalNameGuard.IsCreatable(".", out _), "相对目录记号必须被拦下");
    Assert(!LocalNameGuard.IsCreatable("..", out _), "相对目录记号必须被拦下");
    Assert(!LocalNameGuard.IsCreatable("   ", out _), "空名字必须被拦下");
    Assert(LocalNameGuard.IsCreatable("a. b.txt", out _), "点后跟空格但不在结尾 → 应放行");
    return Task.CompletedTask;
}

static Task CheckLengthAsync()
{
    var tooLong = new string('a', LocalNameGuard.MaxComponentChars + 1) + ".txt";
    Assert(!LocalNameGuard.IsCreatable(tooLong, out var p), "超过 255 字符必须被拦下");
    Assert(p!.Reason.Contains("255"), "原因应给出具体上限,实际 " + p.Reason);

    var overBytes = new string('中', 79) + ".txt"; // 79*3+4 = 241 字节 > 240
    Assert(!LocalNameGuard.IsCreatable(overBytes, out _), "超过 240 字节应由共享名字规则拦下");
    var ok = new string('中', 70) + ".txt";
    Assert(LocalNameGuard.IsCreatable(ok, out _), "70 个汉字 + 扩展名应当放行");
    return Task.CompletedTask;
}

static Task CheckSharedRulesAsync()
{
    // 与服务端共享规则同口径:客户端必须在**上传/落地之前**就与服务端一致,
    // 否则"传上去才被拒"或"本地建不出来"(规则漂移是同一类事故的两面)。
    var decomposed = string.Concat(Enumerable.Repeat("e\u0301", 79)) + ".txt"; // raw 241 / NFC 162
    Assert(LocalNameGuard.IsCreatable(decomposed, out _),
        "分解形式的组合符经 NFC 归一后应在 240 字节内(与服务端同口径)");

    var overBytes = new string('a', 241);
    Assert(!LocalNameGuard.IsCreatable(overBytes, out var p), "241 个 ASCII = 241 字节必须被拦下");
    Assert(p!.Reason.Contains("共享的名字规则") || p.Reason.Contains("字符"),
        "原因应指向具体规则,实际 " + p.Reason);
    return Task.CompletedTask;
}

static Task CheckNoSilentRenameAsync()
{
    var threw = false;
    try
    {
        LocalNameGuard.EnsureCreatable("a?b.txt");
    }
    catch (LocalNameException ex)
    {
        threw = true;
        Assert(ex.Problem.Name == "a?b.txt", "异常必须带原名字,实际 " + ex.Problem.Name);
        Assert(ex.Message.Contains("不会自动改名"), "错误文案应明确「不会自动改名」,实际 " + ex.Message);
    }
    Assert(threw, "不可用的名字必须抛 LocalNameException(让调用方上报),而不是返回一个改好的名字");

    // 结构断言:本类型不得存在任何"清洗/改名"能力 —— 那会被顺手用成静默改名
    var members = typeof(LocalNameGuard).GetMethods().Select(m => m.Name)
        .Concat(typeof(LocalNameGuard).GetProperties().Select(p => p.Name))
        .ToArray();
    var suspicious = members
        .Where(n => n.Contains("Rename", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("Sanitize", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("Fix", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("MakeValid", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("Escape", StringComparison.OrdinalIgnoreCase))
        .ToArray();
    Assert(suspicious.Length == 0,
        "LocalNameGuard 不该提供改名/清洗能力(会变成静默改名),实际存在:" + string.Join(",", suspicious));

    LocalNameGuard.EnsureCreatable("正常.txt"); // 可用的名字不该抛
    return Task.CompletedTask;
}

static Task CheckCallSitesAsync()
{
    // 机械断言:真正碰本地路径的两处必须走 LongPath(否则"加前缀"只停留在工具类里)
    foreach (var rel in new[]
             {
                 "desktop/src/NetDisk.SyncEngine/Watch/DirectoryScanner.cs",
                 "desktop/src/NetDisk.SyncEngine/Onboarding/OnboardingPlan.cs",
             })
    {
        var text = File.ReadAllText(Locate(rel));
        Assert(text.Contains("LongPath.ToExtended", StringComparison.Ordinal),
            rel + " 必须经 LongPath.ToExtended 访问本地路径(DE-D-16)");
    }
    return Task.CompletedTask;
}

// ---------------------------------------------------------------- 工具

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception(message);
    }
}

static string Locate(string relative)
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        var candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(candidate))
        {
            return candidate;
        }
        dir = dir.Parent;
    }
    throw new Exception("找不到 " + relative + "(从 " + AppContext.BaseDirectory + " 向上找)");
}
