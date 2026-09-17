# ============================================================================
# desktop/scripts/verify-publish.ps1 —— 单文件发布的本地验收(发布 → 单文件断言 → 首启行为 → 验活)。
#
# 取代原 verify-msi.ps1 / verify-upgrade.ps1:项目已**移除 WiX/MSI**,改为单文件 exe 分发。
# 验收重点从"MSI 能不能装/卸"变成"发布目录是不是**恰好一个** exe,且能跑起来"。
#
# 断言:
#   ① 发布成功,产物目录里**恰好一个** NetDisk.App.exe(单文件发布);
#   ② 把 exe 拷贝到临时目录(模拟"拷到别的机器")后,首次运行在安装目录生成
#      client.json 与 logs\client.log(配置跟着程序走 + 日志默认开);
#   ③ 新发布 exe 的 `--self-check` 退出码 0(这就是更新器的验活判据);
#   ④ 若设了 NETDISK_SIGN_CERT_THUMBPRINT 环境变量,则对 exe 做 Authenticode 签名。
#
# 用法:pwsh -File desktop/scripts/verify-publish.ps1   (需要 .NET 10 SDK 与 git)
# 退出码:0 = 全过;1 = 有断言失败。
# ============================================================================
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot          # desktop/
$publishDir = Join-Path $root 'publish-verify'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$script:bad = 0

function Check([string]$label, [bool]$ok, [string]$detail) {
    if ($ok) { Write-Host ("PASS  {0,-46} {1}" -f $label, $detail) }
    else { Write-Host ("FAIL  {0,-46} {1}" -f $label, $detail) -ForegroundColor Red; $script:bad++ }
}

# 版本号:1.0.<git 提交数>(单调递增、可复现)。CI 与本地必须同源,否则自动更新比版本会困惑。
$gitCount = (& git -C $root rev-list --count HEAD 2>$null)
if (-not $gitCount) { $gitCount = (Get-Date -Format 'yyMMddHHmm') }
$version = "1.0.$gitCount"
Write-Host "本次构建版本 = $version"

Write-Host "== 1) 发布单文件 exe =="
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
# 发布:框架依赖单文件(2026-09-17 决策,体积优先)。csproj 已默认
# SelfContained=false + PublishSingleFile=true,这里只传版本;不要传 --self-contained,
# 否则会覆盖回自包含(76MB)。
$build = & dotnet publish (Join-Path $root 'src\NetDisk.App\NetDisk.App.csproj') -c Release -r win-x64 `
    -p:PublishSingleFile=true -p:RuntimeIdentifier=win-x64 "-p:Version=$version" -o $publishDir 2>&1
$publishLog = $build -join "`n"
Check "发布成功且 0 错误" ($LASTEXITCODE -eq 0 -and ($publishLog -notmatch 'error|错误')) "exit=$LASTEXITCODE"

Write-Host "== 2) 单文件断言:发布目录恰好一个 exe =="
$exes = @(Get-ChildItem $publishDir -Filter '*.exe' -File -ErrorAction SilentlyContinue)
Check "恰好一个 exe(单文件发布)" ($exes.Count -eq 1) "exe 数=$($exes.Count)"
$others = @(Get-ChildItem $publishDir -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -ne '.exe' })
Check "没有附带文件(dll/pdb/config)" ($others.Count -eq 0) "附带=$($others.Count)"
$exe = $exes[0].FullName
Check "主程序就是 NetDisk.App.exe" ($exes[0].Name -eq 'NetDisk.App.exe') $exes[0].Name

Write-Host "== 3) 模拟『拷到别的机器』:复制到临时目录并首启 =="
$stage = Join-Path $env:TEMP ("netdisk-verify-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$destExe = Join-Path $stage 'NetDisk.App.exe'
Copy-Item $exe $destExe -Force
# 清掉任何旧配置,确保测的是"首次运行"
Remove-Item (Join-Path $stage 'client.json'), (Join-Path $stage 'logs') -Recurse -Force -ErrorAction SilentlyContinue
$headerDeadline = (Get-Date).AddSeconds(30)
$logFile = Join-Path $stage 'logs\client.log'
$proc = Start-Process -FilePath $destExe -PassThru
while ((Get-Date) -lt $headerDeadline -and -not (Test-Path $logFile)) { Start-Sleep -Milliseconds 250 }
Start-Sleep -Seconds 2
Check "首次运行在程序目录生成了 client.json" (Test-Path (Join-Path $stage 'client.json')) (Join-Path $stage 'client.json')
Check "首次运行生成了日志(logs\client.log)" (Test-Path $logFile) $logFile
$logText = if (Test-Path $logFile) { [IO.File]::ReadAllText($logFile) } else { '' }
Check "日志里有会话头(版本/路径/配置)" ($logText -match 'NetDisk 客户端启动' -and $logText -match '配置文件:') "匹配=$($logText -match 'NetDisk 客户端启动')"
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue

Write-Host "== 4) 验活:新发布 exe 的 --self-check =="
& $exe --self-check | Out-Null
Check "新版 --self-check 退出码 0" ($LASTEXITCODE -eq 0) "exit=$LASTEXITCODE"

Write-Host "== 5) 代码签名(仅当设了 NETDISK_SIGN_CERT_THUMBPRINT) =="
$thumb = $env:NETDISK_SIGN_CERT_THUMBPRINT
if ($thumb) {
    $cert = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
        Where-Object { $_.Thumbprint -eq $thumb }
    if ($cert) {
        Set-AuthenticodeSignature -FilePath $exe -Certificate $cert[0] | Out-Null
        $sig = Get-AuthenticodeSignature $exe
        Check "签名状态为 Valid" ($sig.Status -eq 'Valid') "status=$($sig.Status)"
    }
    else {
        Check "找到签名证书" $false "未找到指纹 $thumb 对应的证书"
    }
}
else {
    Write-Host "未设置 NETDISK_SIGN_CERT_THUMBPRINT:跳过签名(本地构建可接受;发布前请配置证书)"
}

Write-Host ""
if ($script:bad -eq 0) {
    Write-Host "发布验收全部通过:单文件 exe、可拷贝运行、首启生成配置与日志、self-check 通过" -ForegroundColor Green
    exit 0
}
Write-Host "$($script:bad) 项失败" -ForegroundColor Red
exit 1
