// 一个"空间 ↔ 本地目录"的绑定(多空间的最小单位)。
//
// 为什么单独一个类型而不是继续往 ClientConfig 上加字段:绑定的数量是**可变的**
// (个人空间 + N 个团队空间),而 ClientConfig 上的 `space_id`/`sync_root` 只能表达一个。
// 旧字段保留是为了**向后兼容**:老配置不用改,`EffectiveBindings` 会把它当作唯一绑定。

using System.Text.Json.Serialization;

namespace NetDisk.SyncEngine.Host;

/// <summary>一个空间与本地同步目录的绑定。</summary>
public sealed record SpaceBinding
{
    /// <summary>远端空间 id(uuid)。</summary>
    [JsonPropertyName("space_id")]
    public string SpaceId { get; init; } = "";

    /// <summary>本地同步目录(绝对路径)。</summary>
    [JsonPropertyName("sync_root")]
    public string SyncRoot { get; init; } = "";

    /// <summary>同步起点(空 = 空间根)。</summary>
    [JsonPropertyName("parent_id")]
    public string? ParentId { get; init; }

    /// <summary>该空间是否用"只读浏览(仅结构)";null = 跟随全局 `structure_only`。</summary>
    [JsonPropertyName("structure_only")]
    public bool? StructureOnly { get; init; }

    /// <summary>给人看的短标识(日志/界面用;空间 id 太长)。</summary>
    [JsonIgnore]
    public string ShortId => SpaceId.Length >= 8 ? SpaceId[..8] : SpaceId;

    /// <summary>这条绑定是否可用(空间与目录都得有)。</summary>
    [JsonIgnore]
    public bool IsUsable => !string.IsNullOrWhiteSpace(SpaceId) && !string.IsNullOrWhiteSpace(SyncRoot);
}
