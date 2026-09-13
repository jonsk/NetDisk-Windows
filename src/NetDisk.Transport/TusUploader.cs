// TUS 分片上传客户端(DE-D-06)。
//
// 协议面(服务端 6.10 / 7.7 A-10):
//   POST   /api/v1/upload/create      {space_id,parent_id,name,size,hash} → upload_id + upload_ticket
//   HEAD   /api/v1/upload/{id}        带 X-Upload-Token → Upload-Offset / Upload-Length(**续传的权威偏移**)
//   PATCH  /tus/{id}                  带 Tus-Resumable / Upload-Offset / X-Upload-Token
//                                     → 204 + 新 Upload-Offset;写满时 200 + X-File-Id(**即时定稿**)
//   POST   /api/v1/upload/{id}/finish 带 X-Upload-Token(服务端未即时定稿时用)
//   DELETE /api/v1/upload/{id}        取消并即时释放预留额度
//
// 四条纪律(全部是被真实故障逼出来的):
//   ①**ticket 走 `X-Upload-Token` 头**,不进 URL:URL 会进网关 access log 与浏览器历史。
//     而且 ticket **在整个任务期间不变**,多次 PATCH 必须一直带着它(服务端只存 ticket 的哈希)。
//   ②**偏移以服务端为准**:每片发之前用 HEAD 拿到的 (或上一片响应头里的) Upload-Offset,
//     而不是本地计数器 —— 本地计数在网络半途中断时是错的,照着它续传会**写坏文件**
//     (把已经写好的字节再写一遍,或跳过一个空洞)。
//   ③**片中失败必须原地重试同一偏移**:退避后重发**同一片**;重试期间 ticket 与偏移都不变。
//   ④**413/507/409 不重试**(额度不足、名字冲突、空间被移出):重试只会一直失败,
//     而 507 还会反复占用预留额度。这些一律抛给上层做可读提示。

using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetDisk.Transport;

/// <summary>建任务入参(元数据;内容另走 PATCH)。</summary>
public sealed record UploadRequest
{
    [JsonPropertyName("space_id")] public required string SpaceId { get; init; }

    /// <summary>目标目录;空 = 空间根目录。</summary>
    [JsonPropertyName("parent_id")] public string? ParentId { get; init; }

    [JsonPropertyName("name")] public required string Name { get; init; }

    /// <summary>声明大小(服务端据此预留额度;必须与真实字节数一致,否则定稿时结算差额)。</summary>
    [JsonPropertyName("size")] public required long Size { get; init; }

    /// <summary>
    /// 显式声明「这次上传要覆盖同目录同名文件」(契约 UploadCreateRequest.allow_overwrite)。
    /// 只有客户端**自己判定过**「这是我的改动、且远端版本没比我已知的更新」时才该置 true;
    /// 不置或置 false 时同名会被 409 name_conflict 拒 —— 这是刻意保留的数据保护。
    /// </summary>
    [JsonPropertyName("allow_overwrite")] public bool? AllowOverwrite { get; init; }

    /// <summary>整文件 SHA-256(可选;给了才可能命中秒传)。</summary>
    [JsonPropertyName("hash")] public string? Hash { get; init; }
}

/// <summary>服务端建任务响应里我们需要的部分。</summary>
public sealed record UploadHandle
{
    [JsonPropertyName("upload_id")] public required string UploadId { get; init; }

    /// <summary>只在建任务响应里出现一次(服务端只存哈希),必须由调用方保管。</summary>
    [JsonPropertyName("upload_ticket")] public required string Ticket { get; init; }

    /// <summary>秒传预检命中时的挑战(非空 = 可以不传内容,按样本摘要定稿)。</summary>
    [JsonPropertyName("fast_upload")] public FastUploadChallenge? FastUpload { get; init; }
}

/// <summary>秒传挑战:客户端按 <see cref="SampleOffsets"/> 取样本算摘要回传。</summary>
public sealed record FastUploadChallenge
{
    [JsonPropertyName("nonce")] public string Nonce { get; init; } = "";

    [JsonPropertyName("sample_offsets")] public List<long> SampleOffsets { get; init; } = new();

    [JsonPropertyName("sample_size")] public int SampleSize { get; init; }
}

/// <summary>定稿结果。</summary>
public sealed record UploadResult
{
    public required string FileId { get; init; }
    public required long Version { get; init; }

    /// <summary>true = 服务端在最后一片里直接定稿(没有单独的 finish 往返)。</summary>
    public required bool FinalizedInline { get; init; }
}

/// <summary>TUS 上传器。</summary>
public sealed class TusUploader
{
    /// <summary>
    /// 默认分片 8MB。与 H5 保持同一量级:小到能在弱网下重传一片不心疼,
    /// 大到不会让"每片一次往返"的开销压过吞吐。
    /// </summary>
    public const int DefaultChunkSize = 8 * 1024 * 1024;

    private const string TusVersion = "1.0.0";
    private const string TusHeader = "Tus-Resumable";
    private const string OffsetHeader = "Upload-Offset";
    private const string TicketHeader = "X-Upload-Token";
    private const string CompleteHeader = "Upload-Complete";

    /// <summary>
    /// 单次上传里最多允许几次"重新对齐偏移"。
    ///
    /// 为什么要封顶:对齐是**自愈**动作,但如果服务端偏移始终不前进(例如磁盘写不进去),
    /// 不封顶就会变成一个不报错、也不结束的循环 —— 用户看到"上传中"永远不动。
    /// </summary>
    private const int MaxResyncs = 3;

    private readonly ApiClient _api;
    private readonly int _chunkSize;

    public TusUploader(ApiClient api, int chunkSize = DefaultChunkSize)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        if (chunkSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSize));
        }
        _chunkSize = chunkSize;
    }

    public int ChunkSize => _chunkSize;

    /// <summary>建上传任务(可命中秒传;命中时 <see cref="UploadHandle.FastUpload"/> 非空)。</summary>
    public async Task<UploadHandle> CreateAsync(UploadRequest request, CancellationToken ct = default)
    {
        // 建任务**不重试**(POST):重试一次就可能建出两个任务并各自预留一份额度。
        var resp = await _api.SendRawAsync(
            HttpMethod.Post, "/api/v1/upload/create",
            contentFactory: () => new StringContent(
                JsonSerializer.Serialize(request, JsonOpts), System.Text.Encoding.UTF8, "application/json"),
            headers: null, idempotent: false, ct).ConfigureAwait(false);

        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw ApiClient.ParseError(resp.StatusCode, text);
        }
        var handle = JsonSerializer.Deserialize<UploadHandle>(text, JsonOpts)
                     ?? throw new ApiException(resp.StatusCode, "invalid_response", "建任务响应无法解析");
        if (string.IsNullOrEmpty(handle.UploadId) || string.IsNullOrEmpty(handle.Ticket))
        {
            throw new ApiException(resp.StatusCode, "invalid_response", "建任务响应缺少 upload_id/upload_ticket");
        }
        return handle;
    }

    /// <summary>取服务端的当前偏移(HEAD)。**续传的权威偏移**,不要用本地计数。</summary>
    public async Task<long> GetServerOffsetAsync(UploadHandle handle, CancellationToken ct = default)
    {
        using var resp = await _api.SendRawAsync(
            HttpMethod.Head, "/api/v1/upload/" + handle.UploadId,
            contentFactory: null,
            headers: new Dictionary<string, string> { [TicketHeader] = handle.Ticket },
            idempotent: true, ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw ApiClient.ParseError(resp.StatusCode, body);
        }
        return ReadOffset(resp) ?? 0;
    }

    /// <summary>
    /// 上传内容(自动续传:先问服务端偏移,再从那个位置继续)。
    ///
    /// <paramref name="content"/> 必须可定位(Seek):续传要跳过服务端已收到的字节。
    /// </summary>
    public async Task<UploadResult> UploadAsync(
        UploadHandle handle, Stream content, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        if (!content.CanSeek)
        {
            throw new ArgumentException("续传需要可定位的流(Seek)", nameof(content));
        }

        var offset = await GetServerOffsetAsync(handle, ct).ConfigureAwait(false);
        if (offset > 0)
        {
            // 服务端已经收到一些字节(上次中断/重试)。**必须**从它的偏移继续,
            // 而不是从头开始 —— 从头重传会让服务端在"已写字节"上再叠一份(文件损坏)。
            content.Seek(offset, SeekOrigin.Begin);
        }
        progress?.Report(offset);

        var buffer = new byte[_chunkSize];
        var resyncs = 0;
        while (true)
        {
            var read = await ReadFullAsync(content, buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                // 内容发完但服务端还没定稿(声明大小与实际不一致,或最后一片恰好是整块):
                // 走显式 finish —— 与"最后一片即定稿"是同一条定稿路径,只是多一次往返。
                return await FinishAsync(handle, offset, finalizedInline: false, ct).ConfigureAwait(false);
            }

            var chunk = new byte[read];
            Array.Copy(buffer, chunk, read);
            PatchOutcome result;
            try
            {
                result = await PatchChunkAsync(handle, chunk, offset, ct, progress).ConfigureAwait(false);
                resyncs = 0;
            }
            catch (ApiException ex) when (ex.Status == HttpStatusCode.Conflict || ex.IsTransient)
            {
                // **片"落了一半"才失败**时,本地偏移已经不再可信:内层重试会带着旧偏移
                // 再打一次,而服务端按 Upload-Offset 校验会回 409(它已经收到了前面那部分)。
                // 正确动作是**回来问服务端当前偏移**、把流定位过去、再继续 ——
                // 这正是"断点续传"要解决的问题,只不过发生在上传过程中而不是重启之后。
                if (++resyncs > MaxResyncs)
                {
                    throw;
                }
                var serverOffset = await GetServerOffsetAsync(handle, ct).ConfigureAwait(false);
                if (serverOffset == offset && !ex.IsTransient)
                {
                    // 一次都没前进、而且不是暂时性错误 → 真的失败(别在这里转圈)
                    throw;
                }
                offset = serverOffset;
                content.Seek(offset, SeekOrigin.Begin);
                progress?.Report(offset);
                continue;
            }
            offset = result.Offset;
            progress?.Report(offset);
            if (result.FileId is not null)
            {
                return new UploadResult { FileId = result.FileId, Version = result.Version, FinalizedInline = true };
            }
        }
    }

    /// <summary>取消任务并即时释放预留额度(6.3:取消不等回收班车)。</summary>
    public async Task CancelAsync(UploadHandle handle, CancellationToken ct = default)
    {
        using var resp = await _api.SendRawAsync(
            HttpMethod.Delete, "/api/v1/upload/" + handle.UploadId,
            contentFactory: null,
            headers: new Dictionary<string, string> { [TicketHeader] = handle.Ticket },
            idempotent: true, ct).ConfigureAwait(false);
        // 幂等:任务已结束也返回 204(取消重试不该报错),所以这里不检查状态码
    }

    private async Task<PatchOutcome> PatchChunkAsync(
        UploadHandle handle, byte[] chunk, long offset, CancellationToken ct,
        IProgress<long>? progress = null)
    {
        using var resp = await _api.SendRawAsync(
            HttpMethod.Patch, "/tus/" + handle.UploadId,
            // 工厂:重试时**重建** body(同一个 HttpContent 不能发两次)
            contentFactory: () =>
            {
                // **按字节上报进度**,而不是按分片:分片默认 8MB,若只在分片边界上报,
                // 小于 16MB 的文件在界面上永远是"上传中"(首尾两档),与卡住无法区分。
                // 这里把 body 包一层计数流:HTTP 栈每读走一段就报一次累计字节
                // (节流交给调用方:传输队列按阈值过滤,所以这里可以每块都报)。
                var body = new CountingStream(new MemoryStream(chunk, writable: false),
                    read => progress?.Report(offset + read));
                var c = new StreamContent(body);
                c.Headers.TryAddWithoutValidation("Content-Type", "application/offset+octet-stream");
                return c;
            },
            headers: new Dictionary<string, string>
            {
                [TusHeader] = TusVersion,
                [OffsetHeader] = offset.ToString(System.Globalization.CultureInfo.InvariantCulture),
                // ticket 每片都带(服务端只存哈希,没有"会话"可依赖)
                [TicketHeader] = handle.Ticket,
            },
            // PATCH 是可安全重试的:同一偏移同一片重发,服务端按 offset 校验幂等
            idempotent: true, ct).ConfigureAwait(false);

        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw ApiClient.ParseError(resp.StatusCode, text);
        }

        var newOffset = ReadOffset(resp) ?? (offset + chunk.Length);
        var fileId = Header(resp, "X-File-Id");
        var version = long.TryParse(Header(resp, "X-File-Version"), out var v) ? v : 0;
        var complete = string.Equals(Header(resp, CompleteHeader), "true", StringComparison.OrdinalIgnoreCase);
        return new PatchOutcome(newOffset, complete ? (fileId ?? "") : null, version);
    }

    private async Task<UploadResult> FinishAsync(
        UploadHandle handle, long offset, bool finalizedInline, CancellationToken ct)
    {
        using var resp = await _api.SendRawAsync(
            HttpMethod.Post, "/api/v1/upload/" + handle.UploadId + "/finish",
            contentFactory: () => new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            headers: new Dictionary<string, string> { [TicketHeader] = handle.Ticket },
            idempotent: false, ct).ConfigureAwait(false);

        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw ApiClient.ParseError(resp.StatusCode, text);
        }
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        // 契约:{file:{id,version}} 或顶层 {id,version}(两种都容忍:响应形态由服务端定)
        var fileEl = root.TryGetProperty("file", out var f) ? f : root;
        var fileId = fileEl.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        var version = fileEl.TryGetProperty("version", out var vEl) ? vEl.GetInt64() : 0;
        return new UploadResult { FileId = fileId, Version = version, FinalizedInline = finalizedInline };
    }

    private static long? ReadOffset(HttpResponseMessage resp)
    {
        var raw = Header(resp, OffsetHeader);
        return long.TryParse(raw, out var v) ? v : null;
    }

    private static string? Header(HttpResponseMessage resp, string name)
        => resp.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    /// <summary>读满一个分片(或读到流结尾)。返回实际字节数。</summary>
    private static async Task<int> ReadFullAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }
            total += n;
        }
        return total;
    }

    /// <summary>一次 PATCH 的结果(服务端偏移 + 是否即时定稿)。</summary>
    private readonly record struct PatchOutcome(long Offset, string? FileId, long Version);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>
/// 计数流:把"读走了多少字节"回调出去(给上传进度用)。
///
/// 为什么需要它:TUS 的分片默认 8MB,**只在分片边界上报进度**的话,
/// 小于 16MB 的文件在界面上永远只有"上传中"两个状态,用户无法与"卡住"区分。
/// 包一层计数流后,HTTP 栈每读一段就报一次,任何大小的文件都有连续进度。
/// 只实现读路径(上传只读),写/定位等一律 Delegate 给内层流。
/// </summary>
internal sealed class CountingStream : Stream
{
    private readonly Stream _inner;
    private readonly Action<long> _onRead;
    private long _read;

    public CountingStream(Stream inner, Action<long> onRead)
    {
        _inner = inner;
        _onRead = onRead;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = _inner.Read(buffer, offset, count);
        if (n > 0)
        {
            _read += n;
            _onRead(_read);
        }
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var n = await _inner.ReadAsync(buffer, ct).ConfigureAwait(false);
        if (n > 0)
        {
            _read += n;
            _onRead(_read);
        }
        return n;
    }

    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}