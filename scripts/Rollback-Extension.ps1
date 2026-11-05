<#
.SYNOPSIS
    Restores the VNManager extension and ExtensionsData directory from a verified publisher backup.

.DESCRIPTION
    Validates the SHA-256 manifest created by Publish-Extension.ps1, preserves the
    current installation in a timestamped safety copy, then restores both directories
    exactly. Playnite and VNInstallOrchestrator must be closed.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$ExtensionDirectory = (Join-Path $env:APPDATA 'Playnite\Extensions\VNManager'),
    [string]$SupervisorDirectory = (Join-Path $env:APPDATA 'Playnite\ExtensionsData\0e1b3f2c-56f4-4eaf-bb7b-7f2c02aa5c5a'),
    [string]$BackupRoot = (Join-Path $env:APPDATA 'Playnite\VNManager-backups'),
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9.-]*$')]
    [string]$BackupId = '8f9dc537-f997-47f1-b4de-7f8232a448b1'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$backupManifestName = 'snapshot.sha256.json'

function Assert-Directory {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$Description)
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { throw "$Description not found: $Path" }
}

function Get-DirectorySnapshot {
    param([Parameter(Mandatory = $true)][string]$Path)
    $root = (Resolve-Path -LiteralPath $Path).ProviderPath.TrimEnd('\')
    $entries = [System.Collections.Generic.List[string]]::new()
    foreach ($directory in Get-ChildItem -LiteralPath $root -Directory -Recurse -Force) {
        [void]$entries.Add("D|$($directory.FullName.Substring($root.Length + 1))")
    }
    foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse -Force) {
        $relativePath = $file.FullName.Substring($root.Length + 1)
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        [void]$entries.Add("F|$relativePath|$($file.Length)|$hash")
    }
    return @($entries | Sort-Object)
}

function Assert-DirectoriesMatch {
    param([Parameter(Mandatory = $true)][string]$Expected, [Parameter(Mandatory = $true)][string]$Actual)
    Assert-Directory -Path $Actual -Description 'Copied directory'
    $difference = @(Compare-Object -ReferenceObject @(Get-DirectorySnapshot -Path $Expected) -DifferenceObject @(Get-DirectorySnapshot -Path $Actual))
    if ($difference.Count -ne 0) { throw "Directory verification failed: $Expected -> $Actual" }
}

function Assert-BackupManifest {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$ExtensionBackup, [Parameter(Mandatory = $true)][string]$DataBackup)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Backup manifest not found: $Path. Recreate the backup with the current publisher before rollback." }
    try { $manifest = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -ErrorAction Stop }
    catch { throw "Backup manifest is invalid: $Path. $_" }
    if ($manifest.Version -ne 1 -or $null -eq $manifest.Snapshots) { throw "Unsupported backup manifest: $Path" }
    $extensionProperty = $manifest.Snapshots.PSObject.Properties['Extensions-VNManager']
    $dataProperty = $manifest.Snapshots.PSObject.Properties['ExtensionsData']
    if ($null -eq $extensionProperty -or $null -eq $dataProperty) { throw "Backup manifest is missing a required snapshot: $Path" }
    $extensionDifference = @(Compare-Object -ReferenceObject @($extensionProperty.Value) -DifferenceObject @(Get-DirectorySnapshot -Path $ExtensionBackup))
    $dataDifference = @(Compare-Object -ReferenceObject @($dataProperty.Value) -DifferenceObject @(Get-DirectorySnapshot -Path $DataBackup))
    if ($extensionDifference.Count -ne 0 -or $dataDifference.Count -ne 0) { throw "Backup integrity verification failed: $Path" }
}

function Copy-DirectoryExact {
    param([Parameter(Mandatory = $true)][string]$Source, [Parameter(Mandatory = $true)][string]$Destination)
    if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Recurse -Force }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse -Force
    }
    Assert-DirectoriesMatch -Expected $Source -Actual $Destination
}

$runningProcesses = Get-Process -Name 'Playnite.DesktopApp', 'Playnite.FullscreenApp', 'VNInstallOrchestrator' -ErrorAction SilentlyContinue
if ($runningProcesses) { throw 'Close Playnite and VNInstallOrchestrator before rollback: they can lock deployed files.' }

Assert-Directory -Path $ExtensionDirectory -Description 'Installed VNManager extension directory'
Assert-Directory -Path $SupervisorDirectory -Description 'Installed VNManager data directory'
if (Test-Path -LiteralPath $BackupRoot -PathType Leaf) { throw "Backup root is a file, not a directory: $BackupRoot" }

$operationId = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$backupDirectory = Join-Path $BackupRoot $BackupId
$extensionBackupDirectory = Join-Path $backupDirectory 'Extensions-VNManager'
$dataBackupDirectory = Join-Path $backupDirectory 'ExtensionsData'
$backupManifestPath = Join-Path $backupDirectory $backupManifestName
$safetyDirectory = Join-Path $BackupRoot "$BackupId-before-rollback-$operationId"
$extensionSafetyDirectory = Join-Path $safetyDirectory 'Extensions-VNManager'
$dataSafetyDirectory = Join-Path $safetyDirectory 'ExtensionsData'

Assert-Directory -Path $backupDirectory -Description 'Fixed rollback backup'
Assert-Directory -Path $extensionBackupDirectory -Description 'Extension rollback backup'
Assert-Directory -Path $dataBackupDirectory -Description 'Extension-data rollback backup'
Assert-BackupManifest -Path $backupManifestPath -ExtensionBackup $extensionBackupDirectory -DataBackup $dataBackupDirectory

Write-Host "Rollback source: $backupDirectory"
Write-Host "Restore extension: $ExtensionDirectory"
Write-Host "Restore data: $SupervisorDirectory"
Write-Host "Current-state safety copy: $safetyDirectory"

$action = "Preserve the current installation, then restore both directories from '$backupDirectory'"
if (-not $PSCmdlet.ShouldProcess($ExtensionDirectory, $action)) { return }
if (Test-Path -LiteralPath $safetyDirectory) { throw "Rollback safety destination already exists: $safetyDirectory" }

New-Item -ItemType Directory -Path $safetyDirectory -Force | Out-Null
Copy-DirectoryExact -Source $ExtensionDirectory -Destination $extensionSafetyDirectory
Copy-DirectoryExact -Source $SupervisorDirectory -Destination $dataSafetyDirectory
Write-Host "Current installation preserved: $safetyDirectory"

try {
    Copy-DirectoryExact -Source $extensionBackupDirectory -Destination $ExtensionDirectory
    Copy-DirectoryExact -Source $dataBackupDirectory -Destination $SupervisorDirectory
}
catch {
    $restoreFailure = $_
    try {
        Copy-DirectoryExact -Source $extensionSafetyDirectory -Destination $ExtensionDirectory
        Copy-DirectoryExact -Source $dataSafetyDirectory -Destination $SupervisorDirectory
    }
    catch { throw "Rollback failed and restoring the pre-rollback safety copy also failed. Rollback: $restoreFailure Safety restore: $_" }
    throw "Rollback failed; the pre-rollback installation was restored. $restoreFailure"
}

Write-Host "Rollback complete. Pre-rollback state preserved: $safetyDirectory"
