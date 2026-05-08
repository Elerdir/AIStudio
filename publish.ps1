##############################################################################
#  AI Studio – build + package skript
#  Použití:
#    .\publish.ps1                   # výchozí verze z projektu
#    .\publish.ps1 -Version "0.2.0"  # přepíše verzi v .iss
#    .\publish.ps1 -SkipInstaller    # jen dotnet publish, bez ISCC
##############################################################################
[CmdletBinding()]
param(
    [string]$Version      = "",
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# ── Verze z csproj pokud nebyla zadána ───────────────────────────────────────
if (-not $Version) {
    $csproj = [xml](Get-Content "AIStudio.App\AIStudio.App.csproj")
    $Version = $csproj.Project.PropertyGroup.Version
    if (-not $Version) { $Version = "0.1.0" }
}
Write-Host "==> Verze: $Version" -ForegroundColor Cyan

# ── dotnet publish ────────────────────────────────────────────────────────────
$publishDir = "publish\win-x64"
Write-Host "==> dotnet publish → $publishDir" -ForegroundColor Cyan

dotnet publish AIStudio.App/AIStudio.App.csproj `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publishDir `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=true `
    -p:Version=$Version

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish selhalo (exit $LASTEXITCODE)"
    exit $LASTEXITCODE
}

Write-Host "==> Publish OK" -ForegroundColor Green

if ($SkipInstaller) {
    Write-Host "==> SkipInstaller – přeskočeno ISCC" -ForegroundColor Yellow
    exit 0
}

# ── Najdi ISCC (Inno Setup compiler) ─────────────────────────────────────────
$isccPaths = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
)

$iscc = $isccPaths | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Warning "Inno Setup 6 nebyl nalezen – installer nebude sestaven."
    Write-Warning "Instaluj z https://jrsoftware.org/isdl.php nebo spusť s -SkipInstaller"
    exit 0
}

# ── Aktualizuj verzi v .iss souboru ──────────────────────────────────────────
$issFile    = "installer\AIStudio.iss"
$issContent = Get-Content $issFile -Raw
$issContent = $issContent -replace '#define AppVersion\s+"[^"]+"', "#define AppVersion      `"$Version`""
Set-Content $issFile $issContent -NoNewline -Encoding utf8

Write-Host "==> Verze v .iss nastavena na $Version" -ForegroundColor Cyan

# ── Spusť ISCC ───────────────────────────────────────────────────────────────
Write-Host "==> Sestavuji installer pomocí ISCC…" -ForegroundColor Cyan
& $iscc "installer\AIStudio.iss"

if ($LASTEXITCODE -ne 0) {
    Write-Error "ISCC selhalo (exit $LASTEXITCODE)"
    exit $LASTEXITCODE
}

$installer = "dist\AIStudio-Setup-$Version.exe"
if (Test-Path $installer) {
    $size = [math]::Round((Get-Item $installer).Length / 1MB, 1)
    Write-Host "==> Installer OK: $installer ($size MB)" -ForegroundColor Green
} else {
    Write-Host "==> Installer sestaven (hledej v dist\)" -ForegroundColor Green
}
