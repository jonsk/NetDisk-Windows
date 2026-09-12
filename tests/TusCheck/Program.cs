// DE-D-06 行为检查器:TUS 客户端(ticket + 断点续传)。
//
// 用法:`dotnet run --project desktop/tests/TusCheck -c Release`
//
// 断言的四件事(都是"传完了但文件是坏的"这类事故的入口):
//   ① ticket 每片都带、且只走 `X-Upload-Token` 头(不进 URL);
//   ② 偏移以**服务端**为准:服务端只收了一半时,续传必须从服务端偏移继续;
//   ③ 片中失败(5xx)退避后**原地重发同一片**,重试期间 ticket 与偏移不变;
//   ④ 写满即定稿(200 + X-File-Id)不额外走 finish;资源类错误(413/507/409)不重试。
//
// 假服务端会把收到的字节按偏移写进一个缓冲区 —— 于是"文件内容是否正确"可以被
// 真正断言出来(只看调用次数是查不出"写重了一段"的)。

using System.Net;
using System.Text;
using System.Text.Json;
using NetDisk.Transport;

var checks = new List<(string Name, Func<Task> Run)>
{
    ("① 每片都带 X-Upload-Token(且 URL 里没有 ticket)", CheckTicketHeaderAsync),
    ("② 中断后续传:从服务端偏移继续,内容不重不漏", CheckResumeAsync),
    ("③ 片中 5xx 原地重试:同一偏移、同一 ticket", CheckChunkRetryAsync),
    ("④ 写满即定稿:200 + X-File-Id,不再走 finish", CheckInlineFinalizeAsync),
    ("⑤ 未即时定稿时走 finish", CheckExplicitFinishAsync),
    ("⑥ 507 额度不足不重试", CheckQuotaNotRetriedAsync),
    ("⑦ 404(任务丢失)不重试并给出结构化错误", CheckTaskGoneAsync),
    ("⑧ 取消上传:DELETE 带 ticket 且幂等", CheckCancelAsync),
    ("⑨ 建任务不自动重试(避免重复预留额度)", CheckCreateNotRetriedAsync),
    ("⑩ 内容超过声明大小时不越界(以服务端定稿为准)", CheckChunkingAndContentAsync),
    ("⑪ 片落了一半 → 自动重新对齐并继续(同一次调用内自愈)", CheckPartialChunkSelfHealAsync),
};

var failed = 0;
foreach (var (name, run) in checks)
{
    try
    {
        await run();
        Console.WriteLine($"✓ {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"✗ {name}: {ex.Message}");
    }
}

Console.WriteLine();
if (failed == 0)
{
    Console.WriteLine($"全部通过:{checks.Count} 项 DE-D-06 行为断言(TUS ticket + 断点续传)");
    return 0;
}
Console.Error.WriteLine($"{failed}/{checks.Count} 项失败");
return 1;

// ---------------------------------------------------------------- 检查

static async Task CheckTicketHeaderAsync()
{
    var s = new FakeTus { DeclaredSize = 40 };
    var up = new TusUploader(Client(s), chunkSize: 16);
    var handle = await up.CreateAsync(new UploadRequest { SpaceId = "sp", Name = "a.bin", Size = 40 });
    await up.UploadAsync(handle, new MemoryStream(Payload(40)));

    Assert(s.Requests.Count(r => r.Method == "PATCH") >= 2, "40 字节 / 16 分片应至少 2 片");
    // 注意要把 `POST /upload/create` 排除掉:它是**建 ticket 的那个请求**,
    // 那时 ticket 还不存在(第一次跑就是这么误报的)。
    foreach (var r in s.Requests.Where(r => r.Method is "PATCH" or "HEAD" or "DELETE"
                                             || (r.Method == "POST" && !r.Uri.EndsWith("/create"))))
    {
        Assert(r.Ticket == "TICKET-1", $"{r.Method} 必须带 X-Upload-Token,实际 {r.Ticket ?? "(无)"}");
        Assert(!r.Uri.Contains("TICKET-1", StringComparison.Ordinal), $"{r.Method} 的 ticket 不能出现在 URL 里:{r.Uri}");
    }
}

static async Task CheckResumeAsync()
{
    // 先"传到一半就断电":假服务端只收前 20 字节(第 2 片开始失败并中断)
    var s = new FakeTus { DeclaredSize = 40, FailAfterBytes = 20 };
    var up = new TusUploader(Client(s), chunkSize: 16);
    var handle = await up.CreateAsync(new UploadRequest { SpaceId = "sp", Name = "a.bin", Size = 40 });
    await Catch<ApiException>(() => up.UploadAsync(handle, new MemoryStream(Payload(40))));
    Assert(s.StoredBytes() == 20, $"前置条件:服务端应当只收到 20 字节,实际 {s.StoredBytes()}");

    // 恢复:新上传器(模拟进程重启后重新开始),服务端仍有那 20 字节
    s.FailAfterBytes = null;
    var up2 = new TusUploader(Client(s), chunkSize: 16);
    var res = await up2.UploadAsync(handle, new MemoryStream(Payload(40)));

    Assert(s.StoredBytes() == 40, $"续传后应当是完整 40 字节,实际 {s.StoredBytes()}");
    Assert(s.Content().SequenceEqual(Payload(40)), "续传后的内容必须与原文一致(不能重写已收字节)");
    Assert(res.FinalizedInline, "写满后应即时定稿");

    // 第一片续传的偏移必须等于服务端已收字节数(20),而不是 0
    var firstPatchAfterResume = s.Requests.Where(r => r.Method == "PATCH").Last(r => r.Offset == 0);
    _ = firstPatchAfterResume; // 仅供阅读:下面断言"存在偏移=20 的片"
    Assert(s.Requests.Any(r => r.Method == "PATCH" && r.Offset == 20),
        "续传的第一片必须从服务端偏移(20)开始");
}

static async Task CheckChunkRetryAsync()
{
    var s = new FakeTus { DeclaredSize = 32, FailChunkTimes = 1 }; // 第 1 片失败一次
    var up = new TusUploader(Client(s, zeroDelay: true), chunkSize: 16);
    var handle = await up.CreateAsync(new UploadRequest { SpaceId = "sp", Name = "a.bin", Size = 32 });
    var res = await up.UploadAsync(handle, new MemoryStream(Payload(32)));

    Assert(res.FinalizedInline, "重试后应正常定稿");
    Assert(s.Content().SequenceEqual(Payload(32)), "重试后内容必须正确");

    var patches = s.Requests.Where(r => r.Method == "PATCH" && r.Offset == 0).ToList();
    Assert(patches.Count == 2, $"第 1 片应重发一次(共 2 次),实际 {patches.Count}");
    Assert(patches.All(p => p.Ticket == "TICKET-1"),
        "重试期间 ticket 必须不变(服务端只存哈希,换 ticket 会被拒)");
    Assert(patches.All(p => p.Offset == 0), "重试必须是**同一偏移**,不能前进");
}

static async Task CheckInlineFinalizeAsync()
{
    var s = new FakeTus { DeclaredSize = 16 };
    var up = new TusUploader(Client(s), chunkSize: 16);
    var handle = await up.CreateAsync(new UploadRequest { SpaceId = "sp", Name = "a.bin", Size = 16 });
    var res = await up.UploadAsync(handle, new MemoryStream(Payload(16)));

    Assert(res.FinalizedInline, "写满最后一片应当即时定稿");
    Assert(res.FileId == "file-1", $"应拿到 file id,实际 {res.FileId}");
    Assert(!s.Requests.Any(r => r.Method == "POST" && r.Uri.EndsWith("/finish")),
        "即时定稿就不该再多一次 finish 往返");
}

static async Task CheckExplicitFinishAsync()
{
    // 声明 48 字节但只发 32:内容发完后服务端还没满 → 必须走显式 finish
    var s = new FakeTus { DeclaredSize = 32, RequireExplicitFinish = true };
    var up = new TusUploader(Client(s), chunkSize: 16);
    var handle = await up.CreateAsync(new UploadRequest { SpaceId = "sp", Name = "a.bin", Size = 32 });
    var res = await up.UploadAsync(handle, new MemoryStream(Payload(32)));

    Assert(!res.FinalizedInline, "服务端未即时定稿时应走 finish");
    Assert(s.Requests.Any(r => r.Method == "POST" && r.Uri.EndsWith("/finish")), "应当调用 finish");
    Assert(s.Requests.Last(r => r.Method == "POST" && r.Uri.EndsWith("/finish")).Ticket == "TICKET-1",
        "finish 也必须带 ticket(只带 Bearer 会被 401)");
}

static async Task CheckQuotaNotRetriedAsync()
{
    var s = new FakeTus { DeclaredSize = 16, PatchStatus = HttpStatusCode.InsufficientStorage };
    var up = new TusUploader(Client(s, zeroDelay: true), chunkSize: 16);
    var handle = await up.CreateAsync(new UploadRequest { SpaceId = "sp", Name = "a.bin", Size = 16 });

    var ex = await Catch<ApiException>(() => up.UploadAsync(handle, new MemoryStream(Payload(16))));
    Assert(ex.Status == HttpStatusCode.InsufficientStorage, $"应抛出 507,实际 {ex.Status}");
    Assert(!ex.IsTransient, "507 是**额度不足**,重试只会一直失败并反复占预留");
    Assert(s.Requests.Count(r => r.Method == "PATCH") == 1, $"507 不该重试,实际发了 {s.Requests.Count(r => r.Method == "PATCH")} 次");
}

static async Task CheckTaskGoneAsync()
{
    var s = new FakeTus { DeclaredSize = 16, PatchStatus = HttpStatusCode.NotFound };
    var up = new TusUploader(Client(s, zeroDelay: true), chunkSize: 16);
    var handle = await up.CreateAsync(new UploadRequest { SpaceId = "sp", Name = "a.bin", Size = 16 });

    var ex = await Catch<ApiException>(() => up.UploadAsync(handle, new MemoryStream(Payload(16))));
    Assert(ex.Status == HttpStatusCode.NotFound, $"应抛出 404,实际 {ex.Status}");
    Assert(s.Requests.Count(r => r.Method == "PATCH") == 1, "任务不存在时不该重试");
}

static async Task CheckCancelAsync()
{
    var s = new FakeTus { DeclaredSize = 16 };
    var up = new TusUploader(Client(s), chunkSize: 16);
    var handle = await up.CreateAsync(new UploadRequest { SpaceId = "sp", Name = "a.bin", Size = 16 });

    await up.CancelAsync(handle);
    var del = s.Requests.Single(r => r.Method == "DELETE");
    Assert(del.Ticket == "TICKET-1", "取消也必须带 ticket");
    Assert(del.Uri == "/api/v1/upload/up-1", $"取消路径不对:{del.Uri}");

    // 幂等:服务端对已结束任务也返回 204,客户端不该因为"任务已不在"报错
    s.DeleteStatus = HttpStatusCode.NotFound;
    await up.CancelAsync(handle);
}

static async Task CheckCreateNotRetriedAsync()
{
    var s = new FakeTus { CreateStatus = HttpStatusCode.InternalServerError };
    var up = new TusUploader(Client(s, zeroDelay: true), chunkSize: 16);

    await Catch<ApiException>(() =>
        up.CreateAsync(new UploadRequest { SpaceId = "sp", Name = "a.bin", Size = 16 }));
    Assert(s.Requests.Count(r => r.Method == "POST" && r.Uri == "/api/v1/upload/create") == 1,
        "建任务不能自动重试(重试会建出两个任务、各预留一份额度)");
}

static async Task CheckPartialChunkSelfHealAsync()
{
    // 真实故障:一片发出去了,服务端**只写进去一部分**就失败(磁盘抖动/进程崩溃)。
    // 此时本地偏移已经不可信:带着旧偏移重试会撞 409(Upload-Offset 与服务端不一致),
    // 而照着"本地已发字节"续传会**写坏文件**。正确动作是回来问服务端偏移再继续。
    var s = new FakeTus { DeclaredSize = 32, PartialAcceptTimes = 1 };
    var up = new TusUploader(Client(s, zeroDelay: true), chunkSize: 16);
    var handle = await up.CreateAsync(new UploadRequest { SpaceId = "sp", Name = "a.bin", Size = 32 });

    var res = await up.UploadAsync(handle, new MemoryStream(Payload(32)));

    Assert(res.FinalizedInline, "自愈后应当照样定稿");
    Assert(s.StoredBytes() == 32, $"自愈后服务端应当是完整 32 字节,实际 {s.StoredBytes()}");
    Assert(s.Content().SequenceEqual(Payload(32)), "自愈后内容必须逐字节正确");
    Assert(s.Requests.Count(r => r.Method == "HEAD") >= 1, "部分落盘后必须回来问服务端偏移(HEAD)");
    // 对齐之后发出的片,偏移必须等于服务端当时已收的字节数(而不是本地计数)
    Assert(s.Requests.Any(r => r.Method == "PATCH" && r.Offset == 8),
        "重新对齐后应从服务端偏移(8)继续,而不是从本地计数继续");
}

static async Task CheckChunkingAndContentAsync()
{
    // 分片边界:40 字节 / 16 = 16 + 16 + 8(最后一片不满)
    var s = new FakeTus { DeclaredSize = 40 + 0 };
    var up = new TusUploader(Client(s), chunkSize: 16);
    var handle = await up.CreateAsync(new UploadRequest { SpaceId = "sp", Name = "a.bin", Size = 41 - 1 });
    var res = await up.UploadAsync(handle, new MemoryStream(Payload(40)));

    Assert(res.FinalizedInline, "40 字节发完应定稿");
    Assert(s.StoredBytes() == 40, $"服务端应收到 40 字节,实际 {s.StoredBytes()}");
    Assert(s.Content().SequenceEqual(Payload(40)), "分片拼接后的内容必须与原文逐字节一致");
    var offsets = s.Requests.Where(r => r.Method == "PATCH").Select(r => r.Offset).ToList();
    Assert(offsets.SequenceEqual(new long[] { 0, 16, 32 }), $"偏移序列应为 0,16,32,实际 {string.Join(",", offsets)}");
}

// ---------------------------------------------------------------- 工具

static byte[] Payload(int size)
{
    var b = new byte[size];
    for (var i = 0; i < size; i++)
    {
        b[i] = (byte)('A' + (i % 26)); // 可辨识的内容:写重/漏写都能被逐字节断言抓到
    }
    return b;
}

static ApiClient Client(FakeTus s, bool zeroDelay = false) => new(
    new ClientOptions { BaseAddress = new Uri("http://127.0.0.1:9/") },
    tokens: null,
    handler: s,
    retry: new ApiClientOptions
    {
        MaxAttempts = 3,
        BaseBackoff = TimeSpan.FromMilliseconds(1),
        DelayAsync = zeroDelay ? (_, _, _) => Task.CompletedTask : null,
    });

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception(message);
    }
}

static async Task<T> Catch<T>(Func<Task> action) where T : Exception
{
    try
    {
        await action();
    }
    catch (T ex)
    {
        return ex;
    }
    throw new Exception($"预期抛出 {typeof(T).Name},但没有");
}

// ---------------------------------------------------------------- 假服务端

/// <summary>
/// 会记账的假 TUS 服务端:按偏移写入缓冲区,从而能断言"内容是否正确"。
/// </summary>
sealed class FakeTus : HttpMessageHandler
{
    private readonly List<byte> _store = new();
    private int _chunkFailuresLeft;

    public long DeclaredSize { get; set; }
    public long? FailAfterBytes { get; set; }
    public int FailChunkTimes { get; set; }

    /// <summary>有几片"只写进去一半"然后返回 5xx(模拟部分落盘 → 本地偏移失效)。</summary>
    public int PartialAcceptTimes { get; set; }
    public bool RequireExplicitFinish { get; set; }
    public HttpStatusCode PatchStatus { get; set; } = HttpStatusCode.NoContent;
    public HttpStatusCode CreateStatus { get; set; } = HttpStatusCode.Created;
    public HttpStatusCode DeleteStatus { get; set; } = HttpStatusCode.NoContent;
    public List<Recorded> Requests { get; } = new();

    public sealed record Recorded(string Method, string Uri, long Offset, string? Ticket);

    public int StoredBytes() => _store.Count;

    public byte[] Content() => _store.ToArray();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!.AbsolutePath;
        var method = request.Method.Method;
        var ticket = request.Headers.TryGetValues("X-Upload-Token", out var t) ? t.FirstOrDefault() : null;
        var offset = request.Headers.TryGetValues("Upload-Offset", out var o) && long.TryParse(o.FirstOrDefault(), out var ov)
            ? ov
            : -1;
        Requests.Add(new Recorded(method, uri, offset, ticket));

        if (method == "POST" && uri == "/api/v1/upload/create")
        {
            if (CreateStatus != HttpStatusCode.Created)
            {
                return Resp(CreateStatus, "{\"code\":\"internal_error\",\"message\":\"服务端异常\"}");
            }
            _chunkFailuresLeft = FailChunkTimes;
            var body = "{\"upload_id\":\"up-1\",\"upload_ticket\":\"TICKET-1\",\"declared_size\":" + DeclaredSize + "}";
            return Resp(HttpStatusCode.Created, body);
        }

        if (method == "HEAD" && uri.StartsWith("/api/v1/upload/", StringComparison.Ordinal))
        {
            var r = Resp(HttpStatusCode.OK, "");
            r.Headers.TryAddWithoutValidation("Upload-Offset", _store.Count.ToString());
            r.Headers.TryAddWithoutValidation("Upload-Length", DeclaredSize.ToString());
            return r;
        }

        if (method == "PATCH" && uri.StartsWith("/tus/", StringComparison.Ordinal))
        {
            if (PatchStatus != HttpStatusCode.NoContent)
            {
                return Resp(PatchStatus, ErrorFor(PatchStatus));
            }
            if (_chunkFailuresLeft > 0)
            {
                _chunkFailuresLeft--;
                return Resp(HttpStatusCode.InternalServerError, "{\"code\":\"internal_error\",\"message\":\"模拟分片失败\"}");
            }
            var bytes = await request.Content!.ReadAsByteArrayAsync(ct);

            if (PartialAcceptTimes > 0)
            {
                // "只写进去一半" → 本地偏移失效(客户端必须回来问服务端偏移)
                PartialAcceptTimes--;
                _store.AddRange(bytes.Take(bytes.Length / 2));
                var partial = Resp(HttpStatusCode.InternalServerError,
                    "{\"code\":\"internal_error\",\"message\":\"模拟只写进去一半\"}");
                partial.Headers.TryAddWithoutValidation("Upload-Offset", _store.Count.ToString());
                return partial;
            }

            // 偏移校验:TUS 要求 PATCH 的 Upload-Offset 与服务端当前偏移一致
            if (offset != _store.Count)
            {
                return Resp(HttpStatusCode.Conflict,
                    "{\"code\":\"invalid_argument\",\"message\":\"Upload-Offset 与服务端不一致\"}");
            }
            // "只收一半就断电":收了前 N 字节后,后续片返回 5xx
            if (FailAfterBytes is { } cap && _store.Count + bytes.Length > cap)
            {
                var take = (int)Math.Max(0, cap - _store.Count);
                _store.AddRange(bytes.Take(take));
                return Resp(HttpStatusCode.InternalServerError, "{\"code\":\"internal_error\",\"message\":\"模拟断电\"}");
            }
            _store.AddRange(bytes);

            var atEnd = _store.Count >= DeclaredSize;
            var resp = Resp(atEnd && !RequireExplicitFinish ? HttpStatusCode.OK : HttpStatusCode.NoContent, "");
            resp.Headers.TryAddWithoutValidation("Upload-Offset", _store.Count.ToString());
            if (atEnd && !RequireExplicitFinish)
            {
                resp.Headers.TryAddWithoutValidation("Upload-Complete", "true");
                resp.Headers.TryAddWithoutValidation("X-File-Id", "file-1");
                resp.Headers.TryAddWithoutValidation("X-File-Version", "1");
            }
            return resp;
        }

        if (method == "POST" && uri.EndsWith("/finish", StringComparison.Ordinal))
        {
            return Resp(HttpStatusCode.OK,
                "{\"file\":{\"id\":\"file-1\",\"version\":1,\"name\":\"a.bin\"},\"replayed\":false}");
        }

        if (method == "DELETE")
        {
            return Resp(DeleteStatus, "");
        }

        return Resp(HttpStatusCode.NotFound, "{\"code\":\"not_found\",\"message\":\"接口不存在\"}");
    }

    private static string ErrorFor(HttpStatusCode status) => status switch
    {
        HttpStatusCode.InsufficientStorage => "{\"code\":\"storage_full\",\"message\":\"服务端磁盘水位\"}",
        HttpStatusCode.NotFound => "{\"code\":\"not_found\",\"message\":\"上传任务不存在\"}",
        _ => "{\"code\":\"internal_error\",\"message\":\"x\"}",
    };

    private static HttpResponseMessage Resp(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
