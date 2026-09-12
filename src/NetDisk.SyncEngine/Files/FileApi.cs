// 文件读写通道(列表 / 详情 / 下载 / 上传)—— MVP 客户端接线的第一块。
//
// 为什么放在 SyncEngine 而不是 Transport:
//   下载要落盘,而落盘必须处理**长路径**(`\\?\` 前缀,见 Paths/LongPath)与
//   "先写 .part 再改名"的原子性 —— 这两件事都是同步引擎的职责。Transport 只提供
//   HTTP 能力(`ApiClient`/`TusUploader`),SyncEngine 依赖 Transport(方向单向),
//   所以这一层放这里既不破坏依赖方向,又能同时用到两边。
//
// 三条与契约/服务端语义相关的约定:
//   1. 列表参数用**契约里的规范名** `space` / `parent`(服务端也接受 `space_id`/
//      `parent_id` 同义别名,但客户端只该用一种,否则服务端一改别名我们就跟着坏)。
//   2. 分页是 **keyset**(用上一页最后一条的 `lower(name)` 作 `after`),不是 offset:
//      边翻页边有写入时 offset 会漏条目/重复条目,而 keyset 不会。
//   3. 下载先写 `<name>.part` 再原子改名:直接写目标文件时,进程在写一半时被结束
//      会留下一个**看起来正常但内容残缺**的文件,而同步引擎会把它当成"已同步"。

using System.Net;
using System.Runtime.CompilerServices;
using NetDisk.SyncEngine.Paths;
using NetDisk.Transport;

namespace NetDisk.SyncEngine.Files;

/// <summary>文件通道:列目录 / 详情 / 下载 / 上传。</summary>
public sealed class FileApi
{
    private readonly ApiClient _api;
    private readonly TusUploader _tus;

    public FileApi(ApiClient api, int uploadChunkSize = TusUploader.DefaultChunkSize)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _tus = new TusUploader(api, uploadChunkSize);
    }

    /// <summary>列一层目录(自动翻完所有分页)。<paramref name="parentId"/> 为空表示空间根。</summary>
    public async Task<IReadOnlyList<EntryView>> ListAsync(
        string spaceId, string? parentId, CancellationToken ct = default)
    {
        var all = new List<EntryView>();
        string? after = null;
        while (true)
        {
            var q = $"/api/v1/files?space={Uri.EscapeDataString(spaceId)}&limit=1000";
            if (!string.IsNullOrEmpty(parentId))
            {
                q += $"&parent={Uri.EscapeDataString(parentId)}";
            }
            if (!string.IsNullOrEmpty(after))
            {
                q += $"&after={Uri.EscapeDataString(after)}";
            }

            var page = await _api.GetAsync<FileListResult>(q, ct).ConfigureAwait(false);
            all.AddRange(page.entries);
            // next_after 为空 = 没有下一页(不要用 entries.Count < limit 判断:
            // 服务端可能在恰好整页时仍给出 next_after)
            if (string.IsNullOrEmpty(page.next_after))
            {
                return all;
            }
            after = page.next_after;
        }
    }

    /// <summary>
    /// 深度优先遍历整个空间(首次对账/慢同步用)。
    /// 产出顺序:**父目录在前、子项在后**,这样调用方可以边遍历边建本地目录。
    /// </summary>
    public async IAsyncEnumerable<EntryView> WalkAsync(
        string spaceId,
        string? rootParent = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var queue = new Queue<string?>();
        queue.Enqueue(rootParent);
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var parent = queue.Dequeue();
            foreach (var e in await ListAsync(spaceId, parent, ct).ConfigureAwait(false))
            {
                yield return e;
                if (e.is_dir)
                {
                    queue.Enqueue(e.id);
                }
            }
        }
    }

    /// <summary>文件详情(单条)。</summary>
    public Task<EntryView> GetEntryAsync(string fileId, CancellationToken ct = default) =>
        _api.GetAsync<EntryView>($"/api/v1/files/{Uri.EscapeDataString(fileId)}", ct);

    /// <summary>列出账号可见的空间(MVP 用它挑出个人空间)。</summary>
    public Task<SpaceList> ListSpacesAsync(CancellationToken ct = default) =>
        _api.GetAsync<SpaceList>("/api/v1/spaces", ct);

    /// <summary>
    /// 建目录。**走 WebDAV MKCOL**,而不是某个 JSON 端点:
    /// 服务端没有"建目录"的 REST 接口(`/api/v1/files` 只有 GET,目录创建属于
    /// WebDAV 能力集),契约里也是如此。用错入口会 404/405,而不是"看起来也行"。
    /// </summary>
    public async Task<EntryView> CreateDirectoryAsync(
        string spaceId, string? parentId, string name, CancellationToken ct = default)
    {
        // WebDAV 路径按 <space_id>/<相对路径> 组织;MVP 只用到"某一层之下"这一种形态,
        // 所以由调用方保证 name 不含分隔符(带分隔符的层级创建由 EnsureParentDir 逐级做)
        var prefix = string.IsNullOrEmpty(parentId) ? "" : $"{parentId}/";
        var path = $"/webdav/{Uri.EscapeDataString(spaceId)}/{prefix}{Uri.EscapeDataString(name)}";
        using var resp = await _api.WebDavAsync("MKCOL", path, null, null, ct).ConfigureAwait(false);
        // MKCOL 成功是 201 Created(201 而非 200/204);已存在是 405,这里当成"已存在"处理
        var id = resp.Headers.TryGetValues("X-File-Id", out var vals) ? vals.FirstOrDefault() : null;
        if (!string.IsNullOrEmpty(id))
        {
            return await GetEntryAsync(id!, ct).ConfigureAwait(false);
        }
        // 服务端未回 X-File-Id 时,回落到按名字查父目录列表
        foreach (var e in await ListAsync(spaceId, parentId, ct).ConfigureAwait(false))
        {
            if (e.is_dir && string.Equals(e.name, name, StringComparison.OrdinalIgnoreCase))
            {
                return e;
            }
        }
        throw new ApiException(resp.StatusCode, "mkdir_no_id",
            $"建目录成功但没有拿到目录 id: {path}");
    }

    /// <summary>按名字在指定父目录下找一个条目(MVP:目录去重靠它)。</summary>
    public async Task<EntryView?> FindByNameAsync(
        string spaceId, string? parentId, string name, CancellationToken ct = default)
    {
        foreach (var e in await ListAsync(spaceId, parentId, ct).ConfigureAwait(false))
        {
            if (string.Equals(e.name, name, StringComparison.Ordinal))
            {
                return e;
            }
        }
        return null;
    }

    /// <summary>
    /// 下载到本地路径(流式,不整文件进内存)。
    /// 返回实际写入的字节数;父目录不存在会自动创建。
    /// </summary>
    public async Task<long> DownloadAsync(string fileId, string localPath, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(LongPath.ToExtended(dir));
        }

        using var resp = await _api.SendRawAsync(
            HttpMethod.Get, $"/api/v1/files/{Uri.EscapeDataString(fileId)}/content",
            contentFactory: null, headers: null, idempotent: true, ct: ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new ApiException(resp.StatusCode, "download_failed",
                $"下载失败({(int)resp.StatusCode}): {Truncate(body, 200)}");
        }

        var part = localPath + ".part";
        long written = 0;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = new FileStream(LongPath.ToExtended(part), FileMode.Create,
                         FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
        {
            var buf = new byte[64 * 1024];
            int n;
            while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                written += n;
            }
        }

        // 原子落地:先 .part 再改名。改成已存在的目标在 Windows 上要 File.Move(overwrite)
        File.Move(LongPath.ToExtended(part), LongPath.ToExtended(localPath), overwrite: true);
        return written;
    }

    /// <summary>
    /// **覆盖**已存在的远端文件(本地改动要传回去时必须走这条)。
    ///
    /// 为什么不能用 TUS 建任务:上传任务是"名字的预留者"(ADR-5),同目录同名会直接
    /// 409 name_conflict —— 实测正是这样:本地改了已有文件,再走 TUS 建任务被拒,
    /// 于是"改本地文件"这个最基本的动作同步不出去。
    /// 覆盖走 WebDAV PUT + **If-Match**(6.6 的乐观锁落点):版本落后会得到 412,
    /// 调用方据此走冲突流程,而不是静默把别人的修改盖掉。
    /// 局限(MVP):MVP 的同步根就是空间根,所以这里直接用相对同步根的路径作为
    /// WebDAV 路径;若将来支持"空间内子目录作为同步根",这里需要把父目录前缀补上。
    /// </summary>
    public async Task<EntryView> UploadOverwriteAsync(
        string spaceId, string relativePath, string localPath, string? ifMatch, CancellationToken ct = default)
    {
        var path = $"/webdav/{Uri.EscapeDataString(spaceId)}/{relativePath}";
        await using var stream = new FileStream(LongPath.ToExtended(localPath), FileMode.Open,
            FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var resp = await _api.WebDavAsync("PUT", path, ifMatch, content, ct).ConfigureAwait(false);
        // 201(新建)/204(覆盖)都算成功;之后按路径查一次详情拿新版本与 ETag
        var dir = Path.GetDirectoryName(relativePath.Replace('/', Path.DirectorySeparatorChar));
        var parentId = string.IsNullOrEmpty(dir) ? null : (string?)null; // MVP:同步根=空间根
        var found = await FindByRelativePathAsync(spaceId, relativePath, ct).ConfigureAwait(false);
        if (found is null)
        {
            throw new ApiException(resp.StatusCode, "overwrite_no_entry",
                $"覆盖成功但按路径找不到该条目: {relativePath}(parent={parentId})");
        }
        return found;
    }

    /// <summary>按相对空间根的路径找条目(MVP:逐级列出定位)。</summary>
    public async Task<EntryView?> FindByRelativePathAsync(
        string spaceId, string relativePath, CancellationToken ct = default)
    {
        var parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? parent = null;
        EntryView? current = null;
        foreach (var part in parts)
        {
            current = await FindByNameAsync(spaceId, parent, part, ct).ConfigureAwait(false);
            if (current is null)
            {
                return null;
            }
            parent = current.id;
        }
        return current;
    }
    /// <summary>上传一个本地文件(走 TUS,带断点续传语义)。</summary>
    public async Task<UploadResult> UploadAsync(
        string spaceId,
        string? parentId,
        string name,
        string localPath,
        IProgress<long>? progress = null,
        CancellationToken ct = default)
    {
        var info = new FileInfo(LongPath.ToExtended(localPath));
        if (!info.Exists)
        {
            throw new FileNotFoundException("上传源文件不存在", localPath);
        }

        var handle = await _tus.CreateAsync(new UploadRequest
        {
            SpaceId = spaceId,
            ParentId = parentId,
            Name = name,
            Size = info.Length,
        }, ct).ConfigureAwait(false);

        await using var stream = new FileStream(LongPath.ToExtended(localPath), FileMode.Open,
            FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        return await _tus.UploadAsync(handle, stream, progress, ct).ConfigureAwait(false);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
