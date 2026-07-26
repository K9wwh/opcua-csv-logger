# Lightweight publish for developers / prepared machines (框架依赖发布).
# Target PC must have the .NET 10 Desktop Runtime installed (the panel is WinForms,
# so it needs the DESKTOP runtime, not just the base runtime).
# For the no-requirements field handover, use .\publish.ps1 instead.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'handover-lite'

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish (Join-Path $root 'OpcUaCsvLogger.csproj') -c Release -o $out
dotnet publish (Join-Path $root 'ConfigPanel\ConfigPanel.csproj') -c Release -o $out

Copy-Item (Join-Path $root 'TROUBLESHOOTING.md') $out
# Seed a starting config for the recipient (edit EndpointUrl / NodeIds before first run).
Copy-Item (Join-Path $root 'config.example.json') (Join-Path $out 'config.json')

Write-Host ''
Write-Host "Done. Lite folder: $out"
Write-Host 'Requires .NET 10 Desktop Runtime on the target PC (winget install Microsoft.DotNet.DesktopRuntime.10).'
Write-Host 'Do NOT add pki\ or data.csv from this machine.'
