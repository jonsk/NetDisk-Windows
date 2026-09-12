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

    [JsonIgnore]
    public string Path { get; private set; } = "";

    /// <summary>配置默认位置:<c>%APPDATA%\NetDisk\client.json</c>。</summary>
    public static string DefaultPath()
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetDisk");
        return System.IO.Path.Combine(dir, "client.json");
    }

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

        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        // File.Replace/Move(overwrite) 保证读到的永远是完整文件
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>能用了吗(地址与同步根都填了)。</summary>
    public bool IsUsable() =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(SyncRoot);
}
