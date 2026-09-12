// 客户端日志(App 层,纯追加写文件)。
//
// 为什么必须有它:桌面客户端的故障现场在**用户机器上**,而托盘气泡一闪而过、
// 状态列表只活在界面上 —— 没有日志就只能靠"你再点一次我看看"。有了
// `%APPDATA%\NetDisk\app.log`,用户直接把文件发过来即可。
//
// 三条纪律:
//   ① **绝不写口令/令牌**:只写人类可读的进展与错误。令牌走 DpapiTokenStore 加密落盘;
//   ② **绝不因为日志失败而影响同步**:磁盘满/权限不足时静默放弃(日志是辅助,不是功能);
//   ③ 单文件不设滚动上限,但超过阈值就整体切换一次(避免无限增长把用户磁盘写满)。

using System.IO;

namespace NetDisk.App;

/// <summary>极简文件日志(线程安全;失败静默)。</summary>
public static class AppLog
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();

    public static string DefaultPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetDisk");
        return Path.Combine(dir, "app.log");
    }

    public static void Write(string tag, string message)
    {
        try
        {
            var path = DefaultPath();
            lock (Gate)
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(path, path + ".1", overwrite: true);
                }
                File.AppendAllText(path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{tag}] {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // 见纪律 ②:日志写不下去不能影响同步
        }
    }
}
