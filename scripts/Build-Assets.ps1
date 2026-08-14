[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$projectRoot = Split-Path -Parent $PSScriptRoot
$assetsDir = Join-Path $projectRoot 'assets'
$sourceSvg = Join-Path $assetsDir 'deepseek-whale.svg'
$sourceUrl = 'https://raw.githubusercontent.com/deepseek-ai/deepseek-harness/master/apps/web/public/favicon.svg'
[System.IO.Directory]::CreateDirectory($assetsDir) | Out-Null

if (-not (Test-Path -LiteralPath $sourceSvg -PathType Leaf)) {
    $client = New-Object System.Net.WebClient
    $client.Headers['User-Agent'] = 'DeepSeekHarnessManager-Build/1.0'
    try {
        $svgText = $client.DownloadString($sourceUrl)
    }
    finally {
        $client.Dispose()
    }
    [System.IO.File]::WriteAllText($sourceSvg, $svgText, [System.Text.UTF8Encoding]::new($false))
}

$browserCandidates = @(@(
    foreach ($commandName in @('chrome.exe', 'msedge.exe')) {
        $command = Get-Command $commandName -ErrorAction SilentlyContinue
        if ($null -ne $command) { $command.Source }
    }
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe"
    "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe"
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -Unique)
if ($browserCandidates.Count -eq 0) { throw 'Chrome or Edge is required once to render the official SVG icon.' }
$browser = $browserCandidates[0]

function Write-PngIco {
    param([string]$PngPath, [string]$IcoPath)
    $pngBytes = [System.IO.File]::ReadAllBytes($PngPath)
    $stream = [System.IO.File]::Open($IcoPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
    try {
        $writer = New-Object System.IO.BinaryWriter($stream)
        try {
            $writer.Write([uint16]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]1)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$pngBytes.Length)
            $writer.Write([uint32]22)
            $writer.Write($pngBytes)
        }
        finally { $writer.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Write-StatusImage {
    param([System.Drawing.Image]$Source, [string]$Name, [string]$DotColor = '')
    $pngPath = Join-Path $assetsDir "$Name.png"
    $icoPath = Join-Path $assetsDir "$Name.ico"
    $bitmap = New-Object System.Drawing.Bitmap(256, 256, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.DrawImage($Source, 8, 8, 240, 240)
        if (-not [string]::IsNullOrWhiteSpace($DotColor)) {
            $borderBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
            $dotBrush = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($DotColor))
            try {
                $graphics.FillEllipse($borderBrush, 180, 180, 68, 68)
                $graphics.FillEllipse($dotBrush, 188, 188, 52, 52)
            }
            finally {
                $borderBrush.Dispose()
                $dotBrush.Dispose()
            }
        }
        $bitmap.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
    Write-PngIco -PngPath $pngPath -IcoPath $icoPath
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("dsh-manager-icon-" + [Guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($tempRoot) | Out-Null
$tempHtml = Join-Path $tempRoot 'render.html'
$tempPng = Join-Path $tempRoot 'render.png'
$tempProfile = Join-Path $tempRoot 'profile'
try {
    $svg = [System.IO.File]::ReadAllText($sourceSvg)
    $html = @"
<!doctype html><html><head><meta charset="utf-8"><style>
html,body{width:100%;height:100%;margin:0;background:transparent;overflow:hidden}body{display:grid;place-items:center}svg{width:88vmin;height:88vmin;display:block}
</style></head><body>$svg</body></html>
"@
    [System.IO.File]::WriteAllText($tempHtml, $html, [System.Text.UTF8Encoding]::new($false))
    $arguments = @(
        '--headless=new', '--disable-gpu', '--disable-extensions', '--hide-scrollbars', '--no-first-run',
        '--default-background-color=00000000', '--window-size=512,512',
        "--user-data-dir=$tempProfile", "--screenshot=$tempPng", ([Uri]::new($tempHtml).AbsoluteUri)
    )
    & $browser @arguments | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $tempPng -PathType Leaf)) { throw "Browser SVG rendering failed with exit code $LASTEXITCODE." }
    $rendered = [System.Drawing.Image]::FromFile($tempPng)
    try {
        Write-StatusImage $rendered 'deepseek-whale'
        Write-StatusImage $rendered 'deepseek-whale-running' '#22C55E'
        Write-StatusImage $rendered 'deepseek-whale-starting' '#4D6BFE'
        Write-StatusImage $rendered 'deepseek-whale-stopped' '#9CA3AF'
        Write-StatusImage $rendered 'deepseek-whale-conflict' '#F59E0B'
        Write-StatusImage $rendered 'deepseek-whale-error' '#EF4444'
    }
    finally { $rendered.Dispose() }
}
finally {
    if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }
}

Get-ChildItem -LiteralPath $assetsDir -File | Select-Object Name, Length
