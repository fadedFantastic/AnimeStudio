<#
.SYNOPSIS
    一键生成绝区零(ZZZ)全量资源清单，用于定位某个资源在哪个 .blk 里。

.DESCRIPTION
    脚本会依次完成：
      1. 定位游戏 StreamingAssets\Blocks 目录
      2. 编译 AnimeStudio.CLI（已编译且无源码改动则跳过）
      3. 下载 Z3-Asset-Map 路径字典，把 container 的 ulong hash 还原成可读路径
      4. 调用 CLI 扫描全部 .blk，生成 AssetMap（资源清单）+ CABMap（依赖表）
      5. 把清单转成 TSV 索引，供 Find-ZzzAsset.ps1 秒级查询

    产物（默认在 <仓库>\zzz-map\ 下）：
      out\zzz_assets.json   资源清单原文（Name/Container/Source/Offset/PathID/Type/Hash）
      out\zzz_assets.tsv    同上的制表符索引，查询用
      Maps\zzz_assets.bin   CABMap，之后单独解包某个 blk 时能自动带上依赖
      Maps\Z3-AssetIndex-Eleiyas.json  路径字典
      build.log             CLI 完整日志

.PARAMETER GameDir
    ZZZ 的 StreamingAssets 目录。省略则按常见安装位置自动探测。

.PARAMETER WorkDir
    工作目录，默认 <仓库>\zzz-map。CLI 会在这里读写 Maps 子目录。

.PARAMETER MapName
    清单文件名（不含扩展名），默认 zzz_assets。

.PARAMETER MapType
    清单格式，可选 JSON / XML / MessagePack，或用逗号组合（如 'JSON,MessagePack'）。
    默认 JSON。MessagePack 生成的 .map 可以用 GUI 的 Maps → Open Asset Browser 打开。

.PARAMETER Full
    生成完整清单（记录 bundle 内每一个对象，含 Transform 等）。
    默认为 Minimal 模式，只记录可导出类型，清单小很多、扫描也更快。

.PARAMETER WithHash
    为每条资源计算内容 hash。默认不算 —— 它要把每个对象的完整字节读一遍，
    实测拖慢约 12%，而且只有做版本间内容比对时才用得上。

.PARAMETER ReadAhead
    后台预读的文件数。机械硬盘上是负优化（预读线程和主线程抢磁头），默认 0。
    资源放在 SSD 上时可以试 4~8。

.PARAMETER WholeStreamingAssets
    扫描整个 StreamingAssets 而不只是 Blocks 子目录。会顺带处理 Audio 等无关文件，更慢。

.PARAMETER PathDictFile
    手动指定已下好的 Z3 路径字典 json，跳过联网下载。

.PARAMETER SkipPathDict
    完全跳过路径字典。container 会保持数字 hash，但资源 Name 仍然可读。

.PARAMETER Rebuild
    强制重新编译 CLI。

.PARAMETER Tfm
    编译目标框架，默认按已安装 SDK 自动选 net10.0-windows 或 net9.0-windows。

.PARAMETER UnityVersion
    强制指定 Unity 版本，仅在日志报版本缺失时才需要。

.EXAMPLE
    .\tools\Build-ZzzAssetMap.ps1

.EXAMPLE
    .\tools\Build-ZzzAssetMap.ps1 -MapType 'JSON,MessagePack' -Full
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [string]$WorkDir,
    [string]$MapName = 'zzz_assets',
    [string]$MapType = 'JSON',
    [switch]$Full,
    [switch]$WithHash,
    [int]$ReadAhead = 0,
    [switch]$WholeStreamingAssets,
    [string]$PathDictFile,
    [switch]$SkipPathDict,
    [switch]$Rebuild,
    [string]$Tfm,
    [string]$UnityVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # 关掉 Invoke-WebRequest 的进度条，下载会快很多

# CLI 硬编码只认这个文件名（AnimeStudio.CLI/Program.cs 里的 ./Maps/Z3-AssetIndex-Eleiyas.json）
$PathDictName = 'Z3-AssetIndex-Eleiyas.json'
$PathDictRepo = 'Eleiyas/Z3-Asset-Map'

$RepoRoot = Split-Path -Parent $PSScriptRoot

function Write-Step([string]$Message) {
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Note([string]$Message) {
    Write-Host "    $Message" -ForegroundColor DarkGray
}

function Format-Size([long]$Bytes) {
    if ($Bytes -ge 1TB) { return '{0:N2} TB' -f ($Bytes / 1TB) }
    if ($Bytes -ge 1GB) { return '{0:N2} GB' -f ($Bytes / 1GB) }
    if ($Bytes -ge 1MB) { return '{0:N1} MB' -f ($Bytes / 1MB) }
    return '{0:N0} KB' -f ($Bytes / 1KB)
}

# ---------------------------------------------------------------- 1. 定位游戏目录

function Resolve-GameDir([string]$Hint) {
    if ($Hint) {
        if (-not (Test-Path -LiteralPath $Hint)) {
            throw "指定的游戏目录不存在：$Hint"
        }
        return (Resolve-Path -LiteralPath $Hint).Path
    }

    # 常见安装位置：官方启动器 / HoYoPlay / 国际服，逐盘符探测
    $suffixes = @(
        'miHoYo Launcher\games\ZenlessZoneZero Game\ZenlessZoneZero_Data\StreamingAssets',
        'HoYoPlay\games\ZenlessZoneZero Game\ZenlessZoneZero_Data\StreamingAssets',
        'ZenlessZoneZero Game\ZenlessZoneZero_Data\StreamingAssets'
    )
    $roots = @()
    foreach ($drive in [System.IO.DriveInfo]::GetDrives()) {
        if (-not $drive.IsReady) { continue }
        $r = $drive.RootDirectory.FullName
        $roots += @($r, (Join-Path $r 'Games'), (Join-Path $r 'Program Files'), (Join-Path $r 'Program Files (x86)'))
    }
    foreach ($root in $roots) {
        foreach ($suffix in $suffixes) {
            $candidate = Join-Path $root $suffix
            if (Test-Path -LiteralPath $candidate) {
                return (Resolve-Path -LiteralPath $candidate).Path
            }
        }
    }
    throw "未能自动找到 ZZZ 的 StreamingAssets 目录，请用 -GameDir 手动指定。"
}

Write-Step '定位游戏资源目录'
$streamingAssets = Resolve-GameDir $GameDir
$scanDir = if ($WholeStreamingAssets) { $streamingAssets } else { Join-Path $streamingAssets 'Blocks' }
if (-not (Test-Path -LiteralPath $scanDir)) {
    throw "扫描目录不存在：$scanDir（若资源不在 Blocks 下，可加 -WholeStreamingAssets）"
}

$scanFiles = @(Get-ChildItem -LiteralPath $scanDir -File -Recurse -ErrorAction SilentlyContinue)
if ($scanFiles.Count -eq 0) { throw "扫描目录里没有文件：$scanDir" }
$scanBytes = [long](($scanFiles | Measure-Object -Property Length -Sum).Sum)
$blkCount = @($scanFiles | Where-Object { $_.Extension -eq '.blk' }).Count
Write-Note "扫描目录：$scanDir"
Write-Note ("文件数：{0:N0}（其中 .blk {1:N0} 个），总大小 {2}" -f $scanFiles.Count, $blkCount, (Format-Size $scanBytes))

# ---------------------------------------------------------------- 2. 编译 CLI

function Resolve-Tfm([string]$Hint) {
    if ($Hint) { return $Hint }
    $sdks = $null
    try {
        # dotnet 把一些诊断信息写到 stderr，这里不能让它变成终止错误
        $ErrorActionPreference = 'Continue'
        $sdks = & dotnet --list-sdks 2>$null
    } catch { }
    if (-not $sdks) {
        throw "未检测到 .NET SDK。请先安装 .NET 10（或 9）SDK：https://dotnet.microsoft.com/download"
    }
    $majors = $sdks | ForEach-Object { if ($_ -match '^(\d+)\.') { [int]$Matches[1] } } | Sort-Object -Unique
    if ($majors -contains 10) { return 'net10.0-windows' }
    if ($majors -contains 9) { return 'net9.0-windows' }
    throw "需要 .NET 9 或 10 SDK，当前已安装：$($majors -join ', ')"
}

Write-Step '准备 AnimeStudio.CLI'
$targetFramework = Resolve-Tfm $Tfm
$cliExe = Join-Path $RepoRoot "AnimeStudio.CLI\bin\Release\$targetFramework\AnimeStudio.CLI.exe"

# 幂等：exe 比所有 .cs 源文件新就不重编
$needBuild = $Rebuild -or -not (Test-Path -LiteralPath $cliExe)
if (-not $needBuild) {
    $exeTime = (Get-Item -LiteralPath $cliExe).LastWriteTimeUtc
    $newestSrc = Get-ChildItem -LiteralPath $RepoRoot -Filter *.cs -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj|\.git)\\' } |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($newestSrc -and $newestSrc.LastWriteTimeUtc -gt $exeTime) {
        Write-Note "检测到源码改动（$($newestSrc.Name)），重新编译"
        $needBuild = $true
    }
}

if ($needBuild) {
    Write-Note "编译中（$targetFramework），首次编译需要拉 NuGet 包，请耐心等待…"
    $savedEap = $ErrorActionPreference
    try {
        # MSBuild 会往 stderr 写还原信息，Stop 模式下会被当成终止错误
        $ErrorActionPreference = 'Continue'
        & dotnet build (Join-Path $RepoRoot 'AnimeStudio.CLI') -c Release -f $targetFramework --nologo -v minimal
    }
    finally { $ErrorActionPreference = $savedEap }
    if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码 $LASTEXITCODE" }
    if (-not (Test-Path -LiteralPath $cliExe)) { throw "编译完成但没找到 $cliExe" }
} else {
    Write-Note '已有可用的 CLI，跳过编译（加 -Rebuild 可强制重编）'
}
Write-Note "CLI：$cliExe"

# ---------------------------------------------------------------- 3. Minimal 开关

# CLI 从自己的 .dll.config 读 minimalAssetMap，Full 模式需要改成 False
$cliConfig = Join-Path (Split-Path -Parent $cliExe) 'AnimeStudio.CLI.dll.config'
if (Test-Path -LiteralPath $cliConfig) {
    $wantMinimal = if ($Full) { 'False' } else { 'True' }
    try {
        [xml]$cfg = Get-Content -LiteralPath $cliConfig -Raw
        $node = @($cfg.configuration.appSettings.add | Where-Object { $_.key -eq 'minimalAssetMap' })[0]
        if ($node -and $node.value -ne $wantMinimal) {
            $node.value = $wantMinimal
            $cfg.Save($cliConfig)
        }
    }
    catch { Write-Warning "改写 $cliConfig 失败，将沿用其中的现有设置：$($_.Exception.Message)" }
    Write-Note "清单范围：$(if ($Full) { '完整（记录全部对象）' } else { 'Minimal（只记录可导出类型）' })"
}

# ---------------------------------------------------------------- 4. 路径字典

if (-not $WorkDir) { $WorkDir = Join-Path $RepoRoot 'zzz-map' }
$mapsDir = Join-Path $WorkDir 'Maps'
$outDir = Join-Path $WorkDir 'out'
$null = New-Item -ItemType Directory -Path $mapsDir -Force
$null = New-Item -ItemType Directory -Path $outDir -Force
$dictPath = Join-Path $mapsDir $PathDictName

function Test-PathDict([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    if ((Get-Item -LiteralPath $Path).Length -lt 1KB) { return $false }
    # 期望形如 {"1234567890":"assets/xxx/yyy.prefab", ...}，只看开头几百字节
    $reader = [System.IO.StreamReader]::new($Path)
    try {
        $buffer = New-Object char[] 256
        $read = $reader.Read($buffer, 0, $buffer.Length)
        if ($read -le 0) { return $false }
        $head = (-join $buffer[0..($read - 1)]).TrimStart()
    }
    finally { $reader.Dispose() }
    return $head.StartsWith('{')
}

Write-Step '准备路径字典'
if ($SkipPathDict) {
    Write-Note '已跳过（-SkipPathDict）。Container 将保持数字 hash，Name 字段仍然可读。'
    if (Test-Path -LiteralPath $dictPath) { Remove-Item -LiteralPath $dictPath -Force }
}
elseif ($PathDictFile) {
    if (-not (Test-Path -LiteralPath $PathDictFile)) { throw "路径字典不存在：$PathDictFile" }
    Copy-Item -LiteralPath $PathDictFile -Destination $dictPath -Force
    Write-Note "已使用本地字典：$PathDictFile"
}
elseif (Test-PathDict $dictPath) {
    Write-Note "已存在，跳过下载：$dictPath（$(Format-Size (Get-Item -LiteralPath $dictPath).Length)）"
}
else {
    try {
        Write-Note "查询 github.com/$PathDictRepo …"
        $headers = @{ 'User-Agent' = 'AnimeStudio-ZzzAssetMap' }
        $listing = Invoke-RestMethod -Uri "https://api.github.com/repos/$PathDictRepo/contents/" -Headers $headers -TimeoutSec 30

        # 挑仓库里最大的那个 json，通常就是完整的 hash → path 字典
        $candidate = $listing |
            Where-Object { $_.type -eq 'file' -and $_.name -like '*.json' } |
            Sort-Object size -Descending | Select-Object -First 1
        if (-not $candidate) { throw "仓库根目录里没有 .json 文件" }

        Write-Note "下载 $($candidate.name)（$(Format-Size $candidate.size)）…"
        Invoke-WebRequest -Uri $candidate.download_url -OutFile $dictPath -Headers $headers -TimeoutSec 600
        Write-Note "已保存为 $dictPath"
    }
    catch {
        Write-Warning "路径字典下载失败：$($_.Exception.Message)"
        Write-Warning "清单仍会正常生成，只是 Container 会是数字 hash。"
        Write-Warning "可手动从 https://github.com/$PathDictRepo 下载后，用 -PathDictFile <文件> 重跑。"
        if (Test-Path -LiteralPath $dictPath) { Remove-Item -LiteralPath $dictPath -Force }
    }
}

# ---------------------------------------------------------------- 5. 扫描

Write-Step '扫描 blk 并生成清单'
# 实测机械硬盘上稳定在 ~77 MB/s（瓶颈是磁盘，不是 CPU），拿它估个大概
$etaMin = [math]::Round($scanBytes / 1MB / 77 / 60, 0)
Write-Note ("{0:N0} 个文件 / {1}，预计约 {2} 分钟。中途可以 Ctrl+C 中断。" -f $scanFiles.Count, (Format-Size $scanBytes), $etaMin)

$logPath = Join-Path $WorkDir 'build.log'
$cliArgs = @(
    '--game', 'ZZZ'
    '--map_op', 'Both'          # 一次扫描同时产出 AssetMap + CABMap
    '--map_type', $MapType
    '--map_name', $MapName
    '--read_ahead', $ReadAhead
)
# 只要清单的话，逐对象算 hash 纯属浪费：它会把每个对象的完整字节再读一遍
if (-not $WithHash) { $cliArgs += '--no_hash' }
if ($UnityVersion) { $cliArgs += @('--unity_version', $UnityVersion) }
$cliArgs += @($scanDir, $outDir)

Write-Note "命令：AnimeStudio.CLI.exe $($cliArgs -join ' ')"
Write-Note "完整日志：$logPath"
Write-Host ''

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$log = [System.IO.StreamWriter]::new($logPath, $false, [System.Text.UTF8Encoding]::new($false))
$errorCount = 0
$cliExit = 0
$lastReport = [datetime]::MinValue

# CLI 的 CWD 决定它去哪里找 Maps\Z3-AssetIndex-Eleiyas.json、往哪里写 CABMap
Push-Location -LiteralPath $WorkDir
$savedEap = $ErrorActionPreference
try {
    # CLI 会往 stderr 写东西，Stop 模式下 2>&1 会把它当成终止错误
    $ErrorActionPreference = 'Continue'

    & $cliExe @cliArgs 2>&1 | ForEach-Object {
        $line = [string]$_
        $log.WriteLine($line)

        if ($line -match '\[(\d+)/(\d+)\]') {
            $cur = [int]$Matches[1]; $tot = [int]$Matches[2]
            # 控制台每秒最多刷一次，免得刷屏拖慢速度
            if (([datetime]::UtcNow - $lastReport).TotalMilliseconds -ge 1000 -or $cur -eq $tot) {
                $lastReport = [datetime]::UtcNow
                $pct = if ($tot -gt 0) { [math]::Round($cur * 100 / $tot, 1) } else { 0 }
                $etaSec = if ($cur -gt 0) { [math]::Min($sw.Elapsed.TotalSeconds / $cur * ($tot - $cur), 86399) } else { 0 }
                $eta = [timespan]::FromSeconds($etaSec)
                Write-Host ("`r  [{0,5:N1}%] {1:N0}/{2:N0}  已用 {3:hh\:mm\:ss}  剩余约 {4:hh\:mm\:ss}   " -f $pct, $cur, $tot, $sw.Elapsed, $eta) -NoNewline
            }
        }
        elseif ($line -match '^\s*\[(Error|Warning)\]|Error while|was not build|was not loaded') {
            $errorCount++
            if ($errorCount -le 15) { Write-Host "`n  $line" -ForegroundColor DarkYellow }
            elseif ($errorCount -eq 16) { Write-Host "`n  （后续错误只写入日志，不再刷屏）" -ForegroundColor DarkYellow }
        }
        elseif ($line -match 'Building|Finished buidling|build successfully|Updated !!|Loaded !!') {
            Write-Host "`n  $line" -ForegroundColor Gray
        }
    }
    if ($null -ne $LASTEXITCODE) { $cliExit = $LASTEXITCODE }
}
finally {
    $ErrorActionPreference = $savedEap
    $log.Dispose()
    Pop-Location
}
Write-Host ''
$sw.Stop()
Write-Note ("扫描耗时 {0:hh\:mm\:ss}，日志中 {1:N0} 条错误/警告" -f $sw.Elapsed, $errorCount)

# ---------------------------------------------------------------- 6. 转 TSV 索引

$jsonPath = Join-Path $outDir "$MapName.json"
$tsvPath = Join-Path $outDir "$MapName.tsv"

# BuildBoth 里的异常只会被 CLI 打印出来、退出码仍是 0，所以这里按产物判断成败
$produced = @(@($jsonPath, (Join-Path $outDir "$MapName.map"), (Join-Path $outDir "$MapName.xml")) |
    Where-Object { Test-Path -LiteralPath $_ })
if ($produced.Count -eq 0) {
    throw "扫描结束但没有生成任何清单文件。请查看 $logPath（搜 'was not build' 或 'Error'）"
}

if ($MapType -match 'JSON' -and (Test-Path -LiteralPath $jsonPath)) {
    Write-Step '生成 TSV 索引'

    # 清单可能有几百 MB，ConvertFrom-Json 会吃光内存，这里用流式逐行状态机
    if (-not ('AnimeStudio.Tools.AssetMapIndexer' -as [type])) {
        Add-Type -Path (Join-Path $PSScriptRoot 'AssetMapIndexer.cs')
    }

    $rows = [AnimeStudio.Tools.AssetMapIndexer]::JsonToTsv($jsonPath, $tsvPath)
    Write-Note ("{0:N0} 条资源 → {1}（{2}）" -f $rows, $tsvPath, (Format-Size (Get-Item -LiteralPath $tsvPath).Length))
}

# ---------------------------------------------------------------- 7. 汇总

Write-Step '完成'
foreach ($f in @($jsonPath, $tsvPath, (Join-Path $outDir "$MapName.map"), (Join-Path $outDir "$MapName.xml"), (Join-Path $mapsDir "$MapName.bin"))) {
    if (Test-Path -LiteralPath $f) {
        Write-Host ("    {0,-12} {1}" -f (Format-Size (Get-Item -LiteralPath $f).Length), $f)
    }
}
Write-Host ''
Write-Host '  查询示例：' -ForegroundColor Cyan
Write-Host "    .\tools\Find-ZzzAsset.ps1 Anby"
Write-Host "    .\tools\Find-ZzzAsset.ps1 Anby -Type Texture2D"
Write-Host "    .\tools\Find-ZzzAsset.ps1 '\.wav$' -Type AudioClip -Top 50"
if (Test-Path -LiteralPath (Join-Path $outDir "$MapName.map")) {
    Write-Host ''
    Write-Host '  也可以用 GUI 打开：Maps → Open Asset Browser → 选 ' -ForegroundColor Cyan -NoNewline
    Write-Host "$MapName.map"
}

if ($cliExit -ne 0) {
    Write-Warning "CLI 退出码为 $cliExit，清单可能不完整，详见 $logPath"
}
