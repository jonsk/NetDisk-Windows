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
    /// **替换**已存在的远端文件(本地改动要传回去时必须走这条)。
    ///
    /// 为什么这么实现(MVP 的取舍,写清楚免得被误读):
    ///   服务端**没有** Bearer 认证的"覆盖"入口 —— 契约里 `upload/create` 与
    ///   `upload/simple` 都只有 {space_id,parent_id,name,size,hash},没有
    ///   file_id/base_version;而 WebDAV PUT 虽然能覆盖,却走 Basic/账密换票
    ///   (ADR-6),客户端**只持有 Bearer 令牌、不持有口令**(刻意的:口令不落盘),
    ///   实测直接 `WebDAV 认证失败: 请提供 Basic 凭据`。
    ///   所以 MVP 用 **先删后传**:DELETE 释放名字与引用,再 TUS 建任务上传。
    ///
    /// 代价(必须知道):远端会得到**新的 file id 与新版本号**(不是原地 +1),
    /// 中间有一小段"文件不存在"的窗口。要根治应当**先补契约再实现**一个
    /// 带 base_version 的覆盖入口(已登记为待办);在那之前,"改本地文件能传上去"
    /// 比"版本号连续"更重要 —— 后者当前根本走不通。
    /// </summary>
    public async Task<EntryView> UploadReplacingAsync(
        string spaceId, string remoteFileId, string? parentId, string name, string localPath,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        using (var del = await _api.SendRawAsync(HttpMethod.Delete,
                   $"/api/v1/files/{Uri.EscapeDataString(remoteFileId)}",
                   contentFactory: null, headers: null, idempotent: true, ct: ct).ConfigureAwait(false))
        {
            // 404 说明另一端已经删掉了:那正好,继续传
            if (!del.IsSuccessStatusCode && del.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                var body = await del.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new ApiException(del.StatusCode, "delete_before_replace_failed",
                    $"替换前删除失败({(int)del.StatusCode}):{Truncate(body, 200)}");
            }
        }

        var res = await UploadAsync(spaceId, parentId, name, localPath, progress, ct).ConfigureAwait(false);
        return await GetEntryAsync(res.FileId, ct).ConfigureAwait(false);
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
