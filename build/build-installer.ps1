# Builds artifacts/WhyLag-<version>-setup.exe with Inno Setup (https://jrsoftware.org/isinfo.php).
# Run build/publish.ps1 first.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version

$onPath = Get-Command iscc.exe -ErrorAction SilentlyContinue
$iscc = @(
    $(if ($onPath) { $onPath.Source }),
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup" }
if (-not (Test-Path (Join-Path $root "artifacts/WhyLag/WhyLag.exe"))) { throw "Run build/publish.ps1 first." }

& $iscc "/DAppVersion=$version" (Join-Path $root "build/installer/whylag.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
Write-Host "Created $(Join-Path $root "artifacts/WhyLag-$version-setup.exe")"
