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
using NetDisk.App.Localization;

namespace NetDisk.App.Views;

/// <summary>列表里的一行(纯展示:不参与任何同步决策)。</summary>
public sealed class SyncRow
{
    public required string File { get; init; }

    /// <summary>引擎给的**真状态**(判定用;展示文案见 <see cref="StateText"/>)。</summary>
    public required SyncState State { get; init; }

    public required string StateText { get; init; }

    public required string Message { get; init; }

    /// <summary>进度列的文字(空 = 此刻没有传输;`42%` = 传到多少了)。</summary>
    public required string ProgressText { get; init; }

    public required long Version { get; init; }

    /// <summary>是否处于「冲突」态(界面据此决定"冲突处理"按钮能不能点)。</summary>
    public bool IsConflict => State == SyncState.Conflict;
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
            Loc.F("Sync.AccountLine", runtime.Config.LastLogin, runtime.Config.BaseUrl, runtime.Config.SyncRoot);

        // 日志开关与路径:开关状态来自配置(默认开),切换即写回配置。
        _suppressLogToggle = true;
        LogToggle.IsChecked = runtime.Config.Logging;
        _suppressLogToggle = false;
        AppLog.Enabled = runtime.Config.Logging;
        LogPathText.Text = Loc.F("Sync.LogPathDefault", AppLog.DefaultPath());

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
            ? Loc.F("Sync.LogPathOn", AppLog.DefaultPath())
            : Loc.F("Sync.LogPathOff", AppLog.DefaultPath());
    }

    private void OnOpenLog(object sender, RoutedEventArgs e) => OpenLogFile();

    /// <summary>用记事本打开客户端日志(界面按钮与托盘菜单共用)。</summary>
    public void OpenLogFile()
    {
        var path = AppLog.DefaultPath();
        try
        {
            if (!File.Exists(path))
            {
                System.Windows.MessageBox.Show(Loc.F("Sync.NoLogYet", path));
                return;
            }
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(Loc.F("Sync.OpenLogFail", ex.Message, path));
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
            NoticeText.Text = Loc.T("Sync.NoSyncConflict");
            return;
        }
        if (StatusList.SelectedItem is not SyncRow row || !row.IsConflict)
        {
            NoticeText.Text = Loc.T("Sync.PickConflictFirst");
            return;
        }
        NoticeText.Text = Loc.F("Sync.ResolvingChoice", choiceText, row.File);
        var ok = await _runtime.Host.ResolveConflictAsync(row.File, choice);
        NoticeText.Text = ok
            ? Loc.F("Sync.ResolvedChoice", choiceText, row.File)
            : Loc.F("Sync.ResolveUnhandlable", row.File);
    }

    private async void OnResolveKeepLocal(object sender, RoutedEventArgs e) =>
        await ResolveAsync(ConflictResolution.KeepLocal, Loc.T("Sync.ChoiceKeepLocal"));

    private async void OnResolveKeepRemote(object sender, RoutedEventArgs e) =>
        await ResolveAsync(ConflictResolution.KeepRemote, Loc.T("Sync.ChoiceKeepRemote"));

    private async void OnResolveKeepBoth(object sender, RoutedEventArgs e) =>
        await ResolveAsync(ConflictResolution.KeepBoth, Loc.T("Sync.ChoiceKeepBoth"));

    private void Render(IReadOnlyList<SyncEntryStatus> status)
    {
        // **刷新前先记住用户选中了哪一行**(按相对路径)。
        //
        // 为什么必须保住:这里每次状态更新都会 `_rows.Clear()` 重建列表,而 ListView 的
        // 选中项是"对象引用" —— 重建之后选中就丢了。同步过程中状态更新非常频繁
        // (每传一片、每次状态变化都会来一条),于是用户"明明点过那一行",再点冲突按钮
        // 却被告诉「请先选中一行「冲突」」。实测反馈就是这么来的(用户报"解决冲突好像有问题")。
        var previouslySelected = (StatusList.SelectedItem as SyncRow)?.File;

        _rows.Clear();
        foreach (var s in status.OrderBy(s => s.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            _rows.Add(new SyncRow
            {
                File = s.RelativePath,
                State = s.State, // 判定用**真状态**,不用展示文案(文案可改,判定不该跟着坏)
                StateText = Describe(s.State),
                Message = s.Message,
                Version = s.Version,
                ProgressText = s.ProgressPercent is { } p ? $"{p:F0}%" : "",
            });
        }

        // 恢复选中(同一路径还在就选回它);没有可恢复的选中项时,**自动选中第一个冲突行** ——
        // "有冲突"通常就意味着"我要处理它",让用户少猜一步。
        var restore = previouslySelected is null
            ? _rows.FirstOrDefault(r => r.IsConflict)
            : _rows.FirstOrDefault(r => string.Equals(r.File, previouslySelected, StringComparison.OrdinalIgnoreCase));
        StatusList.SelectedItem = restore; // 可能为 null(列表为空/没有冲突):下面统一刷新按钮状态
        UpdateConflictActions();

        var synced = status.Count(s => s.State == SyncState.InSync);
        var uploading = status.Count(s => s.State == SyncState.PendingUpload);
        var downloading = status.Count(s => s.State == SyncState.PendingDownload);
        var conflicts = status.Count(s => s.State == SyncState.Conflict);
        var errors = status.Count(s => s.State is SyncState.SpaceRevoked
            or SyncState.PermissionLimited or SyncState.PendingRemoteGone or SyncState.Failed);
        var structureOnly = status.Count(s => s.State == SyncState.StructureOnly);

        SummaryText.Text = status.Count == 0
            ? Loc.T("Sync.NoFiles")
            : Loc.F("Sync.Summary", status.Count, synced, uploading, downloading, conflicts, errors)
              + (structureOnly > 0 ? Loc.F("Sync.SummaryStructureOnly", structureOnly) : "");
    }

    private void OnStatusSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateConflictActions();

    /// <summary>
    /// 按"当前选中了什么"决定三个冲突按钮能不能点,并把原因写在旁边。
    ///
    /// 为什么要有它(而不是等用户点了再报错):第一版按钮一直可点,点下去才知道"要先选中一行冲突" ——
    /// 顺序靠猜;而"为什么不能点"比"点了被拒"更省一次往返。判定用引擎给的真状态
    /// (`SyncState.Conflict`),不用展示文案 —— 文案是给人看的,判定不该跟着文案坏。
    /// </summary>
    private void UpdateConflictActions()
    {
        var row = StatusList.SelectedItem as SyncRow;
        var enabled = row is { IsConflict: true };
        KeepLocalButton.IsEnabled = enabled;
        KeepRemoteButton.IsEnabled = enabled;
        KeepBothButton.IsEnabled = enabled;

        if (row is null)
        {
            ConflictHintText.Text = Loc.T("Sync.ConflictPickHint");
            return;
        }
        if (!row.IsConflict)
        {
            ConflictHintText.Text = Loc.F("Sync.ConflictSelected", row.StateText, row.File);
            return;
        }
        // 冲突行:顺带告诉用户"这次能不能自动处理"(引擎是否还记得本地副本在哪 —— 见 ResolveConflictAsync)
        var resolvable = _runtime?.Host.CanResolveConflict(row.File) ?? false;
        ConflictHintText.Text = resolvable
            ? Loc.F("Sync.ConflictSelectedNow", row.File)
            : Loc.F("Sync.ConflictSelectedNoLocal", row.File);
    }

    /// <summary>状态 → 中文。纯展示映射:引擎加状态时这里必须跟着加(编译器会提醒)。</summary>
    private static string Describe(SyncState state) => state switch
    {
        SyncState.InSync => Loc.T("State.Synced"),
        SyncState.PendingUpload => Loc.T("State.Uploading"),
        SyncState.PendingDownload => Loc.T("State.Downloading"),
        SyncState.Conflict => Loc.T("State.Conflict"),
        SyncState.PendingRemoteGone => Loc.T("State.RemoteDeleted"),
        SyncState.SpaceRevoked => Loc.T("State.SpaceRemoved"),
        SyncState.PermissionLimited => Loc.T("State.Permission"),
        SyncState.Failed => Loc.T("State.Failed"),
        SyncState.StructureOnly => Loc.T("State.StructureOnly"),
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
                PauseButton.Content = Loc.T("Sync.Pause");
                NoticeText.Text = Loc.T("Sync.Resumed");
            }
            else
            {
                await _runtime.Host.PauseAsync();
                PauseButton.Content = Loc.T("Sync.Resume");
                // 暂停后不会再有 StatusChanged 事件,所以在这里明确写一次界面状态,
                // 否则用户看到的是"最后一轮的旧状态" + 一个变成"继续同步"的按钮(自相矛盾)。
                SummaryText.Text = Loc.T("Sync.PausedSummary");
                NoticeText.Text = Loc.T("Sync.Paused");
            }
            AppLog.Write("app", PauseButton.Content?.ToString() == Loc.T("Sync.Resume") ? "用户暂停了同步" : "用户恢复了同步");
        }
        catch (Exception ex)
        {
            NoticeText.Text = Loc.F("Sync.PauseFail", ex.Message);
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
            NoticeText.Text = Loc.T("Sync.Syncing");
            await _runtime.ReconcileAsync();
            NoticeText.Text = Loc.T("Sync.Done");
        }
        catch (Exception ex)
        {
            NoticeText.Text = Loc.F("Sync.SyncFail", ex.Message);
        }
        finally
        {
            SyncNowButton.IsEnabled = true;
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e) => OpenSyncFolder();

    /// <summary>打开同步目录(界面按钮与**托盘菜单**共用同一份实现:两处各写一遍必然漂移)。</summary>
    public void OpenSyncFolder()
    {
        var root = _runtime?.Config.SyncRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            System.Windows.MessageBox.Show(Loc.F("Sync.RootMissing", root ?? Loc.T("Sync.RootUnset")));
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{root}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(Loc.F("Sync.OpenFolderFail", ex.Message));
        }
    }
}
