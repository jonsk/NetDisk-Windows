// 把"正在跑的客户端"接到升级序列上(DE-D-19 的宿主适配器)。
//
// 编排(`UpdateOrchestrator`)只认 `IUpdateHost` 这个窄接口;本文件是它在**真实客户端**里的实现:
//
//   暂停  → SyncHost.PauseAsync(队列先停,在飞请求以取消收尾)
//   排空  → SyncHost.DrainTransfersAsync(是"等",不是"清";超时就让编排器放弃本次升级)
//   迁移  → 重新打开一次状态库(应用本版本待执行的迁移,并验证库可读)
//   重启  → 把**自己复制到临时目录**,用 `--apply-update` 拉起那份副本,然后**立刻退出自己**
//
// 为什么"重启"要复制自己:Windows 会锁住正在运行的 exe,客户端**不能覆盖自己**;
// 而更新器必须活到安装完成(安装过程会替换安装目录里的那个 exe)。用临时副本既解决锁文件,
// 又不违反"单文件发布"(MSI 载荷仍然只有一个 exe)。
//
// 为什么要"立刻退出":msiexec 要替换安装目录里的 exe,而那个文件正被我们锁着 ——
// 客户端不退出,安装器要么失败、要么走重启管理器强杀我们(两种都很难看)。
// 所以 `UpdaterLaunched` 事件是**成功拉起更新器之后立刻**发出的,界面收到就 Shutdown。

using NetDisk.SyncEngine.Update;

namespace NetDisk.SyncEngine.Host;

/// <summary>真实客户端的升级宿主。</summary>
public sealed class SyncHostUpdateHost : IUpdateHost
{
    private readonly SyncRuntime _runtime;
    private readonly string _msiPath;
    private readonly string? _previousMsiPath;
    private readonly string _clientExePath;
    private readonly TimeSpan _drainTimeout;

    /// <param name="msiPath">要装的安装包(界面让用户选的)。</param>
    /// <param name="previousMsiPath">旧安装包(可选):新版验活失败时用它回滚。</param>
    /// <param name="clientExePath">安装目录里的客户端 exe;默认取当前进程路径。</param>
    public SyncHostUpdateHost(
        SyncRuntime runtime,
        string msiPath,
        string? previousMsiPath = null,
        string? clientExePath = null,
        TimeSpan? drainTimeout = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _msiPath = msiPath;
        _previousMsiPath = previousMsiPath;
        _clientExePath = clientExePath ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("拿不到客户端 exe 路径");
        _drainTimeout = drainTimeout ?? TimeSpan.FromMinutes(2);
    }

    /// <summary>更新器已经拉起来了:界面收到后应当**立刻退出**(给安装器让路)。</summary>
    public event Action? UpdaterLaunched;

    /// <summary>给界面用的一句话描述(用户点"检查更新"前要知道"会发生什么")。</summary>
    public string DescribePlan() =>
        "将按顺序执行:暂停同步 → 等传输跑完 → 迁移状态库 → 启动更新器安装\r\n" +
        _msiPath + "\r\n" +
        "然后客户端会退出;安装完成后自动重新启动。" +
        (string.IsNullOrEmpty(_previousMsiPath) ? "" : "\r\n(已提供旧安装包:新版起不来会自动回滚)");

    public Task PauseSyncAsync(CancellationToken ct) => _runtime.Host.PauseAsync();

    public Task ResumeSyncAsync(CancellationToken ct) => _runtime.Host.ResumeAsync(ct);

    public Task<bool> DrainQueueAsync(TimeSpan timeout, CancellationToken ct) =>
        _runtime.Host.DrainTransfersAsync(timeout == default ? _drainTimeout : timeout, ct);

    /// <summary>
    /// 迁移本地状态库:重新打开一次(迁移在打开时执行)。旧版本二进制里当然没有新版本的迁移,
    /// 所以这一步此时多半只是"验证库可读、没有半截状态";真正的迁移由**新版本**首次启动时执行 ——
    /// 那时失败也已经发生在"还能回滚"之后(见 UpdateOrchestrator 的注释)。
    /// </summary>
    public Task MigrateStateAsync(CancellationToken ct)
    {
        var path = StatePath();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return Task.CompletedTask; // 没有状态库(尚未同步过):没什么可迁移的
        }
        using var store = StateStore.Open(path);
        return Task.CompletedTask;
    }

    /// <summary>启动更新器(临时副本)并通知界面退出。</summary>
    public Task RestartIntoUpdaterAsync(CancellationToken ct)
    {
        var stage = Path.Combine(Path.GetTempPath(), "netdisk-updater-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var updater = Path.Combine(stage, "NetDisk.App.exe");
        File.Copy(_clientExePath, updater, overwrite: true);

        var command = new UpdaterLauncher(updater).BuildCommand(
            _msiPath, _clientExePath, silent: true, relaunch: true, previousMsiPath: _previousMsiPath);
        new UpdaterLauncher(updater).Launch(command);
        UpdaterLaunched?.Invoke();
        return Task.CompletedTask;
    }

    public Task RollbackAsync(CancellationToken ct) =>
        throw new NotSupportedException("回滚由更新器(临时副本)负责:它手里有旧安装包与验活结果");

    /// <summary>状态库路径:与配置同目录(与 <c>SyncRuntime</c> 的取法一致)。</summary>
    private string? StatePath() =>
        string.IsNullOrEmpty(_runtime.Config.Path)
            ? null
            : Path.Combine(Path.GetDirectoryName(_runtime.Config.Path)!, "state.db");
}
