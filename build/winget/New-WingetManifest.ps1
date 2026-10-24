# Creates winget manifests for a published release in artifacts/winget/<version>.
# Submit them to https://github.com/microsoft/winget-pkgs (e.g. with `wingetcreate submit <folder>`).
param(
    [Parameter(Mandatory)] [string]$Owner,            # GitHub user or organisation hosting the release
    [string]$Repository = "whylag",
    [string]$Installer,                               # defaults to artifacts/WhyLag-<version>-setup.exe
    [string]$PackageIdentifier = "WhyLag.WhyLag"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version
if (-not $Installer) { $Installer = Join-Path $root "artifacts/WhyLag-$version-setup.exe" }
if (-not (Test-Path $Installer)) { throw "Installer not found: $Installer (run build/build-installer.ps1)" }

$sha = (Get-FileHash $Installer -Algorithm SHA256).Hash
$url = "https://github.com/$Owner/$Repository/releases/download/v$version/$(Split-Path $Installer -Leaf)"
$out = Join-Path $root "artifacts/winget/$version"
New-Item -ItemType Directory -Force $out | Out-Null

$tokens = @{
    "{{Id}}" = $PackageIdentifier
    "{{Version}}" = $version
    "{{Url}}" = $url
    "{{Sha256}}" = $sha
    "{{RepoUrl}}" = "https://github.com/$Owner/$Repository"
    "{{ReleaseDate}}" = (Get-Date -Format "yyyy-MM-dd")
}
foreach ($template in Get-ChildItem (Join-Path $PSScriptRoot "templates") -Filter *.yaml) {
    $text = Get-Content $template.FullName -Raw
    foreach ($k in $tokens.Keys) { $text = $text.Replace($k, $tokens[$k]) }
    $name = $template.Name.Replace("Package", $PackageIdentifier)
    [IO.File]::WriteAllText((Join-Path $out $name), $text.Replace("`r`n", "`n"))
}
Write-Host "Manifests written to $out"
Write-Host "Validate with: winget validate --manifest `"$out`""
