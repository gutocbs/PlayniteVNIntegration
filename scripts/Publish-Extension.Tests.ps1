param(
    [string]$ScriptUnderTest = (Join-Path $PSScriptRoot 'Publish-Extension.ps1')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$backupId = '8f9dc537-f997-47f1-b4de-7f8232a448b1'

function Assert-Equal {
    param($Expected, $Actual, [string]$Message)
    if ($Expected -ne $Actual) { throw "$Message Expected: '$Expected'. Actual: '$Actual'." }
}

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function New-Fixture {
    param([Parameter(Mandatory = $true)][string]$Root)

    $extension = Join-Path $Root 'Extensions\VNManager'
    $data = Join-Path $Root 'ExtensionsData\plugin-id'
    $pluginPublish = Join-Path $Root 'plugin-publish'
    $supervisorPublish = Join-Path $Root 'supervisor-publish'
    $backup = Join-Path $Root 'backups'

    New-Item -ItemType Directory -Path $extension, (Join-Path $data 'Configuration'), $pluginPublish, (Join-Path $supervisorPublish 'Configuration') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $extension 'PlaynitePlugin.dll') -Value 'plugin-old' -NoNewline
    Set-Content -LiteralPath (Join-Path $extension 'extension.yaml') -Value 'extension-old' -NoNewline
    Set-Content -LiteralPath (Join-Path $data 'LauncherSupervisor.exe') -Value 'supervisor-old' -NoNewline
    Set-Content -LiteralPath (Join-Path $data 'Configuration\local.json') -Value 'local-configuration' -NoNewline

    foreach ($file in @('PlaynitePlugin.dll', 'VNInstallation.Protocol.dll', 'System.Text.Json.dll', 'extension.yaml')) {
        Set-Content -LiteralPath (Join-Path $pluginPublish $file) -Value "new-$file" -NoNewline
    }
    foreach ($file in @('LauncherSupervisor.exe', 'LauncherSupervisor.dll', 'LauncherSupervisor.deps.json', 'LauncherSupervisor.runtimeconfig.json')) {
        Set-Content -LiteralPath (Join-Path $supervisorPublish $file) -Value "new-$file" -NoNewline
    }
    Set-Content -LiteralPath (Join-Path $supervisorPublish 'Configuration\local.json') -Value 'published-configuration' -NoNewline

    [pscustomobject]@{ Extension = $extension; Data = $data; PluginPublish = $pluginPublish; SupervisorPublish = $supervisorPublish; Backup = $backup }
}

function Invoke-Publisher {
    param([Parameter(Mandatory = $true)]$Fixture, [Parameter(Mandatory = $true)][string]$BackupRoot, [switch]$WhatIf)

    $arguments = @('-NoProfile', '-File', $ScriptUnderTest, '-SkipBuild', '-ExtensionDirectory', $Fixture.Extension, '-SupervisorDirectory', $Fixture.Data, '-PluginOutput', $Fixture.PluginPublish, '-SupervisorOutput', $Fixture.SupervisorPublish, '-BackupRoot', $BackupRoot)
    if ($WhatIf) { $arguments += '-WhatIf' }
    $output = & (Join-Path $PSHOME 'pwsh.exe') @arguments 2>&1 | Out-String
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("VNManagerPublishTests\" + [Guid]::NewGuid().ToString('N'))
try {
    Assert-True (Test-Path -LiteralPath $ScriptUnderTest -PathType Leaf) "Script under test not found: $ScriptUnderTest"

    $whatIf = New-Fixture -Root (Join-Path $testRoot 'what-if')
    $existingBackup = Join-Path $whatIf.Backup $backupId
    New-Item -ItemType Directory -Path $existingBackup -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $existingBackup 'previous.txt') -Value 'previous-backup' -NoNewline
    $whatIfResult = Invoke-Publisher -Fixture $whatIf -BackupRoot $whatIf.Backup -WhatIf
    Assert-Equal 0 $whatIfResult.ExitCode "WhatIf failed:`n$($whatIfResult.Output)"
    Assert-True $whatIfResult.Output.Contains("Plugin output: $($whatIf.PluginPublish)") 'WhatIf must show the plugin output directory.'
    Assert-True $whatIfResult.Output.Contains("Supervisor output: $($whatIf.SupervisorPublish)") 'WhatIf must show the supervisor output directory.'
    Assert-Equal 'previous-backup' (Get-Content -LiteralPath (Join-Path $existingBackup 'previous.txt') -Raw) 'WhatIf modified the existing backup.'
    Assert-Equal 'plugin-old' (Get-Content -LiteralPath (Join-Path $whatIf.Extension 'PlaynitePlugin.dll') -Raw) 'WhatIf modified the installed plugin.'

    $success = New-Fixture -Root (Join-Path $testRoot 'success')
    $previousBackup = Join-Path $success.Backup $backupId
    New-Item -ItemType Directory -Path $previousBackup -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $previousBackup 'previous.txt') -Value 'previous-backup' -NoNewline
    $successResult = Invoke-Publisher -Fixture $success -BackupRoot $success.Backup
    Assert-Equal 0 $successResult.ExitCode "Publisher failed:`n$($successResult.Output)"

    $backupDirectory = Join-Path $success.Backup $backupId
    Assert-True (Test-Path -LiteralPath (Join-Path $backupDirectory 'snapshot.sha256.json') -PathType Leaf) 'Publisher did not write the verified backup manifest.'
    Assert-Equal 'plugin-old' (Get-Content -LiteralPath (Join-Path $backupDirectory 'Extensions-VNManager\PlaynitePlugin.dll') -Raw) 'The installed plugin was not backed up.'
    Assert-Equal 'supervisor-old' (Get-Content -LiteralPath (Join-Path $backupDirectory 'ExtensionsData\LauncherSupervisor.exe') -Raw) 'The installed supervisor was not backed up.'
    Assert-Equal 'new-PlaynitePlugin.dll' (Get-Content -LiteralPath (Join-Path $success.Extension 'PlaynitePlugin.dll') -Raw) 'The plugin was not deployed.'
    Assert-Equal 'new-LauncherSupervisor.exe' (Get-Content -LiteralPath (Join-Path $success.Data 'LauncherSupervisor.exe') -Raw) 'The supervisor was not deployed.'
    Assert-Equal 'local-configuration' (Get-Content -LiteralPath (Join-Path $success.Data 'Configuration\local.json') -Raw) 'Deployment must preserve local configuration.'
    Assert-Equal 1 @(Get-ChildItem -LiteralPath $success.Backup -Directory | Where-Object Name -Like "$backupId-*").Count 'The previous fixed backup must be archived exactly once.'

    $invalidOutput = New-Fixture -Root (Join-Path $testRoot 'invalid-output')
    $fixedBackup = Join-Path $invalidOutput.Backup $backupId
    New-Item -ItemType Directory -Path $fixedBackup -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $fixedBackup 'previous.txt') -Value 'previous-backup' -NoNewline
    Remove-Item -LiteralPath (Join-Path $invalidOutput.PluginPublish 'VNInstallation.Protocol.dll')
    $invalidResult = Invoke-Publisher -Fixture $invalidOutput -BackupRoot $invalidOutput.Backup
    Assert-True ($invalidResult.ExitCode -ne 0) 'Deployment must reject an incomplete plugin output.'
    Assert-Equal 'previous-backup' (Get-Content -LiteralPath (Join-Path $fixedBackup 'previous.txt') -Raw) 'Output validation modified the fixed backup.'
    Assert-Equal 'plugin-old' (Get-Content -LiteralPath (Join-Path $invalidOutput.Extension 'PlaynitePlugin.dll') -Raw) 'Output validation modified the installed plugin.'

    $backupFailure = New-Fixture -Root (Join-Path $testRoot 'backup-failure')
    $invalidBackupRoot = Join-Path $testRoot 'backup-root-is-a-file'
    Set-Content -LiteralPath $invalidBackupRoot -Value 'not-a-directory' -NoNewline
    $backupFailureResult = Invoke-Publisher -Fixture $backupFailure -BackupRoot $invalidBackupRoot
    Assert-True ($backupFailureResult.ExitCode -ne 0) 'Deployment must fail when backup creation cannot start.'
    Assert-Equal 'plugin-old' (Get-Content -LiteralPath (Join-Path $backupFailure.Extension 'PlaynitePlugin.dll') -Raw) 'Backup failure modified the installed plugin.'

    Write-Host 'Publish-Extension tests passed: verified fixed backup, validation-before-overlay, configuration preservation, and backup failure safety.'
}
finally {
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
