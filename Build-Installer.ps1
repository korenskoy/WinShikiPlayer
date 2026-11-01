#Requires -Version 7.0
[CmdletBinding()]
param([string]$Version, [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'packaging\Build.Common.ps1')
$manifestPath = Join-Path $PSScriptRoot 'packaging\AppxManifest.xml'
$manifestContent = Get-Content -LiteralPath $manifestPath -Raw
[xml]$manifest = $manifestContent
$baseline = [version](ConvertTo-PackageVersion ([string]$manifest.Package.Identity.Version))
$installerDirectory = Join-Path $PSScriptRoot 'artifacts\installer'
New-Item -ItemType Directory -Path $installerDirectory -Force | Out-Null
# Existing archives also reserve their versions if a previous run finished packaging
# but could not persist the source manifest. Never overwrite a released ZIP.
foreach ($archive in Get-ChildItem -LiteralPath $installerDirectory -Filter 'WinShikiPlayer-*-Setup-x64.zip' -File) {
    if ($archive.Name -match '^WinShikiPlayer-(\d+\.\d+\.\d+\.\d+)-Setup-x64\.zip$') {
        $published = [version]$Matches[1]
        if ($published -gt $baseline) { $baseline = $published }
    }
}
if ($Version) {
    $target = [version](ConvertTo-PackageVersion $Version)
    if ($target -le $baseline) { throw ('Укажите версию новее ' + $baseline + '. Без -Version номер сборки увеличивается автоматически.') }
} else {
    if ($baseline.Revision -ge 65535) { throw 'Номер сборки достиг 65535. Укажите следующую версию через -Version.' }
    $target = [version]::new($baseline.Major, $baseline.Minor, $baseline.Build, $baseline.Revision + 1)
}
$packageVersion = $target.ToString(4)
$baseName = 'WinShikiPlayer-' + $packageVersion + '-Setup-x64'
$kitDirectory = Join-Path $PSScriptRoot ('artifacts\installer-kits\' + $baseName)
$packageName = 'WinShikiPlayer-' + $packageVersion + '-x64.msix'
$zipPath = Join-Path $installerDirectory ($baseName + '.zip')
$logPath = Join-Path $PSScriptRoot ('artifacts\installer-build-' + $packageVersion + '.log')
$utf8 = [Text.UTF8Encoding]::new($true)
Start-Transcript -Path $logPath -Force | Out-Null
try {
    Write-Host ('WinShikiPlayer: ' + $baseline + ' -> ' + $packageVersion)
    if (-not $SkipTests) {
        Write-Host '[1/4] Проверка тестов'
        & dotnet run --project (Join-Path $PSScriptRoot 'tests\WinShikiPlayer.Core.Tests') -c Release --no-launch-profile
        if ($LASTEXITCODE -ne 0) { throw 'Тесты не прошли. Установщик не выпущен.' }
    }
    Write-Host '[2/4] Чистая Release-публикация и подпись MSIX'
    & (Join-Path $PSScriptRoot 'Build-MSIX.ps1') -Version $packageVersion
    $builtPackage = Join-Path $installerDirectory $packageName
    Assert-PackageContents $builtPackage $packageVersion
    $sdkBin = Get-WindowsSdkTools $PSScriptRoot
    & (Join-Path $sdkBin 'signtool.exe') verify /pa $builtPackage
    if ($LASTEXITCODE -ne 0) { throw 'Проверка подписи не прошла. Доверие сертификату автоматически не изменяется.' }

    Write-Host '[3/4] Подготовка автономного комплекта'
    Reset-ArtifactDirectory $PSScriptRoot $kitDirectory
    $packagePath = Join-Path $kitDirectory $packageName
    Copy-Item -LiteralPath $builtPackage -Destination $packagePath
    $certificatePath = Join-Path $installerDirectory 'WinShikiPlayer-development.cer'
    Copy-Item -LiteralPath $certificatePath -Destination $kitDirectory
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
    try { $thumbprint = $certificate.Thumbprint } finally { $certificate.Dispose() }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'packaging\Install.cmd') -Destination $kitDirectory
    $installScript = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'packaging\Install-WinShikiPlayer.ps1') -Raw).Replace('@@CERTIFICATE_THUMBPRINT@@', $thumbprint)
    if ($installScript.Contains('@@CERTIFICATE_THUMBPRINT@@')) { throw 'Не заполнен отпечаток сертификата в установщике.' }
    [IO.File]::WriteAllText((Join-Path $kitDirectory 'Install.ps1'), $installScript, $utf8)
    $release = [ordered]@{ version = $packageVersion; package = $packageName; sha256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash; certificate_thumbprint = $thumbprint }
    [IO.File]::WriteAllText((Join-Path $kitDirectory 'installer.json'), ($release | ConvertTo-Json), $utf8)
    $instructions = @"
WinShikiPlayer $packageVersion — Windows x64

1. Полностью распакуйте ZIP в отдельную папку.
2. Запустите Install.cmd.
3. Откройте WinShikiPlayer из меню «Пуск».

Требуется Windows 10 2004 / 19041 или новее. .NET и Windows App SDK входят в пакет.

Сборка подписана локальным сертификатом WinShikiPlayer Development.
Если сертификат ещё не доверен, Windows запросит разрешение администратора
для его добавления в LocalMachine / TrustedPeople.
Отпечаток: $thumbprint.
На компьютере, где сертификат уже доверен, повторное добавление не требуется.

Обновление завершает запущенный WinShikiPlayer и сохраняет LocalState
и учётные данные. Такая же или более новая установленная версия не заменяется.

Личные ключи, OAuth-токены и appsettings.local.json в комплект не включены.
На текущем компьютере используются сохранённые настройки и авторизация.
На новом компьютере подключение настраивается внутри приложения.
Для резервного веб-плеера нужен Microsoft Edge WebView2 Runtime.

Install.ps1 -CheckOnly проверяет комплект без установки и изменения Windows.
"@
    [IO.File]::WriteAllText((Join-Path $kitDirectory 'README.txt'), $instructions, $utf8)
    $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $kitDirectory 'Install.ps1') -CheckOnly
    if ($LASTEXITCODE -ne 0) { throw 'Комплект не прошёл проверку Windows PowerShell 5.1.' }

    Write-Host '[4/4] ZIP, контрольная сумма и сохранение версии'
    $fileNames = @($packageName, 'WinShikiPlayer-development.cer', 'Install.cmd', 'Install.ps1', 'installer.json', 'README.txt')
    $files = $fileNames | ForEach-Object { Join-Path $kitDirectory $_ }
    Compress-Archive -LiteralPath $files -DestinationPath $zipPath -CompressionLevel Optimal
    $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $actual = @($zip.Entries | ForEach-Object FullName)
        if (Compare-Object $fileNames $actual) { throw 'Состав ZIP не соответствует комплекту установки.' }
    } finally { $zip.Dispose() }
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
    [IO.File]::WriteAllText(($zipPath + '.sha256'), ($hash + '  ' + [IO.Path]::GetFileName($zipPath) + "`r`n"), [Text.Encoding]::ASCII)
    if ((Get-Content -LiteralPath $manifestPath -Raw) -ne $manifestContent) { throw 'Исходный манифест изменился во время сборки; его версия не перезаписана.' }
    [IO.File]::WriteAllText($manifestPath, (Set-PackageManifestVersion $manifestContent $packageVersion), [Text.UTF8Encoding]::new($false))
    $latest = [ordered]@{ version = $packageVersion; zip = [IO.Path]::GetFileName($zipPath); sha256 = $hash; msix = $packageName; certificate_thumbprint = $thumbprint; created_at = [DateTimeOffset]::UtcNow.ToString('o') }
    [IO.File]::WriteAllText((Join-Path $installerDirectory 'latest.json'), ($latest | ConvertTo-Json), $utf8)
    Write-Host ('Готово: ' + $zipPath)
    Write-Host ('Журнал: ' + $logPath)
} finally { Stop-Transcript | Out-Null }
