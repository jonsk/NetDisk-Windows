// 冲突解决的两种"入口"(DE-D-12 的收尾件)。
//
// 背景:两端都改了同一个文件时,引擎的默认做法是**两份都保留** —— 本地那一版被另存成
// 副本文件(ConflictNaming),原路径上放远端那一版,界面标「冲突」。这个默认是对的
// (它不丢任何一方的数据),但用户需要两条出路:
//   ① **设置里预先定好**(无人值守):每次冲突都按同一个策略自动处理;
//   ② **界面上逐个处理**:看到「冲突」后自己挑一次(以本地为准 / 以远端为准 / 都保留)。
//
// 这两条路分别对应本文件的两个类型 —— 不要合并成一个:一个是**偏好**,一个是**一次决定**,
// 混在一起以后必然出现"我明明只在这一次选了以本地为准,怎么以后都这样了"。

namespace NetDisk.SyncEngine.Host;

/// <summary>冲突**策略**(配置项 <c>on_conflict</c>;无人值守时使用)。</summary>
public enum ConflictPolicy
{
    /// <summary>两份都保留(默认):本地那一版存成副本、远端那一版留在原路径。</summary>
    KeepBoth,

    /// <summary>以本地为准:本地那一版原地覆盖远端(**放弃远端那一版**)。</summary>
    KeepLocal,

    /// <summary>以远端为准:远端那一版覆盖本地(**放弃本地改动**,不留副本)。</summary>
    KeepRemote,
}

/// <summary>一次**逐文件**冲突解决的选择(界面按钮)。</summary>
public enum ConflictResolution
{
    /// <summary>两份都保留(副本会作为新文件上传)。</summary>
    KeepBoth,

    /// <summary>以本地为准:把本地副本写回原路径并原地覆盖远端。</summary>
    KeepLocal,

    /// <summary>以远端为准:保留远端那一版,删掉本地副本(本地改动被放弃)。</summary>
    KeepRemote,
}
