// 远端文件浏览器的代码后置。
//
// 三条纪律:
//   ① **只读**:这里只调 `RemoteBrowser` 的列目录 / 定位 / 预览三个方法 ——
//      它们都不写远端、也不写同步目录(预览副本落在临时目录)。
//      在浏览页面里加"上传/删除"这类动作是**另一次产品决定**,不该顺手做进来。
//   ② **不在这里拼路径**:面包屑与地址栏都用 `BrowseEntry.Path`(由引擎给出),
//      两处拼路径必然漂移,而漂移表现为"地址栏写着 A、列表里是 B"。
//   ③ **慢操作不卡界面**:列目录/下载预览都是网络操作,全部 await 到后台线程,
//      按钮在等待期间禁用(否则用户会连点,堆出一串重复下载)。

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using NetDisk.SyncEngine.Files;
using NetDisk.SyncEngine.Host;

namespace NetDisk.App.Views;

/// <summary>列表里的一行(纯展示)。</summary>
public sealed class RemoteRow
{
    public required BrowseEntry Entry { get; init; }

    public string Name => Entry.IsDir ? Entry.Name + "\\" : Entry.Name;

    public string KindText => Entry.IsDir ? "目录" : "文件";

    public string SizeText => Entry.SizeText;

    public long Version => Entry.Version;
}

public partial class RemoteView : System.Windows.Controls.UserControl
{
    private readonly ObservableCollection<RemoteRow> _rows = new();
    private RemoteBrowser? _browser;
    private string? _currentId;          // null = 空间根
    private string _currentPath = "";    // "" = 空间根
    private bool _busy;

    public RemoteView()
    {
        InitializeComponent();
        EntryList.ItemsSource = _rows;
        UpButton.IsEnabled = false;
    }

    /// <summary>接上一个已经跑起来的运行时(由主窗口在登录成功后调用)。</summary>
    public void Attach(SyncRuntime runtime)
    {
        _browser = runtime.Browser;
        _currentId = null;
        _currentPath = "";
        StatusText.Text = "";
        _ = NavigateAsync(null, "");
    }

    /// <summary>解绑(退出/重新登录):清掉列表,避免显示上一个账号的内容。</summary>
    public void Detach()
    {
        _browser = null;
        _rows.Clear();
        StatusText.Text = "";
        PathText.Text = "位置:/";
        UpButton.IsEnabled = false;
    }

    private async Task NavigateAsync(string? parentId, string path)
    {
        if (_browser is null || _busy)
        {
            return;
        }
        SetBusy(true);
        try
        {
            var entries = await _browser.ListAsync(parentId, path);
            _currentId = parentId;
            _currentPath = path;
            _rows.Clear();
            foreach (var e in entries)
            {
                _rows.Add(new RemoteRow { Entry = e });
            }
            PathText.Text = "位置:/" + _currentPath;
            UpButton.IsEnabled = _currentPath.Length > 0;
            StatusText.Text = entries.Count == 0 ? "（这一层没有条目）" : $"共 {entries.Count} 项";
        }
        catch (Exception ex)
        {
            StatusText.Text = "列目录失败:" + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RefreshButton.IsEnabled = !busy;
        OpenButton.IsEnabled = !busy;
        UpButton.IsEnabled = !busy && _currentPath.Length > 0;
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) =>
        await NavigateAsync(_currentId, _currentPath);

    private async void OnUp(object sender, RoutedEventArgs e)
    {
        if (_currentPath.Length == 0)
        {
            return;
        }
        var segments = _currentPath.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        segments.RemoveAt(segments.Count - 1);
        // 逐级定位到父目录:路径 → 条目(用引擎给出的路径,见文件头 ②)
        if (_browser is null)
        {
            return;
        }
        if (segments.Count == 0)
        {
            await NavigateAsync(null, "");
            return;
        }
        var parent = await _browser.FindByPathAsync(segments);
        if (parent is null)
        {
            StatusText.Text = "上一级已经不存在了(可能被另一端删掉)——已回到空间根。";
            await NavigateAsync(null, "");
            return;
        }
        await NavigateAsync(parent.Id, parent.Path);
    }

    private async void OnRowDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (EntryList.SelectedItem is not RemoteRow row)
        {
            return;
        }
        if (row.Entry.IsDir)
        {
            await NavigateAsync(row.Entry.Id, row.Entry.Path);
            return;
        }
        await PreviewAsync(row.Entry);
    }

    private async void OnOpen(object sender, RoutedEventArgs e)
    {
        if (EntryList.SelectedItem is not RemoteRow row)
        {
            StatusText.Text = "请先在上面的列表里选中一个文件。";
            return;
        }
        if (row.Entry.IsDir)
        {
            StatusText.Text = "选中的是目录 —— 双击它可以进入。";
            return;
        }
        await PreviewAsync(row.Entry);
    }

    /// <summary>把远端文件下载到**临时目录**再交给系统打开(绝不动同步目录)。</summary>
    private async Task PreviewAsync(BrowseEntry entry)
    {
        if (_browser is null || _busy)
        {
            return;
        }
        StatusText.Text = $"正在取回 {entry.Path} …";
        SetBusy(true);
        try
        {
            var local = await _browser.DownloadToTempAsync(entry);
            StatusText.Text = $"预览副本:{local}(临时目录,不影响同步)";
            Process.Start(new ProcessStartInfo(local) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText.Text = "预览失败:" + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }
}
