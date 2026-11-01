param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [switch]$Publish,
    [switch]$Run,
    [string]$Version,
    [switch]$WithoutApplicationKeys
)
$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'src\WinShikiPlayer\WinShikiPlayer.csproj'
$outputPath = Join-Path $PSScriptRoot $(if ($Publish) { 'artifacts\WinShikiPlayer-publish' } else { 'artifacts\WinShikiPlayer' })
$versionArguments = @()
if ($Version) {
    . (Join-Path $PSScriptRoot 'packaging\Build.Common.ps1')
    $versionArguments = @('-p:Version=' + (ConvertTo-PackageVersion $Version))
}
if ($Publish) {
    if (-not $WithoutApplicationKeys -and (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'appsettings.local.json'))) {
        . (Join-Path $PSScriptRoot 'packaging\Build.Common.ps1')
        $configurationPath = Write-BundledApplicationConfiguration $PSScriptRoot
        $versionArguments += '-p:BundledApplicationConfiguration=' + $configurationPath
    }
    & dotnet publish $projectPath -c Release -p:Platform=x64 -o $outputPath @versionArguments
} else {
    & dotnet build $projectPath -c $Configuration -p:Platform=x64 "-p:OutDir=$outputPath/" @versionArguments
}
if ($LASTEXITCODE -ne 0) { throw 'Сборка завершилась с ошибкой.' }
Write-Host "Готово: $outputPath"
if ($Run) { Start-Process -FilePath (Join-Path $outputPath 'WinShikiPlayer.exe') }
