param([switch]$IncludeLocalConfiguration, [switch]$Launch)
$ErrorActionPreference = 'Stop'
$publishedPath = Join-Path $PSScriptRoot 'artifacts\WinShikiPlayer-publish'
$installPath = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\WinShikiPlayer'))
$expectedPath = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\WinShikiPlayer'))
if ($installPath -ne $expectedPath) { throw 'Неверный путь установки.' }
if (-not (Test-Path -LiteralPath (Join-Path $publishedPath 'WinShikiPlayer.exe'))) { throw 'Сначала выполните .\build.ps1 -Publish.' }
New-Item -ItemType Directory -Path $installPath -Force | Out-Null
Copy-Item -Path (Join-Path $publishedPath '*') -Destination $installPath -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall.ps1') -Destination $installPath -Force
if ($IncludeLocalConfiguration) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'appsettings.local.json') -Destination (Join-Path $installPath 'appsettings.local.json') -Force
}
$installedExe = Join-Path $installPath 'WinShikiPlayer.exe'
$registerProcess = Start-Process -FilePath $installedExe -ArgumentList '--register-protocol' -WindowStyle Hidden -Wait -PassThru
if ($registerProcess.ExitCode -ne 0) { throw 'Не удалось зарегистрировать обработчик ссылок.' }
$programsPath = [Environment]::GetFolderPath('Programs')
$shortcutShell = New-Object -ComObject WScript.Shell
$shortcut = $shortcutShell.CreateShortcut((Join-Path $programsPath 'WinShikiPlayer.lnk'))
$shortcut.TargetPath = $installedExe
$shortcut.WorkingDirectory = $installPath
$shortcut.IconLocation = $installedExe + ',0'
$shortcut.Save()
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\WinShikiPlayer'
New-Item -Path $uninstallKey -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayName -Value 'WinShikiPlayer' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayVersion -Value '0.2.0' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayIcon -Value ($installedExe + ',0') -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name InstallLocation -Value $installPath -PropertyType String -Force | Out-Null
$uninstallScript = Join-Path $installPath 'Uninstall.ps1'
$uninstallCommand = 'powershell.exe -NoProfile -File "' + $uninstallScript + '"'
New-ItemProperty -Path $uninstallKey -Name UninstallString -Value $uninstallCommand -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null
Write-Host "WinShikiPlayer установлен: $installPath"
if ($Launch) { Start-Process -FilePath $installedExe }
