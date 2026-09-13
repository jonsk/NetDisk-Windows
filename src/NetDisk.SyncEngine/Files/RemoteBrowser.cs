// 远端文件浏览器(只读):让用户看**整个空间**的结构,而不是只看同步目录里那一份。
//
// 为什么需要单独一层而不是让界面直接用 FileApi:
//   ① **"浏览"必须与"同步"划清界限**。同步目录里的东西会被上传/下载/删除;
//      而"我只是想看看服务器上有什么"绝不该改变任何一端的文件。这个类**只会**:
//      列目录、按路径定位、把某个文件下载到**临时目录**去预览 —— 没有任何写操作。
//   ② **预览落点必须是唯一的**。两个不同目录里的同名文件(很常见:`a/报告.docx` 与
//      `b/报告.docx`)如果都落到同一个临时文件名上,"打开"就会打开**另一个**文件 ——
//      这类错误在界面上表现为"点开是旧内容",极难排查。所以落点带远端 file_id 前缀。
//   ③ 路径拼装只在这里做一次:界面靠 `BrowseEntry.Path` 显示面包屑,不再自己拼字符串
//      (两处拼路径必然漂移,而漂移的表现是"地址栏写着 A、列表里是 B")。

using NetDisk.SyncEngine.Paths;
using NetDisk.Transport;

namespace NetDisk.SyncEngine.Files;

/// <summary>浏览器里的一条远端条目(只读视图 + 它的相对路径)。</summary>
public sealed record BrowseEntry(string Id, string Name, bool IsDir, long Size, long Version, string Path)
{
    /// <summary>给人看的大小(None 表示目录)。</summary>
    public string SizeText => IsDir ? "" : FormatSize(Size);

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };
}

/// <summary>远端文件浏览器(只读:列目录 / 定位 / 预览下载到临时目录)。</summary>
public sealed class RemoteBrowser
{
    private readonly FileApi _files;
    private string? _spaceId;

    public RemoteBrowser(FileApi files, string? spaceId = null)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _spaceId = string.IsNullOrWhiteSpace(spaceId) ? null : spaceId;
    }

    /// <summary>预览落点目录:**临时目录**下属于本客户端的一个子目录,永不落在同步目录里。</summary>
    public static string PreviewDirectory => Path.Combine(Path.GetTempPath(), "NetDisk-preview");

    private async Task<string> SpaceIdAsync(CancellationToken ct)
    {
        if (_spaceId is not null)
        {
            return _spaceId;
        }
        // 配置里没有空间 id 时(例如刚登录还没同步过)替用户挑个人空间 —— 与登录路径同一套规则
        var spaces = await _files.ListSpacesAsync(ct).ConfigureAwait(false);
        var personal = spaces.spaces?.FirstOrDefault(s => s.kind == "personal") ?? spaces.spaces?.FirstOrDefault();
        _spaceId = personal?.id ?? throw new InvalidOperationException("这个账号没有任何可见空间");
        return _spaceId;
    }

    /// <summary>
    /// 列一层目录(<paramref name="parentId"/> 为 null = 空间根)。
    /// 排序规则:**目录在前**,同类型按名字(与资源管理器一致 —— 用户习惯它)。
    /// </summary>
    public async Task<IReadOnlyList<BrowseEntry>> ListAsync(
        string? parentId, string path = "", CancellationToken ct = default)
    {
        var spaceId = await SpaceIdAsync(ct).ConfigureAwait(false);
        var entries = await _files.ListAsync(spaceId, parentId, ct).ConfigureAwait(false);
        var prefix = string.IsNullOrEmpty(path) ? "" : path.TrimEnd('/') + "/";
        return entries
            .Select(e => new BrowseEntry(e.id, e.name, e.is_dir, e.size, e.version, prefix + e.name))
            .OrderByDescending(e => e.IsDir)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 按路径逐级定位(面包屑/地址栏用)。任一层不存在就返回 null ——
    /// **不抛异常**:路径可能因为另一端刚删掉而失效,那是正常情况,界面显示"路径不存在"即可。
    /// </summary>
    public async Task<BrowseEntry?> FindByPathAsync(IReadOnlyList<string> segments, CancellationToken ct = default)
    {
        var spaceId = await SpaceIdAsync(ct).ConfigureAwait(false);
        string? parentId = null;
        var acc = "";
        BrowseEntry? current = null;
        foreach (var segment in segments)
        {
            if (string.IsNullOrEmpty(segment))
            {
                continue;
            }
            var found = await _files.FindByNameAsync(spaceId, parentId, segment, ct).ConfigureAwait(false);
            if (found is null)
            {
                return null;
            }
            acc = acc.Length == 0 ? found.name : acc + "/" + found.name;
            current = new BrowseEntry(found.id, found.name, found.is_dir, found.size, found.version, acc);
            parentId = found.id;
        }
        return current;
    }

    /// <summary>
    /// 把远端文件**下载到临时目录**并返回本地路径(供"打开/预览")。
    ///
    /// 两条硬约束:
    ///   · **不写同步目录**:写进去会被同步引擎当成"本地新增"再传回服务器(用户只是看了一眼);
    ///   · 落点带 file_id 前缀:不同目录的同名文件不会互相覆盖(见文件头 ②)。
    /// </summary>
    public async Task<string> DownloadToTempAsync(BrowseEntry entry, CancellationToken ct = default)
    {
        if (entry.IsDir)
        {
            throw new InvalidOperationException($"目录不能当作文件预览:{entry.Path}");
        }
        Directory.CreateDirectory(LongPath.ToExtended(PreviewDirectory));
        var target = Path.Combine(PreviewDirectory, $"{entry.Id}-{entry.Name}");
        await _files.DownloadAsync(entry.Id, target, progress: null, ct).ConfigureAwait(false);
        return target;
    }
}
