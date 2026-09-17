# NetDisk 桌面客户端（Windows）

> 独立仓库（从原 `netdisk` monorepo 的 `desktop/` 拆分而来，保留历史）。
> C# + WPF，目标框架 `net10.0-windows`，承担文件同步与大文件传输。**仅支持 Windows x64**。

## 这个客户端做什么

- 与服务端做**双向同步**（状态机 / 冲突裁决 / 配额滞回）
- 大文件**分片上传（TUS）/ 断点续传**，REST / WebDAV 传输
- 系统托盘常驻 + 通知中心 + 开机自启 + 设置界面
- **体积精简、单文件**：单个 EXE（约 3MB，框架依赖）拷贝即运行，无需安装器

## 架构

| 工程 | 职责 |
|------|------|
| `src/NetDisk.App` | WPF 主程序（UI / 托盘 / 通知 / 开机自启 / 组合根）。唯一允许 `UseWPF` + `UseWindowsForms` 的工程 |
| `src/NetDisk.SyncEngine` | 同步引擎（状态机 / 冲突裁决 / 记账 / 自动更新 / 数据路径） |
| `src/NetDisk.Transport` | REST / TUS / WebDAV 传输层、登录回环（PKCE）、令牌提供 |
| `src/NetDisk.ClientCore` | 平台无关核心（登录回环、命名规则、路径预算、游标、分享列表） |
| `tests/*` | 20+ 个零依赖行为检查器（DE-D 系列验收，夹具在 `testdata/`） |

平台无关逻辑全在 `ClientCore / SyncEngine / Transport`，`App` 只做「把状态画出来 + 把用户动作转成用例调用」。

## 运行要求

- **Windows x64**（Windows 10 1709+ / 11）。
- **需先装 .NET Desktop Runtime 10**（一次性；2026-09-17 体积决策由自包含改框架依赖后，exe 从 76.8MB 降到约 3MB，代价是目标机需装 ~30MB 运行库）。
- 首次运行需要**可写目录**（程序同目录或 `%APPDATA%\NetDisk`）。

## 分发与运行

### 方式一：单文件 EXE（推荐，体积小）

```bash
# 产出恰好一个文件：bin/Release/net10.0-windows/win-x64/publish/NetDisk.App.exe（约 3MB）
dotnet publish src/NetDisk.App/NetDisk.App.csproj -c Release -r win-x64 \
  -p:Version=1.0.<git提交数>
```

把 `NetDisk.App.exe` 拷到已装 **.NET Desktop Runtime 10** 的 Windows x64 电脑，**双击即运行**。它：

- **不内嵌运行时**（框架依赖），体积仅约 3MB；
- 首次运行在 **exe 同目录**生成 `client.json`（见「配置」）；
- 程序目录不可写（如 `Program Files`）时，自动回退到 `%APPDATA%\NetDisk`，并在日志中说明回退原因；
- 删除 exe 即「卸载」，无残留注册表（除可选的自动更新登记）。

> 以上单文件属性已在 `NetDisk.App.csproj` 默认开启：`PublishSingleFile` / `SelfContained=false` /
> `RuntimeIdentifier=win-x64` / `IncludeNativeLibrariesForSelfExtract`（SQLite 原生库内嵌）。
> `dotnet publish` 后发布目录里**恰好一个 exe**。裸 `dotnet publish` 会自动取 `1.0.<git提交数>`，
> 无需手写；显式 `-p:Version=...` 仍优先。

> **分发方式说明（2026-09-17）**：项目已**移除 WiX/MSI**，不再提供安装包。分发即「把单个
> `NetDisk.App.exe` 拷到目标 Windows 电脑」。需要开始菜单快捷方式 / 开机自启的用户，由客户端
> 设置界面内的「创建桌面快捷方式」自行生成（不依赖安装器）。

## 配置

配置文件 `client.json` 位于**程序同目录**（不可写则 `%APPDATA%\NetDisk`）：

- **首次启动自动生成**（带 `_说明` 字段，中文可读，用户可直接编辑）；
- **之后每次启动读取该文件**；修改后**重启客户端即生效**（设置界面的「保存并重启同步」也是写这个文件）；
- 配置文件损坏不会崩溃——自动另存为 `.bak` 并以默认值启动。

常用字段：

| 字段 | 含义 |
|------|------|
| `base_url` | 服务端基址（如 `https://disk.example.com`） |
| `sync_root` | 本地同步目录 |
| `space_id` / `parent_id` | 同步的空间 / 空间内目录（空 = 个人空间 / 根） |
| `spaces` | 多空间绑定列表（每个空间对应一个本地目录） |
| `max_concurrency` | 传输并发数（1–16，默认 3） |
| `upload_kbps` / `download_kbps` | 限速（KB/s，0 = 不限） |
| `structure_only` | 只读浏览（仅结构，不下载 / 不上传 / 不删） |
| `on_conflict` | 冲突策略 `keep_both`（默认）/ `keep_local` / `keep_remote` |
| `logging` | 是否记录日志（默认开） |

同目录数据文件：`tokens.bin`（DPAPI 加密令牌）、`state.db`（同步状态）、`logs/client.log`。

## 构建（开发）

```bash
dotnet build NetDisk.sln -c Release --nologo        # 4 个代码工程 + 检查器
dotnet run --project tests/NameRulesCheck -c Release --nologo   # 单检查器示例
```

> 目标框架须为 **.NET 10**（含 `WindowsDesktop.App 10`）。

## 自动更新（单文件 exe 换装模型）

客户端自身兼任更新器：主进程把 **exe 复制到临时目录**，用 `--apply-update --new-exe <新exe> --client <安装目录exe>`
拉起那份副本后**立刻退出**；副本负责把新 exe **换装**进安装目录、再跑 `--self-check` 验活，
验活失败就**还原备份**（回滚），最后拉起新版。序列（暂停 → 排空 → 迁移 → 启动更新器）由
`UpdateOrchestrator` 编排，失败路径一律恢复同步。`--self-check` / `--apply-update` 入口与
`UpdaterRunner`（纯决策 + 可注入 `IProcessRunner` / `IFileSwap`）均保留，所有失败路径有零依赖检查器覆盖（DE-D-19）。

```bash
# 本地验收:发布 → 断言恰好一个 exe → 首启行为 → 验活
pwsh -File scripts/verify-publish.ps1
```

## 测试

`tests/` 下 20+ 个检查器全部零依赖、不连服务端，CI 逐一运行：

```bash
dotnet run --project tests/UpdateCheck -c Release --nologo      # 自动更新序列（DE-D-19）
dotnet run --project tests/NameRulesCheck -c Release --nologo   # 名字规则双端一致（DE-D-12）
# 其余 DE-D-01 .. DE-D-21
```

## 说明

- 客户端契约模型由 `web` 仓库 `gen-csharp.mjs` 生成（提交在本仓 `docs/api/openapi.yaml` 同源）；
  与服务端共享的命名 / 冲突夹具在 `testdata/`，由 CI 锁住双端一致。
- 本仓库**不含架构设计正文**（见 `Server-Ent` / `Server-com` 指向的设计文档）。
- 设计红线与任务清单见 `Doc/功能清单与开发任务清单.md`。
