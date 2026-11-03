<#
.SYNOPSIS
    生成发版用的 update.json、SHA256 清单与 GitHub Release 正文。

.DESCRIPTION
    发版需要四样东西：版本号、下载地址、SHA256、更新说明。
    这些全部从 tag 与 docs/CHANGELOG.md 推出来，人不填任何一项——
    手工在发布页填的东西用几次就会跟实际产物对不上。

    这个脚本被 .github/workflows/release.yml 调用，也可以在本机先预演：

        powershell -File scripts\make-release-manifest.ps1 -Tag v1.12.0 -Repository owner/repo

    跑之前需要先有 dist\IEC104-Server.exe（用 build.bat gui 打，再改名复制过来）。

.NOTES
    本文件必须以 **UTF-8 with BOM** 保存。
    Windows PowerShell 5.1 读无 BOM 的脚本时按 ANSI（简体中文环境是 GBK）解码，
    中文注释和中文字符串会被解成乱码，轻则提示乱码，重则"字符串缺少终止符"直接语法错误。
    pwsh 7 不受影响，但本机没装 pwsh 的时候，BOM 是唯一能让它跑起来的东西。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Tag,

    [Parameter(Mandatory = $true)]
    [string]$Repository,

    [string]$DistDir = 'dist',

    [string]$ChangelogPath = 'docs/CHANGELOG.md'
)

$ErrorActionPreference = 'Stop'

$exe = Join-Path $DistDir 'IEC104-Server.exe'
if (-not (Test-Path $exe)) {
    throw "找不到 $exe。先用 build.bat gui 打出来，再复制/改名到 $DistDir。"
}
if (-not (Test-Path $ChangelogPath)) {
    throw "找不到 $ChangelogPath。更新说明是发版正文的唯一来源，不能缺。"
}

$version = $Tag.TrimStart('v')
$size = (Get-Item $exe).Length
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()

# ── SHA256 清单：<hash>  <文件名>，与 sha256sum 的格式一致 ──
$shaPath = Join-Path $DistDir 'IEC104-Server.exe.sha256'
"$hash  IEC104-Server.exe" | Set-Content $shaPath -Encoding ascii -NoNewline

# ── 从 CHANGELOG 抽出这一版 ──
# 格式：## <版本号> <标题> / YYYY-MM-DD / - 条目
$lines = Get-Content $ChangelogPath -Encoding UTF8
$title = ''
$date = ''
$notes = @()
$hit = $false

foreach ($line in $lines) {
    if ($line -match '^##\s+(\d+\.\d+\.\d+)\b\s*(.*)$') {
        if ($hit) { break }                       # 下一段开始，本段结束
        if ($matches[1] -eq $version) {
            $hit = $true
            $title = $matches[2].Trim()
            continue
        }
    }
    elseif ($hit) {
        if ($line -match '^-\s+(.*)$') {
            $notes += $matches[1].Trim()
        }
        elseif ($line.Trim() -ne '' -and $date -eq '') {
            $date = $line.Trim()
        }
    }
}

if (-not $hit) {
    throw "$ChangelogPath 里没有 $version 这一段。先补更新说明，再打标签。"
}
if ($notes.Count -eq 0) {
    throw "$version 那一段没有条目（以 - 开头的行），Release 正文会是空的。"
}

# ── update.json：客户端 UpdateService 读的就是这些字段 ──
$assetUrl = "https://github.com/$Repository/releases/download/$Tag/IEC104-Server.exe"

$payload = [ordered]@{
    version = $version
    tag     = $Tag
    date    = $date
    title   = $title
    notes   = $notes
    asset   = [ordered]@{
        name   = 'IEC104-Server.exe'
        url    = $assetUrl
        size   = $size
        sha256 = $hash
    }
}

# 显式写「无 BOM 的 UTF-8」：Set-Content -Encoding utf8 在 PowerShell 5.1 会带 BOM、
# 在 pwsh 7 不带。同一段脚本在两个版本下产出不同文件，客户端解析就会时好时坏。
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$json = $payload | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText((Join-Path $PWD (Join-Path $DistDir 'update.json')), $json, $utf8NoBom)

# ── Release 正文：人看的那份 ──
$body = @()
$body += "## $version $title"
$body += ''
$body += ($notes | ForEach-Object { "- $_" })
$body += ''
$body += '---'
$body += '下载 `IEC104-Server.exe` 双击即用（自包含单文件，不需要装 .NET）。'
$body += ''
$body += "SHA256：``$hash``"

[System.IO.File]::WriteAllText(
    (Join-Path $PWD (Join-Path $DistDir 'RELEASE_BODY.md')),
    ($body -join "`n"),
    $utf8NoBom)

Write-Output "version=$version date=$date notes=$($notes.Count) size=$size"
Write-Output "hash=$hash"
