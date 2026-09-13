// 更新器**协议逻辑**(DE-D-19;可注入、可断言的那一半)。
//
// 为什么客户端要兼任"更新器"(而不是再出一个 updater.exe):
//   产品要求 ① 是**单文件发布**,MSI 载荷校验收紧到"恰好一个文件"。多一个 updater.exe
//   既破坏单文件,又要再维护一套发布与签名。所以做法是:
//   客户端把**自己复制到临时目录**,用 `--apply-update` 参数把那份副本拉起来,然后自己退出;
//   副本(不是安装目录里那个文件)去跑 `msiexec` —— 这样"自己覆盖自己"的锁文件问题不存在,
//   安装目录里仍然是**唯一的那个 exe**。
//
// 本文件只放**决策**;真正起进程由注入的 IProcessRunner 做 —— 于是"先装再检查再回滚"的
// 每一步都能在零依赖检查器里跑出来(升级是一锤子买卖,不可能靠人工反复演练来验证顺序)。
//
// 三条硬规矩(都有对应断言):
//   ① msiexec 参数:**MSI 路径加引号**(路径含空格是常态)+ `/qn`(静默,perUser 包不触发 UAC)
//      + `/norestart`(升级途中弹"需要重启"会把无人值守变成有人值守);
//   ② **0 与 3010 都算成功**(3010 = 成功但需要重启;把它当失败会导致"明明装上了却回滚");
//   ③ **装完必须验活**:新版起不来就重装旧版 —— 但"起不来"不能猜,要跑一次
//      `--client --self-check`(客户端自己检查配置/令牌/日志目录)。
//
// 失败时的收尾同样有硬规矩:无论哪条失败路径,只要客户端此刻**能启动**,就要把它拉起来 ——
// 否则用户升级失败后面对的是"程序不见了"。

namespace NetDisk.SyncEngine.Update;

/// <summary>跑外部进程(可注入:检查器用假的,真实运行用 <see cref="ProcessRunner"/>)。</summary>
public interface IProcessRunner
{
    /// <summary>跑进程并等它结束;返回退出码。启动失败应抛异常。</summary>
    Task<int> RunAsync(string exe, string arguments, TimeSpan timeout, CancellationToken ct);

    /// <summary>启动进程但**不等待**(拉起客户端;等它就等于等自己)。</summary>
    void StartDetached(string exe, string arguments);
}

/// <summary>更新器入参(与命令行一一对应)。</summary>
public sealed record UpdaterOptions(
    string MsiPath,
    string ClientExePath,
    bool Silent = true,
    bool RelaunchClient = true,
    /// <summary>旧版 MSI:新版验活失败时用它装回去;为空表示"不验活、不回滚"。</summary>
    string? PreviousMsiPath = null,
    /// <summary>验活超时(默认 90s:客户端启动 + 读配置 + 探一次令牌,足够)。</summary>
    TimeSpan? HealthCheckTimeout = null,
    /// <summary>装 MSI 的超时(默认 10 分钟:大包 + 慢盘)。</summary>
    TimeSpan? InstallTimeout = null);

/// <summary>更新器结论(每一种都要能如实告诉用户)。</summary>
public enum UpdaterOutcomeKind
{
    /// <summary>装上了,且(有旧包时)验活通过。</summary>
    Upgraded,

    /// <summary>msiexec 失败(包坏了/权限/磁盘满):旧版本仍在位。</summary>
    InstallFailed,

    /// <summary>装上了但验活失败,且**没有旧包可回滚**(必须如实说:别假装成功)。</summary>
    HealthCheckFailed,

    /// <summary>装上了但验活失败 → 已重装旧版(用户手里仍是能用的旧版)。</summary>
    HealthCheckFailedRolledBack,

    /// <summary>装上了但验活失败,且**回滚也失败**(最坏情况:必须如实说,不能假装成功)。</summary>
    HealthCheckFailedRollbackFailed,
}

/// <summary>更新器结论。</summary>
public sealed record UpdaterOutcome(
    UpdaterOutcomeKind Kind,
    string Reason,
    int InstallerExitCode,
    bool Relaunched)
{
    public bool Success => Kind == UpdaterOutcomeKind.Upgraded;
}

/// <summary>更新器协议实现(纯决策 + 注入的进程执行)。</summary>
public sealed class UpdaterRunner
{
    /// <summary>msiexec 的固定路径(不改 PATH 查找:服务里 PATH 可能被裁剪)。</summary>
    public const string MsiexecPath = "msiexec.exe";

    /// <summary>msiexec 退出码:0 = 成功;3010 = 成功但需要重启(**也算成功**)。</summary>
    public const int SuccessNeedsReboot = 3010;

    private readonly IProcessRunner _runner;

    public UpdaterRunner(IProcessRunner runner) =>
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));

    /// <summary>装 MSI 的参数(引号与开关集中在这里,免得各处各拼一份)。</summary>
    public static string InstallArguments(string msiPath, bool silent) =>
        $"/i \"{msiPath}\" {(silent ? "/qn" : "/qb")} /norestart";

    /// <summary>把"装上去的客户端能不能活"交给客户端自己回答(见 App 的 --self-check)。</summary>
    public static string SelfCheckArguments => "--self-check";

    /// <summary>执行:装新版 → (给了旧包时)验活 → 失败回滚 → 拉起客户端。</summary>
    public async Task<UpdaterOutcome> RunAsync(UpdaterOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var installTimeout = options.InstallTimeout ?? TimeSpan.FromMinutes(10);
        var healthTimeout = options.HealthCheckTimeout ?? TimeSpan.FromSeconds(90);

        // ① 装新版
        int code;
        try
        {
            code = await _runner.RunAsync(
                MsiexecPath, InstallArguments(options.MsiPath, options.Silent), installTimeout, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 起不了 msiexec(极少见):旧版本还在,把客户端拉起来,让用户至少能用
            Relaunch(options);
            return new UpdaterOutcome(UpdaterOutcomeKind.InstallFailed,
                $"无法启动安装程序:{ex.Message}(旧版本仍在)", -1, options.RelaunchClient);
        }

        if (code != 0 && code != SuccessNeedsReboot)
        {
            Relaunch(options);
            return new UpdaterOutcome(UpdaterOutcomeKind.InstallFailed,
                $"安装失败(msiexec 退出码 {code});旧版本仍在,已重新启动客户端", code, options.RelaunchClient);
        }

        // ② 验活:**总是**验(不因为"没有旧包可回滚"就不验 —— 那会把"装上了但起不来"
        //    谎报成成功)。没有旧包时结论是 HealthCheckFailed,由界面如实告诉用户。
        int health;
        try
        {
            health = await _runner.RunAsync(options.ClientExePath, SelfCheckArguments, healthTimeout, ct)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            health = -1; // 超时/起不来都按"没活"处理 —— 这正是验活的意义
        }

        if (health != 0)
        {
            if (string.IsNullOrWhiteSpace(options.PreviousMsiPath))
            {
                Relaunch(options);
                return new UpdaterOutcome(UpdaterOutcomeKind.HealthCheckFailed,
                    $"新版装上了但验活失败(退出码 {health}),且没有旧安装包可回滚 —— 请手动装回旧版本", code,
                    options.RelaunchClient);
            }

            int rollback;
            try
            {
                rollback = await _runner.RunAsync(
                    MsiexecPath, InstallArguments(options.PreviousMsiPath!, options.Silent), installTimeout, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Relaunch(options);
                return new UpdaterOutcome(UpdaterOutcomeKind.HealthCheckFailedRollbackFailed,
                    $"新版验活失败(退出码 {health}),且回滚无法启动安装程序:{ex.Message}", code,
                    options.RelaunchClient);
            }

            if (rollback != 0 && rollback != SuccessNeedsReboot)
            {
                Relaunch(options);
                return new UpdaterOutcome(UpdaterOutcomeKind.HealthCheckFailedRollbackFailed,
                    $"新版验活失败(退出码 {health}),回滚也失败(msiexec 退出码 {rollback})", code,
                    options.RelaunchClient);
            }

            Relaunch(options);
            return new UpdaterOutcome(UpdaterOutcomeKind.HealthCheckFailedRolledBack,
                $"新版验活失败(退出码 {health}),已回滚到旧版本", code, options.RelaunchClient);
        }

        // ③ 成功:把客户端拉起来
        Relaunch(options);
        return new UpdaterOutcome(UpdaterOutcomeKind.Upgraded,
            $"升级完成(msiexec 退出码 {code})", code, options.RelaunchClient);
    }

    private void Relaunch(UpdaterOptions options)
    {
        if (!options.RelaunchClient)
        {
            return;
        }
        try
        {
            _runner.StartDetached(options.ClientExePath, "");
        }
        catch (Exception)
        {
            // 拉不起来不该把"已经装好了"这件事变成失败:用户手动点图标仍然可用
        }
    }
}
