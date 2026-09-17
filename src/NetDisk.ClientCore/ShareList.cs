namespace NetDisk.ClientCore;

/// <summary>
/// GET /api/v1/shares 的响应包裹(契约 6.3:{ shares: ShareItem[] })。
///
/// 单独成文件(不放进 Models.g.cs):它是契约生成物里**没有具名 schema** 的包裹结构,
/// 与 SpaceCollabClient 里的 ShareCreateRequest 同理 —— 生成器不会覆盖本文件,
/// 重跑 gen:csharp 也不会把它抹掉。
/// </summary>
public sealed record ShareList
{
    public System.Collections.Generic.List<ShareItem> shares { get; init; } = new();
}
