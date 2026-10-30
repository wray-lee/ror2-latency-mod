# Builds Release and writes tmp/RoR2LatencyMod-<version>.zip in Thunderstore layout.
# Usage: powershell -NoProfile -File tools/pack-thunderstore.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
dotnet build "$root/src/RoR2LatencyMod/RoR2LatencyMod.csproj" -c Release | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

$manifest = Get-Content "$root/package/manifest.json" -Raw | ConvertFrom-Json
$stage = "$root/tmp/package-stage"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory "$stage/plugins/RoR2LatencyMod" | Out-Null
Copy-Item "$root/package/manifest.json", "$root/package/icon.png", "$root/README.md", "$root/CHANGELOG.md" $stage
Copy-Item "$root/src/RoR2LatencyMod/bin/Release/netstandard2.1/RoR2LatencyMod.dll" "$stage/plugins/RoR2LatencyMod"

$zip = "$root/tmp/RoR2LatencyMod-$($manifest.version_number).zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path "$stage/*" -DestinationPath $zip
Remove-Item $stage -Recurse -Force
Write-Host "[OK] $zip"
