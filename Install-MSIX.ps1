param([switch]$TrustDevelopmentCertificate, [switch]$IncludeLocalConfiguration)
$ErrorActionPreference = 'Stop'
$packagePath = Join-Path $PSScriptRoot 'artifacts\installer\WinShikiPlayer-0.2.0-x64.msix'
$certificatePath = Join-Path $PSScriptRoot 'artifacts\installer\WinShikiPlayer-development.cer'
$expectedThumbprint = '15DDC3D5EE8B6DF9B10BAF958B002D518FE81EE5'
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
if ($certificate.Thumbprint -ne $expectedThumbprint -or $certificate.Subject -ne 'CN=WinShikiPlayer Development') { throw 'Сертификат не соответствует проверенной локальной сборке.' }
$trustPath = 'Cert:\LocalMachine\TrustedPeople\' + $expectedThumbprint
if (-not (Test-Path -LiteralPath $trustPath)) {
    if (-not $TrustDevelopmentCertificate) { throw 'Для локальной сборки нужно явное разрешение -TrustDevelopmentCertificate.' }
    $escapedCertificatePath = $certificatePath.Replace("'", "''")
    $trustCommand = @"
`$ErrorActionPreference = 'Stop'
`$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new('$escapedCertificatePath')
if (`$certificate.Thumbprint -ne '$expectedThumbprint') { throw 'Неверный сертификат.' }
Import-Certificate -FilePath '$escapedCertificatePath' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
"@
    $encodedTrustCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($trustCommand))
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $trustProcess = Start-Process -FilePath $windowsPowerShell -ArgumentList '-NoProfile','-EncodedCommand',$encodedTrustCommand -Verb RunAs -Wait -PassThru
    if ($trustProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $trustPath)) { throw 'Сертификат разработки не добавлен в доверенные лица.' }
}
# Retire only the portable registration created by this repository; MSIX owns its manifest association.
foreach ($scheme in @('myshikiplayer', 'winshikiplayer')) {
    $schemeKey = 'HKCU:\Software\Classes\' + $scheme
    $commandKey = Join-Path $schemeKey 'shell\open\command'
    $command = (Get-ItemProperty -LiteralPath $commandKey -ErrorAction SilentlyContinue).'(default)'
    if ($command -and $command.Contains('WinShikiPlayer.exe')) { Remove-Item -LiteralPath $schemeKey -Recurse -Force }
}
foreach ($entry in @('MyShikiPlayer','WinShikiPlayer')) { Remove-ItemProperty -LiteralPath 'HKCU:\Software\RegisteredApplications' -Name $entry -ErrorAction SilentlyContinue }
foreach ($key in @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\WinShikiPlayer', 'HKCU:\Software\Classes\WinShikiPlayer.Protocol', 'HKCU:\Software\Classes\Applications\WinShikiPlayer.exe', 'HKCU:\Software\WinShikiPlayer\Capabilities')) {
    if (Test-Path -LiteralPath $key) { Remove-Item -LiteralPath $key -Recurse -Force }
}
$portableShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'WinShikiPlayer.lnk'
if (Test-Path -LiteralPath $portableShortcut) { Remove-Item -LiteralPath $portableShortcut -Force }
Add-AppxPackage -Path $packagePath -ForceApplicationShutdown
$installedPackage = Get-AppxPackage -Name WinShikiPlayer
if (-not $installedPackage) { throw 'MSIX не зарегистрирован для текущего пользователя.' }
if ($IncludeLocalConfiguration) {
    $configurationDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('Packages\' + $installedPackage.PackageFamilyName + '\LocalState')
    New-Item -ItemType Directory -Path $configurationDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'appsettings.local.json') -Destination (Join-Path $configurationDirectory 'appsettings.local.json') -Force
}
Write-Host ('MSIX установлен: ' + $installedPackage.PackageFullName)
Write-Host ('Application ID: ' + $installedPackage.PackageFamilyName + '!App')
