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

    public required long Version { get; init; }
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
        Render(runtime.Host.Status);
    }

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
        Dispatcher.BeginInvoke(new Action(() => Render(status)));
    }

    private void OnNotice(string message)
    {
        Dispatcher.BeginInvoke(new Action(() => NoticeText.Text = message));
    }

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
            });
        }

        var synced = status.Count(s => s.State == SyncState.InSync);
        var uploading = status.Count(s => s.State == SyncState.PendingUpload);
        var downloading = status.Count(s => s.State == SyncState.PendingDownload);
        var conflicts = status.Count(s => s.State == SyncState.Conflict);
        var errors = status.Count(s => s.State is SyncState.SpaceRevoked
            or SyncState.PermissionLimited or SyncState.PendingRemoteGone);

        SummaryText.Text = status.Count == 0
            ? "尚无文件(同步目录为空,或还没完成第一次对账)"
            : $"共 {status.Count} 个文件:已同步 {synced} · 上传中 {uploading} · 下载中 {downloading} · 冲突 {conflicts} · 错误 {errors}";
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
        _ => state.ToString(),
    };

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
