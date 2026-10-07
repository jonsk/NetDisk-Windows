// 分享链接落地页(/s/{token})基址解析的**单一来源**。
//
// 规则:**先读已保存的配置**(用户在登录页/设置页填的服务器地址),再回落到环境变量
// NETDISK_BASE_URL(仅为兼容老用法/CI,普通用户装完 MSI 用不到它)。
//
// 为什么单独抽出来:"我的分享"与"团队空间-建分享"两处此前各写一遍,其中 SpacesView
// 只读环境变量、**忽略配置** —— 表现是"设置页明明填了服务器地址,创建分享却拼出
// 一条空链接"(/s/{token} 前面没有主机名)。基址解析只能有一处实现,否则迟早再次分叉。
using System;
using NetDisk.SyncEngine.Host;

namespace NetDisk.App;

/// <summary>解析分享落地页(/s/{token})的服务器基址。</summary>
public static class ShareLinkBase
{
    /// <summary>配置优先、环境变量兜底;两者都为空时返回空串(调用方据此提示用户未配置服务器)。</summary>
    public static string Resolve()
    {
        var baseUrl = ClientConfig.Load().BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = Environment.GetEnvironmentVariable("NETDISK_BASE_URL");
        }
        return baseUrl ?? "";
    }
}
