// 双端名字规则一致性检查器(DE-D-02 ③)。
//
// 用法:`dotnet run --project desktop/tests/NameRulesCheck [-- <夹具路径>]`
// 退出码非 0 表示客户端规则与服务端(夹具)不一致。
//
// 为什么需要它:客户端必须在**上传之前**拒掉非法名,而规则一旦与服务端漂移,
// 用户就会遇到"客户端说没问题、服务端拒绝"这类无法解释的失败 —— 且只在特定
// 名字上出现。靠人肉比对规则表永远查不全,所以让两端跑**同一批用例**。
//
// 不一致时**改客户端**(服务端是唯一权威);若确实是服务端规则演进,
// 则先改服务端 + 更新夹具,再让本检查器把客户端拉齐。

using System.Text.Json;
using NetDisk.ClientCore;

int exitCode = 0;
try
{
    var fixture = ResolveFixture(args);
    if (fixture is null)
    {
        Console.Error.WriteLine("找不到共享夹具 desktop/testdata/name-cases.json");
        return 2;
    }

    using var doc = JsonDocument.Parse(File.ReadAllText(fixture));
    var cases = doc.RootElement.GetProperty("cases");
    int total = 0, mismatched = 0;
    foreach (var c in cases.EnumerateArray())
    {
        total++;
        var name = c.GetProperty("name").GetString() ?? "";
        var want = c.GetProperty("valid").GetBoolean();
        var note = c.TryGetProperty("note", out var n) ? n.GetString() : "";
        var got = NameRules.IsValidName(name);
        if (got != want)
        {
            mismatched++;
            Console.Error.WriteLine(
                $"✗ 夹具期望 valid={want},客户端判定 valid={got}: name={Short(name)} ({note}) —— 客户端规则与服务端不一致");
        }
    }

    if (mismatched > 0)
    {
        Console.Error.WriteLine($"\n客户端名字规则与服务端不一致:{mismatched}/{total} 条。请改 NetDisk.ClientCore.NameRules 对齐服务端。");
        exitCode = 1;
    }
    else
    {
        Console.WriteLine($"✓ 客户端名字规则与服务端一致(共享夹具 {total} 条,{Path.GetRelativePath(Environment.CurrentDirectory, fixture)})");
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"检查失败: {ex.Message}");
    exitCode = 2;
}
return exitCode;

// ResolveFixture 从命令行或从可执行文件位置向上找 desktop/testdata/name-cases.json。
//
// 向上查找而不是写死相对路径:`dotnet run` 的工作目录是**调用处**,
// 而 CI 可能从任意目录调它 —— 写死相对路径会在"从仓库根跑"与"从 desktop/ 跑"
// 之间表现不同,而那种失败看起来像"夹具丢了"。
static string? ResolveFixture(string[] argv)
{
    if (argv.Length > 0 && File.Exists(argv[0])) return argv[0];
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        var candidate = Path.Combine(dir.FullName, "testdata", "name-cases.json");
        if (File.Exists(candidate)) return candidate;
        // 从 bin/Debug/net10.0 往上若干层后可能落到 desktop/ 或仓库根
        candidate = Path.Combine(dir.FullName, "desktop", "testdata", "name-cases.json");
        if (File.Exists(candidate)) return candidate;
        dir = dir.Parent;
    }
    return null;
}

static string Short(string s) => s.Length <= 24 ? s : s[..24] + "…";
