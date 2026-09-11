// 桌面登录的回环回调 + PKCE(DE-D-03;协议依据 9.12 / RFC 8252 + RFC 7636)。
//
// 为什么放在 ClientCore(平台无关)而不是 App:
//   登录流程的**正确性**(state 校验、PKCE 派生、超时、只绑回环)与 UI 无关,
//   放这里才能被单测/检查器覆盖;App 只负责"用系统浏览器打开 authorize URL"。
//
// 五条纪律(对应 DE-D-03 验收):
//   ① **只绑回环**:监听 127.0.0.1(不是 0.0.0.0,也不是 ::)-"监听全网卡"会让
//      同一局域网里的任何人都能把授权码 POST 给你;
//   ② **随机端口**(:0 由内核分配):固定端口会被别的进程占,而且多开客户端会冲突;
//   ③ **强制 PKCE S256**:verifier 只在客户端存在,授权码被截获也无法兑换;
//   ④ **校验 state**:防 CSRF/串号(别人把自己的授权码塞进你的回调);
//   ⑤ **超时自动关闭**:用户放弃登录时不能留一个监听中的端口。
//
// 客户端**不使用自定义 URL scheme**(9.12):scheme 劫持是移动端常见攻击面,
// 而且需要注册表改动(给安装带来管理员权限风险)。

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace NetDisk.ClientCore;

/// <summary>登录结果:授权码 + 回调地址(兑换令牌时原样回传)。</summary>
public sealed record LoopbackLoginResult(string Code, Uri RedirectUri);

/// <summary>登录失败原因(客户端按码分流,不解析文案)。</summary>
public enum LoopbackLoginError
{
    None = 0,
    Timeout,
    StateMismatch,
    MissingCode,
    Cancelled,
}

/// <summary>登录失败。</summary>
public sealed class LoopbackLoginException : Exception
{
    public LoopbackLoginError Error { get; }

    public LoopbackLoginException(LoopbackLoginError error, string message) : base(message)
        => Error = error;
}

/// <summary>
/// 系统浏览器 + 回环回调的登录会话(一次性)。
///
/// 用法:
/// <code>
/// using var login = new LoopbackLogin();
/// var url = login.BuildAuthorizeUrl(new Uri("https://host/api/v1/auth/oauth/authorize"));
/// OpenInSystemBrowser(url);                       // App 层
/// var result = await login.WaitForCallbackAsync(TimeSpan.FromMinutes(5), ct);
/// </code>
/// </summary>
public sealed class LoopbackLogin : IDisposable
{
    private readonly TcpListener _listener;
    private readonly string _state;
    private readonly string _verifier;
    private bool _disposed;

    /// <summary>PKCE code_verifier(43-128 字符的 base64url 随机串)。</summary>
    public string CodeVerifier => _verifier;

    /// <summary>PKCE code_challenge(= BASE64URL(SHA256(verifier)))。</summary>
    public string CodeChallenge { get; }

    /// <summary>state(防 CSRF/串号)。</summary>
    public string State => _state;

    /// <summary>回调地址(监听端口由内核分配)。</summary>
    public Uri RedirectUri { get; }

    public LoopbackLogin()
    {
        // ① 只绑回环 + ② 随机端口
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        RedirectUri = new Uri($"http://127.0.0.1:{port}/callback");

        _verifier = NewCodeVerifier();
        CodeChallenge = CodeChallengeFor(_verifier);
        _state = NewCodeVerifier(); // 同一套随机源即可(都是高熵 base64url)
    }

    /// <summary>构造授权页 URL(authorize 端点在服务端,B 类业务项由 FE/BE 提供)。</summary>
    public Uri BuildAuthorizeUrl(Uri authorizeEndpoint)
    {
        var sep = authorizeEndpoint.Query.Length > 0 ? "&" : "?";
        var q = $"response_type=code&redirect_uri={Uri.EscapeDataString(RedirectUri.ToString())}" +
                $"&state={Uri.EscapeDataString(_state)}" +
                $"&code_challenge={Uri.EscapeDataString(CodeChallenge)}&code_challenge_method=S256";
        return new Uri(authorizeEndpoint + sep + q);
    }

    /// <summary>
    /// 等待浏览器回调。超时/state 不符/缺 code 都抛 <see cref="LoopbackLoginException"/>。
    /// </summary>
    public async Task<LoopbackLoginResult> WaitForCallbackAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);

        TcpClient? client = null;
        try
        {
            client = await _listener.AcceptTcpClientAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ⑤ 超时/取消都自动关闭监听(Dispose 里做,这里只区分原因)
            throw new LoopbackLoginException(
                ct.IsCancellationRequested ? LoopbackLoginError.Cancelled : LoopbackLoginError.Timeout,
                ct.IsCancellationRequested ? "登录已取消" : $"登录超时({timeout.TotalSeconds:0}s),已关闭回调端口");
        }

        using (client)
        {
            var query = await ReadRequestLineAsync(client, linked.Token).ConfigureAwait(false);
            var (code, state, error) = ParseCallback(query);

            if (error is not null)
            {
                await RespondAsync(client, 400, "登录失败,可关闭此页。").ConfigureAwait(false);
                throw new LoopbackLoginException(LoopbackLoginError.MissingCode, $"授权被拒绝: {error}");
            }
            // ④ state 必须逐字节相同:不校验 state 时,攻击者可以把**自己的**授权码
            // 塞进你的回调,让你的客户端登录成他的账号(用户看到的是"登录成功了")
            if (!string.Equals(state, _state, StringComparison.Ordinal))
            {
                await RespondAsync(client, 400, "state 校验失败,可关闭此页。").ConfigureAwait(false);
                throw new LoopbackLoginException(LoopbackLoginError.StateMismatch, "state 校验失败(可能的 CSRF/串号)");
            }
            if (string.IsNullOrEmpty(code))
            {
                await RespondAsync(client, 400, "缺少授权码,可关闭此页。").ConfigureAwait(false);
                throw new LoopbackLoginException(LoopbackLoginError.MissingCode, "回调缺少 code");
            }

            await RespondAsync(client, 200, "登录完成,可关闭此页。").ConfigureAwait(false);
            return new LoopbackLoginResult(code, RedirectUri);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _listener.Stop(); } catch { /* 关闭失败无需上报 */ }
    }

    // ---- PKCE(RFC 7636)----

    /// <summary>生成 code_verifier:32 字节随机 → base64url(43 字符,满足 43..128)。</summary>
    public static string NewCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64Url(bytes);
    }

    /// <summary>code_challenge = BASE64URL(SHA256(ASCII(verifier))),即 S256(不是 plain)。</summary>
    public static string CodeChallengeFor(string verifier)
    {
        var digest = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64Url(digest);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ---- 最小 HTTP 处理(手写而不是 HttpListener)----
    //
    // 为什么不用 HttpListener:Windows 上它走 http.sys,绑定前缀需要**管理员**
    // 预留 URL ACL —— 普通用户装客户端时登录直接失败,而且错误信息(拒绝访问)
    // 与"端口被占"很难区分。裸 TcpListener 只要求端口可用,权限模型简单。

    private static async Task<string> ReadRequestLineAsync(TcpClient client, CancellationToken ct)
    {
        client.ReceiveTimeout = 5000;
        // **不 using**:释放 NetworkStream 会连带关闭 socket(ownsSocket 默认 true),
        // 于是后面写响应时报 "not allowed on non-connected sockets"。
        // 流由 TcpClient 负责关闭。
        var stream = client.GetStream();
        var buf = new byte[4096];
        var sb = new StringBuilder();
        // 只需读到第一行(GET /callback?code=...&state=... HTTP/1.1)
        while (!sb.ToString().Contains('\n'))
        {
            var n = await stream.ReadAsync(buf, ct).ConfigureAwait(false);
            if (n <= 0) break;
            sb.Append(Encoding.ASCII.GetString(buf, 0, n));
            if (sb.Length > 16 * 1024) break; // 防御异常大的请求头
        }
        var line = sb.ToString().Split('\n')[0].Trim();
        return line;
    }

    private static (string? code, string? state, string? error) ParseCallback(string requestLine)
    {
        var parts = requestLine.Split(' ');
        if (parts.Length < 2) return (null, null, null);
        var target = parts[1];
        var idx = target.IndexOf('?');
        if (idx < 0) return (null, null, null);
        string? code = null, state = null, error = null;
        foreach (var kv in target[(idx + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = kv.IndexOf('=');
            if (eq < 0) continue;
            var k = kv[..eq];
            var v = Uri.UnescapeDataString(kv[(eq + 1)..]);
            switch (k)
            {
                case "code": code = v; break;
                case "state": state = v; break;
                case "error": error = v; break;
            }
        }
        return (code, state, error);
    }

    private static async Task RespondAsync(TcpClient client, int status, string body)
    {
        var html = Encoding.UTF8.GetBytes(
            $"<!doctype html><meta charset=\"utf-8\"><title>NetDisk</title><p>{body}</p>");
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Bad Request")}\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {html.Length}\r\nConnection: close\r\n\r\n");
        using var stream = client.GetStream();
        await stream.WriteAsync(head).ConfigureAwait(false);
        await stream.WriteAsync(html).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }
}
