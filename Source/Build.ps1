$ErrorActionPreference = 'Stop'
$localSource = $PSScriptRoot
$localRoot = Split-Path -Parent $localSource
$localProject = Join-Path $localSource 'pharmacyApp\pharmacyApp.csproj'
$buildStage = Join-Path ([IO.Path]::GetTempPath()) ('PharmacyBuild-' + [guid]::NewGuid().ToString('N'))
$publishStage = Join-Path $buildStage 'publish'
$artifactsStage = Join-Path $buildStage 'artifacts'
# Publishing into an ancestor of the project makes SDK default exclusions hide source files.
# Stage outside the source tree, then install only successful publish output.
& dotnet publish $localProject --artifacts-path $artifactsStage -p:PublishProfile=Local -o $publishStage -v minimal
if ($LASTEXITCODE -ne 0) { throw ('Publish failed. Build files retained at ' + $buildStage) }
foreach ($item in Get-ChildItem -LiteralPath $publishStage -Force) {
    if ($item.Name -in @('config.json','config.jsonc','opening.md','Data','Source','.git','.gitignore')) { continue }
    Copy-Item -LiteralPath $item.FullName -Destination $localRoot -Recurse -Force
}
$resolvedStage = (Resolve-Path -LiteralPath $buildStage).Path
if ($resolvedStage -ne [IO.Path]::GetFullPath($buildStage) -or
    -not $resolvedStage.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Unexpected build cleanup directory.'
}
Remove-Item -LiteralPath $resolvedStage -Recurse -Force
Write-Host "Updated local executable and runtime files in $localRoot. Existing config and Data were preserved."
