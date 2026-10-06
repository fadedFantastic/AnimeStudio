<#
.SYNOPSIS
    在 Build-ZzzAssetMap.ps1 生成的清单里查资源，告诉你它在哪个 .blk。

.DESCRIPTION
    直接对 TSV 索引做流式匹配，几百万条也是秒级返回，不会把清单读进内存。
    所有过滤条件都是正则（大小写不敏感），多个条件之间是「与」的关系。

.PARAMETER Pattern
    整行匹配的正则，最常用。会同时匹配资源名、容器路径、类型、blk 文件名。

.PARAMETER Name
    只匹配资源名（Name 列）。

.PARAMETER Container
    只匹配容器路径（Container 列，需要路径字典才可读）。

.PARAMETER Type
    只匹配 Unity 类型，如 Texture2D / Mesh / AudioClip / TextAsset / MonoBehaviour。

.PARAMETER Blk
    只匹配 blk 文件名，用来反查「某个 blk 里都有什么」。

.PARAMETER Top
    最多返回多少条，默认 100。用 -All 取消限制。

.PARAMETER All
    返回全部匹配项。

.PARAMETER Raw
    直接输出对象而不打印表格和统计，方便接管道。

.PARAMETER Csv
    把结果写到 csv 文件，而不是打印到屏幕。

.PARAMETER Index
    指定 TSV 索引路径，默认 <仓库>\zzz-map\out\zzz_assets.tsv。

.EXAMPLE
    .\tools\Find-ZzzAsset.ps1 Anby
    模糊搜所有跟 Anby 有关的资源

.EXAMPLE
    .\tools\Find-ZzzAsset.ps1 -Name '^Avatar_Female' -Type Mesh
    找名字以 Avatar_Female 开头的模型

.EXAMPLE
    .\tools\Find-ZzzAsset.ps1 -Blk '^2429662787\.blk$' -All
    列出某个 blk 里的全部资源

.EXAMPLE
    .\tools\Find-ZzzAsset.ps1 -Type AudioClip -Csv .\audio.csv -All
    把所有音频导成 csv
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Pattern,
    [string]$Name,
    [string]$Container,
    [string]$Type,
    [string]$Blk,
    [int]$Top = 100,
    [switch]$All,
    [switch]$Raw,
    [string]$Csv,
    [string]$Index
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot

if (-not ($Pattern -or $Name -or $Container -or $Type -or $Blk)) {
    throw "至少要给一个过滤条件。例：Find-ZzzAsset.ps1 Anby  或  Find-ZzzAsset.ps1 -Type Mesh"
}

# ---------------------------------------------------------------- 定位索引

if (-not $Index) { $Index = Join-Path $RepoRoot 'zzz-map\out\zzz_assets.tsv' }

if (-not (Test-Path -LiteralPath $Index)) {
    # 有清单但还没建索引时，就地补一次
    $json = [System.IO.Path]::ChangeExtension($Index, '.json')
    if (Test-Path -LiteralPath $json) {
        Write-Host "首次查询，正在从 $(Split-Path -Leaf $json) 生成索引…" -ForegroundColor DarkGray
        if (-not ('AnimeStudio.Tools.AssetMapIndexer' -as [type])) {
            Add-Type -Path (Join-Path $PSScriptRoot 'AssetMapIndexer.cs')
        }
        $n = [AnimeStudio.Tools.AssetMapIndexer]::JsonToTsv($json, $Index)
        Write-Host ("索引完成，{0:N0} 条资源" -f $n) -ForegroundColor DarkGray
    }
    else {
        throw "找不到清单索引：$Index`n请先运行： .\tools\Build-ZzzAssetMap.ps1"
    }
}

# ---------------------------------------------------------------- 匹配

$opts = [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor
        [System.Text.RegularExpressions.RegexOptions]::Compiled

function New-Re([string]$p) {
    if (-not $p) { return $null }
    return [regex]::new($p, $opts)
}

$rePattern   = New-Re $Pattern
$reName      = New-Re $Name
$reContainer = New-Re $Container
$reType      = New-Re $Type
$reBlk       = New-Re $Blk

# 整行预筛能跳过绝大多数行，省下拆列的开销。但列过滤器里的 ^ / $ 锚的是那一列，
# 拿来匹配整行会漏掉本该命中的行，所以带锚点的一律不参与预筛。
$prefilters = New-Object System.Collections.Generic.List[regex]
if ($rePattern) { $prefilters.Add($rePattern) }      # 本来就是整行语义
foreach ($p in @($Name, $Container, $Type, $Blk)) {
    if ($p -and $p -notmatch '[\^\$]') { $prefilters.Add((New-Re $p)) }
}

Update-TypeData -TypeName 'AnimeStudio.ZzzAsset' `
    -DefaultDisplayPropertySet Name, Type, Blk, Offset, Container -Force

$results = New-Object System.Collections.Generic.List[object]
$limit = if ($All -or $Csv) { [int]::MaxValue } else { $Top }
$scanned = 0
$matched = 0
$blks = New-Object System.Collections.Generic.HashSet[string]

$reader = [System.IO.StreamReader]::new($Index)
try {
    $null = $reader.ReadLine()   # 表头
    while ($null -ne ($line = $reader.ReadLine())) {
        $scanned++

        $ok = $true
        foreach ($re in $prefilters) {
            if (-not $re.IsMatch($line)) { $ok = $false; break }
        }
        if (-not $ok) { continue }

        # Name / Type / Blk / Offset / PathID / Container / Hash / Source
        $f = $line.Split("`t")
        if ($f.Length -lt 8) { continue }

        if ($reName      -and -not $reName.IsMatch($f[0]))      { continue }
        if ($reType      -and -not $reType.IsMatch($f[1]))      { continue }
        if ($reBlk       -and -not $reBlk.IsMatch($f[2]))       { continue }
        if ($reContainer -and -not $reContainer.IsMatch($f[5])) { continue }

        $matched++
        $null = $blks.Add($f[2])

        if ($results.Count -lt $limit) {
            $results.Add([PSCustomObject]@{
                PSTypeName = 'AnimeStudio.ZzzAsset'
                Name       = $f[0]
                Type       = $f[1]
                Blk        = $f[2]
                Offset     = $f[3]
                PathID     = $f[4]
                Container  = $f[5]
                Hash       = $f[6]
                Source     = $f[7]
            })
        }
    }
}
finally { $reader.Dispose() }

# ---------------------------------------------------------------- 输出

if ($matched -eq 0) {
    Write-Host "没有匹配（共扫描 $('{0:N0}' -f $scanned) 条）。" -ForegroundColor Yellow
    Write-Host "提示：清单默认是 Minimal 模式，只收录可导出类型。要找配置表之类请用 -Full 重建。" -ForegroundColor DarkGray
    return
}

if ($Csv) {
    $results | Select-Object Name, Type, Blk, Offset, PathID, Container, Hash, Source |
        Export-Csv -LiteralPath $Csv -NoTypeInformation -Encoding UTF8
    Write-Host ("已导出 {0:N0} 条到 {1}" -f $results.Count, $Csv) -ForegroundColor Green
    return
}

if ($Raw) { return $results }

# Write-Host 和输出流是两条通道，先把表格渲染掉，摘要才不会跑到表格前面
$results | Format-Table | Out-Host

Write-Host ''
Write-Host ("命中 {0:N0} 条，分布在 {1:N0} 个 blk（扫描 {2:N0} 条）" -f $matched, $blks.Count, $scanned) -ForegroundColor Green
if ($matched -gt $results.Count) {
    Write-Host ("只显示前 {0} 条，加 -All 看全部，或 -Csv <文件> 导出" -f $results.Count) -ForegroundColor DarkGray
}

# 顺手给出把这些 blk 解出来的命令
if ($blks.Count -le 5) {
    $cliExe = Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'AnimeStudio.CLI\bin\Release') `
        -Filter 'AnimeStudio.CLI.exe' -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($cliExe) {
        $sample = $results | Select-Object -First 1
        Write-Host ''
        Write-Host '  导出这个 blk：' -ForegroundColor Cyan
        Write-Host ("    & '{0}' --game ZZZ --types {1} '{2}' .\out" -f $cliExe.FullName, $sample.Type, $sample.Source)
    }
}
