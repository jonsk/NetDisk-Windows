NetDisk 客户端(MSI 骨架占位文件)

这个文件的作用只有一个:让 MSI 里**至少有一个文件载荷**,从而
  - Media 表有条目(否则 ICE71: The Media table has no entries);
  - perUser 安装目录能在卸载时被移除(否则 ICE64: 目录在用户配置目录里但没登记 RemoveFile)。

没有载荷的"空 MSI"能编译出 cab,但会在 ICE 校验阶段失败 —— 而 ICE 校验正是
我们想要的那道门禁(WiX 的警告大多对应真实的安装/卸载问题)。

DE-D-20 会用 NetDisk.App 的真实发布产物(可执行文件 + 依赖)替换本占位文件,
并补上图标/快捷方式/updater 通道。届时本文件删除。
