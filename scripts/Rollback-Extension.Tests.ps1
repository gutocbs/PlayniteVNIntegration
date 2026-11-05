param(
    [string]$PublisherScript = (Join-Path $PSScriptRoot 'Publish-Extension.ps1'),
    [string]$RollbackScript = (Join-Path $PSScriptRoot 'Rollback-Extension.ps1')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$backupId = '8f9dc537-f997-47f1-b4de-7f8232a448b1'

function Assert-Equal { param($Expected, $Actual, [string]$Message) if ($Expected -ne $Actual) { throw "$Message Expected: '$Expected'. Actual: '$Actual'." } }
function Assert-True { param([bool]$Condition, [string]$Message) if (-not $Condition) { throw $Message } }

function Invoke-Script { param([string]$Path, [string[]]$Arguments) $output = & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File $Path @Arguments 2>&1 | Out-String; [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output } }

function Wait-ForFile {
    param([string]$Path, [int]$TimeoutMilliseconds = 10000)
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $Path -PathType Leaf) { return $true }
        Start-Sleep -Milliseconds 25
    }
    return $false
}

function New-Fixture {
    param([string]$Root)
    $extension = Join-Path $Root 'Extensions\VNManager'
    $data = Join-Path $Root 'ExtensionsData\plugin-id'
    $pluginPublish = Join-Path $Root 'plugin-publish'
    $supervisorPublish = Join-Path $Root 'supervisor-publish'
    $backup = Join-Path $Root 'backups'
    New-Item -ItemType Directory -Path $extension, (Join-Path $data 'Configuration'), $pluginPublish, $supervisorPublish -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $extension 'PlaynitePlugin.dll') -Value 'plugin-original' -NoNewline
    Set-Content -LiteralPath (Join-Path $data 'LauncherSupervisor.exe') -Value 'supervisor-original' -NoNewline
    Set-Content -LiteralPath (Join-Path $data 'Configuration\local.json') -Value 'configuration-original' -NoNewline
    foreach ($file in @('PlaynitePlugin.dll', 'VNInstallation.Protocol.dll', 'System.Text.Json.dll', 'extension.yaml')) { Set-Content -LiteralPath (Join-Path $pluginPublish $file) -Value "published-$file" -NoNewline }
    foreach ($file in @('LauncherSupervisor.exe', 'LauncherSupervisor.dll', 'LauncherSupervisor.deps.json', 'LauncherSupervisor.runtimeconfig.json')) { Set-Content -LiteralPath (Join-Path $supervisorPublish $file) -Value "published-$file" -NoNewline }
    [pscustomobject]@{ Extension = $extension; Data = $data; PluginPublish = $pluginPublish; SupervisorPublish = $supervisorPublish; Backup = $backup }
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("VNManagerRollbackTests\" + [Guid]::NewGuid().ToString('N'))
$lockerProcess = $null
try {
    Assert-True (Test-Path -LiteralPath $RollbackScript -PathType Leaf) "Rollback script not found: $RollbackScript"
    $fixture = New-Fixture -Root $testRoot
    $commonArguments = @('-Confirm:$false', '-ExtensionDirectory', $fixture.Extension, '-SupervisorDirectory', $fixture.Data, '-BackupRoot', $fixture.Backup)
    $publishArguments = @('-SkipBuild', '-PluginOutput', $fixture.PluginPublish, '-SupervisorOutput', $fixture.SupervisorPublish) + $commonArguments
    $publishResult = Invoke-Script -Path $PublisherScript -Arguments $publishArguments
    Assert-Equal 0 $publishResult.ExitCode "Publisher fixture setup failed:`n$($publishResult.Output)"

    Set-Content -LiteralPath (Join-Path $fixture.Extension 'PlaynitePlugin.dll') -Value 'plugin-deployed' -NoNewline
    Set-Content -LiteralPath (Join-Path $fixture.Extension 'introduced-by-test.dll') -Value 'new-plugin-file' -NoNewline
    Set-Content -LiteralPath (Join-Path $fixture.Data 'LauncherSupervisor.exe') -Value 'supervisor-deployed' -NoNewline
    Set-Content -LiteralPath (Join-Path $fixture.Data 'introduced-by-test.dll') -Value 'new-data-file' -NoNewline

    $whatIfResult = Invoke-Script -Path $RollbackScript -Arguments ($commonArguments + '-WhatIf')
    Assert-Equal 0 $whatIfResult.ExitCode "Rollback WhatIf failed:`n$($whatIfResult.Output)"
    Assert-True $whatIfResult.Output.Contains("Rollback source: $(Join-Path $fixture.Backup $backupId)") 'Rollback WhatIf must show the fixed backup source.'
    Assert-Equal 'plugin-deployed' (Get-Content -LiteralPath (Join-Path $fixture.Extension 'PlaynitePlugin.dll') -Raw) 'Rollback WhatIf modified the plugin.'

    $rollbackResult = Invoke-Script -Path $RollbackScript -Arguments $commonArguments
    Assert-Equal 0 $rollbackResult.ExitCode "Rollback failed:`n$($rollbackResult.Output)"
    Assert-Equal 'plugin-original' (Get-Content -LiteralPath (Join-Path $fixture.Extension 'PlaynitePlugin.dll') -Raw) 'Rollback did not restore the plugin.'
    Assert-Equal 'supervisor-original' (Get-Content -LiteralPath (Join-Path $fixture.Data 'LauncherSupervisor.exe') -Raw) 'Rollback did not restore the supervisor.'
    Assert-Equal 'configuration-original' (Get-Content -LiteralPath (Join-Path $fixture.Data 'Configuration\local.json') -Raw) 'Rollback did not restore local configuration.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $fixture.Extension 'introduced-by-test.dll'))) 'Rollback left a file introduced in the plugin.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $fixture.Data 'introduced-by-test.dll'))) 'Rollback left a file introduced in extension data.'
    Assert-Equal 1 @(Get-ChildItem -LiteralPath $fixture.Backup -Directory | Where-Object Name -Like "$backupId-before-rollback-*").Count 'Rollback must create one safety copy.'

    $corrupt = New-Fixture -Root (Join-Path $testRoot 'corrupt')
    $corruptArguments = @('-Confirm:$false', '-ExtensionDirectory', $corrupt.Extension, '-SupervisorDirectory', $corrupt.Data, '-BackupRoot', $corrupt.Backup)
    $corruptPublish = Invoke-Script -Path $PublisherScript -Arguments (@('-SkipBuild', '-PluginOutput', $corrupt.PluginPublish, '-SupervisorOutput', $corrupt.SupervisorPublish) + $corruptArguments)
    Assert-Equal 0 $corruptPublish.ExitCode "Corrupt-backup publisher setup failed:`n$($corruptPublish.Output)"
    Set-Content -LiteralPath (Join-Path $corrupt.Backup "$backupId\Extensions-VNManager\PlaynitePlugin.dll") -Value 'corrupted' -NoNewline
    $corruptResult = Invoke-Script -Path $RollbackScript -Arguments $corruptArguments
    Assert-True ($corruptResult.ExitCode -ne 0) 'Rollback must reject a corrupt backup.'
    Assert-Equal 0 @(Get-ChildItem -LiteralPath $corrupt.Backup -Directory | Where-Object Name -Like "$backupId-before-rollback-*").Count 'Corrupt-backup rejection must happen before creating a safety copy.'

    $missing = New-Fixture -Root (Join-Path $testRoot 'missing-backup')
    $missingArguments = @('-Confirm:$false', '-ExtensionDirectory', $missing.Extension, '-SupervisorDirectory', $missing.Data, '-BackupRoot', $missing.Backup)
    $missingResult = Invoke-Script -Path $RollbackScript -Arguments $missingArguments
    Assert-True ($missingResult.ExitCode -ne 0) 'Rollback must reject a missing fixed backup.'
    Assert-Equal 'plugin-original' (Get-Content -LiteralPath (Join-Path $missing.Extension 'PlaynitePlugin.dll') -Raw) 'Missing-backup rejection modified the plugin.'
    Assert-Equal 'supervisor-original' (Get-Content -LiteralPath (Join-Path $missing.Data 'LauncherSupervisor.exe') -Raw) 'Missing-backup rejection modified extension data.'
    Assert-Equal 0 @(Get-ChildItem -LiteralPath $missing.Backup -Directory -ErrorAction SilentlyContinue | Where-Object Name -Like "$backupId-before-rollback-*").Count 'Missing-backup rejection must not create a safety copy.'

    $restoreFailure = New-Fixture -Root (Join-Path $testRoot 'restore-failure')
    $restoreArguments = @('-Confirm:$false', '-ExtensionDirectory', $restoreFailure.Extension, '-SupervisorDirectory', $restoreFailure.Data, '-BackupRoot', $restoreFailure.Backup)
    $restorePublish = Invoke-Script -Path $PublisherScript -Arguments (@('-SkipBuild', '-PluginOutput', $restoreFailure.PluginPublish, '-SupervisorOutput', $restoreFailure.SupervisorPublish) + $restoreArguments)
    Assert-Equal 0 $restorePublish.ExitCode "Restore-failure publisher setup failed:`n$($restorePublish.Output)"
    Set-Content -LiteralPath (Join-Path $restoreFailure.Extension 'PlaynitePlugin.dll') -Value 'plugin-deployed' -NoNewline
    Set-Content -LiteralPath (Join-Path $restoreFailure.Extension 'introduced-by-test.dll') -Value 'new-plugin-file' -NoNewline
    Set-Content -LiteralPath (Join-Path $restoreFailure.Data 'LauncherSupervisor.exe') -Value 'supervisor-deployed' -NoNewline
    Set-Content -LiteralPath (Join-Path $restoreFailure.Data 'introduced-by-test.dll') -Value 'new-data-file' -NoNewline
    [IO.File]::WriteAllBytes((Join-Path $restoreFailure.Data 'copy-padding.bin'), [byte[]]::new(32MB))

    $lockReadyPath = Join-Path $restoreFailure.Backup 'lock-ready.txt'
    $lockerScript = Join-Path $testRoot 'lock-backup-after-safety-copy.ps1'
    @'
param([string]$BackupRoot, [string]$BackupId, [string]$ReadyPath)
$deadline = [DateTime]::UtcNow.AddSeconds(10)
while ([DateTime]::UtcNow -lt $deadline) {
    $safetyDirectory = Get-ChildItem -LiteralPath $BackupRoot -Directory -ErrorAction SilentlyContinue | Where-Object Name -Like "$BackupId-before-rollback-*" | Select-Object -First 1
    if ($null -ne $safetyDirectory -and (Test-Path -LiteralPath (Join-Path $safetyDirectory.FullName 'Extensions-VNManager') -PathType Container)) {
        $lockedPath = Join-Path $BackupRoot "$BackupId\ExtensionsData\LauncherSupervisor.exe"
        $stream = [IO.File]::Open($lockedPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        try {
            Set-Content -LiteralPath $ReadyPath -Value 'locked' -NoNewline
            Start-Sleep -Seconds 20
        }
        finally { $stream.Dispose() }
        exit 0
    }
    Start-Sleep -Milliseconds 10
}
exit 1
'@ | Set-Content -LiteralPath $lockerScript -Encoding utf8NoBOM
    $lockerProcess = Start-Process -FilePath (Join-Path $PSHOME 'pwsh.exe') -ArgumentList @('-NoProfile', '-File', $lockerScript, '-BackupRoot', $restoreFailure.Backup, '-BackupId', $backupId, '-ReadyPath', $lockReadyPath) -PassThru
    $restoreFailureResult = Invoke-Script -Path $RollbackScript -Arguments $restoreArguments
    Assert-True (Wait-ForFile -Path $lockReadyPath) 'The restore-failure helper did not lock the backup after the safety copy started.'
    Assert-True ($restoreFailureResult.ExitCode -ne 0) 'Rollback must report a failed restore when its backup source is locked.'
    Assert-True $restoreFailureResult.Output.Contains('Rollback failed; the pre-rollback installation was restored.') "Rollback did not report recovery after restore failure:`n$($restoreFailureResult.Output)"
    Assert-Equal 'plugin-deployed' (Get-Content -LiteralPath (Join-Path $restoreFailure.Extension 'PlaynitePlugin.dll') -Raw) 'Rollback recovery did not restore the deployed plugin.'
    Assert-True (Test-Path -LiteralPath (Join-Path $restoreFailure.Extension 'introduced-by-test.dll')) 'Rollback recovery did not restore the introduced plugin file.'
    Assert-Equal 'supervisor-deployed' (Get-Content -LiteralPath (Join-Path $restoreFailure.Data 'LauncherSupervisor.exe') -Raw) 'Rollback recovery did not restore deployed extension data.'
    Assert-True (Test-Path -LiteralPath (Join-Path $restoreFailure.Data 'introduced-by-test.dll')) 'Rollback recovery did not restore the introduced data file.'
    Assert-Equal 1 @(Get-ChildItem -LiteralPath $restoreFailure.Backup -Directory | Where-Object Name -Like "$backupId-before-rollback-*").Count 'Rollback recovery must retain one safety copy.'

    Write-Host 'Rollback-Extension tests passed: WhatIf preview, verified restore, safety copy, corruption guard, missing-backup guard, and restore-failure recovery.'
}
finally {
    if ($null -ne $lockerProcess -and -not $lockerProcess.HasExited) { Stop-Process -Id $lockerProcess.Id -Force }
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
