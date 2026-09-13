// 同步状态列表的代码后置:把 SyncHost 的事件画出来。
//
// 这一层有两条必须守住的纪律:
//   ① **marshal 回 UI 线程**:StatusChanged/Notice 都是在同步引擎的后台线程上触发的,
//      直接改 ItemsSource 会抛 InvalidOperationException(或者更糟:偶发、难复现的
//      界面错乱)。所以统一走 Dispatcher。
//   ② **不在状态里藏业务判断**:状态到中文的映射是纯粹的展示映射;
//      "什么算冲突""冲突副本叫什么"全部由引擎决定(这里只读 SyncHost 给的字段)。

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using NetDisk.SyncEngine;
using NetDisk.SyncEngine.Host;

namespace NetDisk.App.Views;

/// <summary>列表里的一行(纯展示:不参与任何同步决策)。</summary>
public sealed class SyncRow
{
    public required string File { get; init; }

    public required string StateText { get; init; }

    public required string Message { get; init; }

    /// <summary>进度列的文字(空 = 此刻没有传输;`42%` = 传到多少了)。</summary>
    public required string ProgressText { get; init; }

    public required long Version { get; init; }

    /// <summary>是否处于「冲突」态(界面据此决定"冲突处理"按钮能不能点)。</summary>
    public bool IsConflict => StateText == "冲突";
}

// 基类写全限定名(与既有视图一致):UseWPF + UseWindowsForms 同时开启时
// `UserControl` 有二义性(CS0104)。
public partial class SyncView : System.Windows.Controls.UserControl
{
    private readonly ObservableCollection<SyncRow> _rows = new();
    private SyncRuntime? _runtime;

    public SyncView()
    {
        InitializeComponent();
        StatusList.ItemsSource = _rows;
    }

    /// <summary>接上一个已经跑起来的运行时(由主窗口在登录成功后调用)。</summary>
    public void Attach(SyncRuntime runtime)
    {
        _runtime = runtime;
        runtime.Host.StatusChanged += OnStatusChanged;
        runtime.Host.Notice += OnNotice;

        AccountText.Text =
            $"账号 {runtime.Config.LastLogin} @ {runtime.Config.BaseUrl}    同步目录 {runtime.Config.SyncRoot}";

        // 日志开关与路径:开关状态来自配置(默认开),切换即写回配置。
        _suppressLogToggle = true;
        LogToggle.IsChecked = runtime.Config.Logging;
        _suppressLogToggle = false;
        AppLog.Enabled = runtime.Config.Logging;
        LogPathText.Text = $"日志文件:{AppLog.DefaultPath()}(默认启用;关掉后不再记录,便于对照复现)";

        Render(runtime.Host.Status);
        LogStatusTransitions(runtime.Host.Status, initial: true);
    }

    private bool _suppressLogToggle;

    private void OnLogToggle(object sender, RoutedEventArgs e)
    {
        if (_suppressLogToggle || _runtime is null)
        {
            return;
        }
        var on = LogToggle.IsChecked == true;
        AppLog.Enabled = on;
        _runtime.Config.Logging = on;
        _runtime.Config.Save();
        LogPathText.Text = on
            ? $"日志文件:{AppLog.DefaultPath()}(已启用)"
            : $"日志已关闭(此前记录在 {AppLog.DefaultPath()};重新勾选即继续)";
    }

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        var path = AppLog.DefaultPath();
        try
        {
            if (!File.Exists(path))
            {
                System.Windows.MessageBox.Show($"日志还没生成:{path}\n(勾选「记录日志」后产生)");
                return;
            }
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("打开日志失败: " + ex.Message + "\n" + path);
        }
    }

    /// <summary>
    /// 把**每个文件的状态变化**写进日志(排查"这个文件为什么没同步"时最有用的一类信息)。
    /// 只在状态/说明真的变化时记一行,否则每轮对账都会刷一遍同样的内容。
    /// </summary>
    private void LogStatusTransitions(IReadOnlyList<SyncEntryStatus> status, bool initial = false)
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in status.OrderBy(s => s.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var desc = $"{s.State} {s.Message}".Trim();
            seen[s.RelativePath] = desc;
            if (_lastLogged.TryGetValue(s.RelativePath, out var prev) && prev == desc)
            {
                continue;
            }
            AppLog.Write("file", $"{(initial ? "(初始)" : "")}{s.RelativePath} → {desc} v{s.Version}");
        }
        foreach (var gone in _lastLogged.Keys.Where(k => !seen.ContainsKey(k)).ToArray())
        {
            AppLog.Write("file", $"{gone} → (已不在列表:两端都不存在,或已删除)");
        }
        _lastLogged = seen;
    }

    private Dictionary<string, string> _lastLogged = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>解绑(退出/重新登录时):事件不摘掉会把旧界面一起"复活"。</summary>
    public void Detach()
    {
        if (_runtime is null)
        {
            return;
        }
        _runtime.Host.StatusChanged -= OnStatusChanged;
        _runtime.Host.Notice -= OnNotice;
        _runtime = null;
    }

    private void OnStatusChanged(IReadOnlyList<SyncEntryStatus> status)
    {
        // 后台线程 → UI 线程(见文件头 ①)
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Render(status);
            LogStatusTransitions(status);
        }));
    }

    private void OnNotice(string message)
    {
        Dispatcher.BeginInvoke(new Action(() => NoticeText.Text = message));
    }

    /// <summary>
    /// 冲突处理:对列表里**选中的那一行**执行用户的选择。
    ///
    /// 三条纪律:
    ///   ① 只对「冲突」行生效 —— 对普通行点"以本地为准"会变成一次未经确认的覆盖写;
    ///   ② 结果如实回报:引擎说 `false`(例如冲突来自上一次运行、这次运行不知道副本是哪个)
    ///      就照实说"处理不了,请手动处理这两个文件",**不能**显示成"已解决";
    ///   ③ 动作在引擎里做(`SyncHost.ResolveConflictAsync`),界面不碰文件 —— 否则
    ///      "以本地为准"会变成"界面直接把本地文件覆盖到远端",而那条路没有乐观锁、没有冲突裁决。
    /// </summary>
    private async Task ResolveAsync(ConflictResolution choice, string choiceText)
    {
        if (_runtime is null)
        {
            NoticeText.Text = "同步还没启动,无法处理冲突。";
            return;
        }
        if (StatusList.SelectedItem is not SyncRow row || !row.IsConflict)
        {
            NoticeText.Text = "请先在上面的列表里**选中一行「冲突」**,再点这个按钮。";
            return;
        }
        NoticeText.Text = $"正在按「{choiceText}」处理 {row.File}…";
        var ok = await _runtime.Host.ResolveConflictAsync(row.File, choice);
        NoticeText.Text = ok
            ? $"已按「{choiceText}」处理 {row.File}。"
            : $"{row.File} 处理不了(可能是上一次运行留下的冲突):请手动比对本地副本与服务器上的版本。";
    }

    private async void OnResolveKeepLocal(object sender, RoutedEventArgs e) =>
        await ResolveAsync(ConflictResolution.KeepLocal, "以本地为准");

    private async void OnResolveKeepRemote(object sender, RoutedEventArgs e) =>
        await ResolveAsync(ConflictResolution.KeepRemote, "以远端为准");

    private async void OnResolveKeepBoth(object sender, RoutedEventArgs e) =>
        await ResolveAsync(ConflictResolution.KeepBoth, "都保留");

    private void Render(IReadOnlyList<SyncEntryStatus> status)
    {
        _rows.Clear();
        foreach (var s in status.OrderBy(s => s.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            _rows.Add(new SyncRow
            {
                File = s.RelativePath,
                StateText = Describe(s.State),
                Message = s.Message,
                Version = s.Version,
                ProgressText = s.ProgressPercent is { } p ? $"{p:F0}%" : "",
            });
        }

        var synced = status.Count(s => s.State == SyncState.InSync);
        var uploading = status.Count(s => s.State == SyncState.PendingUpload);
        var downloading = status.Count(s => s.State == SyncState.PendingDownload);
        var conflicts = status.Count(s => s.State == SyncState.Conflict);
        var errors = status.Count(s => s.State is SyncState.SpaceRevoked
            or SyncState.PermissionLimited or SyncState.PendingRemoteGone or SyncState.Failed);
        var structureOnly = status.Count(s => s.State == SyncState.StructureOnly);

        SummaryText.Text = status.Count == 0
            ? "尚无文件(同步目录为空,或还没完成第一次对账)"
            : $"共 {status.Count} 个文件:已同步 {synced} · 上传中 {uploading} · 下载中 {downloading} · 冲突 {conflicts} · 错误 {errors}"
              + (structureOnly > 0 ? $" · 仅结构(未搬内容){structureOnly}" : "");
    }

    /// <summary>状态 → 中文。纯展示映射:引擎加状态时这里必须跟着加(编译器会提醒)。</summary>
    private static string Describe(SyncState state) => state switch
    {
        SyncState.InSync => "已同步",
        SyncState.PendingUpload => "上传中",
        SyncState.PendingDownload => "下载中",
        SyncState.Conflict => "冲突",
        SyncState.PendingRemoteGone => "远端已删除",
        SyncState.SpaceRevoked => "空间已移除",
        SyncState.PermissionLimited => "权限受限",
        SyncState.Failed => "失败",
        SyncState.StructureOnly => "仅结构",
        _ => state.ToString(),
    };

    private async void OnTogglePause(object sender, RoutedEventArgs e)
    {
        if (_runtime is null)
        {
            return;
        }
        PauseButton.IsEnabled = false;
        try
        {
            if (_runtime.Host.IsPaused)
            {
                await _runtime.Host.ResumeAsync();
                PauseButton.Content = "暂停同步";
                NoticeText.Text = "已恢复同步";
            }
            else
            {
                await _runtime.Host.PauseAsync();
                PauseButton.Content = "继续同步";
                // 暂停后不会再有 StatusChanged 事件,所以在这里明确写一次界面状态,
                // 否则用户看到的是"最后一轮的旧状态" + 一个变成"继续同步"的按钮(自相矛盾)。
                SummaryText.Text = "已暂停:不再对账与传输(数据未改动)。点「继续同步」恢复。";
                NoticeText.Text = "已暂停";
            }
            AppLog.Write("app", PauseButton.Content?.ToString() == "继续同步" ? "用户暂停了同步" : "用户恢复了同步");
        }
        catch (Exception ex)
        {
            NoticeText.Text = "暂停/恢复失败: " + ex.Message;
            AppLog.Write("app", $"暂停/恢复失败: {ex}");
        }
        finally
        {
            PauseButton.IsEnabled = true;
        }
    }

    private async void OnSyncNow(object sender, RoutedEventArgs e)
    {
        if (_runtime is null)
        {
            return;
        }
        SyncNowButton.IsEnabled = false;
        try
        {
            NoticeText.Text = "正在对账…";
            await _runtime.ReconcileAsync();
            NoticeText.Text = "对账完成";
        }
        catch (Exception ex)
        {
            NoticeText.Text = "对账失败: " + ex.Message;
        }
        finally
        {
            SyncNowButton.IsEnabled = true;
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var root = _runtime?.Config.SyncRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            System.Windows.MessageBox.Show("同步目录还不存在:" + (root ?? "(未配置)"));
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{root}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("打开目录失败: " + ex.Message);
        }
    }
}
