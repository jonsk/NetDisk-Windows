// 远端变更事件流(SSE)的客户端封装 —— MVP 接线的必要一块。
//
// 服务端在 `/api/v1/events` 推 `ready` 与 `change` 两种事件;`SseParser` 已经写好
// (逐行喂、出帧),这里只负责"把 HTTP 响应体按行喂给解析器并 yield 帧"。
//
// 两个容易做错的地方:
//   1. **不能等整段响应**:SSE 是长连接,读完才返回的写法会永远卡住;
//      必须边读边 yield。所以用 `IAsyncEnumerable` + `StreamReader.ReadLineAsync`。
//   2. **行尾**:SSE 规范里 `\n` 与 `\r\n` 都合法,`ReadLineAsync` 已经处理掉,
//      但空行是**帧分隔符**,不能丢弃 —— 解析器靠它收尾一帧。

using System.Runtime.CompilerServices;
using NetDisk.Transport;

namespace NetDisk.SyncEngine.Host;

/// <summary>变更事件流:打开 `/api/v1/events`,产出解析后的帧。</summary>
public sealed class SseChangeStream
{
    private readonly ApiClient _api;
    private readonly ITokenProvider _tokens;
    private readonly string _spaceId;

    public SseChangeStream(ApiClient api, ITokenProvider tokens, string spaceId)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _spaceId = spaceId;
    }

    public string SpaceId => _spaceId;

    /// <summary>读到连接结束或取消为止。</summary>
    public async IAsyncEnumerable<SseEvent> ReadAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // 事件流是长连接:不设客户端超时,断开由 ct 或服务端决定
        using var resp = await _api.SendRawAsync(
            HttpMethod.Get, "/api/v1/events",
            contentFactory: null, headers: null, idempotent: true, ct: ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new ApiException(resp.StatusCode, "events_failed",
                $"变更事件流不可用({(int)resp.StatusCode}):{body}");
        }

        var parser = new SseParser();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                yield break; // 服务端关闭了连接:由上层决定重连
            }
            if (parser.Feed(line, out var frame) && frame is not null)
            {
                yield return frame;
            }
        }
    }
}
