#Requires -Version 7.0
param([Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][string]$ZipPath, [Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'Build.Common.ps1')
$version = ConvertTo-PackageVersion $Version
$directory = Join-Path $repositoryRoot 'artifacts\setup-build'
Reset-ArtifactDirectory $repositoryRoot $directory
$source = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Setup.cs') -Raw).Replace('@@VERSION@@', $version).Replace('@@PAYLOAD_SHA256@@', (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash)
$sourcePath = Join-Path $directory 'Setup.cs'
[IO.File]::WriteAllText($sourcePath, $source, [Text.UTF8Encoding]::new($true))
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ ('/out:' + $OutputPath) ('/win32icon:' + (Join-Path $repositoryRoot 'src\WinShikiPlayer\Assets\App.ico')) ('/win32manifest:' + (Join-Path $PSScriptRoot 'Setup.manifest')) ('/resource:' + $ZipPath + ',WinShikiPlayer.Setup.Payload') /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll $sourcePath
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать единый установочный EXE.' }
Sign-DevelopmentFile $repositoryRoot $OutputPath
$check = Start-Process -FilePath $OutputPath -ArgumentList '--check-only' -WindowStyle Hidden -Wait -PassThru -RedirectStandardError (Join-Path $directory 'check-error.log')
if ($check.ExitCode -ne 0) { Get-Content -LiteralPath (Join-Path $directory 'check-error.log'); throw 'Единый установочный EXE не прошёл проверку вложенного пакета.' }
$hash = (Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash
[IO.File]::WriteAllText(($OutputPath + '.sha256'), ($hash + '  ' + [IO.Path]::GetFileName($OutputPath) + "`r`n"), [Text.Encoding]::ASCII)
Write-Host ('Единый установщик: ' + $OutputPath)
