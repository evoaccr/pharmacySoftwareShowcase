$ErrorActionPreference = 'Stop'
$localSource = $PSScriptRoot
$localRoot = Split-Path -Parent $localSource
$localProject = Join-Path $localSource 'pharmacyApp\pharmacyApp.csproj'
& dotnet publish $localProject -p:PublishProfile=Local -o $localRoot -v minimal
if ($LASTEXITCODE -ne 0) { throw 'local publish failed. Close the app before rebuilding.' }
Write-Host "Updated local executable and runtime files in $localRoot. Existing config and Data were preserved."
