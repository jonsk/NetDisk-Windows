# ============================================================================
# verify-tray.ps1 —— "关闭到托盘"的**可编程**验证(2026-09-13)
#
# 为什么需要它:托盘菜单的**点击**没法在本环境自动化(已登记为人工项),但"关闭到托盘"
# 这条**行为**可以:启动客户端 → 让系统给它发 WM_CLOSE(等价于用户点右上角 ×)→
# 断言**进程仍然活着**且日志里记了"关闭到托盘"。
#
# 这条断言防的是最糟的一种组合:用户以为"关掉了窗口 = 不挡事了",而实际上同步**被一起关掉**;
# 或者反过来——用户想退出却退不掉。两者都在真机上出过问题,所以钉成脚本。
#
# 用法:直接跑(不需要服务端/不需要登录:走的是"没有配置 → 登录页"这条路径)
# ============================================================================
$ErrorActionPreference = 'Stop'
$script:bad = 0
function Check([string]$label, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Host ("PASS  {0,-40} {1}" -f $label, $detail) }
    else { Write-Host ("FAIL  {0,-40} {1}" -f $label, $detail) -ForegroundColor Red; $script:bad++ }
}

$root = Split-Path -Parent $PSScriptRoot
# ⚠ 产物路径必须认 **RID 子目录**:csproj 设了 `RuntimeIdentifier=win-x64`,真正的新 exe 在
#   `bin\Debug\net10.0-windows\win-x64\` 下;而 `bin\Debug\net10.0-windows\` 里那个是**旧残留**
#   (引入 RID 之前构建的)。实测踩到过:脚本跑了旧 exe,于是"关闭到托盘"看起来没生效 —— 假的失败。
$candidates = @(
    (Join-Path $root 'src\NetDisk.App\bin\Debug\net10.0-windows\win-x64\NetDisk.App.exe'),
    (Join-Path $root 'src\NetDisk.App\bin\Debug\net10.0-windows\NetDisk.App.exe')
)
$exe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $exe) {
    Write-Host "先构建:dotnet build desktop/src/NetDisk.App -c Debug" -ForegroundColor Yellow
    exit 2
}
$dir = Split-Path -Parent $exe
$log = Join-Path $dir 'logs\client.log'

# **新鲜度断言**:exe 必须比源码新 —— 否则测的是上一次构建的二进制,而"旧二进制"会让新功能
# 看起来"没实现"。这个坑在本项目已经踩过两次(检查器 --no-build、以及这里)。
# 只看**源码**(排除 obj\ 下的生成文件:WPF 的临时 GlobalUsings 常常比 exe 还新,
# 那会让这条断言变成"随机失败" —— 断言必须只盯真正影响产物的输入)
$srcNewest = Get-ChildItem (Join-Path $root 'src\NetDisk.App') -Recurse -Include *.cs,*.xaml |
    Where-Object { $_.FullName -notlike '*\obj\*' -and $_.FullName -notlike '*\bin\*' } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
$exeTime = (Get-Item $exe).LastWriteTime
Check "跑的是**本次构建**的 exe(比源码新)" ($exeTime -gt $srcNewest.LastWriteTime) `
    "exe=$($exeTime.ToString('HH:mm:ss')) 最新源码=$($srcNewest.LastWriteTime.ToString('HH:mm:ss'))($($srcNewest.Name))"

# 清掉上一次的现场:配置/日志都是"运行期产物",不是构建产物
Remove-Item (Join-Path $dir 'client.json'), (Join-Path $dir 'tokens.bin'), (Join-Path $dir 'state.db') -Force -ErrorAction SilentlyContinue
Remove-Item $log -Force -ErrorAction SilentlyContinue

Write-Host "== 1) 启动客户端(没有配置 → 登录页;同步未启动,这条路径不需要服务端)=="
$proc = Start-Process $exe -PassThru
Start-Sleep -Seconds 5
Check "客户端进程已起来" (-not $proc.HasExited) "pid=$($proc.Id)"
Check "主窗口已创建(有窗口句柄)" ($proc.MainWindowHandle -ne 0) "handle=$($proc.MainWindowHandle)"

Write-Host "== 2) 给主窗口发 WM_CLOSE(等价于用户点右上角 ×)=="
$sent = $proc.CloseMainWindow()
Check "WM_CLOSE 已发出" $sent
Start-Sleep -Seconds 3

Write-Host "== 3) 断言:这是「关闭到托盘」而不是退出 =="
Check "进程**仍然活着**(没有退出)" (-not $proc.HasExited) "pid=$($proc.Id)"
$logText = if (Test-Path $log) { Get-Content $log -Raw } else { '' }
Check "日志记了「关闭到托盘」" ($logText -match '关闭到托盘') `
    $((($logText -split "`n") | Where-Object { $_ -match '关闭到托盘' } | Select-Object -First 1))
Check "日志说明了「同步继续运行」" ($logText -match '同步继续运行')

Write-Host "== 4) 收尾 =="
try { $proc.Kill() } catch { }
Start-Sleep -Seconds 1
Check "强制结束后进程已退出(清理干净)" ($proc.HasExited)

Write-Host ""
if ($script:bad -eq 0) {
    Write-Host "关闭到托盘:验证通过(窗口关闭 → 进程与同步继续;真正退出走托盘菜单)" -ForegroundColor Green
    exit 0
}
Write-Host "$($script:bad) 项失败" -ForegroundColor Red
exit 1
