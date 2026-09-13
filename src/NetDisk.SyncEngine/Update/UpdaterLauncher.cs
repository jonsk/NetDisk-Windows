// 独立更新器的启动(DE-D-19;Windows 侧)。
//
// 为什么要**独立进程**:Windows 会锁住正在运行的 exe,客户端**不能覆盖自己**;
// 而且 `msiexec` 卸载旧版本时会要求"客户端已退出" —— 自己卸载自己的结果是
// "卸载到一半进程没了",留下半装状态。所以流程是:客户端把 MSI 路径与收尾动作告诉
// 更新器进程 → 客户端退出 → 更新器装 MSI → 更新器把客户端拉起来。
//
// 命令行的两条纪律(检查器断言):
//   - **MSI 路径必须加引号**(路径含空格是常态;不加引号会被拆成多个参数);
//   - **必须 /qn**(静默):升级途中弹 UI 会让"自动更新"变成"用户必须点一下",
//     而 perUser 包不需要提权,静默升级不会触发 UAC(R-26 的 perUser 正是为此)。
//
// 失败回滚:更新器发现"新版本装上了但起不来"(见 `--health-check`),就用**旧 MSI**
// 再装回去 —— 这一半需要真实 MSI 与本机管理员以外权限,属于需要真机演练的部分,
// 这里把命令行与顺序固定下来并做断言,真机演练列进 DE-D-21 的 soak/发布检查。

using System.Diagnostics;

namespace NetDisk.SyncEngine.Update;

/// <summary>更新器启动参数。</summary>
public sealed record UpdaterCommand(
    string UpdaterPath,
    string MsiPath,
    string ClientExePath,
    bool Silent = true,
    bool RelaunchClient = true,
    /// <summary>旧安装包(可选):新版验活失败时用它装回去。空 = 明确"没有回滚退路"。</summary>
    string? PreviousMsiPath = null)
{
    /// <summary>拼出更新器的命令行(引号与开关都在这里统一,免得各处各拼一份)。</summary>
    public string ToArguments()
    {
        var silent = Silent ? " /qn" : " /qb";
        var relaunch = RelaunchClient ? " --relaunch" : "";
        var previous = string.IsNullOrWhiteSpace(PreviousMsiPath)
            ? ""
            : $" --previous-msi \"{PreviousMsiPath}\"";
        return $"--msi \"{MsiPath}\"{silent} --client \"{ClientExePath}\"{relaunch}{previous}";
    }

    public override string ToString() => "\"" + UpdaterPath + "\" " + ToArguments();
}

/// <summary>更新器进程启动器(把"启动"与"命令行构造"分开,后者可单测)。</summary>
public sealed class UpdaterLauncher
{
    private readonly string _updaterPath;

    public UpdaterLauncher(string updaterPath)
    {
        if (string.IsNullOrWhiteSpace(updaterPath))
        {
            throw new ArgumentException("更新器路径不能为空", nameof(updaterPath));
        }
        _updaterPath = updaterPath;
    }

    /// <summary>构造更新器命令(纯函数,可断言)。</summary>
    public UpdaterCommand BuildCommand(
        string msiPath, string clientExePath, bool silent = true, bool relaunch = true,
        string? previousMsiPath = null)
    {
        if (string.IsNullOrWhiteSpace(msiPath))
        {
            throw new ArgumentException("MSI 路径不能为空", nameof(msiPath));
        }
        return new UpdaterCommand(_updaterPath, msiPath.Trim(), clientExePath.Trim(), silent, relaunch,
            string.IsNullOrWhiteSpace(previousMsiPath) ? null : previousMsiPath.Trim());
    }

    /// <summary>
    /// 启动更新器(**不等待**:客户端马上要退出,等它就永远等不到)。
    /// </summary>
    public void Launch(UpdaterCommand command)
    {
        var psi = new ProcessStartInfo(command.UpdaterPath, command.ToArguments())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var proc = Process.Start(psi)
                   ?? throw new InvalidOperationException("更新器进程启动失败:" + command.UpdaterPath);
        _ = proc; // 刻意不 WaitForExit:客户端随后就退出,由更新器独立完成升级
    }
}
