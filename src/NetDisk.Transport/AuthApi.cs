// 认证 API:登录 / 刷新 / 登出(DE-D-04)。
//
// 这一层**只做 HTTP 与错误分类**,不碰存储、不碰 UI:DPAPI 落盘在 SyncEngine
// (Windows 能力),“被吊销要不要退出”在 TokenSession(也属 SyncEngine)。
// 这样切分的理由是刷新令牌**会轮换**(见下),而“谁负责持久化新令牌”必须只有一个
// 明确的答案 —— Transport 只产出 TokenSet,由会话负责在**释放刷新锁之前**落盘。

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetDisk.Transport;

/// <summary>认证失败的种类(决定上层的动作:重试 / 刷新 / 静默退出)。</summary>
public enum AuthFailureKind
{
    /// <summary>不是认证失败。</summary>
    None,

    /// <summary>账号或口令不对(登录接口 401)。</summary>
    InvalidCredentials,

    /// <summary>access 过期(401 <c>token_expired</c>)——可刷新后重试。</summary>
    TokenExpired,

    /// <summary>登录状态已被服务端吊销(401 <c>token_revoked</c>)——**必须重新登录,重试没有意义**。</summary>
    TokenRevoked,

    /// <summary>被限流(429)——退避后重试。</summary>
    RateLimited,

    /// <summary>网络层失败(连不上/超时)——退避后重试。</summary>
    Network,

    /// <summary>其它(5xx 等)。</summary>
    Other,
}

/// <summary>认证失败异常(带分类与业务码)。</summary>
public sealed class AuthException : Exception
{
    public AuthException(AuthFailureKind kind, string message, string? code = null, int statusCode = 0)
        : base(message)
    {
        Kind = kind;
        Code = code;
        StatusCode = statusCode;
    }

    public AuthFailureKind Kind { get; }

    /// <summary>服务端业务码(<c>token_expired</c>/<c>token_revoked</c>/…),仅诊断用。</summary>
    public string? Code { get; }

    public int StatusCode { get; }

    /// <summary>是否应当重新登录(而不是重试)。</summary>
    public bool RequiresRelogin => Kind is AuthFailureKind.TokenRevoked or AuthFailureKind.InvalidCredentials;
}

/// <summary>服务端 <c>/api/v1/auth/*</c> 返回的令牌对象(字段名与契约一致)。</summary>
public sealed record TokenResponse
{
    [JsonPropertyName("access_token")] public string AccessToken { get; init; } = "";
    [JsonPropertyName("refresh_token")] public string RefreshToken { get; init; } = "";
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }

    /// <summary>refresh 的有效期(秒)。只用于诊断与界面提示,不参与刷新判定。</summary>
    [JsonPropertyName("refresh_expires_in")] public int RefreshExpiresIn { get; init; }
}

/// <summary>
/// 认证客户端。
///
/// **刷新令牌会轮换**:服务端每次刷新都发新 refresh 并作废旧的(重放旧 refresh 会得到
/// <c>token_revoked</c>)。因此上层必须①单飞刷新、②把新令牌**先落盘**再让其它请求用旧
/// 结果继续跑 —— 否则"进程在刷新后、落盘前崩溃"会让用户凭空被登出,而并发刷新会让
/// 一半请求拿着已被作废的 refresh。
/// </summary>
public sealed class AuthApi
{
    private readonly HttpClient _http;
    private readonly ClientOptions _options;
    private readonly TimeProvider _clock;

    public AuthApi(ClientOptions options, HttpMessageHandler? handler = null, TimeProvider? clock = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = options.RequestTimeout;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>账密登录(桌面端固定 <c>audience=desktop</c>)。</summary>
    public async Task<TokenSet> LoginAsync(string login, string password, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            login,
            password,
            audience = _options.Audience,
        });
        return await PostTokenAsync("/api/v1/auth/login", body, isLogin: true, ct).ConfigureAwait(false);
    }

    /// <summary>刷新令牌。失败时按分类抛 <see cref="AuthException"/>。</summary>
    public Task<TokenSet> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new AuthException(AuthFailureKind.TokenRevoked, "本地没有刷新令牌");
        }
        var body = JsonSerializer.Serialize(new { refresh_token = refreshToken });
        return PostTokenAsync("/api/v1/auth/refresh", body, isLogin: false, ct);
    }

    /// <summary>登出(吊销 refresh)。**失败不抛**:登出失败不该阻止本地清理。</summary>
    public async Task LogoutAsync(string refreshToken, CancellationToken ct = default)
    {
        try
        {
            using var req = NewRequest(HttpMethod.Post, "/api/v1/auth/logout",
                JsonSerializer.Serialize(new { refresh_token = refreshToken }));
            using var _ = await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // 离线登出:本地必须照样清干净(7.2:本地状态不能依赖网络可达)
        }
    }

    private async Task<TokenSet> PostTokenAsync(string path, string body, bool isLogin, CancellationToken ct)
    {
        HttpResponseMessage resp;
        string payload;
        try
        {
            using var req = NewRequest(HttpMethod.Post, path, body);
            resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            payload = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }
            throw new AuthException(AuthFailureKind.Network, $"连接服务端失败:{ex.Message}");
        }

        if (!resp.IsSuccessStatusCode)
        {
            throw Classify(resp.StatusCode, payload, isLogin);
        }

        var token = ParseToken(payload, isLogin);
        var now = _clock.GetUtcNow();
        return new TokenSet
        {
            AccessToken = token.AccessToken,
            RefreshToken = token.RefreshToken,
            // expires_in 是**相对秒数**,必须折算成绝对时刻:存相对值会在
            // "进程重启 + 时钟前进"后把已过期令牌当成有效(6.10 同类教训:相对量落库必错)
            AccessExpiresAt = now.AddSeconds(token.ExpiresIn),
        };
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string path, string jsonBody)
    {
        var req = new HttpRequestMessage(method, new Uri(_options.BaseAddress, path));
        req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        return req;
    }

    private static TokenResponse ParseToken(string payload, bool isLogin)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            // 登录/刷新都把令牌包在 `token` 里(契约 6.3);登录响应另有 user/personal_space
            if (doc.RootElement.TryGetProperty("token", out var t))
            {
                var parsed = t.Deserialize<TokenResponse>();
                if (parsed is not null && !string.IsNullOrEmpty(parsed.AccessToken))
                {
                    return parsed;
                }
            }
        }
        catch (JsonException ex)
        {
            throw new AuthException(AuthFailureKind.Other,
                (isLogin ? "登录" : "刷新") + $"响应不是合法 JSON:{ex.Message}");
        }
        throw new AuthException(AuthFailureKind.Other, (isLogin ? "登录" : "刷新") + "响应缺少 token 字段");
    }

    /// <summary>把 HTTP 状态 + 业务码映射成可执行的动作(而不是让上层去猜状态码)。</summary>
    private static AuthException Classify(HttpStatusCode status, string payload, bool isLogin)
    {
        var code = ExtractCode(payload);
        var message = ExtractMessage(payload) ?? $"HTTP {(int)status}";
        return (int)status switch
        {
            401 when code == "token_revoked" => new AuthException(
                AuthFailureKind.TokenRevoked, message, code, 401),
            401 when code == "token_expired" => new AuthException(
                AuthFailureKind.TokenExpired, message, code, 401),
            // 登录接口的 401 没有专属业务码(服务端口径是 unauthorized)
            401 when isLogin => new AuthException(AuthFailureKind.InvalidCredentials, message, code, 401),
            401 => new AuthException(AuthFailureKind.Other, message, code, 401),
            429 => new AuthException(AuthFailureKind.RateLimited, message, code, 429),
            _ => new AuthException(AuthFailureKind.Other, message, code, (int)status),
        };
    }

    private static string? ExtractCode(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ExtractMessage(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
