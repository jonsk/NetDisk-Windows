// 数据文件位置的行为检查器(产品要求 ① 的三条)。
//
// 为什么值得单独一个检查器:
//   `ClientPaths` 决定配置/令牌/状态库/日志**放在哪**,而它的两个关键分支
//   (程序目录不可用 → 回退 %APPDATA%;旧位置还有数据 → 一次性迁移)**只在特定现场才走到**。
//   MSI 安装演练只能看到"正常那一条"(装完文件确实落在安装目录),证明不了回退与迁移。
//   这里的做法与项目里其它检查器一致:用注入点把三种现场**造出来**,逐条断言。
//
// 用法:`dotnet run --project desktop/tests/PathsCheck -c Release`

using NetDisk.SyncEngine.Host;

var checks = new List<(string Name, Func<Task> Run)>
{
    ("① 程序目录可写 → 四类数据都在程序目录里,且没有回退", CheckProgramDirAsync),
    ("② 旧位置有数据 → **一次性迁移**到程序目录(内容一致)", CheckMigrationAsync),
    ("③ 迁移**不覆盖**已有文件(目标存在就留着,源文件不动)", CheckMigrationKeepsExistingAsync),
    ("④ 程序目录不可用 → 回退 %APPDATA% 且**说明原因**", CheckFallbackAsync),
    ("⑤ 回退落点就是旧目录 → 不自己搬自己(不抛异常)", CheckFallbackNoSelfMoveAsync),
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
    Console.WriteLine($"全部通过:{checks.Count} 项 DE-D-07 路径断言(程序目录 / 回退 / 一次性迁移)");
    return 0;
}
Console.Error.WriteLine($"{failed}/{checks.Count} 项失败");
return 1;

// ---------------------------------------------------------------- 各场景

static async Task CheckProgramDirAsync()
{
    using var sandbox = new Sandbox();
    ClientPaths.ExeDirectoryOverride = sandbox.Root;
    ClientPaths.ResetCacheForTests();

    var dir = ClientPaths.DataDirectory;
    Assert(SamePath(dir, sandbox.Root), $"数据目录应为程序目录,实际 {dir}");

    // 四类数据必须同处一处(否则"配置在哪"会变成猜谜)
    foreach (var (label, path) in new[]
             {
                 ("配置", ClientPaths.ConfigPath),
                 ("令牌", ClientPaths.TokenPath),
                 ("状态库", ClientPaths.StatePath),
                 ("日志", ClientPaths.LogPath),
             })
    {
        Assert(SamePath(Path.GetDirectoryName(path)!, dir) || path.StartsWith(dir, StringComparison.OrdinalIgnoreCase),
            $"{label}路径应落在数据目录下:{path} vs {dir}");
    }

    // 目录可写时不该有回退原因(有原因就说明"配置在别处",会让用户找错地方)
    Assert(ClientPaths.FallbackReason is null, $"程序目录可写时不该有回退原因:{ClientPaths.FallbackReason}");

    // 真正写一次:可写的判据是"真能写",不是"看起来能写"
    File.WriteAllText(ClientPaths.ConfigPath, "{\"base_url\":\"x\"}");
    Assert(File.ReadAllText(ClientPaths.ConfigPath).Contains("base_url"), "配置必须能真的写到那里");
    await Task.CompletedTask;
}

static async Task CheckMigrationAsync()
{
    using var sandbox = new Sandbox();
    var legacy = Path.Combine(sandbox.Root, "legacy-appdata");
    var exeDir = Path.Combine(sandbox.Root, "program");
    Directory.CreateDirectory(legacy);
    Directory.CreateDirectory(exeDir);
    // 旧位置的三件套(模拟"用户从上一次版本升级上来")
    File.WriteAllText(Path.Combine(legacy, "client.json"), "{\"base_url\":\"old-server\"}");
    File.WriteAllText(Path.Combine(legacy, "tokens.bin"), "old-token-cipher");
    File.WriteAllText(Path.Combine(legacy, "state.db"), "old-state");

    ClientPaths.ExeDirectoryOverride = exeDir;
    ClientPaths.LegacyDirectoryOverride = legacy;
    ClientPaths.ResetCacheForTests();

    var dir = ClientPaths.DataDirectory;
    Assert(SamePath(dir, exeDir), $"应使用程序目录,实际 {dir}");
    foreach (var name in new[] { "client.json", "tokens.bin", "state.db" })
    {
        var target = Path.Combine(exeDir, name);
        Assert(File.Exists(target), $"{name} 应被迁移到程序目录");
    }
    Assert(File.ReadAllText(Path.Combine(exeDir, "client.json")).Contains("old-server"),
        "迁移后的配置内容必须还是用户的(不能被默认值覆盖)");
    // Move 语义:搬完旧位置不该再留一份(两份配置必然漂移,表现是"我改的设置没生效")
    Assert(!File.Exists(Path.Combine(legacy, "client.json")), "迁移后旧位置的配置文件不应还在(Move 语义)");
    await Task.CompletedTask;
}

static async Task CheckMigrationKeepsExistingAsync()
{
    using var sandbox = new Sandbox();
    var legacy = Path.Combine(sandbox.Root, "legacy2");
    var exeDir = Path.Combine(sandbox.Root, "program2");
    Directory.CreateDirectory(legacy);
    Directory.CreateDirectory(exeDir);
    File.WriteAllText(Path.Combine(legacy, "client.json"), "OLD");
    File.WriteAllText(Path.Combine(exeDir, "client.json"), "NEW"); // 程序目录里已经有更新的配置

    ClientPaths.ExeDirectoryOverride = exeDir;
    ClientPaths.LegacyDirectoryOverride = legacy;
    ClientPaths.ResetCacheForTests();

    _ = ClientPaths.DataDirectory;
    Assert(File.ReadAllText(Path.Combine(exeDir, "client.json")) == "NEW",
        "目标已存在时**不得**覆盖(那会把用户新配置换成旧的)");
    Assert(File.Exists(Path.Combine(legacy, "client.json")), "目标已存在时源文件应留在原处(只搬不覆盖)");
    await Task.CompletedTask;
}

static async Task CheckFallbackAsync()
{
    using var sandbox = new Sandbox();
    var legacy = Path.Combine(sandbox.Root, "legacy3");
    // 用一个**不存在**的路径模拟"程序目录不可用"(真实现场是装到 Program Files、
    // 或目录被 ACL 收紧 —— 判据是 IsWritable 的真实写入探测,不存在同样判为不可写)
    var missingExeDir = Path.Combine(sandbox.Root, "does-not-exist");

    ClientPaths.ExeDirectoryOverride = missingExeDir;
    ClientPaths.LegacyDirectoryOverride = legacy;
    ClientPaths.ResetCacheForTests();

    var dir = ClientPaths.DataDirectory;
    Assert(SamePath(dir, legacy), $"程序目录不可用时应回退到旧位置,实际 {dir}");
    Assert(ClientPaths.FallbackReason is { } reason && reason.Contains("回退", StringComparison.Ordinal),
        $"回退必须留下可读原因(启动时会写进日志),实际 '{ClientPaths.FallbackReason}'");
    Assert(Directory.Exists(dir), "回退落点应被创建出来");
    await Task.CompletedTask;
}

static async Task CheckFallbackNoSelfMoveAsync()
{
    using var sandbox = new Sandbox();
    var legacy = Path.Combine(sandbox.Root, "legacy4");
    Directory.CreateDirectory(legacy);

    // 回退落点 == 旧目录:此时"迁移"是自己搬自己 —— 必须被识别出来并跳过(否则 Move 会抛/丢文件)
    ClientPaths.ExeDirectoryOverride = Path.Combine(sandbox.Root, "missing4");
    ClientPaths.LegacyDirectoryOverride = legacy;
    ClientPaths.ResetCacheForTests();
    File.WriteAllText(Path.Combine(legacy, "client.json"), "KEEP");

    var dir = ClientPaths.DataDirectory;
    Assert(SamePath(dir, legacy), "应回退到该目录");
    Assert(File.ReadAllText(Path.Combine(legacy, "client.json")) == "KEEP",
        "自搬自己必须被跳过:回退场景下文件要原样留着");
    await Task.CompletedTask;
}

// ---------------------------------------------------------------- 工具

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception(message);
    }
}

static bool SamePath(string a, string b) => string.Equals(
    Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
    Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
    StringComparison.OrdinalIgnoreCase);

/// <summary>临时沙箱:每个场景一个,退出时清掉注入点与目录(不给下一个场景留状态)。</summary>
sealed class Sandbox : IDisposable
{
    public Sandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "netdisk-paths-check", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public void Dispose()
    {
        ClientPaths.ExeDirectoryOverride = null;
        ClientPaths.LegacyDirectoryOverride = null;
        ClientPaths.ResetCacheForTests();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception)
        {
            // 清理失败不影响结论
        }
    }
}
