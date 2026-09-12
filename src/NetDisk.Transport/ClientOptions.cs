// Transport 的骨架(DE-D-01;DE-D-05 补齐 REST/WebDAV 客户端,TUS 见 DE-D-06)。
//
// 这一版只放一件**必须现在就想清楚**的事:客户端与服务端之间唯一的安全通道
// 是"带 audience 的令牌 + 契约生成的 DTO"。因此这里定义客户端配置的形状,
// 并明确它与 ServerContract 的关系,免得后面各处自己拼 URL/自己解析 JSON。

using NetDisk.ClientCore;

namespace NetDisk.Transport;

/// <summary>客户端连接配置。</summary>
public sealed class ClientOptions
{
    /// <summary>服务端基址(生产由 Nginx 终止 TLS,这里必须是 https)。</summary>
    public required Uri BaseAddress { get; init; }

    /// <summary>
    /// 受众(R-14 分端):桌面端固定 <c>desktop</c>。
    ///
    /// **不能省**:分端的意义是"令牌在哪个客户端泄露,能力就限制在那边" ——
    /// 桌面令牌拿不到 web 后台的能力(见服务端 adminChain 的 audience 校验)。
    /// </summary>
    public string Audience { get; init; } = "desktop";

    /// <summary>HTTP 超时(大文件传输不适用:上传/下载走各自的流式限时)。</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// 令牌集合(access + refresh + **绝对**过期时刻),由登录流程产出、由 DPAPI 存储(DE-D-04)。
///
/// 名字刻意不叫 `TokenPair`:契约生成物(`ClientCore.Generated.Models.g.cs`)里已经有一个
/// `TokenPair`,那是**线上的形状**(`expires_in` 是相对秒数)。两者同名会让"TokenPair 到底
/// 带不带绝对时刻"在不同文件里有不同答案 —— 而相对量落盘正是本项目反复踩到的坑
/// (见 AuthApi 里 expires_in → 绝对时刻的注释;同名冲突在首次编译时以 CS0738 的形式炸出来)。
/// </summary>
public sealed record TokenSet
{
    public required string AccessToken { get; init; }
    public required string RefreshToken { get; init; }
    public required DateTimeOffset AccessExpiresAt { get; init; }
}

/// <summary>
/// 令牌提供者:Transport 每跳取一次 access token,由上层负责刷新(DE-D-04)。
///
/// 用委托而不是让 Transport 自己管刷新:刷新涉及 DPAPI 落盘与"被吊销就退出"
/// 的 UI 决策(DE-D-04 ③),把那些塞进 HTTP 层会让这一层同时依赖存储与 UI。
/// </summary>
public interface ITokenProvider
{
    /// <summary>取当前可用的 access token;必要时由实现方负责先刷新。</summary>
    ValueTask<string> GetAccessTokenAsync(CancellationToken ct);

    /// <summary>服务端明确拒绝该令牌(401)时回调,供上层决定是否重新登录。</summary>
    void OnUnauthorized();
}
