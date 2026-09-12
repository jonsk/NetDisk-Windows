// 团队空间协作 + 分享创建(H5 与桌面端**同一组接口**;DE-D-17)。
//
// 契约来源:6.3 接口表 / `docs/api/openapi.yaml`(FE-W-11 落地的 `/api/v1/spaces*` 五条 +
// `/api/v1/shares`)。桌面端这一层**只负责发请求与搬运结果** ——
//
// **权限判断不在端上(4.3,本项的验收项之一)**:
// 客户端不做任何"我是不是 owner / 我的权限够不够"的判断来决定**要不要发这个请求**。
// 理由与 spacesvc 的包注释同源:判权只要有两个实现(服务端 + 端上),就一定会漂移;
// 而漂移的表现是"端上以为没权限、于是不发请求",用户看到按钮灰着、服务端却明明允许
// (或者反过来,端上放行了、服务端拒绝,用户只看到一句没头没尾的失败)。
//
// 所以这里的职责边界是:
//   - 服务端返回的 `is_owner` / `permission` **只用于展示**(按钮提示、角色标签);
//   - **任何动作都照发**,被拒就如实把服务端的错误交给 UI 显示(403/409/404 各有文案);
//   - 这个类里**不出现**任何把角色与动作绑起来的判断(检查器用机械规则 + 反射盯着)。
//
// 分享创建也走同一层:桌面端与 H5 共用 `/api/v1/shares`(6.3),不另开一套。

using System.Text.Json.Serialization;
using NetDisk.ClientCore;

namespace NetDisk.Transport;

// 空间/成员/分享的**线上形状一律用契约生成物**(`NetDisk.ClientCore.Generated.Models.g.cs`:
// SpaceView / MemberView / SpaceList / MemberList / ShareCreated)。
//
// 这一条不是风格问题:我最初在本文件里手写了一套同名的 `SpaceView`/`MemberView`,
// 加入契约并重新生成之后,同一个 `SpaceView` 就有了**两个定义** —— 编译期以
// `typeof(SpaceView)` 解析到哪一个的形式炸出来(检查器 ⑨ 当场变红)。
// 与 DE-D-04 的 `TokenPair` 同类:**同名不同义的类型不会报错,只会让两边悄悄分叉**。
// 契约是唯一权威,所以这里只留"契约里没有具名 schema"的请求体(ShareCreateRequest)。

/// <summary>创建分享的入参(契约 6.3 `/api/v1/shares`)。</summary>
public sealed record ShareCreateRequest
{
    [JsonPropertyName("file_id")] public required string FileId { get; init; }
    [JsonPropertyName("password")] public string? Password { get; init; }
    [JsonPropertyName("expires_in_hours")] public int? ExpiresInHours { get; init; }
    [JsonPropertyName("max_downloads")] public int? MaxDownloads { get; init; }
}

/// <summary>创建分享的结果说明:契约里 `ShareCreated` **没有** `url` 字段(只回 token),
/// 所以"分享链接"由客户端按契约路由拼出来 —— 拼法集中在这里,免得各处各写一个。</summary>
public static class ShareLinks
{
    /// <summary>按 6.3 的免登录路由 `/s/{token}` 拼链接。</summary>
    public static string LinkFor(ShareCreated created, string baseAddress)
        => baseAddress.TrimEnd('/') + "/s/" + created.token;
}

/// <summary>协作与分享的客户端(H5 与桌面端同一组接口)。</summary>
public sealed class SpaceCollabClient
{
    private readonly ApiClient _api;

    public SpaceCollabClient(ApiClient api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }

    // ---- 空间(与 H5 完全同一组路径)----

    /// <summary>GET /api/v1/spaces —— 我能看到的空间(服务端按权限过滤)。</summary>
    public Task<SpaceList> ListMineAsync(CancellationToken ct = default)
        => _api.GetAsync<SpaceList>("/api/v1/spaces", ct);

    /// <summary>POST /api/v1/spaces —— 建团队空间。</summary>
    public Task<SpaceView> CreateAsync(string name, CancellationToken ct = default)
        => _api.PostAsync<SpaceView>("/api/v1/spaces", new { name }, ct);

    /// <summary>POST /api/v1/spaces/{id}/members —— 按**用户名**邀请(与 H5 同语义)。</summary>
    public Task<MemberView> InviteAsync(string spaceId, string username, string permission,
        CancellationToken ct = default)
        => _api.PostAsync<MemberView>($"/api/v1/spaces/{spaceId}/members",
            new { username, permission }, ct);

    /// <summary>GET /api/v1/spaces/{id}/members</summary>
    public Task<MemberList> ListMembersAsync(string spaceId, CancellationToken ct = default)
        => _api.GetAsync<MemberList>($"/api/v1/spaces/{spaceId}/members", ct);

    /// <summary>DELETE /api/v1/spaces/{id}/members/{userId}</summary>
    public Task<string> RemoveMemberAsync(string spaceId, string userId, CancellationToken ct = default)
        => _api.DeleteAsync<string>($"/api/v1/spaces/{spaceId}/members/{userId}", ct);

    /// <summary>POST /api/v1/spaces/{id}/leave</summary>
    public Task<string> LeaveAsync(string spaceId, CancellationToken ct = default)
        => _api.PostAsync<string>($"/api/v1/spaces/{spaceId}/leave", new { }, ct);

    /// <summary>POST /api/v1/spaces/{id}/transfer —— 按用户名转让(H5 同语义)。</summary>
    public Task<string> TransferAsync(string spaceId, string newOwnerUsername, CancellationToken ct = default)
        => _api.PostAsync<string>($"/api/v1/spaces/{spaceId}/transfer",
            new { new_owner_username = newOwnerUsername }, ct);

    /// <summary>DELETE /api/v1/spaces/{id} —— 解散(硬删;UI 必须二次确认)。</summary>
    public Task<string> DissolveAsync(string spaceId, CancellationToken ct = default)
        => _api.DeleteAsync<string>($"/api/v1/spaces/{spaceId}", ct);

    // ---- 分享(与 H5 共用 /api/v1/shares)----

    /// <summary>POST /api/v1/shares —— 创建分享链接(免登录出口)。</summary>
    public Task<ShareCreated> CreateShareAsync(ShareCreateRequest request, CancellationToken ct = default)
        => _api.PostAsync<ShareCreated>("/api/v1/shares", request, ct);

    /// <summary>DELETE /api/v1/shares/{id} —— 撤销分享。</summary>
    public Task<string> RevokeShareAsync(string shareId, CancellationToken ct = default)
        => _api.DeleteAsync<string>($"/api/v1/shares/{shareId}", ct);

    /// <summary>
    /// 把服务端返回的错误变成**可展示**的文案(而不是在端上判断"我该不该做")。
    ///
    /// 注意这里是"解释服务端的决定",不是"替服务端做决定":方法名与语义都刻意写成
    /// `DescribeFailure` 而不是 `CanOperate` —— 后者一旦存在,就一定会有人拿它去挡请求。
    /// </summary>
    public static string DescribeFailure(ApiException ex) => ex.Code switch
    {
        "forbidden" => ex.Message,                       // 服务端已给出可读原因(如"只有空间所有者可以解散空间")
        "space_revoked" => "你已不在该空间中(或空间被冻结),请刷新后重试",
        "space_gone" => "该空间已不存在",
        "conflict" => ex.Message,                        // 409 的具体原因(重名/版本/目录任务进行中)
        "not_found" => ex.Message,
        _ => $"操作失败:{ex.Message}",
    };
}
