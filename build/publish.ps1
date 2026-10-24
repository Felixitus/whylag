# Builds the self-contained release files (app, CLI, bundled PresentMon) into artifacts/WhyLag and a portable ZIP.
# No .NET installation is needed on the target PC.
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$out = Join-Path $artifacts "WhyLag"
$version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version

# Keep in sync with src/WhyLag.Collectors/PresentMonLocator.cs
$presentMonUrl = "https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/PresentMon-2.6.0-x64.exe"
$presentMonSha256 = "b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out, (Join-Path $out "tools") | Out-Null

# Each project is published into its own folder first; sharing one output folder makes the second publish
# remove files of the first.
foreach ($project in "WhyLag.App", "WhyLag.Cli") {
    $staging = Join-Path $artifacts "staging/$project"
    dotnet publish (Join-Path $root "src/$project") -c $Configuration -r $Runtime --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $staging
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $project" }
    Copy-Item (Join-Path $staging "*.exe") $out
}
Remove-Item (Join-Path $artifacts "staging") -Recurse -Force

# PresentMon (MIT, Intel) is bundled unmodified, verified against the official release checksum.
$cache = Join-Path $artifacts "cache"
New-Item -ItemType Directory -Force $cache | Out-Null
$presentMon = Join-Path $cache (Split-Path $presentMonUrl -Leaf)
if (-not (Test-Path $presentMon) -or (Get-FileHash $presentMon -Algorithm SHA256).Hash.ToLower() -ne $presentMonSha256) {
    Invoke-WebRequest $presentMonUrl -OutFile $presentMon -UseBasicParsing
}
if ((Get-FileHash $presentMon -Algorithm SHA256).Hash.ToLower() -ne $presentMonSha256) {
    Remove-Item $presentMon
    throw "PresentMon checksum mismatch"
}
Copy-Item $presentMon (Join-Path $out "tools")

foreach ($file in "LICENSE", "README.md", "THIRD_PARTY_NOTICES.md") { Copy-Item (Join-Path $root $file) $out }

$zip = Join-Path $artifacts "WhyLag-$version-$Runtime-portable.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$out/*" -DestinationPath $zip
Write-Host "Created $zip"
