function Write-BundledApplicationConfiguration([string]$RepositoryRoot) {
    $local = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'appsettings.local.json') -Raw | ConvertFrom-Json
    $selected = [ordered]@{}
    foreach ($key in @('SHIKIMORI_CLIENT_ID', 'SHIKIMORI_CLIENT_SECRET', 'SHIKIMORI_REDIRECT_URI', 'SHIKIMORI_BASE_URL', 'SHIKIMORI_USER_AGENT', 'KODIK_API_TOKEN')) {
        $value = [string]$local.$key
        if ($value.Length) { $selected[$key] = $value }
    }
    foreach ($key in @('SHIKIMORI_CLIENT_ID', 'SHIKIMORI_CLIENT_SECRET', 'KODIK_API_TOKEN')) {
        if (-not $selected.Contains($key)) { throw ('В локальной конфигурации не заполнен ' + $key) }
    }
    if ($selected['SHIKIMORI_REDIRECT_URI'] -ne 'winshikiplayer://oauth') { throw 'Сборка WinShikiPlayer требует redirect winshikiplayer://oauth.' }
    $directory = Join-Path $RepositoryRoot 'artifacts\bundled-configuration'
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $path = Join-Path $directory 'BundledConfiguration.json'
    [IO.File]::WriteAllText($path, ($selected | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    return $path
}

function Sign-DevelopmentFile([string]$RepositoryRoot, [string]$FilePath) {
    $directory = Join-Path $RepositoryRoot 'artifacts\signing'
    Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
    $bytes = [IO.File]::ReadAllBytes((Join-Path $directory 'password.dpapi'))
    $password = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect($bytes, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
    try {
        $sdk = Get-WindowsSdkTools $RepositoryRoot
        & (Join-Path $sdk 'signtool.exe') sign /fd SHA256 /f (Join-Path $directory 'WinShikiPlayer-development.pfx') /p $password $FilePath
        if ($LASTEXITCODE -ne 0) { throw 'Не удалось подписать установочный EXE.' }
        & (Join-Path $sdk 'signtool.exe') verify /pa $FilePath
        if ($LASTEXITCODE -ne 0) { throw 'Подпись установочного EXE не прошла проверку.' }
    } finally { $password = $null }
}

function ConvertTo-PackageVersion([string]$Value) {
    if ($Value -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'Версия должна иметь вид 0.3.0 или 0.3.0.1.' }
    $parsed = [version]$Value
    $revision = [Math]::Max(0, $parsed.Revision)
    foreach ($part in @($parsed.Major, $parsed.Minor, $parsed.Build, $revision)) {
        if ($part -gt 65535) { throw 'Компоненты версии MSIX должны быть от 0 до 65535.' }
    }
    return ([version]::new($parsed.Major, $parsed.Minor, $parsed.Build, $revision)).ToString(4)
}

function Set-PackageManifestVersion([string]$Content, [string]$Version) {
    $match = [regex]::Match($Content, '(<Identity\b[^>]*\bVersion=")[^"]*(")')
    if (-not $match.Success) { throw 'В манифесте не найдена версия Identity.' }
    $replacement = $match.Groups[1].Value + $Version + $match.Groups[2].Value
    return $Content.Remove($match.Index, $match.Length).Insert($match.Index, $replacement)
}

function Reset-ArtifactDirectory([string]$RepositoryRoot, [string]$Path) {
    $root = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/')
    $artifacts = [IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
    $target = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    if (-not $target.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw ('Очистка разрешена только внутри artifacts: ' + $target)
    }
    # Verify the absolute target and every ancestor before recursive deletion.
    $ancestor = $target
    while ($ancestor.Length -ge $root.Length) {
        if (Test-Path -LiteralPath $ancestor) {
            $item = Get-Item -LiteralPath $ancestor -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw ('Каталог сборки не должен быть ссылкой: ' + $ancestor) }
        }
        if ($ancestor -eq $root) { break }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    New-Item -ItemType Directory -Path $target -Force | Out-Null
}

function Get-WindowsSdkTools([string]$RepositoryRoot) {
    [xml]$project = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'src\WinShikiPlayer\WinShikiPlayer.csproj') -Raw
    $reference = $project.SelectSingleNode('//PackageReference[@Include="Microsoft.Windows.SDK.BuildTools"]')
    if (-not $reference) { throw 'В проекте не указан Microsoft.Windows.SDK.BuildTools.' }
    $cache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
    $bin = Join-Path $cache ('microsoft.windows.sdk.buildtools\' + $reference.Version + '\bin')
    $directories = Get-ChildItem -LiteralPath $bin -Directory | Where-Object Name -Match '^\d+\.\d+\.\d+\.\d+$' | Sort-Object { [version]$_.Name } -Descending
    foreach ($directory in $directories) {
        $x64 = Join-Path $directory.FullName 'x64'
        if ((Test-Path -LiteralPath (Join-Path $x64 'makeappx.exe')) -and (Test-Path -LiteralPath (Join-Path $x64 'signtool.exe'))) { return $x64 }
    }
    throw 'Не найдены makeappx.exe и signtool.exe из Windows SDK. Выполните dotnet restore.'
}

function Assert-PackageContents([string]$PackagePath, [string]$Version) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $names = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        foreach ($required in @('AppxManifest.xml', 'App.xbf', 'MainWindow.xbf', 'Controls/SeekIcon.xbf', 'resources.pri', 'WinShikiPlayer.exe', 'libass.dll', 'AppxSignature.p7x')) {
            if ($required -notin $names) { throw ('В MSIX отсутствует обязательный файл: ' + $required) }
        }
        if ($names | Where-Object { $_ -match '(?i)(\.local\.json|\.pfx|\.dpapi|\.log)$|(^|/)(history|settings|navigation|subtitle-offsets|playback-progress|cache-[^/]+)\.json$|(^|/)\.git/' }) {
            throw 'В MSIX обнаружены локальная конфигурация, ключи или пользовательские данные.'
        }
        $reader = [IO.StreamReader]::new($archive.GetEntry('AppxManifest.xml').Open())
        try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $identity = $manifest.Package.Identity
        if ($identity.Name -ne 'WinShikiPlayer' -or $identity.Version -ne $Version -or $identity.ProcessorArchitecture -ne 'x64' -or $identity.Publisher -ne 'CN=WinShikiPlayer Development') {
            throw 'Имя, версия, архитектура или издатель MSIX не соответствуют сборке.'
        }
        $protocols = $manifest.GetElementsByTagName('Protocol', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')
        if (-not ($protocols | Where-Object { $_.Name -eq 'winshikiplayer' })) { throw 'В MSIX не объявлен OAuth-протокол winshikiplayer.' }
    } finally { $archive.Dispose() }
}
