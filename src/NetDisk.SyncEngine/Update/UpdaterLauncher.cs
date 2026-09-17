// 独立更新器的启动(DE-D-19;Windows 侧)。
//
// 为什么要**独立进程**:Windows 会锁住正在运行的 exe,客户端**不能覆盖自己**;
// 而且换装要替换安装目录里的 exe —— 自己替换自己会"文件被占用"。所以流程是:
// 客户端把新版本 exe 路径告诉**更新器进程**(自己的临时副本)→ 客户端退出 →
// 更新器换装 exe → 更新器把客户端拉起来。
//
// 命令行的纪律(检查器断言):
//   - **新版本 exe 路径必须加引号**(路径含空格是常态;不加引号会被拆成多个参数);
//   - **必须带 --client**(告诉更新器装完拉起哪个 exe)。

using System.Diagnostics;

namespace NetDisk.SyncEngine.Update;

/// <summary>更新器启动参数。</summary>
public sealed record UpdaterCommand(
    string UpdaterPath,
    string NewExePath,
    string ClientExePath,
    bool RelaunchClient = true)
{
    /// <summary>拼出更新器的命令行(引号与开关都在这里统一,免得各处各拼一份)。</summary>
    public string ToArguments()
    {
        var relaunch = RelaunchClient ? " --relaunch" : "";
        return $"--new-exe \"{NewExePath}\" --client \"{ClientExePath}\"{relaunch}";
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
        string newExePath, string clientExePath, bool relaunch = true)
    {
        if (string.IsNullOrWhiteSpace(newExePath))
        {
            throw new ArgumentException("新版本 exe 路径不能为空", nameof(newExePath));
        }
        return new UpdaterCommand(_updaterPath, newExePath.Trim(), clientExePath.Trim(), relaunch);
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
