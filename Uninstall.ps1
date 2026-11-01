$ErrorActionPreference = 'Stop'
$installPath = [IO.Path]::GetFullPath($PSScriptRoot)
$expectedPath = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\WinShikiPlayer'))
if ($installPath -ne $expectedPath) { throw 'Удаление разрешено только из папки установленного WinShikiPlayer.' }
$installedExe = Join-Path $installPath 'WinShikiPlayer.exe'
if (Get-Process WinShikiPlayer -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $installedExe }) { throw 'Закройте WinShikiPlayer перед удалением.' }
$protocolCommand = (Get-ItemProperty -LiteralPath 'HKCU:\Software\Classes\winshikiplayer\shell\open\command' -ErrorAction SilentlyContinue).'(default)'
if ($protocolCommand -and $protocolCommand.IndexOf($installedExe, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    Remove-Item -LiteralPath 'HKCU:\Software\Classes\winshikiplayer' -Recurse -Force
}
foreach ($key in @('HKCU:\Software\Classes\WinShikiPlayer.Protocol', 'HKCU:\Software\WinShikiPlayer', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\WinShikiPlayer')) {
    if (Test-Path -LiteralPath $key) { Remove-Item -LiteralPath $key -Recurse -Force }
}
Remove-ItemProperty -LiteralPath 'HKCU:\Software\RegisteredApplications' -Name 'WinShikiPlayer' -ErrorAction SilentlyContinue
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'WinShikiPlayer.lnk'
if (Test-Path -LiteralPath $shortcutPath) { Remove-Item -LiteralPath $shortcutPath -Force }
Set-Location -LiteralPath ([Environment]::GetFolderPath('LocalApplicationData'))
Remove-Item -LiteralPath $installPath -Recurse -Force
Write-Host 'WinShikiPlayer удалён. История и настройки пользователя сохранены.'
