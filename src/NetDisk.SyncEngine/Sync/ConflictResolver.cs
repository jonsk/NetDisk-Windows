// 冲突解决(DE-D-12 ①③):服务端 version 裁决 + 手动选择保留哪端。
//
// 7.2 定稿里最要紧的一句话是:**自动策略以服务端 `version` 为准,mtime 仅用于展示**。
// 这不是风格问题:两台机器的时钟随时可能差几分钟(时区/手动改时间/虚拟机休眠),
// 用 mtime 判"谁更新"会把**更新**的那份覆盖成**更旧**的那份 —— 而用户看到的是"同步完成"。
// 服务端的 version 是单调的、由单一权威分配的,所以只有它能裁决。
// 因此本类型**不接受 mtime 参与决策**:签名里就没有它(检查器再断言一次:
// 两份只在 mtime 上不同的条目必须得到完全相同的决议)。
//
// 冲突的处置不能"二选一丢掉一边":网盘没有回收站(4.5),被丢掉的那一侧内容就永久没了。
// 所以两种策略都**先保留败方**:
//   自动 / 保留服务端 → 本地那份改名为冲突副本(本地改名,不传不下载),再把服务端版本拉下来;
//   保留本地       → 远端那份先改名成冲突副本(一次 MOVE),再把本地版本上传覆盖规范名。
//
// 为什么"保留本地"要多一步远端改名:直接把本地传上去会覆盖远端内容,而那份内容可能
// 正是用户想留的旧版本 —— 先改名等于给它留一条命,用户事后还能在远端看到它。

using NetDisk.ClientCore;

namespace NetDisk.SyncEngine.Sync;

/// <summary>保留哪一端作为"规范名"上的内容。</summary>
public enum ConflictSide
{
    /// <summary>保留服务端版本(自动策略的默认)。</summary>
    Server,

    /// <summary>保留本地版本(用户手动选)。</summary>
    Local,
}

/// <summary>冲突处置里的一个动作(按顺序执行)。</summary>
public enum ConflictActionKind
{
    /// <summary>把本地文件改名为冲突副本(本地操作,不产生网络流量)。</summary>
    RenameLocalToConflictCopy,

    /// <summary>把远端文件改名为冲突副本(一次 MOVE,保住败方内容)。</summary>
    RenameRemoteToConflictCopy,

    /// <summary>下载远端版本到规范路径。</summary>
    Download,

    /// <summary>上传本地版本到规范路径(带 base_version 以覆盖)。</summary>
    Upload,
}

public sealed record ConflictAction(ConflictActionKind Kind, string TargetName, string Note);

/// <summary>冲突处置决议。</summary>
public sealed record ConflictResolution(
    ConflictSide Canonical,
    bool DecidedByServerVersion,
    IReadOnlyList<ConflictAction> Actions,
    string Reason);

/// <summary>冲突裁决器(纯函数)。</summary>
public static class ConflictResolver
{
    /// <summary>
    /// 自动策略:**服务端 version 裁决**。败方(本地)以冲突副本形式保留。
    /// </summary>
    /// <param name="entry">冲突条目(只用 fileId / 路径 / 远端版本,不用 mtime)。</param>
    /// <param name="timestamp">
    /// 冲突副本的时间戳,**必须由调用方持久化后传入**(而不是每次现取当前时间):
    /// 否则"同一个冲突重试两次"会生成两个不同的副本名,用户目录里会冒出重复副本。
    /// </param>
    public static ConflictResolution ResolveAuto(FileSyncEntry entry, string timestamp, int maxNameBytes = ConflictNaming.DefaultMaxBytes)
    {
        var copyName = ConflictNaming.Suggest(LocalNameOf(entry), timestamp, maxNameBytes);
        return new ConflictResolution(
            ConflictSide.Server,
            DecidedByServerVersion: true,
            new[]
            {
                new ConflictAction(ConflictActionKind.RenameLocalToConflictCopy, copyName,
                    "先把本地那份改名为冲突副本(内容不丢,且不需要上传/下载)"),
                new ConflictAction(ConflictActionKind.Download, LocalNameOf(entry),
                    "再把服务端版本拉到规范路径(服务端 version 是唯一权威)"),
            },
            $"服务端 version={entry.RemoteVersion} 裁决;本地版本另存为 {copyName}");
    }

    /// <summary>手动策略:用户选择保留哪一端;败方同样以冲突副本保留。</summary>
    public static ConflictResolution ResolveManual(
        FileSyncEntry entry, ConflictSide keep, string timestamp, int maxNameBytes = ConflictNaming.DefaultMaxBytes)
    {
        var copyName = ConflictNaming.Suggest(LocalNameOf(entry), timestamp, maxNameBytes);
        if (keep == ConflictSide.Local)
        {
            return new ConflictResolution(
                ConflictSide.Local,
                DecidedByServerVersion: false,
                new[]
                {
                    new ConflictAction(ConflictActionKind.RenameRemoteToConflictCopy, copyName,
                        "先把远端那份改名保留(一次 MOVE),之后再上传本地版本 —— 否则远端内容会被直接覆盖掉"),
                    new ConflictAction(ConflictActionKind.Upload, LocalNameOf(entry),
                        $"上传本地版本覆盖规范名(base_version={entry.RemoteVersion})"),
                },
                $"用户选择保留本地版本;远端版本另存为 {copyName}");
        }

        return new ConflictResolution(
            ConflictSide.Server,
            DecidedByServerVersion: false,
            new[]
            {
                new ConflictAction(ConflictActionKind.RenameLocalToConflictCopy, copyName,
                    "本地那份改名为冲突副本保留"),
                new ConflictAction(ConflictActionKind.Download, LocalNameOf(entry),
                    "下载服务端版本到规范路径"),
            },
            $"用户选择保留服务端版本;本地版本另存为 {copyName}");
    }

    /// <summary>从条目里取本地文件名(不含目录)。</summary>
    private static string LocalNameOf(FileSyncEntry entry)
    {
        var name = Path.GetFileName(entry.LocalPath);
        return string.IsNullOrEmpty(name) ? entry.FileId : name;
    }
}
