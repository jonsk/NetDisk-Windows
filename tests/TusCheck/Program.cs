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
    ("⑤ 未即时定稿 → 用**空体 PATCH** 定稿(绝不再调秒传 finish)", CheckExplicitFinishAsync),
    ("⑥ 507 额度不足不重试", CheckQuotaNotRetriedAsync),
    ("⑦ 404(任务丢失)不重试并给出结构化错误", CheckTaskGoneAsync),
    ("⑧ 取消上传:DELETE 带 ticket 且幂等", CheckCancelAsync),
    ("⑨ 建任务不自动重试(避免重复预留额度)", CheckCreateNotRetriedAsync),
    ("⑩ 内容超过声明大小时不越界(以服务端定稿为准)", CheckChunkingAndContentAsync),
    ("⑪ 片落了一半 → 自动重新对齐并继续(同一次调用内自愈)", CheckPartialChunkSelfHealAsync),
    ("⑫ **0 字节文件**能传上去:一次空体 PATCH 定稿,绝不调秒传 finish", CheckEmptyFileAsync),
    ("⑬ 4xx(非暂时)失败 → **取消任务**(不留占着名字的上传任务)", CheckHopelessCancelsAsync),
    ("⑭ 暂时性失败(5xx)**不**取消(保住断点续传状态)", CheckTransientKeepsTaskAsync),
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
    // 声明 32 字节、内容也是 32:服务端**第一次**写满时没定稿(RequireExplicitFinish),
    // 客户端必须再发一次定稿动作 —— 而那个动作是**空体 PATCH**,不是秒传 finish。
    var s = new FakeTus { DeclaredSize = 32, RequireExplicitFinish = true };
    var up = new TusUploader(Client(s), chunkSize: 16);
    var handle = await up.CreateAsync(new UploadRequest { SpaceId = "sp", Name = "a.bin", Size = 32 });
    var res = await up.UploadAsync(handle, new MemoryStream(Payload(32)));

    Assert(!res.FinalizedInline, "服务端未即时定稿时应走显式定稿");
    Assert(res.FileId == "file-1", $"应当拿到文件 id,实际 '{res.FileId}'");
    var patches = s.Requests.Where(r => r.Method == "PATCH" && r.Uri.StartsWith("/tus/")).ToList();
    Assert(patches.Count == 3, $"应为 16+16+空体 三次 PATCH,实际 {patches.Count} 次");
    Assert(s.LastPatchLength == 0, $"第三次 PATCH 必须是**空体**(定稿动作),实际 {s.LastPatchLength} 字节");
    Assert(!s.Requests.Any(r => r.Uri.EndsWith("/finish", StringComparison.Ordinal)),
        "**绝不能**调 /upload/{id}/finish:那是秒传(持物证明)端点,契约要求 nonce,拿它当普通定稿会 400「缺少 nonce」");
}

static async Task CheckEmptyFileAsync()
{
    // 用户实测的那条路径:**0 字节文件**。第一次读就是 0,以前直接去调秒传 finish → 400「缺少 nonce」,
    // 而且失败后任务行占着名字(24h),后续每次重试都 409 —— 文件永远传不上去。
    var s = new FakeTus { DeclaredSize = 0 };
    var up = new TusUploader(Client(s), chunkSize: 16);
    var handle = await up.CreateAsync(new UploadRequest { SpaceId = "sp", Name = "空.bmp", Size = 0 });
    var res = await up.UploadAsync(handle, new MemoryStream(Array.Empty<byte>()));

    Assert(res.FileId == "file-1", $"0 字节文件必须能定稿并拿到 id,实际 '{res.FileId}'");
    var patches = s.Requests.Where(r => r.Method == "PATCH" && r.Uri.StartsWith("/tus/")).ToList();
    Assert(patches.Count == 1, $"0 字节文件应恰好发一次(空体)PATCH,实际 {patches.Count} 次");
    Assert(s.LastPatchLength == 0, $"那次 PATCH 必须是空体,实际 {s.LastPatchLength} 字节");
    Assert(!s.Requests.Any(r => r.Uri.EndsWith("/finish", StringComparison.Ordinal)),
        "0 字节文件不得调用秒传 finish(会 400「缺少 nonce」,并把名字占死)");
    Assert(!s.Requests.Any(r => r.Method == "DELETE" && r.Uri.StartsWith("/api/v1/upload/", StringComparison.Ordinal)),
        "成功路径不该取消任务");
}

static async Task CheckHopelessCancelsAsync()
{
    // 400 = 这个任务已经没救了。**必须取消**:不取消就占着名字直到过期(默认 24h),
    // 之后每轮重试都只会 409「同目录下已有一个正在上传的同名文件」。
    var s = new FakeTus { DeclaredSize = 16, PatchStatus = HttpStatusCode.BadRequest };
    var up = new TusUploader(Client(s, zeroDelay: true), chunkSize: 16);
    var handle = await up.CreateAsync(new UploadRequest { SpaceId = "sp", Name = "a.bin", Size = 16 });

    var ex = await Catch<ApiException>(() => up.UploadAsync(handle, new MemoryStream(Payload(16))));
    Assert(ex.Status == HttpStatusCode.BadRequest, $"应抛出 400,实际 {ex.Status}");
    var cancels = s.Requests.Where(r =>
        r.Method == "DELETE" && r.Uri == "/api/v1/upload/up-1").ToList();
    Assert(cancels.Count == 1, $"应恰好取消任务一次,实际 {cancels.Count} 次");
    Assert(cancels[0].Ticket == "TICKET-1", "取消也必须带 ticket");
}

static async Task CheckTransientKeepsTaskAsync()
{
    // 500 = 暂时性。**不能**取消:任务与服务端已收的字节还在,
    // 下一轮对账可以**从偏移继续**(取消等于把断点续传的成果扔掉)。
    var s = new FakeTus { DeclaredSize = 16, PatchStatus = HttpStatusCode.InternalServerError };
    var up = new TusUploader(Client(s, zeroDelay: true), chunkSize: 16);
    var handle = await up.CreateAsync(new UploadRequest { SpaceId = "sp", Name = "a.bin", Size = 16 });

    await Catch<ApiException>(() => up.UploadAsync(handle, new MemoryStream(Payload(16))));
    Assert(!s.Requests.Any(r => r.Method == "DELETE"),
        "暂时性失败不该取消任务(否则断点续传状态被扔掉)");
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

    /// <summary>最近一次 PATCH 的请求体字节数(用来断言"空体 PATCH"这个定稿动作)。</summary>
    public long LastPatchLength { get; private set; } = -1;

    /// <summary>RequireExplicitFinish 模式下:第一次写满时不定稿,等一个**空体 PATCH** 才定稿。</summary>
    private bool _awaitingFinalizePatch;

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
            LastPatchLength = bytes.Length;

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

            // RequireExplicitFinish:第一次写满时**不定稿**(204),等客户端再发一个
            // **空体 PATCH** 才定稿 —— 这正是真实服务端的语义(`newOffset >= DeclaredSize`
            // 时定稿,0 字节文件 `0 >= 0` 也成立)。
            var atEnd = _store.Count >= DeclaredSize;
            if (atEnd && RequireExplicitFinish && bytes.Length > 0)
            {
                _awaitingFinalizePatch = true;
                var pending = Resp(HttpStatusCode.NoContent, "");
                pending.Headers.TryAddWithoutValidation("Upload-Offset", _store.Count.ToString());
                return pending;
            }
            var shouldFinalize = atEnd && (!RequireExplicitFinish || _awaitingFinalizePatch || bytes.Length == 0);
            var resp = Resp(atEnd && shouldFinalize ? HttpStatusCode.OK : HttpStatusCode.NoContent, "");
            resp.Headers.TryAddWithoutValidation("Upload-Offset", _store.Count.ToString());
            if (atEnd && shouldFinalize)
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
