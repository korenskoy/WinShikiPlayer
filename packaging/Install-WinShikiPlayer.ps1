#Requires -Version 5.1
param([switch]$TrustDevelopmentCertificate, [switch]$CheckOnly)
$ErrorActionPreference = 'Stop'

$release = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'installer.json') -Raw | ConvertFrom-Json
$packagePath = Join-Path $PSScriptRoot $release.package
$certificatePath = Join-Path $PSScriptRoot 'WinShikiPlayer-development.cer'
$expectedThumbprint = '@@CERTIFICATE_THUMBPRINT@@'
$expectedPublisher = 'CN=WinShikiPlayer Development'

if ([IO.Path]::GetFileName($release.package) -ne $release.package) { throw 'Неверное имя установочного пакета.' }
if ((Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash -ne $release.sha256) { throw 'Установочный пакет повреждён. Распакуйте архив заново.' }
$certificate = New-Object Security.Cryptography.X509Certificates.X509Certificate2($certificatePath)
if ($certificate.Thumbprint -ne $expectedThumbprint -or $certificate.Subject -ne $expectedPublisher) { throw 'Сертификат не соответствует WinShikiPlayer.' }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    $entry = $archive.GetEntry('AppxManifest.xml')
    if (-not $entry) { throw 'В пакете отсутствует манифест.' }
    $reader = New-Object IO.StreamReader($entry.Open())
    try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $identity = $manifest.Package.Identity
    if ($identity.Name -ne 'WinShikiPlayer' -or $identity.Publisher -ne $expectedPublisher -or $identity.ProcessorArchitecture -ne 'x64' -or $identity.Version -ne $release.version) {
        throw 'Пакет не соответствует описанию установщика.'
    }
} finally { $archive.Dispose() }

if ($CheckOnly) { Write-Host ('Проверка успешна: WinShikiPlayer ' + $release.version + ', x64.'); return }
if (-not [Environment]::Is64BitOperatingSystem) { throw 'Требуется 64-разрядная Windows.' }
if ([Environment]::OSVersion.Version.Build -lt 19041) { throw 'Требуется Windows 10 2004 (19041) или новее.' }
$installed = Get-AppxPackage -Name WinShikiPlayer | Sort-Object Version -Descending | Select-Object -First 1
if ($installed -and [version]$installed.Version -ge [version]$release.version) {
    Write-Host ('Уже установлена версия ' + $installed.Version + '. WinShikiPlayer доступен в меню «Пуск».')
    return
}

$trustPath = 'Cert:\LocalMachine\TrustedPeople\' + $expectedThumbprint
if (-not (Test-Path -LiteralPath $trustPath)) {
    if (-not $TrustDevelopmentCertificate) { throw 'Сначала запустите Install.cmd: требуется доверие локальному сертификату разработки.' }
    Write-Host 'Windows запросит разрешение добавить сертификат WinShikiPlayer Development в доверенные лица.'
    $escapedCertificate = $certificatePath.Replace("'", "''")
    $trustCommand = @"
`$ErrorActionPreference = 'Stop'
`$certificate = New-Object Security.Cryptography.X509Certificates.X509Certificate2('$escapedCertificate')
if (`$certificate.Thumbprint -ne '$expectedThumbprint' -or `$certificate.Subject -ne '$expectedPublisher') { throw 'Неверный сертификат.' }
Import-Certificate -FilePath '$escapedCertificate' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($trustCommand))
    $powershellPath = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $trustProcess = Start-Process -FilePath $powershellPath -ArgumentList '-NoProfile','-EncodedCommand',$encoded -Verb RunAs -WindowStyle Hidden -Wait -PassThru
    if ($trustProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $trustPath)) { throw 'Сертификат не добавлен. Установка отменена.' }
}

Write-Host ('Установка WinShikiPlayer ' + $release.version + '...')
Add-AppxPackage -Path $packagePath -ForceApplicationShutdown
$installed = Get-AppxPackage -Name WinShikiPlayer
if (-not $installed -or [version]$installed.Version -lt [version]$release.version) { throw 'Windows не подтвердила установку пакета.' }
Write-Host ('Готово: WinShikiPlayer ' + $installed.Version + '. Откройте приложение из меню «Пуск».')
