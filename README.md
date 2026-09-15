# desktop · 网盘桌面客户端（仅 Windows）

> 独立仓库（从原 monorepo `netdisk` 的 `desktop/` 拆分而来，保留历史）。
> C# + WPF（.NET 8+，目标框架 `net10.0-windows`），承担文件同步与大文件传输。

## 仓库内容

- `src/NetDisk.App` — WPF 主程序（UI / 托盘 / 通知 / 开机自启）
- `src/NetDisk.SyncEngine` — 双向同步引擎（状态机 / 冲突裁决 / 记账）
- `src/NetDisk.Transport` — REST / TUS / WebDAV 传输层
- `src/NetDisk.ClientCore` — 平台无关核心（登录回环、命名规则、路径预算、游标）
- `src/NetDisk.Setup` — WiX 安装打包（perUser，R-25：固定 WiX 6.0.2）
- `tests/*` — 20+ 个零依赖行为检查器（DE-D 系列验收）
- `testdata/` — 与服务端共享的夹具（名字规则 / 冲突命名）

## 构建

```bash
dotnet build NetDisk.sln -c Release --nologo      # 5 个代码工程 + 检查器
dotnet build src/NetDisk.Setup/NetDisk.Setup.wixproj -c Release --nologo   # MSI
dotnet run --project tests/NameRulesCheck -c Release --nologo   # 单检查器示例
```

> 目的框架必须 `.NET 10`（含 WindowsDesktop.App 10）；WiX 锁定 6.0.2，禁止升级 v7。

## 说明

客户端契约模型由 `web` 仓库的 `gen-csharp.mjs` 生成（提交在本仓库内）；
与服务端共享的名字/冲突夹具在 `testdata/`（服务端生成、双端一致性由 CI 锁住）。
本仓库不含架构设计正文（见 `Server-Ent` / `Server-com` README 指向的设计文档）。
