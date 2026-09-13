# ============================================================================
# verify-upgrade.ps1 —— 自动更新的**真机升级演练**(DE-D-19 / 目标 ③ 的"含真机升级演练")
#
# 与 verify-msi.ps1 的分工:
#   · verify-msi.ps1      :单版本能不能装、能不能卸、首启行为对不对;
#   · verify-upgrade.ps1  :**从旧版升到新版**走的是客户端自己的更新器
#                           (`NetDisk.App.exe --apply-update`),以及升级失败时不留半装状态。
#
# 为什么必须真机演练:
#   升级链路上每一步都只有在真实安装里才成立 —— 更新器是**临时目录里的客户端副本**,
#   它去跑 msiexec 覆盖安装目录里的 exe(不是覆盖自己);"新版起不来就回滚"要用真实的
#   `--self-check` 回答。检查器只能证明协议决策(见 UpdateCheck ⑪~⑱),证明不了"真的装上了"。
#
# 断言(每条都对应一个真实的坑):
#   ① 升级后 exe 的 FileVersion 就是新版 —— MSI 会因"文件版本不更新"静默保留旧 exe,
#      表现是"安装成功、用户跑的还是旧程序"(verify-msi 里踩到过,这里在升级路径上再钉一次);
#   ② 升级后**只有一个** NetDisk 产品注册 —— 叠加注册会导致卸载只摘一层;
#   ③ **用户数据必须还在**(client.json / logs / tokens.bin / state.db):升级不是重置;
#   ④ 升级后的客户端 `--self-check` 退出码 0(这就是更新器的验活判据);
#   ⑤ 用一个**不存在的安装包**再跑一次更新器 → 退出码 2(安装失败),且**当前版本仍然可用**
#      (不留半装状态)。
#
# 用法:pwsh -File desktop/scripts/verify-upgrade.ps1   (需要 git;perUser 安装,不需要提权)
# ============================================================================
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot          # desktop/
$msi = Join-Path $root 'src\NetDisk.Setup\bin\Release\NetDisk.msi'
$msiV1 = Join-Path $env:TEMP 'netdisk-v1.msi'
$install = Join-Path $env:LOCALAPPDATA 'NetDisk'
$exe = Join-Path $install 'NetDisk.App.exe'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$script:bad = 0

function Check([string]$label, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Host ("PASS  {0,-46} {1}" -f $label, $detail) }
    else { Write-Host ("FAIL  {0,-46} {1}" -f $label, $detail) -ForegroundColor Red; $script:bad++ }
}

# 枚举已安装的 NetDisk 产品(返回 ProductCode)。
#
# ⚠ 这里换过两次实现,踩到的坑值得留着:
#   ① 最初用注册表 `HKCU:\...\Installer\UserData\<SID>\Products` —— **本机那个键根本不存在**,
#      枚举永远返回空:清基线变成空操作,而"基线干净"断言**永远真空通过**。
#      实测后果:升级演练里残留的新版本挡住了要装的旧版(MSI 拒绝降级),演练从第一步就错。
#   ② 改用 Windows Installer COM 的 `Installer.Products` —— **perUser 安装的产品不在里面**
#      (它返回 0 项)。perUser 是自动更新免 UAC 的前提,恰恰最需要能被枚举到。
#   ③ 最终用 `Get-Package -ProviderName msi`(PackageManagement 的 msi 提供程序):
#      它列出了 Name=NetDisk / Version=1.0.160,ProductCode 就是 FastPackageReference。
function Get-NetDiskProductCodes {
    @(Get-Package -ProviderName msi -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like '*NetDisk*' } |
        ForEach-Object { "$($_.FastPackageReference)" })
}
function Uninstall-AllNetDisk {
    foreach ($code in (Get-NetDiskProductCodes)) {
        Start-Process msiexec.exe -ArgumentList "/x $code /qn" -Wait | Out-Null
    }
}
function Build-Msi([string]$version, [string]$out) {
    $build = & dotnet build (Join-Path $root 'src\NetDisk.Setup\NetDisk.Setup.wixproj') -c Release --nologo "-p:ProductVersion=$version" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "打包失败(version=$version):`n$build" }
    Copy-Item $msi $out -Force
}
function Install-Msi([string]$path) {
    $log = Join-Path $env:TEMP ("netdisk-upgrade-" + [guid]::NewGuid().ToString('N') + ".log")
    $p = Start-Process msiexec.exe -ArgumentList "/i `"$path`" /qn /l*v `"$log`"" -Wait -PassThru
    return $p.ExitCode
}

Write-Host "== 0) 清基线:先卸载任何已注册的 NetDisk 产品 =="
Uninstall-AllNetDisk
Check "基线干净(没有已注册的 NetDisk 产品)" ((Get-NetDiskProductCodes).Count -eq 0)

$gitCount = (& git -C $root rev-list --count HEAD 2>$null)
if (-not $gitCount) { throw "拿不到 git 提交数(升级演练需要两个**不同**的版本号)" }
$v1 = "1.0.$gitCount"
$v2 = "1.0.$([int]$gitCount + 1)"
Write-Host "旧版 = $v1 ; 新版 = $v2"

Write-Host "== 1) 造旧版安装包并安装(模拟「用户机器上已装了旧版」)=="
Build-Msi $v1 $msiV1
Check "旧版安装成功" ((Install-Msi $msiV1) -eq 0)
$ver1 = if (Test-Path $exe) { (Get-Item $exe).VersionInfo.FileVersion } else { '' }
Check "装上去的是旧版" ($ver1 -like "*$v1*") "实际 '$ver1'"

Write-Host "== 2) 造用户数据(升级**不得**清掉它们)=="
# 跑一次客户端会生成 client.json / logs(与 verify-msi 的首启检查同一机制),
# 另外手工放一个 tokens.bin 与 state.db 占位:升级必须原样保留。
$cfgPath = Join-Path $install 'client.json'
$tokenPath = Join-Path $install 'tokens.bin'
$statePath = Join-Path $install 'state.db'
& $exe --self-check | Out-Null
Set-Content -Path $tokenPath -Value 'seeded-token-ciphertext' -NoNewline
Set-Content -Path $statePath -Value 'seeded-state' -NoNewline
$cfgBefore = if (Test-Path $cfgPath) { Get-Content $cfgPath -Raw } else { '' }
Check "旧版已生成配置(升级前)" (Test-Path $cfgPath) $cfgPath
Check "旧版 --self-check 通过(升级前的基线)" ($LASTEXITCODE -eq 0) "exit=$LASTEXITCODE"

Write-Host "== 3) 用**客户端自带的更新器**升级到新版 =="
Build-Msi $v2 (Join-Path $env:TEMP 'netdisk-v2.msi')
$msiV2 = Join-Path $env:TEMP 'netdisk-v2.msi'
# 更新器 = 客户端自己的**临时副本**(单文件发布:不能多一个 updater.exe)
$stage = Join-Path $env:TEMP ("netdisk-updater-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$updater = Join-Path $stage 'NetDisk.App.exe'
Copy-Item $exe $updater -Force
$args = "--apply-update --msi `"$msiV2`" --client `"$exe`" --previous-msi `"$msiV1`" --no-relaunch"
# **限时**:更新器"卡住不退出"比"报错"糟糕得多(演练里实测踩到过:UI 线程同步等待异步 → 死锁,
# 看起来像"MSI 装得慢",实际永远等不到)。等不到就杀掉并如实报失败。
#
# 用 ProcessStartInfo 而不是 `Start-Process -PassThru`:后者返回的对象在**不 -Wait** 时拿不到
# ExitCode(实测 `exit=` 是空的,于是"升级成功"这条断言永远失败)——
# 而我们要的正是"限时等待 + 拿退出码",两者必须同时成立。
function Invoke-WithTimeout([string]$exe, [string]$arguments, [int]$timeoutMs) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = $arguments
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $proc = [System.Diagnostics.Process]::Start($psi)
    if (-not $proc.WaitForExit($timeoutMs)) {
        try { $proc.Kill($true) } catch { }
        return @{ TimedOut = $true; ExitCode = $null }
    }
    return @{ TimedOut = $false; ExitCode = $proc.ExitCode }
}
$up = Invoke-WithTimeout $updater $args 300000
Check "更新器在 5 分钟内退出(不许挂起)" (-not $up.TimedOut) $(if ($up.TimedOut) { "超时未退出(已强杀)" } else { "exit=$($up.ExitCode)" })
if (-not $up.TimedOut) {
    Check "更新器退出码 0(升级成功)" ($up.ExitCode -eq 0) "exit=$($up.ExitCode)"
}

$ver2 = if (Test-Path $exe) { (Get-Item $exe).VersionInfo.FileVersion } else { '' }
Check "升级后 exe 就是**新版**" ($ver2 -like "*$v2*") "期望含 $v2,实际 '$ver2'"
Check "只注册了**一个** NetDisk 产品(没有叠加安装)" ((Get-NetDiskProductCodes).Count -eq 1) `
    "注册数=$((Get-NetDiskProductCodes).Count)"

Write-Host "== 4) 升级不得动用户数据 =="
Check "配置仍在" (Test-Path $cfgPath) $cfgPath
$cfgAfter = if (Test-Path $cfgPath) { Get-Content $cfgPath -Raw } else { '' }
Check "配置内容未被重置(逐字节一致)" ($cfgAfter -eq $cfgBefore) `
    "len before=$($cfgBefore.Length) after=$($cfgAfter.Length)"
Check "tokens.bin 仍在(不会被升级清掉)" (Test-Path $tokenPath)
Check "state.db 仍在(状态库不是重置)" (Test-Path $statePath)
Check "日志仍在(升级过程有据可查)" (Test-Path (Join-Path $install 'logs\client.log'))

Write-Host "== 5) 升级后的客户端能活(更新器的验活判据)=="
& $exe --self-check | Out-Null
Check "新版 --self-check 退出码 0" ($LASTEXITCODE -eq 0) "exit=$LASTEXITCODE"

Write-Host "== 6) 升级**失败**路径:不留半装状态 =="
$badMsi = Join-Path $env:TEMP 'netdisk-does-not-exist.msi'
Remove-Item $badMsi -ErrorAction SilentlyContinue
$argsBad = "--apply-update --msi `"$badMsi`" --client `"$exe`" --previous-msi `"$msiV1`" --no-relaunch"
$upBad = Invoke-WithTimeout $updater $argsBad 300000
Check "失败路径的更新器也要限时退出" (-not $upBad.TimedOut) $(if ($upBad.TimedOut) { "超时未退出(已强杀)" } else { "exit=$($upBad.ExitCode)" })
if (-not $upBad.TimedOut) {
    Check "安装包不存在时更新器报「安装失败」(退出码 2)" ($upBad.ExitCode -eq 2) "exit=$($upBad.ExitCode)"
}
$ver3 = if (Test-Path $exe) { (Get-Item $exe).VersionInfo.FileVersion } else { '' }
Check "失败后当前版本仍然是可用的新版(旧版仍在位)" ($ver3 -like "*$v2*") "实际 '$ver3'"
& $exe --self-check | Out-Null
Check "失败后客户端仍能启动(--self-check 0)" ($LASTEXITCODE -eq 0) "exit=$LASTEXITCODE"

Write-Host "== 7) 收尾:卸载 + 清理临时文件 =="
Uninstall-AllNetDisk
Check "卸载后没有 NetDisk 产品注册" ((Get-NetDiskProductCodes).Count -eq 0)
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $msiV1, $msiV2 -Force -ErrorAction SilentlyContinue
# 用户数据是刻意的:卸载不清数据(与 verify-msi 同一口径),这里手工清掉测试残留
Remove-Item $tokenPath, $statePath -Force -ErrorAction SilentlyContinue

Write-Host ""
if ($script:bad -eq 0) {
    Write-Host "升级演练全部通过:旧版安装 → 更新器升级 → 用户数据保留 → 验活通过 → 失败路径不留半装状态" -ForegroundColor Green
    exit 0
}
Write-Host "$($script:bad) 项失败" -ForegroundColor Red
exit 1
