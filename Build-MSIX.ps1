#Requires -Version 7.0
param([string]$Version, [switch]$SkipPublish, [switch]$WithoutApplicationKeys)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'packaging\Build.Common.ps1')
$manifestContent = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'packaging\AppxManifest.xml') -Raw
[xml]$manifest = $manifestContent
$packageVersion = ConvertTo-PackageVersion $(if ($Version) { $Version } else { [string]$manifest.Package.Identity.Version })
$sourcePath = Join-Path $PSScriptRoot 'artifacts\WinShikiPlayer-publish'
$stagePath = Join-Path $PSScriptRoot 'artifacts\msix-stage'
$packageDirectory = Join-Path $PSScriptRoot 'artifacts\installer'
$signingDirectory = Join-Path $PSScriptRoot 'artifacts\signing'
if (-not $SkipPublish) {
    Reset-ArtifactDirectory $PSScriptRoot $sourcePath
    & (Join-Path $PSScriptRoot 'build.ps1') -Publish -Version $packageVersion -WithoutApplicationKeys:$WithoutApplicationKeys
}
$sdkBin = Get-WindowsSdkTools $PSScriptRoot
foreach ($directory in @($packageDirectory, $signingDirectory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
Reset-ArtifactDirectory $PSScriptRoot $stagePath
Copy-Item -Path (Join-Path $sourcePath '*') -Destination $stagePath -Recurse -Force
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $stagePath 'AppxManifest.xml'), (Set-PackageManifestVersion $manifestContent $packageVersion), $utf8)
Copy-Item -LiteralPath (Join-Path $sourcePath 'WinShikiPlayer.pri') -Destination (Join-Path $stagePath 'resources.pri') -Force
if (Get-ChildItem -LiteralPath $stagePath -Recurse -File | Where-Object { $_.Name -match '(?i)(\.local\.json|\.pfx|\.dpapi|\.log)$' }) { throw 'В каталоге пакета обнаружены локальные файлы или ключи.' }
$packagePath = Join-Path $packageDirectory ('WinShikiPlayer-' + $packageVersion + '-x64.msix')
& (Join-Path $sdkBin 'makeappx.exe') pack /d $stagePath /p $packagePath /o
if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать MSIX.' }
$pfxPath = Join-Path $signingDirectory 'WinShikiPlayer-development.pfx'
$passwordPath = Join-Path $signingDirectory 'password.dpapi'
$certificatePath = Join-Path $packageDirectory 'WinShikiPlayer-development.cer'
Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
if ((Test-Path -LiteralPath $pfxPath) -and (Test-Path -LiteralPath $passwordPath)) {
    $protectedPassword = [IO.File]::ReadAllBytes($passwordPath)
    $passwordBytes = [Security.Cryptography.ProtectedData]::Unprotect($protectedPassword, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $signingPassword = [Text.Encoding]::UTF8.GetString($passwordBytes)
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($pfxPath, $signingPassword)
} else {
    $rsa = [Security.Cryptography.RSA]::Create(3072)
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=WinShikiPlayer Development', $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false,$false,0,$true))
    $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new([Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature,$true))
    $usages = [Security.Cryptography.OidCollection]::new(); $usages.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3')) | Out-Null
    $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($usages,$true))
    $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddYears(1))
    $randomPassword = [byte[]]::new(32); [Security.Cryptography.RandomNumberGenerator]::Fill($randomPassword)
    $signingPassword = [Convert]::ToBase64String($randomPassword)
    [IO.File]::WriteAllBytes($pfxPath, $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx,$signingPassword))
    $protectedPassword = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($signingPassword),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser)
    [IO.File]::WriteAllBytes($passwordPath,$protectedPassword)
}
if ($certificate.Subject -ne [string]$manifest.Package.Identity.Publisher) { throw 'Издатель сертификата не соответствует манифесту.' }
if ($certificate.NotAfter -le (Get-Date)) { throw 'Сертификат подписи истёк. Требуется обновить сертификат разработки.' }
try {
    [IO.File]::WriteAllBytes($certificatePath, $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    & (Join-Path $sdkBin 'signtool.exe') sign /fd SHA256 /f $pfxPath /p $signingPassword $packagePath
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось подписать MSIX.' }
    Assert-PackageContents $packagePath $packageVersion
} finally { $certificate.Dispose(); $signingPassword = $null }
# Keep the path used by the existing local installation script.
Copy-Item -LiteralPath $packagePath -Destination (Join-Path $packageDirectory 'WinShikiPlayer-0.2.0-x64.msix') -Force
Write-Host ('Пакет: ' + $packagePath)
Write-Host ('Сертификат разработки: ' + $certificatePath)
