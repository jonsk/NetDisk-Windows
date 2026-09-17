// 更新器**协议逻辑**(DE-D-19;可注入、可断言的那一半)。
//
// 为什么客户端要兼任"更新器"(而不是再出一个 updater.exe):
//   产品要求是**单文件发布**,发布目录里**恰好一个 exe**。多一个 updater.exe 既破坏单文件,
//   又要再维护一套发布与签名。所以做法是:
//   客户端把**自己复制到临时目录**,用 `--apply-update` 参数把那份副本拉起来,然后自己退出;
//   副本(不是安装目录里那个文件)去**换装**安装目录里的 exe —— 这样"自己覆盖自己"的锁文件问题
//   不存在,安装目录里最终仍然是**唯一的那个 exe**。
//
// 换装模型(取代原 WiX/MSI 的 msiexec):
//   ① 先把当前 exe 备份一份(回滚用);
//   ② 把新版本 exe 复制覆盖到安装目录(换装);
//   ③ 跑一次 `--self-check` 验活:新版起不来就**还原备份**(回滚),再拉起旧版;
//   ④ 验活通过就删掉备份、拉起新版。
//
// 本文件只放**决策**;真正起进程由注入的 IProcessRunner 做,文件拷贝由注入的 IFileSwap 做 ——
// 于是"先换装再验活再回滚"的每一步都能在零依赖检查器里跑出来(升级是一锤子买卖,
// 不可能靠人工反复演练来验证顺序)。
//
// 失败时的收尾同样有硬规矩:无论哪条失败路径,只要客户端此刻**能启动**,就要把它拉起来 ——
// 否则用户升级失败后面对的是"程序不见了"。

using System.IO;

namespace NetDisk.SyncEngine.Update;

/// <summary>跑外部进程(可注入:检查器用假的,真实运行用 <see cref="ProcessRunner"/>)。</summary>
public interface IProcessRunner
{
    /// <summary>跑进程并等它结束;返回退出码。启动失败应抛异常。</summary>
    Task<int> RunAsync(string exe, string arguments, TimeSpan timeout, CancellationToken ct);

    /// <summary>启动进程但**不等待**(拉起客户端;等它就等于等自己)。</summary>
    void StartDetached(string exe, string arguments);
}

/// <summary>换装涉及的文件操作(可注入:检查器用假的,真实运行用 <see cref="RealFileSwap"/>)。</summary>
public interface IFileSwap
{
    /// <summary>文件是否存在。</summary>
    bool Exists(string path);

    /// <summary>把当前 exe 备份一份(覆盖写)。</summary>
    void Backup(string source, string backup);

    /// <summary>把新版本 exe 复制覆盖到安装目录(换装)。</summary>
    void SwapIn(string newExe, string target);

    /// <summary>把备份还原回安装目录(回滚)。</summary>
    void Restore(string backup, string target);

    /// <summary>删除文件(成功后清掉旧备份)。</summary>
    void Delete(string path);
}

/// <summary>更新器入参(与命令行一一对应)。</summary>
public sealed record UpdaterOptions(
    /// <summary>新版本 exe 的完整路径(用户选的 / 下载下来的)。</summary>
    string NewExePath,
    /// <summary>安装目录里要被替换的当前 exe 路径。</summary>
    string ClientExePath,
    bool RelaunchClient = true,
    /// <summary>当前 exe 的备份路径:新版验活失败时用它还原。空 = 明确"没有回滚退路"。</summary>
    string? BackupExePath = null,
    /// <summary>验活超时(默认 90s:客户端启动 + 读配置 + 探一次令牌,足够)。</summary>
    TimeSpan? HealthCheckTimeout = null);

/// <summary>更新器结论(每一种都要能如实告诉用户)。</summary>
public enum UpdaterOutcomeKind
{
    /// <summary>换装成功,且验活通过。</summary>
    Upgraded,

    /// <summary>换装失败(写不进目标 / 磁盘满 / 权限):旧版本仍在位。</summary>
    InstallFailed,

    /// <summary>换装成功但验活失败,且**没有备份可回滚**(必须如实说:别假装成功)。</summary>
    HealthCheckFailed,

    /// <summary>换装成功但验活失败 → 已还原备份(用户手里仍是能用的旧版)。</summary>
    HealthCheckFailedRolledBack,

    /// <summary>换装成功但验活失败,且**回滚也失败**(最坏情况:必须如实说,不能假装成功)。</summary>
    HealthCheckFailedRollbackFailed,
}

/// <summary>更新器结论。</summary>
public sealed record UpdaterOutcome(
    UpdaterOutcomeKind Kind,
    string Reason,
    int ExitCode,
    bool Relaunched)
{
    public bool Success => Kind == UpdaterOutcomeKind.Upgraded;
}

/// <summary>更新器协议实现(纯决策 + 注入的进程/文件执行)。</summary>
public sealed class UpdaterRunner
{
    /// <summary>把"换装上去的客户端能不能活"交给客户端自己回答(见 App 的 --self-check)。</summary>
    public const string SelfCheckArguments = "--self-check";

    /// <summary>不显式给备份路径时,自动用 `<client>.previous` 作为备份。</summary>
    public const string BackupSuffix = ".previous";

    private readonly IProcessRunner _runner;
    private readonly IFileSwap _files;

    public UpdaterRunner(IProcessRunner runner, IFileSwap? files = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _files = files ?? new RealFileSwap();
    }

    /// <summary>执行:备份 → 换装 → 验活 → (失败回滚) → 拉起客户端。</summary>
    public async Task<UpdaterOutcome> RunAsync(UpdaterOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var healthTimeout = options.HealthCheckTimeout ?? TimeSpan.FromSeconds(90);
        var backup = string.IsNullOrWhiteSpace(options.BackupExePath)
            ? options.ClientExePath + BackupSuffix
            : options.BackupExePath;

        // ① 备份当前 exe(回滚用)。换装失败/新版起不来时靠它还原。
        //    不显式给备份路径时也**自动**备份(真实运行路径),确保总有回退。
        //    备份失败不是致命的:只是失去回滚能力,仍尝试换装(随后会走"无备份"分支)。
        try
        {
            if (_files.Exists(options.ClientExePath))
            {
                _files.Backup(options.ClientExePath, backup);
            }
        }
        catch (Exception)
        {
            // 备份失败:后续若需要回滚会用不到它,如实走"无备份"分支
        }

        // ② 换装:把新版 exe 拷进安装目录(覆盖旧版)。
        try
        {
            var sameFile = string.Equals(
                Path.GetFullPath(options.NewExePath),
                Path.GetFullPath(options.ClientExePath),
                StringComparison.OrdinalIgnoreCase);
            if (!sameFile)
            {
                _files.SwapIn(options.NewExePath, options.ClientExePath);
            }
        }
        catch (Exception ex)
        {
            // 换装失败:旧版仍在位。把客户端拉起来,让用户至少能用。
            Relaunch(options);
            return new UpdaterOutcome(UpdaterOutcomeKind.InstallFailed,
                $"换装失败(无法写入目标 exe:{ex.Message});旧版本仍在,已重新启动客户端", -1, options.RelaunchClient);
        }

        // ③ 验活:**总是**验(不因为"没有备份可回滚"就不验 —— 那会把"装上了但起不来"
        //    谎报成成功)。没有备份时结论是 HealthCheckFailed,由界面如实告诉用户。
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
            if (!_files.Exists(backup))
            {
                Relaunch(options);
                return new UpdaterOutcome(UpdaterOutcomeKind.HealthCheckFailed,
                    $"新版换装成功但验活失败(退出码 {health}),且没有可用备份可回滚 —— 请手动恢复旧版本", -1,
                    options.RelaunchClient);
            }

            try
            {
                _files.Restore(backup, options.ClientExePath);
            }
            catch (Exception ex)
            {
                Relaunch(options);
                return new UpdaterOutcome(UpdaterOutcomeKind.HealthCheckFailedRollbackFailed,
                    $"新版验活失败(退出码 {health}),且回滚失败:{ex.Message}", -1, options.RelaunchClient);
            }

            Relaunch(options);
            return new UpdaterOutcome(UpdaterOutcomeKind.HealthCheckFailedRolledBack,
                $"新版验活失败(退出码 {health}),已回滚到旧版本", 0, options.RelaunchClient);
        }

        // ④ 成功:清掉备份,拉起新版(此时 ClientExePath 已是新版本)
        try
        {
            _files.Delete(backup);
        }
        catch (Exception)
        {
            // 备份删不掉不影响升级成功
        }

        Relaunch(options);
        return new UpdaterOutcome(UpdaterOutcomeKind.Upgraded, "升级完成(换装 + 验活通过)", 0, options.RelaunchClient);
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

/// <summary>基于 <see cref="File"/> 的真实换装实现。</summary>
public sealed class RealFileSwap : IFileSwap
{
    public bool Exists(string path) => File.Exists(path);

    public void Backup(string source, string backup) => File.Copy(source, backup, overwrite: true);

    public void SwapIn(string newExe, string target) => File.Copy(newExe, target, overwrite: true);

    public void Restore(string backup, string target) => File.Copy(backup, target, overwrite: true);

    public void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
