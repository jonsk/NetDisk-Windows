// ============================================================================
// 本地化资源表（2026-09-18，桌面端多语言改造 P0）。
//
// 为什么用**代码内字典**而不是 .resx 卫星程序集:
//   项目有"发布目录恰好一个 exe"的铁律(verify-publish.ps1/CI 断言)。
//   .resx 的每种语言都会编译成 `xx/App.resources.dll` 卫星程序集 —— 那会在发布目录
//   多出文件,直接违背单文件约束。代码内字典天然是一个 exe、零新增依赖(守项目纪律)、
//   且切语言即时生效、缺失回退默认。
//
// 约定:
//   · `Base` 恒为**默认语言**表,当前作为中文;
//   · 每个语言表(Base/en/...)键集与 Base 完全一致(缺失会在测试 I18nCheck 抓出来);
//   · 键命名 `<区域>.<语义>`(如 Settings.Language / Sync.UploadProgress / Error.SpaceGone);
//   · 复合/带占位符的串用 {0} {1},调用方走 Loc.Format(禁止手工 + 拼接,防中英文语序错)。
// ============================================================================

using System.Globalization;

namespace NetDisk.App.Localization;

/// <summary>本地化资源表:按 Culture 返回字符串,缺失回退默认。</summary>
public static class Locale
{
    // ---- 默认语言(当前 = 简体中文)----
    public static readonly CultureInfo DefaultCulture = new("zh-Hans");
    public const string DefaultCode = "zh";

    // ---- 各语言表 ----
    private static readonly IReadOnlyDictionary<string, string> _base = BuildBase();
    private static readonly IReadOnlyDictionary<string, string> _en = BuildEn();

    /// <summary>按 culture code("zh"/"en")取表;未知 code 回退默认表。</summary>
    public static IReadOnlyDictionary<string, string> TableFor(string code)
    {
        return code switch
        {
            "en" => _en,
            _ => _base, // 未知/zh 一律回退默认
        };
    }

    /// <summary>已知语言清单(用于设置界面下拉;code → 母语名)。</summary>
    public static IReadOnlyList<(string Code, string SelfName)> Languages { get; } =
        new (string, string)[]
        {
            ("zh", "简体中文"),
            ("en", "English"),
        };

    private static Dictionary<string, string> BuildBase() => new()
    {
        // ===== 通用/App =====
        ["App.WindowTitle"] = "NetDisk 桌面端",
        ["App.AboutTab"] = "关于",
        ["App.CheckUpdate"] = "检查更新(选择新版本程序…)",
        ["App.CheckUpdateDesc"] = "升级会:暂停同步 → 等正在传的文件传完 → 迁移本地状态库 → 安装新版本 → 自动重启客户端。本地文件与配置不会被删除。",
        ["App.AboutDesc"] = "本机与服务器双向同步:本地改动自动上传,服务器改动自动下载,两端同时改动时保留冲突副本。",
        ["App.AboutPlatform"] = "平台无关逻辑在 NetDisk.ClientCore / SyncEngine / Transport;本窗口只做渲染与动作转发。",

        // ===== 登录页 =====
        ["Login.Title"] = "登录 NetDisk",
        ["Login.Subtitle"] = "填写服务器地址与账号口令。登录成功后本机开始与服务器双向同步。",
        ["Login.Server"] = "服务器地址",
        ["Login.Username"] = "用户名",
        ["Login.Password"] = "口令",
        ["Login.SyncRoot"] = "同步目录",
        ["Login.Browse"] = "浏览…",
        ["Login.Start"] = "登录并开始同步",
        ["Login.FirstRun"] = "首次运行",
        ["Login.FirstRunHint"] = "登录后会先列出服务器上的文件,确认目录与数据量,再由你决定是否开始同步。",

        // ===== 主窗口 Tabs =====
        ["Main.Sync"] = "同步",
        ["Main.Remote"] = "远端文件",
        ["Main.Spaces"] = "团队空间",
        ["Main.MyShares"] = "我的分享",
        ["Main.Settings"] = "设置",

        // ===== 同步页 =====
        ["Sync.NotStarted"] = "尚未开始同步",
        ["Sync.SyncNow"] = "立即同步",
        ["Sync.Pause"] = "暂停同步",
        ["Sync.OpenFolder"] = "打开同步目录",
        ["Sync.Log"] = "记录日志",
        ["Sync.OpenLog"] = "打开日志",
        ["Sync.ColFile"] = "文件",
        ["Sync.ColState"] = "状态",
        ["Sync.ColProgress"] = "进度",
        ["Sync.ColMessage"] = "说明",
        ["Sync.ColVersion"] = "版本",
        ["Sync.ConflictHint"] = "冲突处理(先选中列表里的「冲突」行):",
        ["Sync.KeepLocal"] = "以本地为准(覆盖远端)",
        ["Sync.KeepRemote"] = "以远端为准(覆盖本地)",
        ["Sync.KeepBoth"] = "都保留",
        ["Sync.ConflictPickHint"] = "先在下面的列表里点一行「冲突」",
        ["Sync.StateLegend"] = "状态含义:已同步 = 两端一致;上传中/下载中 = 正在传输;冲突 = 两端都改过,本地版本已另存为冲突副本,可用上面的按钮处理;失败 = 需要处理的失败;仅结构 = 只读浏览模式下未搬内容。",
        ["Sync.DeleteNote"] = "删除是双向同步的:在同步目录里删掉文件,服务器上也会删;另一端删掉的文件,这里也会删。删除在服务端不可恢复,且删掉的文件不会再出现在上面的列表里(可在日志里查记录)。",

        // ===== 远端浏览 =====
        ["Remote.Root"] = "位置:/",
        ["Remote.Up"] = "返回上级",
        ["Remote.Refresh"] = "刷新",
        ["Remote.Preview"] = "预览(下载到临时目录并打开)",
        ["Remote.ColName"] = "名称",
        ["Remote.ColKind"] = "类型",
        ["Remote.ColSize"] = "大小",
        ["Remote.ColVersion"] = "版本",
        ["Remote.Footer"] = "双击目录进入、双击文件预览(先下载到临时目录再打开,不会改动同步目录,也不会上传任何东西)。这个页面是只读的:不能在这里改名/删除/上传。",

        // ===== 团队空间 =====
        ["Spaces.Title"] = "团队空间",
        ["Spaces.Subtitle"] = "只显示服务端允许我看到的空间",
        ["Spaces.Create"] = "建空间",
        ["Spaces.NewSpaceHint"] = "(输入名称后点建空间)",
        ["Spaces.CreateShareTitle"] = "创建分享链接",
        ["Spaces.ShareFileId"] = "文件 id(在客户端文件树里右键获取)",
        ["Spaces.ShareExpires"] = "有效期(小时,留空=默认7天)",
        ["Spaces.ShareMaxDownloads"] = "最大下载次数(留空=不限)",
        ["Spaces.SharePassword"] = "访问口令(留空=无密码)",
        ["Spaces.CreateShare"] = "创建分享",
        ["Spaces.NoSelection"] = "未选择空间",
        ["Spaces.Members"] = "成员",
        ["Spaces.Username"] = "用户名",
        ["Spaces.Invite"] = "邀请",
        ["Spaces.RemoveMember"] = "移除所选成员",
        ["Spaces.TransferTo"] = "转让给(用户名)",
        ["Spaces.Transfer"] = "转让",
        ["Spaces.Leave"] = "退出空间",
        ["Spaces.Dissolve"] = "解散空间(硬删,不可恢复)",
        ["Spaces.DissolveHint"] = "为避免误点:请把空间名原样输入下面的框,再点解散。",
        ["Spaces.DissolveConfirm"] = "解散空间",
        ["Spaces.RoleOwner"] = "你是所有者",

        // ===== 我的分享 =====
        ["Shares.Refresh"] = "刷新",
        ["Shares.ColName"] = "名称",
        ["Shares.ColStatus"] = "状态",
        ["Shares.ColExpires"] = "有效期至",
        ["Shares.ColDownloaded"] = "已下载",
        ["Shares.ColLimit"] = "上限",
        ["Shares.ColActions"] = "操作",
        ["Shares.CopyLink"] = "复制链接",
        ["Shares.Open"] = "打开",
        ["Shares.Revoke"] = "吊销",
        ["Shares.Footer"] = "「我的分享」列出你创建的所有分享链接。吊销后链接立即失效且不可恢复;复制/打开始终指向免登录落地页 /s/{token}。",
        ["Share.StatusActive"] = "生效中",
        ["Share.StatusRevoked"] = "已吊销",
        ["Share.ReplyOpen"] = "已打开",

        // ===== 设置页 =====
        ["Settings.Title"] = "设置",
        ["Settings.Server"] = "服务器地址",
        ["Settings.SyncRoot"] = "同步目录",
        ["Settings.Browse"] = "浏览…",
        ["Settings.Concurrency"] = "并发数(1-16)",
        ["Settings.Upload"] = "上行限速(KB/s)",
        ["Settings.Download"] = "下行限速(KB/s)",
        ["Settings.LimitNote"] = "限速填 0 表示不限速。改动在「保存并重启同步」后生效。",
        ["Settings.Log"] = "记录日志(默认启用)",
        ["Settings.StructureOnly"] = "只读浏览(仅结构):只同步目录结构,不下载/不上传/不删",
        ["Settings.StructureOnlyNote"] = "勾选后:远端目录结构会建到本地,文件**不落盘**(列表里显示为「仅结构」),本地文件也不上传,两端都不删除任何东西。",
        ["Settings.OnConflict"] = "两端都改了怎么办",
        ["Settings.OnConflictKeepBoth"] = "都保留(默认:本地那一版存成副本)",
        ["Settings.OnConflictKeepLocal"] = "以本地为准(覆盖远端)",
        ["Settings.OnConflictKeepRemote"] = "以远端为准(覆盖本地)",
        ["Settings.OnConflictNote"] = "这是**无人值守**策略;无论选哪条,同步页里都能对某一次冲突单独改主意(先选中「冲突」行)。",
        ["Settings.SpacesTitle"] = "同步的空间(每个空间一个本地目录)",
        ["Settings.AddSpace"] = "添加空间…",
        ["Settings.RemoveBinding"] = "移除选中绑定",
        ["Settings.RemoveBindingNote"] = "（只解除绑定，不删除本地文件）",
        ["Settings.SpaceHint"] = "改动空间后需要「保存并重启同步」生效；每个空间有自己的状态库(state-<空间短码>.db)。",
        ["Settings.Save"] = "保存并重启同步",
        ["Settings.Logout"] = "退出登录",
        ["Settings.Language"] = "界面语言",
        ["Settings.LanguageNote"] = "切换后立即生效,重启后保持。",
        ["Settings.PathFormat"] = "配置文件:{0}\n数据目录:{1}",
        ["Settings.PathFallback"] = " (回退:{0})",
        ["Settings.StatusRestarting"] = "正在按新配置重启同步…",
        ["Settings.StatusRestarted"] = "已按新配置重启同步。",
        ["Settings.StatusAdded"] = "已添加。点「保存并重启同步」后按新配置生效。",
        ["Settings.StatusRemoved"] = "已移除(本地文件没有删除)。点「保存并重启同步」后生效。",
        ["Settings.StatusPickBinding"] = "请先在上面选中一条绑定。",

        // ===== 空间选择窗口 =====
        ["SpacePick.Title"] = "选择要同步的空间",
        ["SpacePick.Hint"] = "选择要同步到这个本地目录的空间:",
        ["SpacePick.Next"] = "下一步:选目录",
        ["SpacePick.Cancel"] = "取消",

        // ===== 错误/提示(C# 调用) =====
        ["Error.AddSpaceList"] = "取空间列表失败:{0}",
        ["Error.NoSpaces"] = "这个账号没有可见空间。",
        ["Error.DirBound"] = "这个目录已经绑给另一个空间了:{0}。请为每个空间选不同的目录。",
        ["Error.CannotRemovePrimary"] = "第一条是**主空间**(对应上面的服务器/同步目录设置),不能在这里移除;请先移除其它空间。",
        ["Error.BadBaseUrl"] = "服务器地址要写成完整地址,例如 http://10.14.37.187",
        ["Error.EmptyRoot"] = "同步目录不能为空",
        ["Error.BadConcurrency"] = "并发数要填数字(1-16)",
        ["Error.BadRateLimit"] = "限速要填数字(KB/s,0 = 不限速)",
        ["Error.ConcurrencyRange"] = "并发数必须在 1..16(0 会让队列永不执行;过大把机器与带宽打满)",
        ["Error.NegativeRate"] = "限速不能为负(0 = 不限速)",
        ["Error.RestartFail"] = "重启同步失败: {0}",
        ["Error.LogoutConfirmMsg"] = "退出登录会吊销服务器上的登录状态并删除本机令牌(文件不会被删除)。继续吗?",
        ["Error.LogoutTitle"] = "退出登录",
        ["Error.LogoutFail"] = "退出登录失败: {0}",

        // ===== MainWindow 关于页 / 更新(C#) =====
        ["Main.VersionFormat"] = "版本 {0}",
        ["Main.TrayTitle"] = "NetDisk 仍在后台同步",
        ["Main.TrayBody"] = "窗口已隐藏到托盘;要完全退出请右键托盘图标选「退出」。",
        ["Main.NoSyncYet"] = "同步还没启动,先登录再升级。",
        ["Main.PickExeTitle"] = "选择 NetDisk 新版本程序 (NetDisk.App.exe)",
        ["Main.PickExeFilter"] = "NetDisk 程序 (*.exe)|*.exe",
        ["Main.UpdateConfirmTitle"] = "检查更新",
        ["Main.UpdateConfirmStart"] = "现在开始升级?",
        ["Main.UpdateCancel"] = "已取消(没有做任何改动)。",
        ["Main.Upgrading"] = "正在升级(暂停同步 → 等传输跑完 → 迁移状态库 → 启动更新器换装)…",
        ["Main.UpgradeStarted"] = "升级已启动:{0}",
        ["Main.UpgradeFailed"] = "这次没有升级成功({0});同步已恢复。",

        // ===== 登录页(C#) =====
        ["Login.PickSyncDir"] = "选择同步目录",
        ["Login.FillAll"] = "服务器地址、用户名、口令、同步目录都要填。",
        ["Login.LoggingIn"] = "正在登录…",
        ["Login.LoginFail"] = "登录失败: {0}",
        ["Login.Listing"] = "正在读取服务器上的文件清单…",
        ["Login.RootUnusable"] = "同步目录不可用: {0}",
        ["Login.EmptySpace"] = "服务器上还没有任何文件(这是一个空空间,正常):首次同步会把这个目录里的文件上传上去。",
        ["Login.CountSummary"] = "服务器上有 {0} 个文件、{1} 个目录,合计约 {2}。",
        ["Login.SyncDirSummary"] = "同步目录:{0}\n(首次同步会把服务器上的文件下载到该目录,并把该目录里的文件上传到服务器。)",
        ["Login.ConfirmFirstSync"] = "确认首次同步",
        ["Login.ConfirmStartQuestion"] = "现在开始同步吗?",
        ["Login.Cancelled"] = "已取消:没有开始同步,也没有改动任何文件。",
        ["Login.Starting"] = "正在启动同步…",
        ["Login.StartFail"] = "同步未能启动(配置不完整或令牌不可用),请重新登录。",
        ["Login.Unexpected"] = "发生未预期的错误: {0}",

        // ===== 同步页(C#) =====
        ["Sync.AccountLine"] = "账号 {0} @ {1}    同步目录 {2}",
        ["Sync.LogPathDefault"] = "日志文件:{0}(默认启用;关掉后不再记录,便于对照复现)",
        ["Sync.LogPathOn"] = "日志文件:{0}(已启用)",
        ["Sync.LogPathOff"] = "日志已关闭(此前记录在 {0};重新勾选即继续)",
        ["Sync.NoLogYet"] = "日志还没生成:{0}\n(勾选「记录日志」后产生)",
        ["Sync.OpenLogFail"] = "打开日志失败: {0}\n{1}",
        ["Sync.NoSyncConflict"] = "同步还没启动,无法处理冲突。",
        ["Sync.PickConflictFirst"] = "请先在上面的列表里**选中一行「冲突」**,再点这个按钮。",
        ["Sync.ResolvingChoice"] = "正在按「{0}」处理 {1}…",
        ["Sync.ResolvedChoice"] = "已按「{0}」处理 {1}。",
        ["Sync.ResolveUnhandlable"] = "{0} 处理不了(可能是上一次运行留下的冲突):请手动比对本地副本与服务器上的版本。",
        ["Sync.ChoiceKeepLocal"] = "以本地为准",
        ["Sync.ChoiceKeepRemote"] = "以远端为准",
        ["Sync.ChoiceKeepBoth"] = "都保留",
        ["Sync.NoFiles"] = "尚无文件(同步目录为空,或还没完成第一次对账)",
        ["Sync.Summary"] = "共 {0} 个文件:已同步 {1} · 上传中 {2} · 下载中 {3} · 冲突 {4} · 错误 {5}",
        ["Sync.SummaryStructureOnly"] = " · 仅结构(未搬内容){0}",
        ["Sync.ConflictSelected"] = "选中的是「{0}」的 {1} —— 冲突处理只对「冲突」行生效",
        ["Sync.ConflictSelectedNow"] = "已选中冲突:{0}",
        ["Sync.ConflictSelectedNoLocal"] = "已选中冲突:{0} —— 本地副本位置没有记录(可能来自上一次运行);点按钮会说明怎么手动处理",
        ["Sync.Resume"] = "继续同步",
        ["Sync.Resumed"] = "已恢复同步",
        ["Sync.PausedSummary"] = "已暂停:不再对账与传输(数据未改动)。点「继续同步」恢复。",
        ["Sync.Paused"] = "已暂停",
        ["Sync.PauseFail"] = "暂停/恢复失败: {0}",
        ["Sync.Syncing"] = "正在对账…",
        ["Sync.Done"] = "对账完成",
        ["Sync.SyncFail"] = "对账失败: {0}",
        ["Sync.RootMissing"] = "同步目录还不存在:{0}",
        ["Sync.RootUnset"] = "(未配置)",
        ["Sync.OpenFolderFail"] = "打开目录失败: {0}",
        ["State.Synced"] = "已同步",
        ["State.Uploading"] = "上传中",
        ["State.Downloading"] = "下载中",
        ["State.Conflict"] = "冲突",
        ["State.RemoteDeleted"] = "远端已删除",
        ["State.SpaceRemoved"] = "空间已移除",
        ["State.Permission"] = "权限受限",
        ["State.Failed"] = "失败",
        ["State.StructureOnly"] = "仅结构",

        // ===== 远端浏览(C#) =====
        ["Remote.KindDir"] = "目录",
        ["Remote.KindFile"] = "文件",
        ["Remote.RootAt"] = "位置:/{0}",
        ["Remote.EmptyHere"] = "（这一层没有条目）",
        ["Remote.Count"] = "共 {0} 项",
        ["Remote.ListFail"] = "列目录失败:{0}",
        ["Remote.ParentGone"] = "上一级已经不存在了(可能被另一端删掉)——已回到空间根。",
        ["Remote.PickFile"] = "请先在上面的列表里选中一个文件。",
        ["Remote.IsDir"] = "选中的是目录 —— 双击它可以进入。",
        ["Remote.Fetching"] = "正在取回 {0} …",
        ["Remote.PreviewCopy"] = "预览副本:{0}(临时目录,不影响同步)",
        ["Remote.PreviewFail"] = "预览失败:{0}",

        // ===== 团队空间(C#) =====
        ["Spaces.NotLoggedIn"] = "尚未登录:请先在「同步」页填写服务器地址并登录。",
        ["Spaces.OpFail"] = "操作失败",
        ["Spaces.Count"] = "共 {0} 个空间(服务端按权限过滤)",
        ["Spaces.RoleMember"] = "你在该空间中是成员(能否操作由服务端判定)",
        ["Spaces.Created"] = "已创建空间:{0}",
        ["Spaces.PickOne"] = "请先选择一个空间。",
        ["Spaces.PickMember"] = "请先选择一个成员。",
        ["Spaces.Invited"] = "已邀请 {0}({1})",
        ["Spaces.RemovedMem"] = "已移除 {0}",
        ["Spaces.Transferred"] = "已转让给 {0}",
        ["Spaces.Left"] = "已退出该空间",
        ["Spaces.DissolveConfirm"] = "为确认这是有意操作:请把空间名原样输入后再点解散。",
        ["Spaces.Dissolved"] = "已解散空间:{0}(硬删,不可恢复)",
        ["Spaces.ShareCreatedPrefix"] = "分享已创建(免登录出口):",
        ["Spaces.SharePwdSet"] = " 已设口令;",
        ["Spaces.ShareNoPwd"] = " 无口令;",
        ["Spaces.Share7d"] = " 默认7天有效;",
        ["Spaces.ShareExpiresAt"] = " 有效期至 {0};",
        ["Spaces.ShareUnlimited"] = " 下载次数不限;",
        ["Spaces.ShareMaxDownloads"] = " 最多 {0} 次下载;",

        // ===== 我的分享(C#) =====
        ["Shares.NotLoggedIn"] = "尚未登录:请先在「同步」页登录。",
        ["Shares.Empty"] = "你还没有创建任何分享链接。",
        ["Shares.Count"] = "共 {0} 条分享",
        ["Shares.LoadFail"] = "加载失败:{0}",
        ["Shares.RevokeConfirmBody"] = "确定吊销分享「{0}」?\n吊销后该链接立即失效且不可恢复。",
        ["Shares.RevokeConfirmTitle"] = "吊销确认",
        ["Shares.RevokedName"] = "已吊销「{0}」",
        ["Shares.RevokeFail"] = "吊销失败:{0}",
        ["Shares.Copied"] = "链接已复制到剪贴板",
        ["Shares.OpenFail"] = "无法打开链接:{0}",

        // ===== 空间选择窗口(C#) =====
        ["SpacePick.Bound"] = "  — 已绑定",
        ["SpacePick.KindFmt"] = "  ({0})",
        ["SpacePick.IndependentDir"] = "每个空间需要一个**独立**的本地目录。",
        ["SpacePick.AlreadyBound"] = "已有 {0} 个空间绑定;已绑定的空间不再重复列出可选。",
        ["SpacePick.PickOne"] = "请先选中一个空间。",

        // ===== 托盘(C#) =====
        ["Tray.OpenMain"] = "打开主界面",
        ["Tray.Quit"] = "退出",

        // ===== 分享 converter(C#) =====
        ["Share.StatusValid"] = "有效",
        ["Share.Unlimited"] = "不限",
    };

    private static Dictionary<string, string> BuildEn() => new()
    {
        // ===== Common / App =====
        ["App.WindowTitle"] = "NetDisk Desktop",
        ["App.AboutTab"] = "About",
        ["App.CheckUpdate"] = "Check for Update (select new program…)",
        ["App.CheckUpdateDesc"] = "Upgrade will: pause sync → wait for in-flight transfers → migrate local state → install new version → restart the client. Your local files and config will not be deleted.",
        ["App.AboutDesc"] = "Two-way sync with the server: local changes upload, server changes download, and when both changed a conflict copy is kept.",
        ["App.AboutPlatform"] = "Platform-agnostic logic lives in NetDisk.ClientCore / SyncEngine / Transport; this window only renders and forwards actions.",

        // ===== Login =====
        ["Login.Title"] = "Sign in to NetDisk",
        ["Login.Subtitle"] = "Enter the server address and your credentials. After signing in, this machine starts syncing with the server.",
        ["Login.Server"] = "Server address",
        ["Login.Username"] = "Username",
        ["Login.Password"] = "Password",
        ["Login.SyncRoot"] = "Sync folder",
        ["Login.Browse"] = "Browse…",
        ["Login.Start"] = "Sign in & start syncing",
        ["Login.FirstRun"] = "First run",
        ["Login.FirstRunHint"] = "After signing in, files on the server will be listed first so you can review the folder and data size before deciding whether to start syncing.",

        // ===== Main Tabs =====
        ["Main.Sync"] = "Sync",
        ["Main.Remote"] = "Remote files",
        ["Main.Spaces"] = "Team spaces",
        ["Main.MyShares"] = "My shares",
        ["Main.Settings"] = "Settings",

        // ===== Sync page =====
        ["Sync.NotStarted"] = "Not syncing yet",
        ["Sync.SyncNow"] = "Sync now",
        ["Sync.Pause"] = "Pause sync",
        ["Sync.OpenFolder"] = "Open sync folder",
        ["Sync.Log"] = "Logging",
        ["Sync.OpenLog"] = "Open log",
        ["Sync.ColFile"] = "File",
        ["Sync.ColState"] = "State",
        ["Sync.ColProgress"] = "Progress",
        ["Sync.ColMessage"] = "Message",
        ["Sync.ColVersion"] = "Version",
        ["Sync.ConflictHint"] = "Conflict handling (select a \"conflict\" row first):",
        ["Sync.KeepLocal"] = "Keep local (overwrite remote)",
        ["Sync.KeepRemote"] = "Keep remote (overwrite local)",
        ["Sync.KeepBoth"] = "Keep both",
        ["Sync.ConflictPickHint"] = "First click a \"conflict\" row in the list below",
        ["Sync.StateLegend"] = "State meanings: synced = both sides match; uploading/downloading = transferring; conflict = both changed and the local copy was saved as a conflict copy, handle it with the buttons above; failed = needs attention; structure-only = content not pulled in read-only browse mode.",
        ["Sync.DeleteNote"] = "Deletion is two-way: delete a file in the sync folder and it is deleted on the server too; files deleted on the other side are deleted here as well. Deletion is irreversible on the server, and deleted files no longer appear in the list above (you can look them up in the log).",

        // ===== Remote browse =====
        ["Remote.Root"] = "Location:/",
        ["Remote.Up"] = "Go up",
        ["Remote.Refresh"] = "Refresh",
        ["Remote.Preview"] = "Preview (download to temp and open)",
        ["Remote.ColName"] = "Name",
        ["Remote.ColKind"] = "Type",
        ["Remote.ColSize"] = "Size",
        ["Remote.ColVersion"] = "Version",
        ["Remote.Footer"] = "Double-click a folder to enter, double-click a file to preview (downloaded to a temp folder first; the sync folder is untouched and nothing is uploaded). This page is read-only: you cannot rename/delete/upload here.",

        // ===== Team spaces =====
        ["Spaces.Title"] = "Team spaces",
        ["Spaces.Subtitle"] = "Only spaces the server allows me to see are shown",
        ["Spaces.Create"] = "Create space",
        ["Spaces.NewSpaceHint"] = "(enter a name then create)",
        ["Spaces.CreateShareTitle"] = "Create share link",
        ["Spaces.ShareFileId"] = "File id (right-click in the client file tree to get it)",
        ["Spaces.ShareExpires"] = "Validity (hours; empty = 7 days default)",
        ["Spaces.ShareMaxDownloads"] = "Max downloads (empty = unlimited)",
        ["Spaces.SharePassword"] = "Access password (empty = no password)",
        ["Spaces.CreateShare"] = "Create share",
        ["Spaces.NoSelection"] = "No space selected",
        ["Spaces.Members"] = "Members",
        ["Spaces.Username"] = "Username",
        ["Spaces.Invite"] = "Invite",
        ["Spaces.RemoveMember"] = "Remove selected member",
        ["Spaces.TransferTo"] = "Transfer to (username)",
        ["Spaces.Transfer"] = "Transfer",
        ["Spaces.Leave"] = "Leave space",
        ["Spaces.Dissolve"] = "Dissolve space (hard delete, irreversible)",
        ["Spaces.DissolveHint"] = "To prevent mis-clicks, type the space name as-is into the box below, then dissolve.",
        ["Spaces.DissolveConfirm"] = "Dissolve space",
        ["Spaces.RoleOwner"] = "You are the owner",

        // ===== My shares =====
        ["Shares.Refresh"] = "Refresh",
        ["Shares.ColName"] = "Name",
        ["Shares.ColStatus"] = "Status",
        ["Shares.ColExpires"] = "Expires at",
        ["Shares.ColDownloaded"] = "Downloaded",
        ["Shares.ColLimit"] = "Limit",
        ["Shares.ColActions"] = "Actions",
        ["Shares.CopyLink"] = "Copy link",
        ["Shares.Open"] = "Open",
        ["Shares.Revoke"] = "Revoke",
        ["Shares.Footer"] = "\"My shares\" lists every share link you created. Revoking makes the link invalid immediately and irreversibly; copy/open always point to the no-login landing page /s/{token}.",
        ["Share.StatusActive"] = "Active",
        ["Share.StatusRevoked"] = "Revoked",
        ["Share.ReplyOpen"] = "Opened",

        // ===== Settings =====
        ["Settings.Title"] = "Settings",
        ["Settings.Server"] = "Server address",
        ["Settings.SyncRoot"] = "Sync folder",
        ["Settings.Browse"] = "Browse…",
        ["Settings.Concurrency"] = "Concurrency (1-16)",
        ["Settings.Upload"] = "Upload limit (KB/s)",
        ["Settings.Download"] = "Download limit (KB/s)",
        ["Settings.LimitNote"] = "0 means unlimited. Changes take effect after \"Save & restart sync\".",
        ["Settings.Log"] = "Logging (enabled by default)",
        ["Settings.StructureOnly"] = "Read-only browse (structure only): sync the directory structure without downloading/uploading/deleting",
        ["Settings.StructureOnlyNote"] = "When checked: the remote directory structure is built locally, file content is NOT downloaded (shown as \"structure only\" in the list), local files are not uploaded, and nothing is deleted on either side.",
        ["Settings.OnConflict"] = "When both sides changed",
        ["Settings.OnConflictKeepBoth"] = "Keep both (default: save local copy as a copy)",
        ["Settings.OnConflictKeepLocal"] = "Keep local (overwrite remote)",
        ["Settings.OnConflictKeepRemote"] = "Keep remote (overwrite local)",
        ["Settings.OnConflictNote"] = "This is the unattended policy; regardless of choice, you can still decide per conflict in the sync page (select a \"conflict\" row first).",
        ["Settings.SpacesTitle"] = "Syncing spaces (one local folder each)",
        ["Settings.AddSpace"] = "Add space…",
        ["Settings.RemoveBinding"] = "Remove selected binding",
        ["Settings.RemoveBindingNote"] = "(only unbinds; local files are not deleted)",
        ["Settings.SpaceHint"] = "Space changes take effect after \"Save & restart sync\"; each space has its own state store (state-<space-shortcode>.db).",
        ["Settings.Save"] = "Save & restart sync",
        ["Settings.Logout"] = "Sign out",
        ["Settings.Language"] = "Language",
        ["Settings.LanguageNote"] = "Takes effect immediately and persists across restarts.",
        ["Settings.PathFormat"] = "Config file:{0}\nData dir:{1}",
        ["Settings.PathFallback"] = " (fallback:{0})",
        ["Settings.StatusRestarting"] = "Restarting sync with the new config…",
        ["Settings.StatusRestarted"] = "Sync restarted with the new config.",
        ["Settings.StatusAdded"] = "Added. Takes effect after \"Save & restart sync\".",
        ["Settings.StatusRemoved"] = "Removed (local files not deleted). Takes effect after \"Save & restart sync\".",
        ["Settings.StatusPickBinding"] = "Select a binding above first.",

        // ===== Space pick window =====
        ["SpacePick.Title"] = "Choose a space to sync",
        ["SpacePick.Hint"] = "Choose the space to sync into this local folder:",
        ["SpacePick.Next"] = "Next: choose folder",
        ["SpacePick.Cancel"] = "Cancel",

        // ===== Errors / prompts (C#) =====
        ["Error.AddSpaceList"] = "Failed to load space list:{0}",
        ["Error.NoSpaces"] = "This account has no visible spaces.",
        ["Error.DirBound"] = "This folder is already bound to another space:{0}. Choose a different folder per space.",
        ["Error.CannotRemovePrimary"] = "The first entry is the primary space (mapped to the server/sync-folder settings above) and cannot be removed here; remove other spaces first.",
        ["Error.BadBaseUrl"] = "Server address must be a full URL, e.g. http://10.14.37.187",
        ["Error.EmptyRoot"] = "Sync folder cannot be empty",
        ["Error.BadConcurrency"] = "Concurrency must be a number (1-16)",
        ["Error.BadRateLimit"] = "Rate limit must be a number (KB/s, 0 = unlimited)",
        ["Error.ConcurrencyRange"] = "Concurrency must be 1..16 (0 stalls the queue; too high saturates the machine and bandwidth)",
        ["Error.NegativeRate"] = "Rate limit cannot be negative (0 = unlimited)",
        ["Error.RestartFail"] = "Failed to restart sync: {0}",
        ["Error.LogoutConfirmMsg"] = "Signing out will revoke the server-side login and delete the local token (files are not deleted). Continue?",
        ["Error.LogoutTitle"] = "Sign out",
        ["Error.LogoutFail"] = "Sign out failed: {0}",

        // ===== MainWindow About / update (C#) =====
        ["Main.VersionFormat"] = "Version {0}",
        ["Main.TrayTitle"] = "NetDisk is still syncing",
        ["Main.TrayBody"] = "The window is hidden to the tray; to fully quit, right-click the tray icon and choose \"Quit\".",
        ["Main.NoSyncYet"] = "Sync hasn't started; sign in first, then upgrade.",
        ["Main.PickExeTitle"] = "Select the new NetDisk program (NetDisk.App.exe)",
        ["Main.PickExeFilter"] = "NetDisk program (*.exe)|*.exe",
        ["Main.UpdateConfirmTitle"] = "Check for updates",
        ["Main.UpdateConfirmStart"] = "Start upgrading now?",
        ["Main.UpdateCancel"] = "Cancelled (nothing was changed).",
        ["Main.Upgrading"] = "Upgrading (pausing sync → letting transfers finish → migrating state → launching the updater to swap)…",
        ["Main.UpgradeStarted"] = "Upgrade started:{0}",
        ["Main.UpgradeFailed"] = "This upgrade didn't succeed ({0}); sync has resumed.",

        // ===== Login page (C#) =====
        ["Login.PickSyncDir"] = "Choose sync folder",
        ["Login.FillAll"] = "Server address, username, password, and sync folder are all required.",
        ["Login.LoggingIn"] = "Signing in…",
        ["Login.LoginFail"] = "Sign-in failed: {0}",
        ["Login.Listing"] = "Reading the server file list…",
        ["Login.RootUnusable"] = "Sync folder is not usable: {0}",
        ["Login.EmptySpace"] = "The server has no files yet (an empty space; this is normal): the first sync uploads this folder's files.",
        ["Login.CountSummary"] = "The server has {0} files, {1} folders, about {2} total.",
        ["Login.SyncDirSummary"] = "Sync folder:{0}\n(The first sync will download the server's files to this folder and upload this folder's files to the server.)",
        ["Login.ConfirmFirstSync"] = "Confirm first sync",
        ["Login.ConfirmStartQuestion"] = "Start syncing now?",
        ["Login.Cancelled"] = "Cancelled: no sync started and nothing was changed.",
        ["Login.Starting"] = "Starting sync…",
        ["Login.StartFail"] = "Sync couldn't start (incomplete config or unavailable token); please sign in again.",
        ["Login.Unexpected"] = "An unexpected error occurred: {0}",

        // ===== Sync page (C#) =====
        ["Sync.AccountLine"] = "Account {0} @ {1}    Sync folder {2}",
        ["Sync.LogPathDefault"] = "Log file:{0} (enabled by default; turning it off stops logging for reproducing)",
        ["Sync.LogPathOn"] = "Log file:{0} (enabled)",
        ["Sync.LogPathOff"] = "Logging is off (previously logged at {0}; re-check to continue)",
        ["Sync.NoLogYet"] = "The log isn't generated yet:{0}\n(created after checking \"Logging\")",
        ["Sync.OpenLogFail"] = "Failed to open log: {0}\n{1}",
        ["Sync.NoSyncConflict"] = "Sync hasn't started; can't handle conflicts.",
        ["Sync.PickConflictFirst"] = "First **select a \"conflict\" row** in the list above, then click this button.",
        ["Sync.ResolvingChoice"] = "Handling {1} with \"{0}\"…",
        ["Sync.ResolvedChoice"] = "Handled {1} with \"{0}\".",
        ["Sync.ResolveUnhandlable"] = "{0} can't be handled (perhaps a leftover conflict from a previous run): please compare the local copy with the server version manually.",
        ["Sync.ChoiceKeepLocal"] = "Keep local",
        ["Sync.ChoiceKeepRemote"] = "Keep remote",
        ["Sync.ChoiceKeepBoth"] = "Keep both",
        ["Sync.NoFiles"] = "No files yet (the sync folder is empty, or the first reconciliation isn't done)",
        ["Sync.Summary"] = "{0} files: synced {1} · uploading {2} · downloading {3} · conflict {4} · error {5}",
        ["Sync.SummaryStructureOnly"] = " · structure-only (not pulled){0}",
        ["Sync.ConflictSelected"] = "Selected: {1} (\"{0}\") — conflict handling only applies to \"conflict\" rows",
        ["Sync.ConflictSelectedNow"] = "Conflict selected:{0}",
        ["Sync.ConflictSelectedNoLocal"] = "Conflict selected:{0} — the local copy location isn't recorded (perhaps from a previous run); click a button to see how to handle manually",
        ["Sync.Resume"] = "Resume sync",
        ["Sync.Resumed"] = "Sync resumed",
        ["Sync.PausedSummary"] = "Paused: no reconciliation or transfer (data unchanged). Click \"Resume sync\" to continue.",
        ["Sync.Paused"] = "Paused",
        ["Sync.PauseFail"] = "Failed to pause/resume: {0}",
        ["Sync.Syncing"] = "Reconciling…",
        ["Sync.Done"] = "Reconciliation complete",
        ["Sync.SyncFail"] = "Reconciliation failed: {0}",
        ["Sync.RootMissing"] = "The sync folder doesn't exist:{0}",
        ["Sync.RootUnset"] = "(not set)",
        ["Sync.OpenFolderFail"] = "Failed to open folder: {0}",
        ["State.Synced"] = "Synced",
        ["State.Uploading"] = "Uploading",
        ["State.Downloading"] = "Downloading",
        ["State.Conflict"] = "Conflict",
        ["State.RemoteDeleted"] = "Deleted remotely",
        ["State.SpaceRemoved"] = "Space removed",
        ["State.Permission"] = "Permission limited",
        ["State.Failed"] = "Failed",
        ["State.StructureOnly"] = "Structure only",

        // ===== Remote browse (C#) =====
        ["Remote.KindDir"] = "Folder",
        ["Remote.KindFile"] = "File",
        ["Remote.RootAt"] = "Location:/{0}",
        ["Remote.EmptyHere"] = "(no entries here)",
        ["Remote.Count"] = "{0} items",
        ["Remote.ListFail"] = "Failed to list directory:{0}",
        ["Remote.ParentGone"] = "The parent no longer exists (perhaps deleted by the other side) — back at the space root.",
        ["Remote.PickFile"] = "Select a file in the list above first.",
        ["Remote.IsDir"] = "You selected a folder — double-click it to enter.",
        ["Remote.Fetching"] = "Fetching {0} …",
        ["Remote.PreviewCopy"] = "Preview copy:{0} (temp folder; doesn't affect sync)",
        ["Remote.PreviewFail"] = "Failed to preview:{0}",

        // ===== Team spaces (C#) =====
        ["Spaces.NotLoggedIn"] = "Not signed in: enter the server address and sign in on the Sync page first.",
        ["Spaces.OpFail"] = "Operation failed",
        ["Spaces.Count"] = "{0} spaces (the server filters by permission)",
        ["Spaces.RoleMember"] = "You're a member of this space (whether you can act is decided by the server)",
        ["Spaces.Created"] = "Space created:{0}",
        ["Spaces.PickOne"] = "Select a space first.",
        ["Spaces.PickMember"] = "Select a member first.",
        ["Spaces.Invited"] = "Invited {0} ({1})",
        ["Spaces.RemovedMem"] = "Removed {0}",
        ["Spaces.Transferred"] = "Transferred to {0}",
        ["Spaces.Left"] = "Left this space",
        ["Spaces.DissolveConfirm"] = "To confirm this is intentional, type the space name as-is before dissolving.",
        ["Spaces.Dissolved"] = "Space dissolved:{0} (hard delete, irreversible)",
        ["Spaces.ShareCreatedPrefix"] = "Share created (no-login entry):",
        ["Spaces.SharePwdSet"] = " password set;",
        ["Spaces.ShareNoPwd"] = " no password;",
        ["Spaces.Share7d"] = " valid 7 days by default;",
        ["Spaces.ShareExpiresAt"] = " expires {0};",
        ["Spaces.ShareUnlimited"] = " unlimited downloads;",
        ["Spaces.ShareMaxDownloads"] = " max {0} downloads;",

        // ===== My shares (C#) =====
        ["Shares.NotLoggedIn"] = "Not signed in: sign in on the Sync page first.",
        ["Shares.Empty"] = "You haven't created any shares yet.",
        ["Shares.Count"] = "{0} shares",
        ["Shares.LoadFail"] = "Failed to load:{0}",
        ["Shares.RevokeConfirmBody"] = "Revoke share \"{0}\"?\nAfter revoking, the link becomes invalid immediately and irreversibly.",
        ["Shares.RevokeConfirmTitle"] = "Confirm revoke",
        ["Shares.RevokedName"] = "Revoked \"{0}\"",
        ["Shares.RevokeFail"] = "Failed to revoke:{0}",
        ["Shares.Copied"] = "Link copied to clipboard",
        ["Shares.OpenFail"] = "Failed to open link:{0}",

        // ===== Space pick window (C#) =====
        ["SpacePick.Bound"] = " — bound",
        ["SpacePick.KindFmt"] = " ({0})",
        ["SpacePick.IndependentDir"] = "Each space needs its own **separate** local folder.",
        ["SpacePick.AlreadyBound"] = "{0} bindings; already-bound spaces aren't listed again.",
        ["SpacePick.PickOne"] = "Select a space first.",

        // ===== Tray (C#) =====
        ["Tray.OpenMain"] = "Open main window",
        ["Tray.Quit"] = "Quit",

        // ===== Share converters (C#) =====
        ["Share.StatusValid"] = "Active",
        ["Share.Unlimited"] = "Unlimited",
    };
}
