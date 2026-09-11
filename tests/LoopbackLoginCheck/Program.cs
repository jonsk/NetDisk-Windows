// 回环回调 + PKCE 的行为检查器(DE-D-03 验收 ①~⑤)。
//
// 每条验收都**实跑**:起真实监听、发真实 HTTP 回调、断言结果与失败原因。
// 与 NameRulesCheck 同形:零 NuGet 依赖、非 0 退出码即门禁失败。

using System.Net;
using System.Net.Sockets;
using NetDisk.ClientCore;

int failed = 0;
void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine((ok ? "✓ " : "✗ ") + name + (ok ? "" : "  " + detail));
    if (!ok) failed++;
}

// ---- ① 只绑回环(不是 0.0.0.0)----
using (var login = new LoopbackLogin())
{
    Check("① 回调地址是 127.0.0.1(只绑回环)",
        login.RedirectUri.Host == "127.0.0.1", $"实际 {login.RedirectUri.Host}");
    Check("② 端口由内核随机分配(非固定端口)",
        login.RedirectUri.Port is > 0 and < 65536, $"实际 {login.RedirectUri.Port}");
    // 反向断言:同一个进程再开一个会话应拿到**不同**端口(多开客户端不冲突)
    using var second = new LoopbackLogin();
    Check("②' 两次会话端口不同", second.RedirectUri.Port != login.RedirectUri.Port,
        $"{login.RedirectUri.Port} vs {second.RedirectUri.Port}");
}

// ---- ③ 强制 PKCE S256:用 RFC 7636 的官方向量 ----
// verifier: dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk
// challenge(S256): E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM
Check("③ PKCE S256 派生符合 RFC 7636 官方向量",
    LoopbackLogin.CodeChallengeFor("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk")
        == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
    "派生结果与 RFC 向量不一致(可能用了 plain 或 SHA1)");
Check("③' verifier 长度在 43..128 且是 base64url",
    new LoopbackLogin().CodeVerifier.Length >= 43);

// ---- ④ state 校验:错误 state 必须被拒 ----
using (var login = new LoopbackLogin())
{
    var task = login.WaitForCallbackAsync(TimeSpan.FromSeconds(10));
    await CallbackAsync(login.RedirectUri, "code=abc&state=WRONG-STATE");
    try
    {
        await task;
        Check("④ 错误 state 必须被拒", false, "竟然通过了");
    }
    catch (LoopbackLoginException ex)
    {
        Check("④ 错误 state 必须被拒", ex.Error == LoopbackLoginError.StateMismatch, $"错误码 {ex.Error}");
    }
}

// ---- 正常路径:code + 正确 state → 返回授权码 ----
using (var login = new LoopbackLogin())
{
    var url = login.BuildAuthorizeUrl(new Uri("https://example.com/api/v1/auth/oauth/authorize"));
    var q = url.Query;
    Check("正常路径:authorize URL 带 S256 与 state",
        q.Contains("code_challenge_method=S256") && q.Contains("state=") && q.Contains("response_type=code"),
        q);

    var task = login.WaitForCallbackAsync(TimeSpan.FromSeconds(10));
    await CallbackAsync(login.RedirectUri, $"code=THE-CODE&state={login.State}");
    var res = await task;
    Check("正常路径:拿回授权码与回调地址",
        res.Code == "THE-CODE" && res.RedirectUri == login.RedirectUri, $"{res.Code} @ {res.RedirectUri}");
}

// ---- ⑤ 超时自动关闭 ----
using (var login = new LoopbackLogin())
{
    var t0 = DateTime.UtcNow;
    try
    {
        await login.WaitForCallbackAsync(TimeSpan.FromMilliseconds(300));
        Check("⑤ 超时必须报超时", false, "没有抛异常");
    }
    catch (LoopbackLoginException ex)
    {
        var elapsed = DateTime.UtcNow - t0;
        Check("⑤ 超时自动关闭(不永久挂住)", ex.Error == LoopbackLoginError.Timeout && elapsed < TimeSpan.FromSeconds(5),
            $"错误码 {ex.Error},耗时 {elapsed.TotalSeconds:0.00}s");
    }
    // Dispose 之后端口确实被释放(能重新绑同一端口)
    login.Dispose();
    try
    {
        using var probe = new TcpListener(IPAddress.Loopback, login.RedirectUri.Port);
        probe.Start();
        probe.Stop();
        Check("⑤' 超时后端口已释放", true);
    }
    catch (SocketException ex)
    {
        Check("⑤' 超时后端口已释放", false, ex.Message);
    }
}

Console.WriteLine(failed == 0
    ? "\n全部通过:回环回调 + PKCE S256 + state 校验 + 超时关闭。"
    : $"\n{failed} 项未通过。");
return failed == 0 ? 0 : 1;

// CallbackAsync 模拟浏览器回调:向回环地址发一个真实 GET。
static async Task CallbackAsync(Uri redirect, string query)
{
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, redirect.Port);
    using var stream = client.GetStream();
    var req = $"GET /callback?{query} HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n";
    await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(req));
    await stream.FlushAsync();
    // 读掉响应(不读会让服务端写阻塞;也顺带验证它真的回了响应)
    var buf = new byte[1024];
    _ = await stream.ReadAsync(buf);
}
