// DE-D-13 行为检查器:SSE 消费 + /changes 游标双态。
//
// 用法:`dotnet run --project desktop/tests/ChangeFeedCheck -c Release`
//
// 这里最要紧的一条是**重连顺序**:先逐空间补拉、再重建连接。顺序反了会留下
// "连接已建立但游标还是旧的"窗口,窗口内到达的事件被当成重复丢掉 → 永久漏一段变更,
// 而界面上一切正常。所以检查器不只断言"补过拉",而是断言**连接回调发生在最后一次补拉之后**。

using NetDisk.ClientCore;
using NetDisk.SyncEngine.Sync;
using NetDisk.Transport;

var checks = new List<(string Name, Func<Task> Run)>
{
    ("① SSE 报文解析:完整帧(含帧 id 与 event)", CheckParseBasicAsync),
    ("② 分块到达:半个帧也不丢(解析器保持状态)", CheckParseChunkedAsync),
    ("③ 多行 data 按行拼接 / CRLF / 注释心跳", CheckParseMultilineAsync),
    ("④ 畸形帧被丢弃而不是崩掉(靠补拉兜底)", CheckMalformedFrameAsync),
    ("⑤ 帧 id 拆 {space,seq}:两个空间各自推进游标", CheckPerSpaceCursorAsync),
    ("⑥ 重复/乱序帧幂等忽略(游标不倒退)", CheckDuplicateAndOutOfOrderAsync),
    ("⑦ 事件不直接落定态:只产出「待补拉」意图", CheckEventDoesNotApplyStateAsync),
    ("⑧ 重连顺序:先逐空间补拉,再重建连接", CheckReconnectOrderAsync),
    ("⑨ 补拉翻页直到 has_more=false", CheckPagingAsync),
    ("⑩ cursor_expired(409)→ 该空间全量重扫,绝不静默当空", CheckCursorExpiredAsync),
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
    Console.WriteLine($"全部通过:{checks.Count} 项 DE-D-13 行为断言(SSE + 游标双态)");
    return 0;
}
Console.Error.WriteLine($"{failed}/{checks.Count} 项失败");
return 1;

// ---------------------------------------------------------------- 检查

static Task CheckParseBasicAsync()
{
    var p = new SseParser();
    var frames = p.FeedText("id: sp-1:42\nevent: change\ndata: {\"k\":1}\n\n").ToList();
    Assert(frames.Count == 1, $"应解析出 1 帧,实际 {frames.Count}");
    Assert(frames[0].Id == "sp-1:42", $"帧 id 应保留原样,实际 {frames[0].Id}");
    Assert(frames[0].Event == "change", $"事件名应为 change,实际 {frames[0].Event}");
    Assert(frames[0].Data == "{\"k\":1}", $"data 应为原文,实际 {frames[0].Data}");
    Assert(frames[0].IsChange, "IsChange 应为 true");
    return Task.CompletedTask;
}

static Task CheckParseChunkedAsync()
{
    // 一次 TCP 读只拿到半个帧:解析器必须**保持状态**,下一段补上后凑成整帧
    var p = new SseParser();
    var a = p.FeedText("id: sp-1:7\nevent: cha").ToList();
    Assert(a.Count == 0, "半个帧不该产出事件");
    var b = p.FeedText("nge\ndata: {\"x\":2}\n\n").ToList();
    Assert(b.Count == 1 && b[0].Id == "sp-1:7" && b[0].Data == "{\"x\":2}",
        $"分块到达后应凑成完整帧,实际 {b.Count} 帧");
    return Task.CompletedTask;
}

static Task CheckParseMultilineAsync()
{
    var p = new SseParser();
    // 多行 data(规范里按 \n 拼接)+ CRLF 行尾 + 注释心跳 + data 冒号后一个空格
    var frames = p.FeedText(
        ": keep-alive\r\n" +
        "event: change\r\n" +
        "data: line1\r\n" +
        "data: line2\r\n" +
        "\r\n").ToList();
    Assert(frames.Count == 1, $"心跳不该产生事件,实际 {frames.Count} 帧");
    Assert(frames[0].Data == "line1\nline2", $"多行 data 应以换行拼接,实际「{frames[0].Data}」");

    var p2 = new SseParser();
    var f2 = p2.FeedText("event: ready\ndata: {\"ok\":1}\n\n").ToList();
    Assert(f2.Count == 1 && f2[0].IsReady, "ready 帧应被识别(它没有 id,不该推进游标)");
    return Task.CompletedTask;
}

static Task CheckMalformedFrameAsync()
{
    Assert(!ChangeFrameId.TryParse("no-colon", out _), "没有冒号的 id 应解析失败");
    Assert(!ChangeFrameId.TryParse("sp-1:", out _), "没有 seq 的 id 应解析失败");
    Assert(!ChangeFrameId.TryParse("sp-1:abc", out _), "seq 非数字应解析失败");
    Assert(!ChangeFrameId.TryParse(null, out _), "null 应解析失败");
    Assert(ChangeFrameId.TryParse("01a0-93a1:99", out var ok) && ok.SpaceId == "01a0-93a1" && ok.Seq == 99,
        "uuid 形态的空间 id(含连字符)应正确按最后一个冒号切分");

    var feed = new ChangeFeedSync((_, _, _) => Task.FromResult(new ChangePage(Array.Empty<string>(), 0, false, "x")));
    var action = feed.OnEvent(new SseEvent("garbage", "change", "{}"));
    Assert(action.Kind == FeedActionKind.None, $"畸形帧应被丢弃,实际 {action.Kind}");
    Assert(action.Reason.Contains("无法解析"), $"应说明丢弃原因,实际「{action.Reason}」");
    return Task.CompletedTask;
}

static Task CheckPerSpaceCursorAsync()
{
    var feed = new ChangeFeedSync((_, _, _) => Task.FromResult(new ChangePage(Array.Empty<string>(), 0, false, "x")));
    feed.SetCursor(new SpaceCursor { SpaceId = "sp-A", LastSeq = 10 });
    feed.SetCursor(new SpaceCursor { SpaceId = "sp-B", LastSeq = 0 });

    var a = feed.OnEvent(new SseEvent("sp-A:11", "change", "{}"));
    Assert(a.Kind == FeedActionKind.PullRequired && a.SpaceId == "sp-A" && a.FromSeq == 10 && a.ToSeq == 11,
        $"sp-A 应提示补拉 10→11,实际 {a}");

    var b = feed.OnEvent(new SseEvent("sp-B:500", "change", "{}"));
    Assert(b.SpaceId == "sp-B" && b.FromSeq == 0 && b.ToSeq == 500,
        $"sp-B 的游标必须**独立**推进(多空间共用一个连接),实际 {b}");

    Assert(feed.CursorOf("sp-A")!.LastSeq == 10,
        "**事件本身不该推进游标**:游标要由补拉的权威结果推进(验收③)");
    return Task.CompletedTask;
}

static Task CheckDuplicateAndOutOfOrderAsync()
{
    var feed = new ChangeFeedSync((_, _, _) => Task.FromResult(new ChangePage(Array.Empty<string>(), 0, false, "x")));
    feed.SetCursor(new SpaceCursor { SpaceId = "sp-A", LastSeq = 50 });

    var dup = feed.OnEvent(new SseEvent("sp-A:50", "change", "{}"));
    Assert(dup.Kind == FeedActionKind.None, $"等于游标的帧应忽略,实际 {dup.Kind}");

    var older = feed.OnEvent(new SseEvent("sp-A:21", "change", "{}"));
    Assert(older.Kind == FeedActionKind.None && older.Reason.Contains("乱序"),
        $"乱序(更小 seq)的帧必须忽略:否则游标倒退会让同一段被反复补拉,实际「{older.Reason}」");
    Assert(feed.CursorOf("sp-A")!.LastSeq == 50, "游标不得因为乱序帧而倒退");
    return Task.CompletedTask;
}

static Task CheckEventDoesNotApplyStateAsync()
{
    // 验收③:事件只产出"要补拉"的意图,不携带/不落任何文件状态。
    // FeedAction 里只有 seq 与原因 —— 没有版本、没有路径、没有 kind,
    // 因此调用方**没有办法**拿事件去改本地状态。
    var feed = new ChangeFeedSync((_, _, _) => Task.FromResult(new ChangePage(Array.Empty<string>(), 0, false, "x")));
    feed.SetCursor(new SpaceCursor { SpaceId = "sp-A", LastSeq = 1 });
    var action = feed.OnEvent(new SseEvent("sp-A:2", "change", "{\"file_id\":\"f1\",\"version\":9}"));

    Assert(action.Kind == FeedActionKind.PullRequired, $"应要求补拉,实际 {action.Kind}");
    var props = typeof(FeedAction).GetProperties().Select(p => p.Name).ToArray();
    Assert(!props.Any(p => p.Contains("Version") || p.Contains("FileId") || p.Contains("Path")),
        $"FeedAction 不该携带文件状态字段(否则调用方会拿事件直接改状态),实际字段:{string.Join(",", props)}");
    return Task.CompletedTask;
}

static Task CheckReconnectOrderAsync()
{
    var events = new List<string>();
    var pullCount = 0;
    var feed = new ChangeFeedSync(
        pull: (space, since, _) =>
        {
            pullCount++;
            events.Add($"pull:{space}:since={since}");
            return Task.FromResult(new ChangePage(Array.Empty<string>(), since + 5, false, space));
        },
        reconnect: _ =>
        {
            events.Add("reconnect");
            return Task.CompletedTask;
        });
    feed.SetCursor(new SpaceCursor { SpaceId = "sp-A", LastSeq = 10 });
    feed.SetCursor(new SpaceCursor { SpaceId = "sp-B", LastSeq = 20 });

    var results = feed.ReconnectAsync().GetAwaiter().GetResult();

    Assert(results.Count == 2, $"两个空间都应补拉,实际 {results.Count}");
    Assert(pullCount == 2, $"应补拉 2 次(每空间一次),实际 {pullCount}");
    Assert(events.Count == 3 && events[^1] == "reconnect",
        $"**必须先补拉完再重建连接**(最后一步是 reconnect),实际顺序:{string.Join(" → ", events)}");
    Assert(feed.CursorOf("sp-A")!.LastSeq == 15 && feed.CursorOf("sp-B")!.LastSeq == 25,
        "补拉后游标应推进到服务端给出的 next_seq");
    return Task.CompletedTask;
}

static Task CheckPagingAsync()
{
    var pages = 0;
    var feed = new ChangeFeedSync((space, since, _) =>
    {
        pages++;
        // 第一页说"还有更多",第二页收尾 —— 客户端必须一直翻到 has_more=false
        return Task.FromResult(pages == 1
            ? new ChangePage(new[] { "1", "2" }, since + 100, true, space)
            : new ChangePage(new[] { "3" }, since + 40, false, space));
    });
    feed.SetCursor(new SpaceCursor { SpaceId = "sp-A", LastSeq = 0 });

    var action = feed.CatchUpAsync("sp-A").GetAwaiter().GetResult();
    Assert(pages == 2, $"应翻两页,实际 {pages}");
    Assert(action.ToSeq == 140, $"游标应推进到最后一页的 next_seq=140,实际 {action.ToSeq}");
    Assert(feed.CursorOf("sp-A")!.LastSeq == 140, "游标应与服务端 next_seq 一致");
    return Task.CompletedTask;
}

static Task CheckCursorExpiredAsync()
{
    var feed = new ChangeFeedSync((space, since, _) =>
        throw new ApiException(System.Net.HttpStatusCode.Conflict, "cursor_expired", "游标超窗"));
    feed.SetCursor(new SpaceCursor { SpaceId = "sp-A", LastSeq = 3 });

    var action = feed.CatchUpAsync("sp-A").GetAwaiter().GetResult();
    Assert(action.Kind == FeedActionKind.FullRescanRequired,
        $"409 cursor_expired 必须转成「该空间全量重扫」,实际 {action.Kind}");
    Assert(feed.CursorOf("sp-A")!.NeedsFullRescan, "游标状态应标记 NeedsFullRescan(按空间,不影响别的空间)");

    // 再收到该空间的事件也必须坚持"全量重扫",不能被事件带偏
    var after = feed.OnEvent(new SseEvent("sp-A:999", "change", "{}"));
    Assert(after.Kind == FeedActionKind.FullRescanRequired,
        "超窗未完成全量重扫之前,事件不应把它带回增量补拉(否则永久漏掉超窗那段)");
    return Task.CompletedTask;
}

// ---------------------------------------------------------------- 工具

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception(message);
    }
}
