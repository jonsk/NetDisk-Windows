// SSE 报文解析(DE-D-13 ①)。
//
// 服务端帧格式(syncsse.Encode,契约 6.9):
//
//	id: {space_id}:{change_seq}
//	event: change
//	data: {json}
//	(空行)
//
// 以及连接建立时的 `event: ready`(无 id)。
//
// 为什么要自己解析而不是找个库:这一层要断言的语义非常具体 ——
//   - **帧 id 拆成 `{space, seq}`** 用来推进**该空间**的游标(多空间共用一个连接,
//     把 id 当成一个不透明的字符串就等于放弃了"每空间独立游标");
//   - 解析必须能容忍**分块到达**(一次 TCP 读可能只拿到半个帧)与 CRLF/LF 混用;
//   - `data:` 可以是多行(规范允许),按行拼接。
// 这些用库反而看不见(库帮你"处理掉"了,而处理错了你也发现不了)。

using System.Text;

namespace NetDisk.Transport;

/// <summary>一条 SSE 事件。</summary>
public sealed record SseEvent(string? Id, string Event, string Data)
{
    /// <summary>注释/心跳(以 `:` 开头的行)不会产生事件,这里只用于区分 `ready`。</summary>
    public bool IsReady => string.Equals(Event, "ready", StringComparison.Ordinal);

    public bool IsChange => string.Equals(Event, "change", StringComparison.Ordinal);
}

/// <summary>帧 id 拆出来的 (space, seq)。</summary>
public readonly record struct ChangeFrameId(string SpaceId, long Seq)
{
    /// <summary>
    /// 解析 `{space_id}:{change_seq}`。
    ///
    /// 解析失败返回 false 而**不是抛异常**:一条畸形帧不该让整条同步通道崩掉 ——
    /// 丢掉它、靠下一次 `/changes` 补拉即可(事件本来就是"尽力而为"的加速通道)。
    /// </summary>
    public static bool TryParse(string? id, out ChangeFrameId parsed)
    {
        parsed = default;
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }
        var sep = id.LastIndexOf(':');
        if (sep <= 0 || sep == id.Length - 1)
        {
            return false;
        }
        var space = id[..sep];
        // 空间 id 可能是 uuid(含 '-'),所以从**最后一个**冒号切分;seq 必须是十进制整数
        var seqPart = id[(sep + 1)..];
        if (!long.TryParse(seqPart, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var seq))
        {
            return false;
        }
        parsed = new ChangeFrameId(space, seq);
        return true;
    }
}

/// <summary>增量读取 SSE 事件的解析器(按帧派发,容忍分块到达)。</summary>
public sealed class SseParser
{
    private readonly StringBuilder _data = new();
    private string? _id;
    private string _event = "";

    /// <summary>解析一行(不含换行);返回 true 表示凑齐了一帧。</summary>
    public bool Feed(string line, out SseEvent? frame)
    {
        frame = null;
        // CRLF:统一去掉行尾的 \r
        if (line.EndsWith('\r'))
        {
            line = line[..^1];
        }

        if (line.Length == 0)
        {
            // 空行 = 一帧结束。规范:没有 data 的帧不派发(注释块/心跳)
            if (_data.Length == 0 && string.IsNullOrEmpty(_event))
            {
                _id = null;
                return false;
            }
            frame = new SseEvent(_id, _event.Length == 0 ? "message" : _event, _data.ToString());
            _data.Clear();
            _id = null;
            _event = "";
            return true;
        }

        if (line[0] == ':')
        {
            // 注释(服务端心跳用它保活):不产生事件,但也不清空当前帧
            return false;
        }

        var colon = line.IndexOf(':');
        var field = colon < 0 ? line : line[..colon];
        var value = colon < 0 ? "" : line[(colon + 1)..];
        if (value.StartsWith(' '))
        {
            value = value[1..]; // 规范:冒号后的**一个**空格是分隔符,不算数据
        }

        switch (field)
        {
            case "id":
                _id = value;
                break;
            case "event":
                _event = value;
                break;
            case "data":
                if (_data.Length > 0)
                {
                    _data.Append('\n');
                }
                _data.Append(value);
                break;
            default:
                break; // retry: 等字段忽略(重连策略由客户端自己按退避决定)
        }
        return false;
    }

    /// <summary>把一段文本(可能包含多行/多帧)灌进去,产出所有完整的帧。</summary>
    public IEnumerable<SseEvent> FeedText(string text)
    {
        foreach (var line in SplitLines(text))
        {
            if (Feed(line, out var frame) && frame is not null)
            {
                yield return frame;
            }
        }
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                yield return text[start..i];
                start = i + 1;
            }
        }
        if (start < text.Length)
        {
            yield return text[start..];
        }
    }
}
