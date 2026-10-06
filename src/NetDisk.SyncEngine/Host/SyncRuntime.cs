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
        : this(config, session, api, host, Array.Empty<(SpaceBinding Binding, SyncHost Host)>())
    {
    }

    private SyncRuntime(
        ClientConfig config, TokenSession session, ApiClient api, SyncHost host,
        IReadOnlyList<(SpaceBinding Binding, SyncHost Host)> extraHosts)
    {
        Config = config;
        Session = session;
        Api = api;
        Host = host;
        SecondaryHosts = extraHosts;
        Browser = new NetDisk.SyncEngine.Files.RemoteBrowser(new NetDisk.SyncEngine.Files.FileApi(api), config.SpaceId);
        // 构造时就订阅:保证**从第一条**进展开始记录,不受调用方订阅时机影响
        Host.Notice += OnHostNotice;
        foreach (var (binding, extra) in extraHosts)
        {
            // 次要空间的进展也进同一份历史,但**带上空间前缀** —— 多空间下"这条日志是哪个空间说的"
            // 是排查时第一个要回答的问题,靠上下文猜迟早猜错。
            var prefix = $"[空间 {binding.ShortId}] ";
            extra.Notice += m => OnHostNotice(prefix + m);
        }
    }

    /// <summary>
    /// **次要空间**的宿主(多空间)。主空间始终是 <see cref="Host"/>,它对应配置里的 legacy 三件套。
    ///
    /// 为什么次空间不合并成一个"多空间宿主":`SyncHost` 的对账/状态库/监听器都是**按空间**组织的,
    /// 合并只会把"哪个空间的哪条状态"搅在一起;而每空间一个宿主天然隔离,失败也互不牵连
    /// (一个空间被冻结/移出,另一个照常同步)。
    /// </summary>
    public IReadOnlyList<(SpaceBinding Binding, SyncHost Host)> SecondaryHosts { get; }

    /// <summary>全部宿主(主 + 次),界面与生命周期管理按它遍历。</summary>
    public IReadOnlyList<SyncHost> AllHosts
    {
        get
        {
            var list = new List<SyncHost> { Host };
            list.AddRange(SecondaryHosts.Select(x => x.Host));
            return list;
        }
    }

    public ClientConfig Config { get; }

    public TokenSession Session { get; }

    public ApiClient Api { get; }

    /// <summary>
    /// 远端文件浏览器(只读:看**整个空间**的结构,而不是只看同步目录里那一份)。
    /// 与同步共用同一条已注入令牌的连接 —— 界面不需要自己造客户端,也就不会漏注入令牌
    /// (漏注入的表现是"列表永远空的",极易被误判成服务端问题)。
    /// </summary>
    public NetDisk.SyncEngine.Files.RemoteBrowser Browser { get; }

    public SyncHost Host { get; }

    /// <summary>实时进展(引擎后台线程触发,订阅方自行 marshal)。</summary>
    public event Action<string>? Notice;

    /// <summary>最近的进展(带时间;用于界面初次绑定与日志补记)。</summary>
    public IReadOnlyList<(DateTimeOffset At, string Message)> RecentNotices => _notices.ToArray();

    /// <summary>令牌密文文件(与 client.json 同目录,DPAPI 加密,绝不含口令)。</summary>
    public static string DefaultTokenPath() => ClientPaths.TokenPath;

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
        // **也必须走多空间构建**:这是真实客户端的**启动主路径**(有配置 + 令牌可用就直接进同步页),
        // 只给登录路径接多空间、启动路径不接的话,用户配了第二个空间重启后就"少了一个空间",
        // 而界面上没有任何提示(实测:检查器里两个空间只起了 1 个宿主)。
        return BuildRuntime(config, session, api, clock);
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

        // 校准要同步的空间:本地缓存的 space_id / spaces 可能已失效——换账号、
        // 空间被解散/移出、或服务端清空重建导致 uuid 变化。旧逻辑"非空即沿用"会让
        // 客户端拿着无权访问的 id 去拉文件,直接 403 space_revoked(表现为登录后立刻弹
        // "你没有该空间的访问权限")。所以这里**总是**拉一次可见空间并按可见集合校正缓存,
        // 最后才回落到个人空间。用户看不见空间的 uuid,让他在登录页手填是不可用的。
        var visible = await files.ListSpacesAsync(ct).ConfigureAwait(false);
        ResolveSpaceBindings(config, visible);

        config.LastLogin = user;
        config.Save();

        return BuildRuntime(config, session, api);
    }

    /// <summary>
    /// 按服务端**可见空间集合**校正本地绑定(登录后调用)。
    ///
    /// 规则:
    ///   ① 多空间绑定(<c>spaces</c>):剔除"已不可见或没配同步目录"的项;
    ///   ② 主空间(legacy 三件套):<c>space_id</c> 不可见则清空,退回重新解析;
    ///   ③ 校正后仍无可用空间 → 落到个人空间(没有则明确报错)。
    ///
    /// 这样换账号 / 空间被移出 / 服务端重建(uuid 变化)后,客户端不会再拿着旧 id 去撞 403。
    /// </summary>
    private static void ResolveSpaceBindings(ClientConfig config, SpaceList visible)
    {
        var visibleIds = new HashSet<string>(
            visible.spaces.Select(s => s.id), StringComparer.OrdinalIgnoreCase);

        // ① 多空间绑定:只保留仍然可见且配了同步目录的
        if (config.Spaces.Count > 0)
        {
            config.Spaces = config.Spaces
                .Where(b => !string.IsNullOrWhiteSpace(b.SyncRoot) && visibleIds.Contains(b.SpaceId))
                .ToList();
        }

        // ② 主空间:不可见则清空,避免沿用失效 id
        if (!string.IsNullOrWhiteSpace(config.SpaceId) && !visibleIds.Contains(config.SpaceId))
        {
            config.SpaceId = "";
            config.ParentId = "";
        }

        // ③ 仍无空间可用 → 个人空间(用户看不见 uuid,替他定下来)
        if (config.Spaces.Count == 0 && string.IsNullOrWhiteSpace(config.SpaceId))
        {
            var personal = visible.spaces.FirstOrDefault(s => s.kind == "personal")
                           ?? visible.spaces.FirstOrDefault();
            if (personal is null)
            {
                throw new InvalidOperationException("这个账号没有任何可见空间,无法同步");
            }
            config.SpaceId = personal.id;
        }
    }

    /// <summary>
    /// 按**生效绑定**建运行时:第一个是主空间(沿用旧的单空间配置与 <c>state.db</c>),
    /// 其余每个空间一个宿主 + **独立状态库**。
    ///
    /// 状态库必须按空间分开(而不是共用一个库):`sync_state` 虽然以 file_id 为主键、
    /// 也带 space_id,但同步根身份、游标、目录行都按"一个空间一棵树"来组织 ——
    /// 混在一个库里,某天一条按路径查询就会跨空间命中(那是最难查的一类串数据)。
    /// 分开还有一个实际好处:一个空间被冻结/移出时,删掉它的库就能干净重来。
    /// </summary>
    private static SyncRuntime BuildRuntime(
        ClientConfig config, TokenSession session, ApiClient api, TimeProvider? clock = null)
    {
        var bindings = config.EffectiveBindings;
        if (bindings.Count == 0)
        {
            // 还没配好(登录路径会先解析个人空间并写回配置,所以这里基本只在"手动构造"时出现)
            return new SyncRuntime(config, session, api,
                new SyncHost(config, session, api, clock, statePath: StatePathFor(config)));
        }

        var primary = bindings[0];
        var primaryConfig = config.ForBinding(primary);
        primaryConfig.Spaces = config.Spaces; // 主空间保留原配置对象上的写法(便于后续保存)
        var primaryHost = new SyncHost(primaryConfig, session, api, clock, statePath: StatePathFor(config));

        var extras = new List<(SpaceBinding, SyncHost)>();
        for (var i = 1; i < bindings.Count; i++)
        {
            var binding = bindings[i];
            var clone = config.ForBinding(binding);
            var host = new SyncHost(clone, session, api, clock, statePath: StatePathForSpace(config, binding));
            extras.Add((binding, host));
        }
        return new SyncRuntime(config, session, api, primaryHost, extras);
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

    /// <summary>次要空间的状态库:<c>state-&lt;空间短 id&gt;.db</c>(与主空间的 state.db 并列)。</summary>
    private static string? StatePathForSpace(ClientConfig config, SpaceBinding binding) =>
        string.IsNullOrEmpty(config.Path)
            ? null
            : Path.Combine(Path.GetDirectoryName(config.Path)!, $"state-{binding.ShortId}.db");

    /// <summary>
    /// 启动同步(内部会先确认令牌可用)。返回 false = 需要重新登录。
    /// **所有空间都要起来**:只有一个起来的话,用户在界面上看到的是"部分空间在同步"而没有任何提示。
    /// </summary>
    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        if (!await Host.StartAsync(ct).ConfigureAwait(false))
        {
            return false;
        }
        foreach (var (binding, host) in SecondaryHosts)
        {
            try
            {
                if (!await host.StartAsync(ct).ConfigureAwait(false))
                {
                    OnHostNotice($"[空间 {binding.ShortId}] 启动失败:令牌/配置不可用,该空间本轮未同步");
                }
            }
            catch (Exception ex)
            {
                // 一个空间起不来不该拖垮其它空间(这正是"每空间一个宿主"的价值)
                OnHostNotice($"[空间 {binding.ShortId}] 启动异常:{ex.Message}");
            }
        }
        return true;
    }

    /// <summary>手动触发一次对账(UI 的"立即同步"):主空间 + 所有次要空间。</summary>
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        await Host.ReconcileAsync(ct).ConfigureAwait(false);
        foreach (var (_, host) in SecondaryHosts)
        {
            try
            {
                await host.ReconcileAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnHostNotice($"[次要空间] 对账异常:{ex.Message}");
            }
        }
    }

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
        foreach (var (_, host) in SecondaryHosts)
        {
            try
            {
                await host.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 退出路径不抛:一个空间释放失败不该阻止其它空间收尾(也不该让进程崩)
            }
        }
    }
}
