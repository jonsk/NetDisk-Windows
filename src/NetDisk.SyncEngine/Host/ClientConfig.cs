// 客户端配置(服务器地址 / 同步根 / 空间与父目录)—— MVP 接线的第二块。
//
// 为什么必须有它:在此之前客户端只认环境变量 `NETDISK_BASE_URL`,而**用户装完 MSI
// 是打不开"环境变量"的** —— 于是那个团队空间页在真实使用中只会显示"未配置服务端地址"。
// 配置写进文件是"能让普通用户用起来"的最低要求。
//
// 三条纪律:
//   1. **原子写**(临时文件 + 替换):配置被写坏会让客户端连启动都做不到,
//      而进程恰好在写配置时被杀是完全可能的(用户点"保存"后立刻关窗口)。
//   2. **这里不放任何 secret**:令牌由 DpapiTokenStore 单独加密落盘(与配置分离),
//      配置进日志/工单时不会顺带泄露凭据。
//   3. 反序列化失败**不抛**(返回空配置并保留坏文件为 .bak):让用户能重新填,
//      而不是每次启动都崩在一个他已经改不动的地方。

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetDisk.SyncEngine.Host;

/// <summary>客户端本地配置。</summary>
public sealed class ClientConfig
{
    /// <summary>服务端基址(如 http://10.14.37.187)。</summary>
    [JsonPropertyName("base_url")]
    public string BaseUrl { get; set; } = "";

    /// <summary>本地同步根(绝对路径)。</summary>
    [JsonPropertyName("sync_root")]
    public string SyncRoot { get; set; } = "";

    /// <summary>同步的空间 id(空 = 用个人空间)。</summary>
    [JsonPropertyName("space_id")]
    public string SpaceId { get; set; } = "";

    /// <summary>空间内作为同步根的目录 id(空 = 空间根目录)。</summary>
    [JsonPropertyName("parent_id")]
    public string ParentId { get; set; } = "";

    /// <summary>上次登录的用户名(只为少打一次字;不存口令)。</summary>
    [JsonPropertyName("last_login")]
    public string LastLogin { get; set; } = "";

    /// <summary>是否已完成首次运行(决定了启动时进向导还是进同步页)。</summary>
    [JsonPropertyName("onboarded")]
    public bool Onboarded { get; set; }

    /// <summary>
    /// 是否记录日志(界面上的开关;**默认开**)。
    ///
    /// 默认开的理由:客户端出问题时现场在**用户机器上**,而"默认关掉"意味着
    /// 用户来反馈时手上什么都没有,只能让他复现一次。日志是本地文件、不含口令与令牌,
    /// 代价只有几十 KB —— 用"可能要用户复现一次"换这点磁盘不划算。
    /// </summary>
    [JsonPropertyName("logging")]
    public bool Logging { get; set; } = true;

    /// <summary>传输并发上限(1..16;默认 3,与引擎默认一致)。</summary>
    [JsonPropertyName("max_concurrency")]
    public int MaxConcurrency { get; set; } = 3;

    /// <summary>上传限速(KB/s;0 = 不限速)。</summary>
    [JsonPropertyName("upload_kbps")]
    public int UploadKbps { get; set; }

    /// <summary>下载限速(KB/s;0 = 不限速)。</summary>
    [JsonPropertyName("download_kbps")]
    public int DownloadKbps { get; set; }

    /// <summary>面向用户的字段说明(首次运行生成配置时写进去,免得用户对着 JSON 猜)。</summary>
    [JsonPropertyName("_说明")]
    public string Help { get; set; } =
        "base_url=服务器地址;sync_root=本地同步目录;max_concurrency=并发数(1-16);" +
        "upload_kbps/download_kbps=限速(KB/s,0=不限);logging=是否记录日志(日志在 logs\\client.log);" +
        "onboarded=是否已完成首次运行。删掉本文件会在下次启动时重新生成。";

    [JsonIgnore]
    public string Path { get; private set; } = "";

    /// <summary>
    /// 配置默认位置:**程序所在目录**下的 <c>client.json</c>(该目录不可写时回退 %APPDATA%\NetDisk)。
    /// 见 <see cref="ClientPaths"/>:位置只有一处定义,别处不许再拼路径。
    /// </summary>
    public static string DefaultPath() => ClientPaths.ConfigPath;

    /// <summary>读取配置;文件不存在或坏了都返回一个新对象(坏文件另存 .bak)。</summary>
    public static ClientConfig Load(string? path = null)
    {
        path ??= DefaultPath();
        var cfg = new ClientConfig { Path = path };
        if (!File.Exists(path))
        {
            return cfg;
        }

        try
        {
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<ClientConfig>(json) ?? cfg;
            loaded.Path = path;
            return loaded;
        }
        catch (Exception)
        {
            // 坏配置不能让它每次都崩:留一份 .bak 供排查,然后当作"没配过"
            try
            {
                File.Move(path, path + ".bak", overwrite: true);
            }
            catch (Exception)
            {
                // 连备份都失败也不该阻止用户重新配置
            }
            return new ClientConfig { Path = path };
        }
    }

    /// <summary>原子保存。</summary>
    public void Save()
    {
        var path = string.IsNullOrEmpty(Path) ? DefaultPath() : Path;
        Path = path;
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // UnsafeRelaxedJsonEscaping:这个文件是**给用户看的**(里面有 `_说明` 字段),
        // 默认编码器会把中文写成 \u8BF4\u660E —— 用户打开一看全是转义码,等于没有说明。
        // 文件本身就是 UTF-8,不转义是安全且正确的(不涉及 HTML/JS 注入场景)。
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        // File.Replace/Move(overwrite) 保证读到的永远是完整文件
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>能用了吗(地址与同步根都填了)。</summary>
    public bool IsUsable() =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(SyncRoot);
}
