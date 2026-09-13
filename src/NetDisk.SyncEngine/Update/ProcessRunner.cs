// 真实进程执行(UpdaterRunner 的 Windows 实现)。
//
// 与协议逻辑分开的理由:那一半要能在零依赖检查器里跑(升级顺序不可能靠人工反复演练),
// 这一半只做"CreateProcess + 等 / 不等",没有决策。
//
// 两条实现细节:
//   · `UseShellExecute = false` + `CreateNoWindow = true`:更新器自己是临时目录里的副本,
//     在用户桌面上弹一个黑框只会让人以为出问题了;
//   · 拉起客户端用 `Process.Start` 且**不等待** —— 我们(临时副本)随后就要退出,
//     等客户端等于等一个依赖我们退出的东西。

using System.Diagnostics;

namespace NetDisk.SyncEngine.Update;

/// <summary>基于 <see cref="Process"/> 的真实执行器。</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<int> RunAsync(string exe, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)
                         ?? throw new InvalidOperationException("进程启动失败:" + exe);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 超时:把子进程收掉,再如实抛出去(调用方按"没活/没装上"处理)
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // 进程可能刚好自己退出了
            }
            throw new TimeoutException($"等待 {exe} 超过 {timeout.TotalSeconds:F0}s");
        }
        return proc.ExitCode;
    }

    public void StartDetached(string exe, string arguments)
    {
        var psi = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        _ = Process.Start(psi);
    }
}
