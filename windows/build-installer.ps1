# Builds the QuackSnap Windows installer (Setup.exe) + portable zip, on Windows.
#
#   powershell -ExecutionPolicy Bypass -File windows\build-installer.ps1 [-Version 1.0.0]
#
# Output: artifacts\releases\QuackSnap-win-Setup.exe
param([string]$Version = "1.0.0")
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$pub  = Join-Path $root "artifacts\publish"
$rel  = Join-Path $root "artifacts\releases"
$icon = Join-Path $root "windows\QuackSnap\Assets\QuackSnap.ico"

Write-Host "==> Restoring build tools (vpk)..."
dotnet tool restore

Write-Host "==> Publishing self-contained win-x64..."
if (Test-Path $pub) { Remove-Item -Recurse -Force $pub }
dotnet publish (Join-Path $root "windows\QuackSnap") -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=false -o $pub

Write-Host "==> Packaging installer $Version..."
# On Windows no cross-compile directive is needed.
dotnet vpk pack `
  -u QuackSnap -v $Version -p $pub -e QuackSnap.exe -r win-x64 `
  --packTitle "QuackSnap" --packAuthors "Peter Herrera" --icon $icon `
  -o $rel -y

Write-Host ""
Write-Host "Done. Installer: $rel\QuackSnap-win-Setup.exe"
