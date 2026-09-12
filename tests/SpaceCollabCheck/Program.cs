// DE-D-17 行为检查器:团队空间协作 + 分享创建。
//
// 用法:`dotnet run --project desktop/tests/SpaceCollabCheck -c Release`
//
// 两条验收在这里被拆成可执行断言:
//   - **与 H5 同一组接口**:客户端实际使用的每条路径都必须**在契约里存在**
//     (多写 = 发明接口;少写 = 功能没接上)。契约是唯一权威(`docs/api/openapi.yaml`)。
//   - **权限判断不在端上**(4.3):机械规则扫源码 + **行为断言**证明"服务端 403 时请求
//     确实发出去了"(端上没有提前拦下),然后才把服务端的原因交给 UI。

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetDisk.Transport;

const string SpaceClientSource = "desktop/src/NetDisk.Transport/SpaceCollabClient.cs";

var checks = new List<(string Name, Func<Task> Run)>
{
    ("① 所有路径都在契约里存在(与 H5 同一组接口)", CheckPathsExistInContractAsync),
    ("② 协作五条路径与 H5/FE-W-11 完全一致", CheckSpacePathsAsync),
    ("③ 分享创建走契约里的 /api/v1/shares", CheckSharePathAsync),
    ("④ 请求体字段名与契约一致(用户名邀请/转让)", CheckPayloadFieldsAsync),
    ("⑤ 服务端 403:请求**已经发出**才失败(端上没提前拦)", CheckForbiddenStillCallsAsync),
    ("⑥ 403/409/404 的失败文案来自服务端(端上只解释)", CheckFailureDescriptionsAsync),
    ("⑦ 机械规则:协作源码里没有「角色→是否发请求」的判断", CheckNoPermissionGateAsync),
    ("⑧ 机械规则:视图层同样不含权限判断", CheckViewLayerCleanAsync),
    ("⑨ 空间视图的 is_owner/permission 只用于展示(不参与任何分支)", CheckDisplayOnlyAsync),
    ("⑩ 解散是硬删:服务层不提供「回收站」式语义", CheckDissolveIsHardDeleteAsync),
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
    Console.WriteLine($"全部通过:{checks.Count} 项 DE-D-17 行为断言(协作与分享)");
    return 0;
}
Console.Error.WriteLine($"{failed}/{checks.Count} 项失败");
return 1;

// ---------------------------------------------------------------- 检查

static Task CheckPathsExistInContractAsync()
{
    var contract = File.ReadAllText(Locate("docs/api/openapi.yaml"));
    var declared = Regex.Matches(contract, @"^\s{2}(/api/v1/[^\s:]+):", RegexOptions.Multiline)
        .Select(m => m.Groups[1].Value)
        .ToHashSet(StringComparer.Ordinal);

    var used = UsedPaths();
    Assert(used.Count >= 8, $"应当至少用到 8 条路径,实际 {used.Count}");
    var missing = used.Where(p => !declared.Contains(p)).ToArray();
    Assert(missing.Length == 0,
        "客户端用了契约里不存在的路径(发明接口会静默失败):" + string.Join(",", missing));
    return Task.CompletedTask;
}

static Task CheckSpacePathsAsync()
{
    var used = UsedPaths();
    string[] required =
    {
        "/api/v1/spaces",
        "/api/v1/spaces/{id}/members",
        "/api/v1/spaces/{id}/members/{userId}",
        "/api/v1/spaces/{id}/leave",
        "/api/v1/spaces/{id}/transfer",
        "/api/v1/spaces/{id}",
    };
    foreach (var p in required)
    {
        Assert(used.Contains(p), $"缺路径 {p}(H5 用的就是这一组,桌面端不许另开一套)");
    }
    return Task.CompletedTask;
}

static Task CheckSharePathAsync()
{
    var used = UsedPaths();
    Assert(used.Contains("/api/v1/shares"), "分享创建必须走契约里的 /api/v1/shares");
    Assert(used.Contains("/api/v1/shares/{id}"), "撤销分享同样走契约路径");
    return Task.CompletedTask;
}

static Task CheckPayloadFieldsAsync()
{
    // 邀请与转让在服务端都**接受用户名**(FE-W-11 的 resolveUser);字段名必须与契约一致,
    // 否则服务端会 400(而且是在用户点了按钮之后才发现)。
    var source = File.ReadAllText(Locate(SpaceClientSource));
    Assert(source.Contains("new { username, permission }", StringComparison.Ordinal),
        "邀请的请求体必须是 {username, permission}(与 H5 同字段)");
    Assert(source.Contains("new { new_owner_username = newOwnerUsername }", StringComparison.Ordinal),
        "转让的请求体必须是 {new_owner_username}(与 H5 同字段)");
    Assert(source.Contains("new { name }", StringComparison.Ordinal),
        "建空间的请求体必须是 {name}");
    return Task.CompletedTask;
}

static async Task CheckForbiddenStillCallsAsync()
{
    // **本项最关键的行为断言**:即使服务端刚告诉我们"你不是 owner"(is_owner=false),
    // 动作也必须照发;被拒之后才把服务端的原因交给 UI。
    // 若端上拿 is_owner 当门禁(4.3 禁止),这条断言会看到**没有第二个请求**。
    var stub = new Stub(new Queue<(HttpStatusCode, string)>(new[]
    {
        (HttpStatusCode.OK,
            "{\"spaces\":[{\"id\":\"sp-1\",\"kind\":\"team\",\"name\":\"团队\",\"owner_id\":\"u-2\",\"is_owner\":false}],\"total\":1}"),
        (HttpStatusCode.Forbidden,
            "{\"code\":\"forbidden\",\"message\":\"只有空间所有者可以解散空间\"}"),
    }));
    var client = stub.Client();

    var list = await client.ListMineAsync(); // 服务端说:我不是 owner(仅供展示)
    Assert(list.spaces.Count == 1 && list.spaces[0].is_owner == false,
        "前置条件:服务端返回 is_owner=false");

    var ex = await Catch<ApiException>(() => client.DissolveAsync("sp-1"));

    Assert(stub.CallCount == 2,
        $"端上不得用 is_owner 当门禁(那会把判权搬到端上):应发出第 2 个请求,实际总共 {stub.CallCount} 次");
    Assert(stub.LastPath == "/api/v1/spaces/sp-1", "应调用解散契约路径,实际 " + stub.LastPath);
    Assert(ex.Code == "forbidden", "应把服务端的业务码原样带出,实际 " + ex.Code);
}

static Task CheckFailureDescriptionsAsync()
{
    var forbidden = SpaceCollabClient.DescribeFailure(
        new ApiException(HttpStatusCode.Forbidden, "forbidden", "只有空间所有者可以解散空间"));
    Assert(forbidden == "只有空间所有者可以解散空间",
        "403 的文案应当**用服务端给的原因**(端上另写一套会与判权逻辑漂移):" + forbidden);

    var revoked = SpaceCollabClient.DescribeFailure(
        new ApiException(HttpStatusCode.Forbidden, "space_revoked", "你没有该空间的访问权限"));
    Assert(revoked.Contains("刷新"), "space_revoked 应提示刷新(成员关系变了),实际 " + revoked);

    var gone = SpaceCollabClient.DescribeFailure(
        new ApiException(HttpStatusCode.Gone, "space_gone", "空间不存在或已被解散"));
    Assert(gone.Contains("不存在"), "space_gone 应说明空间已不存在,实际 " + gone);

    var conflict = SpaceCollabClient.DescribeFailure(
        new ApiException(HttpStatusCode.Conflict, "conflict", "空间内仍有 1 个条目,请先清空再解散"));
    Assert(conflict.Contains("先清空"), "409 应透出服务端的具体原因,实际 " + conflict);
    return Task.CompletedTask;
}

static Task CheckNoPermissionGateAsync()
{
    // 机械规则(4.3):协作源码里不得出现"角色/是否 owner → 要不要发请求"的判断。
    // 允许出现的东西:把服务端给的字段**原样搬运**(record 定义、JsonPropertyName 注解);
    // 禁止的:`IsOwner &&`、`Permission == "manager"`、`if (...permission...) { return; }` 之类。
    var source = StripComments(File.ReadAllText(Locate(SpaceClientSource)));

    string[] forbidden =
    {
        "IsOwner &&",
        "IsOwner&&",
        "== \"manager\"",
        "== \"editor\"",
        "== \"reader\"",
        "Permission ==",
        "permission ==",
        "canManage",
        "CanManage",
        "HasPermission",
        "CheckPermission",
        "EnsureOwner",
    };
    foreach (var f in forbidden)
    {
        Assert(!source.Contains(f, StringComparison.Ordinal),
            $"协作源码里出现 {f}:权限判断必须在服务端(4.3),端上不得用它决定是否发请求");
    }

    // 反面对照:必须真的有"把服务端原因解释给用户"的能力(否则上面那条禁则很容易靠删功能满足)
    Assert(source.Contains("DescribeFailure", StringComparison.Ordinal),
        "客户端应当只**解释**服务端的失败原因(DescribeFailure),而不是自己判断权限");
    return Task.CompletedTask;
}

static Task CheckViewLayerCleanAsync()
{
    // 视图层(若已落地)同样不得持有权限判断 —— 判权在服务端是**全局**纪律,不是某一层的事。
    var view = TryLocate("desktop/src/NetDisk.App/Views/SpacesView.xaml.cs");
    if (view is null)
    {
        return Task.CompletedTask; // 视图尚未落地时不阻断(该项由 DE-D-17 的 UI 部分交付)
    }
    var source = StripComments(File.ReadAllText(view));
    foreach (var f in new[] { "== \"manager\"", "Permission ==", "HasPermission", "CanManage" })
    {
        Assert(!source.Contains(f, StringComparison.Ordinal),
            $"视图层出现 {f}:判权不得在端上(按钮显隐可以用服务端给的 is_owner,但不能作为**动作**的门禁)");
    }
    return Task.CompletedTask;
}

static Task CheckDisplayOnlyAsync()
{
    // `is_owner` / `permission` 只能被**读出来展示**;不存在任何"用它们做分支"的方法。
    // 这里断言的是**契约生成物**的属性名(与契约字段一致:is_owner / permission)。
    var perms = typeof(SpaceView).GetProperties().Select(p => p.Name).ToArray();
    Assert(perms.Contains("is_owner"), "空间视图应带 is_owner(供 UI 展示「我是所有者」)");

    // 结构断言:客户端类型上不得存在"判断我能不能做某事"的方法
    var suspicious = typeof(SpaceCollabClient).GetMethods()
        .Select(m => m.Name)
        .Where(n => n.StartsWith("Can", StringComparison.Ordinal)
                    || n.Contains("Permission", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("IsAllowed", StringComparison.OrdinalIgnoreCase))
        .ToArray();
    Assert(suspicious.Length == 0,
        "SpaceCollabClient 不该提供能力判断方法(会被顺手当成端上门禁):" + string.Join(",", suspicious));

    var memberPerms = typeof(MemberView).GetProperties().Select(p => p.Name).ToArray();
    Assert(memberPerms.Contains("permission"), "成员视图应带 permission(供 UI 显示角色标签)");

    // 契约的 ShareCreated **只回 token**(没有 url 字段)→ 链接按 /s/{token} 拼
    var created = new ShareCreated
    {
        id = "s1",
        token = "abc123",
        file_id = "f1",
        need_password = false,
    };
    Assert(ShareLinks.LinkFor(created, "https://pan.example.com") == "https://pan.example.com/s/abc123",
        "必须按 /s/{token} 拼分享链接(否则 UI 只能显示空白)");
    return Task.CompletedTask;
}

static Task CheckDissolveIsHardDeleteAsync()
{
    // 解散是**硬删**(4.5 无回收站):UI 必须二次确认,而服务层不得提供"移到回收站"这类
    // 让人误以为可恢复的语义(那会让"解散"看起来可撤销)。
    var source = File.ReadAllText(Locate(SpaceClientSource));
    Assert(source.Contains("硬删"), "解散的注释必须写明是硬删(UI 据此做二次确认)");
    foreach (var bad in new[] { "RecycleBin", "回收站", "Restore", "Undelete" })
    {
        Assert(!source.Contains(bad, StringComparison.Ordinal),
            $"协作客户端不该出现 {bad}:解散没有回收站(4.5),提供恢复语义会让人误以为可撤销");
    }
    return Task.CompletedTask;
}

// ---------------------------------------------------------------- 工具


static HashSet<string> UsedPaths()
{
    var source = File.ReadAllText(Locate(SpaceClientSource));
    // 抓字符串字面量里的 /api/v1/... 路径,并把插值表达式归一成契约里的 {param} 形态
    var paths = new HashSet<string>(StringComparer.Ordinal);
    foreach (Match m in Regex.Matches(source, "\"(/api/v1/[^\"]*)\""))
    {
        var p = m.Groups[1].Value;
        p = Regex.Replace(p, @"\{[^}]*\}", "{id}");            // {spaceId} → {id}
        p = Regex.Replace(p, @"/\{id\}/members/\{id\}", "/{id}/members/{userId}"); // 末尾 id → userId
        paths.Add(p);
    }
    return paths;
}

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
    throw new Exception("预期抛出 " + typeof(T).Name + ",但没有");
}

/// <summary>去注释(与 WatcherCheck 同款;注释里提到被禁词不该误报)。</summary>
static string StripComments(string source)
{
    var sb = new StringBuilder(source.Length);
    var inLine = false;
    var inBlock = false;
    var inString = false;
    var escaped = false;
    for (var i = 0; i < source.Length; i++)
    {
        var c = source[i];
        var next = i + 1 < source.Length ? source[i + 1] : '\0';
        if (inLine)
        {
            if (c == '\n') { inLine = false; sb.Append(c); }
            continue;
        }
        if (inBlock)
        {
            if (c == '*' && next == '/') { inBlock = false; i++; }
            else if (c == '\n') { sb.Append(c); }
            continue;
        }
        if (inString)
        {
            sb.Append(c);
            if (escaped) { escaped = false; }
            else if (c == '\\') { escaped = true; }
            else if (c == '"') { inString = false; }
            continue;
        }
        if (c == '/' && next == '/') { inLine = true; i++; continue; }
        if (c == '/' && next == '*') { inBlock = true; i++; continue; }
        if (c == '"') { inString = true; }
        sb.Append(c);
    }
    return sb.ToString();
}

static string Locate(string relative)
    => TryLocate(relative) ?? throw new Exception("找不到 " + relative);

static string? TryLocate(string relative)
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        var candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(candidate))
        {
            return candidate;
        }
        dir = dir.Parent;
    }
    return null;
}

/// <summary>按脚本回应的 HTTP 桩(多个响应按顺序出队;只剩一个时重复使用)。</summary>
sealed class Stub : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _responses;
    private (HttpStatusCode Status, string Body) _last = (HttpStatusCode.OK, "{}");

    public Stub(Queue<(HttpStatusCode, string)> responses) => _responses = responses;

    public Stub(HttpStatusCode status, string body)
        : this(new Queue<(HttpStatusCode, string)>(new[] { (status, body) }))
    {
    }

    public int CallCount { get; private set; }

    public string LastPath { get; private set; } = "";

    public SpaceCollabClient Client()
    {
        var api = new ApiClient(
            new ClientOptions { BaseAddress = new Uri("http://127.0.0.1:9/") },
            tokens: null, handler: this,
            retry: new ApiClientOptions { DelayAsync = (_, _, _) => Task.CompletedTask });
        return new SpaceCollabClient(api);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        CallCount++;
        LastPath = request.RequestUri!.AbsolutePath;
        if (_responses.Count > 1)
        {
            _last = _responses.Dequeue();
        }
        else if (_responses.Count == 1)
        {
            _last = _responses.Peek();
        }
        return Task.FromResult(new HttpResponseMessage(_last.Status)
        {
            Content = new StringContent(_last.Body, Encoding.UTF8, "application/json"),
        });
    }
}
