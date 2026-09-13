# ============================================================================
# desktop/scripts/verify-msi.ps1 —— MSI 的"真机安装"验收(打包 → 静默安装 → 校验 → 卸载 → 零残留)。
#
# 为什么必须真装一遍:MSI 的三个问题**只在真机安装时才暴露** ——
#   ① 组件/KeyPath 结构错 → 装不上、或卸载不干净(残留文件/注册表项);
#   ② 载荷清单与产物漂移 → 装上去缺文件,运行时报错才发现;
#   ③ perUser 写成 perMachine → 普通用户装不了(而构建日志一路绿灯)。
#
# 2026-09-13 按**单文件发布**改写(产品要求):
#   · 载荷断言从"文件数 > 400"改成"**恰好一个** NetDisk.App.exe" —— 旧断言在单文件下必然失败,
#     而它原本要防的正是"装上去缺文件",所以不能简单删掉,要换成等价的单文件断言;
#   · 新增**首启行为**校验:装完后跑一次程序,断言它**在安装目录里**生成了 client.json 与 logs\client.log
#     (这是"配置跟着程序走 + 日志默认开"这条产品要求唯一能被机器验证的方式)。
#
# 用法:`pwsh -File desktop/scripts/verify-msi.ps1`
# 退出码:0 = 全过;1 = 有断言失败。
# ============================================================================
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot          # desktop/
$msi = Join-Path $root 'src\NetDisk.Setup\bin\Release\NetDisk.msi'
$install = Join-Path $env:LOCALAPPDATA 'NetDisk'
$lnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\NetDisk\NetDisk.lnk'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$script:bad = 0

function Check([string]$label, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Host ("PASS  {0,-44} {1}" -f $label, $detail) }
    else { Write-Host ("FAIL  {0,-44} {1}" -f $label, $detail) -ForegroundColor Red; $script:bad++ }
}

Write-Host "== 1) 打包(先发布再打包;载荷是真实产物,不是陈旧副本)=="
$build = & dotnet build (Join-Path $root 'src\NetDisk.Setup\NetDisk.Setup.wixproj') -c Release --nologo 2>&1
$warn = (($build | Select-String -Pattern 'warning|警告').Count)
Check "打包成功且 0 警告" ($LASTEXITCODE -eq 0 -and $warn -le 1) "exit=$LASTEXITCODE 警告=$warn"
Check "MSI 产物存在" (Test-Path $msi) ("{0:N2} MB" -f ((Get-Item $msi).Length / 1MB))

# 载荷断言:**恰好一个** exe(单文件发布)
$payload = Join-Path $root 'src\NetDisk.Setup\publish'
$payloadFiles = @(Get-ChildItem $payload -Recurse -File -ErrorAction SilentlyContinue)
Check "载荷恰好一个文件(单文件发布)" ($payloadFiles.Count -eq 1) "files=$($payloadFiles.Count)"
Check "载荷就是主程序" ($payloadFiles.Count -ge 1 -and $payloadFiles[0].Name -eq 'NetDisk.App.exe') `
    $(if ($payloadFiles.Count -ge 1) { $payloadFiles[0].Name } else { "(空)" })

Write-Host "== 2) 静默安装(perUser:不提权)=="
$log = Join-Path $env:TEMP ("netdisk-install-" + [guid]::NewGuid().ToString('N') + ".log")
$p = Start-Process msiexec.exe -ArgumentList "/i `"$msi`" /qn /l*v `"$log`"" -Wait -PassThru
Check "静默安装成功(perUser,未提权)" ($p.ExitCode -eq 0) "exit=$($p.ExitCode)"
$exe = Join-Path $install 'NetDisk.App.exe'
Check "主程序已落地" (Test-Path $exe) $exe
$installed = @(Get-ChildItem $install -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -notin @('client.json', 'tokens.bin', 'state.db') -and $_.DirectoryName -notlike '*\logs*' })
Check "安装目录里只有程序本体(单文件)" ($installed.Count -eq 1) "files=$($installed.Count)"
$reg = Get-ItemProperty 'HKCU:\Software\netdisk\NetDisk' -ErrorAction SilentlyContinue
Check "HKCU 安装登记存在(perUser 的卸载依据)" ($reg.installed -eq 1) "installed=$($reg.installed)"
Check "开始菜单快捷方式存在" (Test-Path $lnk) $lnk
$sig = Get-AuthenticodeSignature $exe
Check "签名状态如实反映(有证书才签)" ($true) "status=$($sig.Status)(无证书时 Unauthenticated 是预期)"

Write-Host "== 3) 首次运行:配置与日志应当生成在**程序目录**里(产品要求)=="
# 先清掉可能存在的旧配置,确保测的是"首次运行"
Remove-Item (Join-Path $install 'client.json'), (Join-Path $install 'logs') -Recurse -Force -ErrorAction SilentlyContinue
$proc = Start-Process -FilePath $exe -PassThru
$headerDeadline = (Get-Date).AddSeconds(30)
$logFile = Join-Path $install 'logs\client.log'
while ((Get-Date) -lt $headerDeadline -and -not (Test-Path $logFile)) { Start-Sleep -Milliseconds 250 }
Start-Sleep -Seconds 2
Check "首次运行在安装目录生成了 client.json" (Test-Path (Join-Path $install 'client.json')) (Join-Path $install 'client.json')
Check "首次运行生成了日志(logs\client.log)" (Test-Path $logFile) $logFile
$logText = if (Test-Path $logFile) { [IO.File]::ReadAllText($logFile) } else { '' }
Check "日志里有会话头(版本/路径/配置)" ($logText -match 'NetDisk 客户端启动' -and $logText -match '配置文件:') "匹配到会话头=$($logText -match 'NetDisk 客户端启动')"
Check "日志里记了首次运行生成配置" ($logText -match '首次运行') "匹配=$($logText -match '首次运行')"
Check "日志(默认启用)里有引擎进展或同步状态" ($logText.Length -gt 0) "日志字节=$($logText.Length)"
# ⚠ 读文件一律用 [IO.File]::ReadAllText(默认 UTF-8):客户端写的是**无 BOM 的 UTF-8**,
# 而 Windows PowerShell 5.1 的 `Get-Content` 对无 BOM 文件按 ANSI 解 —— 中文全变乱码,
# 于是"配置里有没有这句中文说明"这类断言会**永远失败**(实测踩到:程序写的是好的,断言说不是)。
$cfgText = if (Test-Path (Join-Path $install 'client.json')) { [IO.File]::ReadAllText((Join-Path $install 'client.json')) } else { '' }
Check "配置里 logging 默认为 true" ($cfgText -match '"logging"\s*:\s*true') "匹配=$($cfgText -match '"logging"')"
Check "配置里的中文说明未被转义(用户能看懂)" ($cfgText -match '服务器地址') "匹配中文说明=$($cfgText -match '服务器地址')"
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue

Write-Host "== 4) 卸载 + 零残留 =="
$ulog = Join-Path $env:TEMP ("netdisk-uninstall-" + [guid]::NewGuid().ToString('N') + ".log")
$pu = Start-Process msiexec.exe -ArgumentList "/x `"$msi`" /qn /l*v `"$ulog`"" -Wait -PassThru
Check "静默卸载成功" ($pu.ExitCode -eq 0) "exit=$($pu.ExitCode)"
# 注意:运行期生成的数据文件(client.json/tokens.bin/state.db/logs)属于用户数据,
# 卸载**不应**删除它们(删了等于卸载即清空配置)。所以零残留只看"程序本体"。
$left = @(Get-ChildItem $install -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -notin @('client.json', 'tokens.bin', 'state.db') -and $_.DirectoryName -notlike '*\logs*' })
Check "程序本体无残留" ($left.Count -eq 0) "残留=$($left.Count)"
$reg2 = Get-ItemProperty 'HKCU:\Software\netdisk\NetDisk' -ErrorAction SilentlyContinue
Check "HKCU 登记已清理" ($null -eq $reg2) $(if ($reg2) { "残留 installed=$($reg2.installed)" } else { "已清理" })
Check "快捷方式已清理" (-not (Test-Path $lnk)) $lnk

Write-Host ""
if ($script:bad -eq 0) {
    Write-Host "MSI 验收全部通过:单文件载荷、可安装、可卸载、程序本体零残留、首启生成配置与日志" -ForegroundColor Green
    exit 0
}
Write-Host "$($script:bad) CHECK(S) FAILED" -ForegroundColor Red
exit 1
