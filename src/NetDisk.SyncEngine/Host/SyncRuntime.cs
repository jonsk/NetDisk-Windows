// 客户端运行时(组合根):把"配置 → 登录 → 令牌 → ApiClient → SyncHost"这条链搭起来。
//
// 为什么放在 SyncEngine 而不是 App(目标 ⑤):
//   它是**纯组合**,一行 WPF 都不用 —— 却决定了"客户端到底能不能登录/能不能同步"。
//   放在 App 里就只能靠人手点着看(而且 App 依赖 WPF,单测跑不起来);
//   放在这里,检查器可以直接跑"登录 → 落令牌 → 用令牌重启"这条最容易坏、
//   又最不容易被发现的路径(实测:UI 自动启动这条路径第一次就是坏的)。
//
// 边界:**编排仍然在 SyncHost**。这里只做 new + 接线上,不含任何同步策略。

using System.Collections.Concurrent;
using NetDisk.SyncEngine.Files;
using NetDisk.Transport;

namespace NetDisk.SyncEngine.Host;

/// <summary>登录 + 同步宿主的一次运行实例。</summary>
public sealed class SyncRuntime : IAsyncDisposable
{
    // 进展历史的环形缓冲:UI 往往在 StartAsync **之后**才订阅事件,
    // 而头几轮对账的进展恰恰是排查问题最需要的(实测:日志里一片空白,看起来像"没同步")。
    // 引擎侧留存最近若干条,谁什么时候接上都能补看。
    private const int NoticeHistoryLimit = 200;
    private readonly ConcurrentQueue<(DateTimeOffset At, string Message)> _notices = new();

    private SyncRuntime(ClientConfig config, TokenSession session, ApiClient api, SyncHost host)
    {
        Config = config;
        Session = session;
        Api = api;
        Host = host;
        // 构造时就订阅:保证**从第一条**进展开始记录,不受调用方订阅时机影响
        Host.Notice += OnHostNotice;
    }

    public ClientConfig Config { get; }

    public TokenSession Session { get; }

    public ApiClient Api { get; }

    public SyncHost Host { get; }

    /// <summary>实时进展(引擎后台线程触发,订阅方自行 marshal)。</summary>
    public event Action<string>? Notice;

    /// <summary>最近的进展(带时间;用于界面初次绑定与日志补记)。</summary>
    public IReadOnlyList<(DateTimeOffset At, string Message)> RecentNotices => _notices.ToArray();

    /// <summary>令牌密文文件(与 client.json 同目录,DPAPI 加密,绝不含口令)。</summary>
    public static string DefaultTokenPath()
    {
        var dir = Path.GetDirectoryName(ClientConfig.DefaultPath())!;
        return Path.Combine(dir, "tokens.bin");
    }

    /// <summary>
    /// 用**已保存的令牌**建运行时(启动时用:用户不需要每天重输口令)。
    /// 令牌不可用时由 <see cref="StartAsync"/> 返回 false,UI 再退回登录页。
    /// </summary>
    /// <param name="tokenPath">令牌密文路径;默认与配置同目录(检查器可注入临时路径)。</param>
    public static SyncRuntime FromStoredToken(
        ClientConfig config, TimeProvider? clock = null, string? tokenPath = null)
    {
        var session = BuildSession(config, tokenPath);
        var api = BuildApi(config, session);
        return new SyncRuntime(config, session, api,
            new SyncHost(config, session, api, clock, statePath: StatePathFor(config)));
    }

    /// <summary>
    /// 账密登录并建运行时。登录失败**抛出带可读原因的异常**(UI 直接显示),
    /// 不返回一个"半登录"的对象 —— 那样 UI 只能靠猜。
    /// </summary>
    public static async Task<SyncRuntime> SignInAsync(
        ClientConfig config, string user, string password,
        string? tokenPath = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            throw new InvalidOperationException("请先填写服务器地址");
        }

        var session = BuildSession(config, tokenPath);
        await session.SignInAsync(user, password).ConfigureAwait(false);
        if (!session.IsSignedIn)
        {
            throw new InvalidOperationException("登录未成功(请检查用户名、口令与服务器地址)");
        }

        var api = BuildApi(config, session);
        var files = new FileApi(api);

        // 解析要同步的空间:配置里指定了就用它,否则挑个人空间。
        // 用户看不见空间的 uuid,让他在登录页手填是不可用的 —— 这里替他定下来。
        if (string.IsNullOrWhiteSpace(config.SpaceId))
        {
            var spaces = await files.ListSpacesAsync(ct).ConfigureAwait(false);
            var personal = spaces.spaces?.FirstOrDefault(s => s.kind == "personal")
                           ?? spaces.spaces?.FirstOrDefault();
            if (personal is null)
            {
                throw new InvalidOperationException("这个账号没有任何可见空间,无法同步");
            }
            config.SpaceId = personal.id;
        }

        config.LastLogin = user;
        config.Save();

        return new SyncRuntime(config, session, api,
            new SyncHost(config, session, api, statePath: StatePathFor(config)));
    }

    /// <summary>
    /// 状态库与**配置放在一起**(而不是写死 %APPDATA%):这样"从哪读配置"就决定了
    /// "状态写到哪",检查器用临时目录里的配置就能完全隔离(不会碰用户真实状态库)。
    /// 真实客户端下两者都在 %APPDATA%\NetDisk,行为不变。
    /// </summary>
    private static string? StatePathFor(ClientConfig config) =>
        string.IsNullOrEmpty(config.Path)
            ? null
            : Path.Combine(Path.GetDirectoryName(config.Path)!, "state.db");

    /// <summary>启动同步(内部会先确认令牌可用)。返回 false = 需要重新登录。</summary>
    public Task<bool> StartAsync(CancellationToken ct = default) => Host.StartAsync(ct);

    /// <summary>手动触发一次对账(UI 的"立即同步")。</summary>
    public Task ReconcileAsync(CancellationToken ct = default) => Host.ReconcileAsync(ct);

    private void OnHostNotice(string message)
    {
        _notices.Enqueue((DateTimeOffset.UtcNow, message));
        while (_notices.Count > NoticeHistoryLimit && _notices.TryDequeue(out _))
        {
            // 丢掉最旧的:历史只用于排查最近发生了什么
        }
        Notice?.Invoke(message);
    }

    private static TokenSession BuildSession(ClientConfig config, string? tokenPath)
    {
        var store = new DpapiTokenStore(tokenPath ?? DefaultTokenPath());
        var auth = new AuthApi(new ClientOptions { BaseAddress = new Uri(config.BaseUrl) });
        return new TokenSession(store, auth, TimeProvider.System);
    }

    // 令牌必须注入 ApiClient:不注入的话所有请求都是匿名的,表现是"列表永远是空的"
    // 或者 401 —— 而这在 UI 上看起来只像"没有文件",极易被当成服务端问题。
    private static ApiClient BuildApi(ClientConfig config, ITokenProvider tokens) =>
        new(new ClientOptions { BaseAddress = new Uri(config.BaseUrl) }, tokens: tokens);

    public async ValueTask DisposeAsync()
    {
        Host.Notice -= OnHostNotice;
        await Host.DisposeAsync().ConfigureAwait(false);
    }
}
