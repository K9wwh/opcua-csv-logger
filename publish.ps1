# Builds the ready-to-handover folder (自包含发布, no .NET needed on the target PC).
# Usage:  .\publish.ps1   ->  zip the resulting 'handover' folder and give it away.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'handover'

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish (Join-Path $root 'OpcUaCsvLogger.csproj') -c Release -r win-x64 --self-contained -o $out
dotnet publish (Join-Path $root 'ConfigPanel\ConfigPanel.csproj') -c Release -r win-x64 --self-contained -o $out

Copy-Item (Join-Path $root 'TROUBLESHOOTING.md') $out

Write-Host ''
Write-Host "Done. Handover folder: $out"
Write-Host 'Zip it and hand it over. Do NOT add pki\ or data.csv from this machine.'
Write-Host '(New PC = new client certificate = one-time trust step on the PLC, see TROUBLESHOOTING.md.)'
