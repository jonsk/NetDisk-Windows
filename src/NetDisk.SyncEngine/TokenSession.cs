// 令牌会话:启动恢复 + 静默刷新 + 被吊销就静默退出(DE-D-04 ②③)。
//
// 三个必须在这里(而不是各自散落)解决的正确性问题:
//
// ①**单飞刷新**。服务端的 refresh **一次一换**(旧的立刻作废,重放得到 token_revoked)。
//   若 N 个并发请求各自发现"access 要过期了"并各刷一次,只有第一个能成功,其余全部
//   拿着已被作废的 refresh 被登出 —— 用户看到的是"同步突然要求重新登录",而根因是
//   客户端自己的并发。所以刷新必须串行化,并且在**拿到锁之后重新判断**是否还需要刷
//   (等锁期间别人可能已经刷好了)。
//
// ②**新令牌先落盘再放行**。落盘发生在释放刷新锁**之前**:否则等到"刷新成功但还没
//   写盘"时崩溃,下次启动会拿旧 refresh 去刷 —— 而它已经被轮换掉了,等于永久登出。
//
// ③**被吊销 = 静默退出**。服务端在"停用账号 / 改口令 / 管理员吊销"时自增
//   `token_version`,此后 refresh 返回 401 `token_revoked`。客户端**不能重试**,
//   也不能弹一个吓人的错误框:按 7.2 与 R-19,应当**不删本地文件**、停止同步、
//   回到登录态(事件通知 UI)。检测时限由 `ProbeInterval` 保证(默认 10min < 15min
//   的验收要求),而任何一次资源请求拿到 `token_revoked` 会立刻触发,不必等探活。

using NetDisk.Transport;

namespace NetDisk.SyncEngine;

/// <summary>退出登录的原因(UI 据此决定提示语:被吊销不是"出错了")。</summary>
public enum SignOutReason
{
    /// <summary>用户主动登出。</summary>
    UserRequested,

    /// <summary>服务端吊销(停用/改密/管理员吊销)——静默退出,不删本地文件。</summary>
    Revoked,

    /// <summary>刷新失败且不可恢复(网络连续失败、服务端 5xx)。</summary>
    RefreshFailed,
}

/// <summary>令牌会话:实现 <see cref="ITokenProvider"/>,Transport 的每跳从这里取 access。</summary>
public sealed class TokenSession : ITokenProvider, IDisposable
{
    /// <summary>默认探活间隔。**必须 ≤15min**(DE-D-04 ③的验收口径:吊销后 15min 内静默退出)。</summary>
    public static readonly TimeSpan DefaultProbeInterval = TimeSpan.FromMinutes(10);

    /// <summary>默认提前刷新窗口:access 剩余不足这个时长就先刷。</summary>
    public static readonly TimeSpan DefaultRefreshAhead = TimeSpan.FromMinutes(2);

    /// <summary>15 分钟是 DE-D-04 ③写死的验收上限,这里作为常量暴露给检查器断言。</summary>
    public static readonly TimeSpan RevocationDeadline = TimeSpan.FromMinutes(15);

    private readonly ITokenStore _store;
    private readonly AuthApi _auth;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _refreshAhead;
    private readonly TimeSpan _probeInterval;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private TokenSet? _tokens;
    private bool _forceRefresh;
    private bool _revoked;

    public TokenSession(
        ITokenStore store,
        AuthApi auth,
        TimeProvider? clock = null,
        TimeSpan? refreshAhead = null,
        TimeSpan? probeInterval = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _clock = clock ?? TimeProvider.System;
        _refreshAhead = refreshAhead ?? DefaultRefreshAhead;
        _probeInterval = probeInterval ?? DefaultProbeInterval;
    }

    /// <summary>会话已失效(被吊销/刷新失败/用户登出)。UI 收到后回登录页,**不删本地文件**。</summary>
    public event Action<SignOutReason>? SignedOut;

    /// <summary>探活间隔(供检查器断言 ≤15min)。</summary>
    public TimeSpan ProbeInterval => _probeInterval;

    public bool IsSignedIn => _tokens is not null && !_revoked;

    /// <summary>当前 refresh 令牌(仅诊断/登出用,不用于展示)。</summary>
    public string? RefreshToken => _tokens?.RefreshToken;

    /// <summary>启动恢复:从存储读令牌;access 已过期则立刻静默刷新一次。</summary>
    /// <returns>恢复成功返回 true;无令牌或令牌已失效返回 false(调用方展示登录页)。</returns>
    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        _tokens = await _store.LoadAsync(ct).ConfigureAwait(false);
        if (_tokens is null)
        {
            return false;
        }
        if (!NeedsRefresh(_tokens))
        {
            return true;
        }
        try
        {
            await RefreshLockedAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (AuthException ex) when (ex.RequiresRelogin)
        {
            await HandleRevokedAsync(SignOutReason.Revoked, ct).ConfigureAwait(false);
            return false;
        }
        catch (AuthException)
        {
            // 网络问题不该把用户登出:手里的 refresh 仍然有效,等下次请求/探活再刷。
            // 但 access 已过期,**这次**确实没有可用令牌 —— 由 GetAccessTokenAsync 抛错。
            return _tokens is not null;
        }
    }

    /// <summary>账密登录并落盘。</summary>
    public async Task SignInAsync(string login, string password, CancellationToken ct = default)
    {
        var tokens = await _auth.LoginAsync(login, password, ct).ConfigureAwait(false);
        await PersistAsync(tokens, ct).ConfigureAwait(false);
        _revoked = false;
    }

    /// <summary>取 access token;需要时就地静默刷新(单飞)。</summary>
    /// <remarks>
    /// 返回 <see cref="ValueTask{TResult}"/> 而不是 <c>Task</c>:与 <see cref="ITokenProvider"/>
    /// 的契约一致(接口先于本类存在),且热路径(每次请求取令牌)在"不需要刷新"时无额外分配。
    /// </remarks>
    public async ValueTask<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        var current = _tokens;
        if (current is not null && !_forceRefresh && !NeedsRefresh(current))
        {
            return current.AccessToken;
        }
        if (_revoked)
        {
            throw new AuthException(AuthFailureKind.TokenRevoked, "登录状态已失效,请重新登录");
        }
        await RefreshLockedAsync(ct).ConfigureAwait(false);
        return _tokens!.AccessToken;
    }

    /// <summary>
    /// Transport 拿到 401 时回调:下一次取令牌时强制刷新一次(可能是服务端提前吊销了 access)。
    /// </summary>
    public void OnUnauthorized() => _forceRefresh = true;

    /// <summary>
    /// 资源请求直接看到 <c>token_revoked</c> 时调用:**立刻**静默退出,不等探活。
    /// </summary>
    public async Task NotifyRevokedAsync(CancellationToken ct = default)
        => await HandleRevokedAsync(SignOutReason.Revoked, ct).ConfigureAwait(false);

    /// <summary>探活一次:刷新令牌;若被吊销则静默退出。</summary>
    public async Task ProbeOnceAsync(CancellationToken ct = default)
    {
        if (_tokens is null || _revoked)
        {
            return;
        }
        try
        {
            await RefreshLockedAsync(ct).ConfigureAwait(false);
        }
        catch (AuthException ex) when (ex.RequiresRelogin)
        {
            await HandleRevokedAsync(SignOutReason.Revoked, ct).ConfigureAwait(false);
        }
        catch (AuthException)
        {
            // 网络抖动:静默忽略,下一轮再试(探活失败不等于被吊销)
        }
    }

    /// <summary>按 <see cref="ProbeInterval"/> 周期性探活(宿主在后台跑,直到 ct 取消)。</summary>
    public async Task RunProbeLoopAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_probeInterval, _clock, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            await ProbeOnceAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>主动登出:吊销服务端 refresh + 清本地密文。</summary>
    public async Task SignOutAsync(CancellationToken ct = default)
    {
        var refresh = _tokens?.RefreshToken;
        if (!string.IsNullOrEmpty(refresh))
        {
            await _auth.LogoutAsync(refresh, ct).ConfigureAwait(false);
        }
        await HandleRevokedAsync(SignOutReason.UserRequested, ct).ConfigureAwait(false);
    }

    private bool NeedsRefresh(TokenSet tokens)
        => tokens.AccessExpiresAt - _clock.GetUtcNow() <= _refreshAhead;

    private async Task RefreshLockedAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 双重检查:等锁期间别人可能已经刷好了(否则 N 个并发请求会刷 N 次,
            // 而服务端一次一换 → 只有第一个成功,其余全被"自己"顶掉)
            var current = _tokens;
            if (current is not null && !_forceRefresh && !NeedsRefresh(current))
            {
                return;
            }
            if (current is null)
            {
                throw new AuthException(AuthFailureKind.TokenRevoked, "本地没有登录状态");
            }

            var fresh = await _auth.RefreshAsync(current.RefreshToken, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(fresh.RefreshToken))
            {
                // 契约:刷新响应里**必须**有新 refresh(轮换)。缺失说明服务端行为变了,
                // 继续用旧 refresh 只会在下一次刷新时被拒 —— 当作不可恢复处理更诚实。
                throw new AuthException(AuthFailureKind.TokenRevoked, "刷新响应缺少 refresh_token");
            }
            await PersistAsync(fresh, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PersistAsync(TokenSet tokens, CancellationToken ct)
    {
        // 先落盘再改内存:反过来会出现"内存里是新令牌、盘上还是旧的"的窗口
        await _store.SaveAsync(tokens, ct).ConfigureAwait(false);
        _tokens = tokens;
        _forceRefresh = false;
    }

    private async Task HandleRevokedAsync(SignOutReason reason, CancellationToken ct)
    {
        var wasSignedIn = _tokens is not null;
        _revoked = true;
        _tokens = null;
        _forceRefresh = false;
        await _store.ClearAsync(ct).ConfigureAwait(false);
        if (wasSignedIn || reason != SignOutReason.Revoked)
        {
            SignedOut?.Invoke(reason);
        }
    }

    public void Dispose() => _gate.Dispose();
}
