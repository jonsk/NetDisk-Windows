// 自动更新的"升级序列"(DE-D-19)。
//
// 清单给的顺序是**硬要求**:暂停同步 → 排空队列 → 迁移 SQLite → 重启。每一步的理由都不一样,
// 而且顺序错了都会留下"看起来升级成功、其实数据没跟上"的状态:
//
//  1) **先暂停同步**:不暂停就换版本,新版本启动时会看到"旧版本还在写"的中间态;
//  2) **再排空队列**:这一步是**等**,不是清空。正在上传/下载的任务被硬杀掉,会留下
//     半截的暂存文件与"已预留但没结算"的额度(服务端要等 24h 回收班车才释放);
//     所以排空有超时,**超时就放弃本次升级**(下次再说),而不是杀任务;
//  3) **迁移状态库**:先迁移再重启,是为了让"迁移失败"发生在**还能回退**的时候。
//     如果让新版本第一次启动时迁移,失败就已经是"新版本 + 旧 schema"的现场了。
//  4) **最后重启/交给 updater**:更新器是**独立进程**(见 UpdaterLauncher):客户端自己
//     不能覆盖自己正在运行的 exe(Windows 会锁文件),也不能在自己被卸载时自杀式继续跑。
//
// **失败必须回到可用状态**:任何一步失败都要 ①不重启 ②**恢复同步**(否则用户面对一个
// "暂停着、什么都不干"的客户端)③尝试回滚(MSI 的 MajorUpgrade 本身是事务性的,
// 安装失败时旧版本仍在位;若新版本装上了但启动失败,更新器用旧 MSI 再装回去)。
//
// 这一层刻意做成"可注入步骤"的纯编排:升级流程是**一锤子买卖**,不可能靠人工反复演练
// 来验证顺序,只能在检查器里把每一步的调用次序与失败路径跑出来。

namespace NetDisk.SyncEngine.Update;

/// <summary>升级序列的每一步(便于断言顺序与失败路径)。</summary>
public enum UpdateStep
{
    PauseSync,
    DrainQueue,
    MigrateState,
    Restart,
}

/// <summary>升级结果。</summary>
public sealed record UpdateOutcome(
    bool Upgraded,
    UpdateStep? FailedAt,
    bool RolledBack,
    bool SyncResumed,
    string Reason);

/// <summary>升级编排的依赖(由宿主实现;检查器注入假的来跑顺序与失败路径)。</summary>
public interface IUpdateHost
{
    /// <summary>暂停同步(folder watcher + 状态机);**必须幂等**。</summary>
    Task PauseSyncAsync(CancellationToken ct);

    /// <summary>恢复同步(**任何路径都必须被调用**,否则用户面对一个卡住的客户端)。</summary>
    Task ResumeSyncAsync(CancellationToken ct);

    /// <summary>
    /// 排空传输队列:等待在飞任务结束。返回 true = 已排空;false = 超时(放弃本次升级)。
    /// </summary>
    Task<bool> DrainQueueAsync(TimeSpan timeout, CancellationToken ct);

    /// <summary>迁移本地状态库(SQLite);失败应抛异常(调用方据此回退)。</summary>
    Task MigrateStateAsync(CancellationToken ct);

    /// <summary>启动独立更新器并退出自己;失败应抛异常。</summary>
    Task RestartIntoUpdaterAsync(CancellationToken ct);

    /// <summary>回滚到旧版本(重装旧 MSI / 让新版本自愈)。失败应抛异常。</summary>
    Task RollbackAsync(CancellationToken ct);
}

/// <summary>升级参数。</summary>
public sealed record UpdatePolicy
{
    /// <summary>排空队列的超时(默认 2 分钟:大文件上传可能还在跑,但不能无限等)。</summary>
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>升级编排器。</summary>
public sealed class UpdateOrchestrator
{
    private readonly IUpdateHost _host;
    private readonly UpdatePolicy _policy;

    public UpdateOrchestrator(IUpdateHost host, UpdatePolicy? policy = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _policy = policy ?? new UpdatePolicy();
    }

    /// <summary>已执行的步骤序列(诊断;检查器用它断言顺序)。</summary>
    public List<UpdateStep> Executed { get; } = new();

    /// <summary>
    /// 执行升级序列。**任何失败都不抛给调用方**:升级失败不该把客户端炸掉 ——
    /// 返回 <see cref="UpdateOutcome"/> 让 UI 如实告诉用户"这次没升级成,已回到原样"。
    /// </summary>
    public async Task<UpdateOutcome> RunAsync(CancellationToken ct = default)
    {
        UpdateStep? failedAt = null;
        var reason = "";

        try
        {
            Executed.Add(UpdateStep.PauseSync);
            await _host.PauseSyncAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 暂停都失败:什么都不该继续做(队列还在跑,迁移状态库风险更大)
            return new UpdateOutcome(false, UpdateStep.PauseSync, false, false,
                "暂停同步失败,已放弃本次升级:" + ex.Message);
        }

        try
        {
            Executed.Add(UpdateStep.DrainQueue);
            if (!await _host.DrainQueueAsync(_policy.DrainTimeout, ct).ConfigureAwait(false))
            {
                failedAt = UpdateStep.DrainQueue;
                reason = $"传输队列在 {_policy.DrainTimeout.TotalSeconds:0}s 内没有排空,已放弃本次升级" +
                         "(不硬杀在飞任务:那会留下半截暂存文件与未结算的预留额度)";
                return await AbortAsync(failedAt.Value, reason, ct).ConfigureAwait(false);
            }

            Executed.Add(UpdateStep.MigrateState);
            await _host.MigrateStateAsync(ct).ConfigureAwait(false);

            Executed.Add(UpdateStep.Restart);
            await _host.RestartIntoUpdaterAsync(ct).ConfigureAwait(false);

            // 交给更新器之后就由新进程接管;这里的"恢复同步"由新版本启动时自己做
            return new UpdateOutcome(true, null, false, false, "已交给更新器,即将重启完成升级");
        }
        catch (Exception ex)
        {
            failedAt ??= Executed.Count > 0 ? Executed[^1] : UpdateStep.MigrateState;
            reason = "升级序列失败(" + failedAt + "):" + ex.Message;
        }

        return await AbortAsync(failedAt.Value, reason, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 失败路径:回滚 + **恢复同步**。恢复同步无论回滚成败都要做 ——
    /// 用户宁可要一个"旧版本 + 能同步"的客户端,也不要一个"暂停着、什么都不干"的客户端。
    /// </summary>
    private async Task<UpdateOutcome> AbortAsync(UpdateStep failedAt, string reason, CancellationToken ct)
    {
        var rolledBack = false;
        try
        {
            await _host.RollbackAsync(ct).ConfigureAwait(false);
            rolledBack = true;
        }
        catch (Exception ex)
        {
            reason += "(回滚也失败:" + ex.Message + ")";
        }

        var resumed = false;
        try
        {
            await _host.ResumeSyncAsync(ct).ConfigureAwait(false);
            resumed = true;
        }
        catch (Exception ex)
        {
            reason += "(恢复同步失败:" + ex.Message + ")";
        }

        return new UpdateOutcome(false, failedAt, rolledBack, resumed, reason);
    }
}
