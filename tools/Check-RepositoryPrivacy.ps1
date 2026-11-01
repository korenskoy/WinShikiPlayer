#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $repositoryRoot 'packaging\Build.Common.ps1')
$trackedOutput = @(& git -C $repositoryRoot -c core.quotepath=false ls-files --cached -z)
if ($LASTEXITCODE -ne 0) { throw 'Cannot read the Git index.' }
$paths = @(([string]::Join("`n", $trackedOutput) -split "`0") | Where-Object { $_ })
if ($paths.Count -eq 0) { throw 'The Git index is empty. Stage the intended source files first.' }

# Check the index, including force-added files. Never print file contents or values.
$forbidden = '(?i)(^|/)(artifacts|output|\.reference|\.codex|\.agents|bin|obj|LocalState|LocalCache)(/|$)|\.local\.json$|\.(pfx|p12|pem|key|snk|dpapi|cer|crt|msix|msixbundle|appx|appxbundle|zip|7z|log|dmp)$|(^|/)\.env($|\.)|(^|/)(settings|session|tokens|oauth-session|history|watch-history|playback-progress|episode-progress|navigation|subtitle-offsets|home-cache|ui-error)\.json$|(^|/)(cache-|filters-)[^/]*\.json$'
$blocked = @($paths | Where-Object { $_ -ne '.env.example' -and $_ -match $forbidden })
if ($blocked.Count) { $blocked | ForEach-Object { Write-Output ('Blocked private/build file: ' + ($_ -replace '[\r\n]', '?')) }; throw 'Private or generated files are staged.' }
$ignored = @($paths | & git -C $repositoryRoot -c core.quotepath=false check-ignore --no-index --stdin)
if ($LASTEXITCODE -notin @(0, 1)) { throw 'Cannot verify ignored paths.' }
if ($ignored.Count) { $ignored | ForEach-Object { Write-Output ('Ignored file was staged: ' + $_) }; throw 'Remove ignored files from the index.' }
$indexEntries = @(& git -C $repositoryRoot ls-files --stage)
if ($indexEntries | Where-Object { $_.StartsWith('120000 ') -or $_.StartsWith('160000 ') }) { throw 'Symlinks and submodules require separate privacy review.' }

$gitleaksCommand = Get-Command gitleaks -ErrorAction SilentlyContinue
$gitleaksPath = if ($gitleaksCommand) { $gitleaksCommand.Source } else { Join-Path $repositoryRoot 'artifacts\privacy-tools\gitleaks\gitleaks.exe' }
if (-not (Test-Path -LiteralPath $gitleaksPath)) { throw 'Install Gitleaks on PATH (or artifacts/privacy-tools/gitleaks/gitleaks.exe). Commit was blocked without scanning.' }
$snapshot = Join-Path $repositoryRoot ('artifacts\privacy-index-' + [Guid]::NewGuid().ToString('N'))
Reset-ArtifactDirectory $repositoryRoot $snapshot
try {
    # Export exactly the staged blobs, rather than scanning possibly different working files.
    & git -C $repositoryRoot checkout-index --all ('--prefix=' + $snapshot.Replace('\', '/') + '/')
    if ($LASTEXITCODE -ne 0) { throw 'Cannot export the staged source tree.' }
    $localConfig = Join-Path $repositoryRoot 'appsettings.local.json'
    $localValues = @()
    if (Test-Path -LiteralPath $localConfig) {
        $configuration = Get-Content -LiteralPath $localConfig -Raw | ConvertFrom-Json
        foreach ($name in @('SHIKIMORI_CLIENT_ID', 'SHIKIMORI_CLIENT_SECRET', 'KODIK_API_TOKEN')) {
            $value = [string]$configuration.$name
            if ($value.Length -ge 8) { $localValues += $value }
        }
    }
    foreach ($path in $paths) {
        $filePath = Join-Path $snapshot $path
        $content = [IO.File]::ReadAllText($filePath)
        foreach ($value in $localValues) {
            if ($content.Contains($value, [StringComparison]::Ordinal)) { throw ('A local credential is present in staged file: ' + $path) }
        }
        # Keep the single documented third-party DLL; exclude accidental app/user binaries.
        if ([IO.Path]::GetExtension($path) -in @('.exe', '.dll', '.pdb')) {
            if ($path -ne 'src/WinShikiPlayer/Native/x64/libass.dll' -or
                (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash -ne '8AD4493F884D32F146853DFE33C05D4498066FA1FF0832A40E5260D94633524D') {
                throw ('Unreviewed binary in the Git index: ' + $path)
            }
        }
    }
    $reportPath = Join-Path $repositoryRoot 'artifacts\privacy-gitleaks.json'
    $logPath = Join-Path $repositoryRoot 'artifacts\privacy-gitleaks.log'
    & $gitleaksPath dir $snapshot --config (Join-Path $snapshot '.gitleaks.toml') --redact=100 --no-banner --no-color --report-format json --report-path $reportPath *> $logPath
    $scanExitCode = $LASTEXITCODE
    if ($scanExitCode -ne 0) {
        if (Test-Path -LiteralPath $reportPath) {
            @(Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json) | ForEach-Object {
                Write-Output ('Secret finding: {0}, {1}:{2}' -f $_.RuleID, [IO.Path]::GetRelativePath($snapshot, $_.File), $_.StartLine)
            }
        }
        throw ('Gitleaks did not pass (exit ' + $scanExitCode + '). Values are redacted; inspect artifacts/privacy-gitleaks.json locally.')
    }
    Write-Output ('Privacy check passed: {0} staged files; no local credential matches or Gitleaks findings.' -f $paths.Count)
}
finally {
    # The shared helper verifies this absolute path stays inside artifacts and rejects links.
    Reset-ArtifactDirectory $repositoryRoot $snapshot
    Remove-Item -LiteralPath $snapshot
}
