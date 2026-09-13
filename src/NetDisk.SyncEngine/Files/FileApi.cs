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
using NetDisk.SyncEngine.Onboarding;
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

    /// <summary>
    /// 改用名(契约 <c>PATCH /api/v1/files/{id}</c>,带 base_version 乐观锁)。
    ///
    /// 用它而不是 `POST /files/{id}/move` 的两个理由:
    ///   ① 纯改名只需要 name,不需要搬父目录;
    ///   ② move **超阈值会返回 202 异步任务**(条目仍在原处,不允许乐观移动),
    ///      而 rename 是同步 200,调用方能拿到确定结果 —— 改名这种小操作不该引入
    ///      "任务在跑、状态未知"的中间态。
    /// 服务端在提交后会推一条 updated 事件,所以另一端能及时看到。
    /// </summary>
    public Task<EntryView> RenameAsync(
        string fileId, string newName, long baseVersion = 0, CancellationToken ct = default) =>
        _api.PatchAsync<EntryView>(
            $"/api/v1/files/{Uri.EscapeDataString(fileId)}",
            new { name = newName },
            baseVersion == 0 ? null : baseVersion,
            ct);

    /// <summary>
    /// 列出子树并算出**相对路径**(首次运行向导的容量预估要用它)。
    ///
    /// 为什么要单独一个方法:列表接口只给 parent_id,**不给路径**;而"这次要同步多少
    /// 数据、有多少个文件"必须按相对路径来算(也是后面逐级建目录的依据)。
    /// 这里一次列完整棵树,再按 id→(name,parent) 把路径上溯拼出来 ——
    /// 不能对每个条目单独上溯(那是 N×深度 次请求,大目录下会明显变慢)。
    /// </summary>
    /// <param name="rootParentId">作为"根"的那一层(null = 空间根)。返回的路径相对它。</param>
    public async Task<IReadOnlyList<RemoteEntry>> CollectTreeAsync(
        string spaceId, string? rootParentId = null, CancellationToken ct = default)
    {
        var all = new List<EntryView>();
        await foreach (var e in WalkAsync(spaceId, rootParentId, ct).ConfigureAwait(false))
        {
            all.Add(e);
        }

        // id → 条目,之后按 parent_id 上溯拼路径(向上直到 rootParentId 为止)
        var byId = new Dictionary<string, EntryView>(StringComparer.Ordinal);
        foreach (var e in all)
        {
            byId[e.id] = e;
        }

        var result = new List<RemoteEntry>(all.Count);
        foreach (var e in all)
        {
            var parts = new List<string>();
            var cur = e;
            var depth = 0;
            while (cur is not null && depth < 10_000)
            {
                parts.Insert(0, cur.name);
                depth++;
                if (cur.parent_id is null || string.Equals(cur.parent_id, rootParentId, StringComparison.Ordinal))
                {
                    break;
                }
                if (!byId.TryGetValue(cur.parent_id, out var parent))
                {
                    break; // 父条目不在本次结果里(被权限裁掉/并发删除):按当前已拼出的路径给出
                }
                cur = parent;
            }
            result.Add(new RemoteEntry(string.Join('/', parts), e.is_dir, e.size, parts.Count));
        }
        return result;
    }

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
    /// <param name="progress">
    /// 已写入字节数的进度回调(可空)。**每读完一块就报一次**,
    /// 节流交给调用方(传输队列按阈值节流)—— 在这里自己节流会让"小文件也能显示进度"落空。
    /// </param>
    public async Task<long> DownloadAsync(string fileId, string localPath,
        IProgress<long>? progress = null, CancellationToken ct = default)
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
                progress?.Report(written);
            }
        }

        // 原子落地:先 .part 再改名。改成已存在的目标在 Windows 上要 File.Move(overwrite)
        File.Move(LongPath.ToExtended(part), LongPath.ToExtended(localPath), overwrite: true);
        return written;
    }

    // 这里原本有一个 UploadReplacingAsync(先 DELETE 再 TUS 建任务),已删除。
    // 删除理由(留档,别再写回来):它能让"本地改动传回去"看起来能用,代价是远端
    // 拿到**新的 file id、版本号回到 1**。这不只是不优雅 —— 客户端的冲突判据是
    // "远端 version > 本地已知 version",版本号一倒退该判据**永远不成立**,
    // 于是另一端的修改会被本地版本静默覆盖(实测:双端同改后本地既不生成冲突副本、
    // 用户也看不到任何提示)。根治办法是把"覆盖意图"补进契约并让服务端原地覆盖:
    //   upload/create 增加 allow_overwrite → TUS 定稿时原地生成新版本(同一 file id)。
    // 现已落地(契约 + 迁移 00013 + 服务端 + 客户端 flag),覆盖走 UploadAsync(..., allowOverwrite: true)。

    /// <summary>
    /// 删除一个条目(契约 <c>DELETE /api/v1/files/{id}</c>)。
    ///
    /// ⚠ **必须按 file_id 删,绝不按名字删**。这是双向删除能安全工作的前提:
    /// 用户在这一端删掉 X 的同时,另一端可能刚新建了一个**同名**的 X'(新的 file id)。
    /// 按名字删会误杀对方刚建的文件;按 id 删只删"我们确实知道的那一份",
    /// 与"复活的一律当新文件"这条语义天然一致。
    ///
    /// 服务端是**硬删**(契约原文:硬删,无回收站;目录删整棵子树),所以调用方
    /// 必须先通过"信号完整性"检查(见 SyncHost 的删除分支),不能凭一次不可信的观察就删。
    /// </summary>
    public Task<DeleteResult> DeleteAsync(string fileId, CancellationToken ct = default) =>
        _api.DeleteAsync<DeleteResult>($"/api/v1/files/{Uri.EscapeDataString(fileId)}", ct);

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
        bool allowOverwrite = false,
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
            // 只在显式覆盖时下发 true;其余留 null(服务端默认 false = 同名 409)
            AllowOverwrite = allowOverwrite ? true : null,
        }, ct).ConfigureAwait(false);

        await using var stream = new FileStream(LongPath.ToExtended(localPath), FileMode.Open,
            FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        return await _tus.UploadAsync(handle, stream, progress, ct).ConfigureAwait(false);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
