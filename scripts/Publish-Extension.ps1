<#
.SYNOPSIS
    Builds and safely deploys the VNManager Playnite extension and LauncherSupervisor.

.DESCRIPTION
    Builds both outputs unless -SkipBuild is used, validates them, creates verified
    snapshots of the installed extension and ExtensionsData directory, and only then
    overlays the new files. The fixed snapshot can be restored by Rollback-Extension.ps1.

    Existing Configuration\*.json files are preserved during publish. Playnite and
    VNInstallOrchestrator must be closed.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$ExtensionDirectory = (Join-Path $env:APPDATA 'Playnite\Extensions\VNManager'),
    [string]$SupervisorDirectory = (Join-Path $env:APPDATA 'Playnite\ExtensionsData\0e1b3f2c-56f4-4eaf-bb7b-7f2c02aa5c5a'),
    # Outside Playnite\Extensions so a backup is never discovered as another extension.
    [string]$BackupRoot = (Join-Path $env:APPDATA 'Playnite\VNManager-backups'),
    # Shared stable identifier used by the publisher and rollback scripts.
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9.-]*$')]
    [string]$BackupId = '8f9dc537-f997-47f1-b4de-7f8232a448b1',
    # Required with -SkipBuild. These make a prebuilt package deployment explicit and testable.
    [string]$PluginOutput,
    [string]$SupervisorOutput,
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$supervisorProject = Join-Path $repositoryRoot 'VNManager\LauncherSupervisor\LauncherSupervisor.csproj'
$pluginProject = Join-Path $repositoryRoot 'VNManager\PlaynitePlugin\PlaynitePlugin.csproj'
$operationId = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$backupManifestName = 'snapshot.sha256.json'

if ([string]::IsNullOrWhiteSpace($PluginOutput)) {
    if ($SkipBuild) { throw '-PluginOutput is required when -SkipBuild is used.' }
    $PluginOutput = Join-Path $repositoryRoot 'VNManager\PlaynitePlugin\bin\Release\net462'
}
if ([string]::IsNullOrWhiteSpace($SupervisorOutput)) {
    if ($SkipBuild) { throw '-SupervisorOutput is required when -SkipBuild is used.' }
    $SupervisorOutput = Join-Path $repositoryRoot 'VNManager\LauncherSupervisor\bin\Release\net10.0\publish'
}

function Invoke-DotNet {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE." }
}

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

function Assert-BackupMatchesSource {
    param([Parameter(Mandatory = $true)][string]$Source, [Parameter(Mandatory = $true)][string]$Backup)
    Assert-Directory -Path $Backup -Description 'Backup directory'
    $difference = @(Compare-Object -ReferenceObject @(Get-DirectorySnapshot -Path $Source) -DifferenceObject @(Get-DirectorySnapshot -Path $Backup))
    if ($difference.Count -ne 0) { throw "Backup verification failed: $Source -> $Backup" }
}

function Backup-Directory {
    param([Parameter(Mandatory = $true)][string]$Source, [Parameter(Mandatory = $true)][string]$Destination)
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse -Force
    }
    Assert-BackupMatchesSource -Source $Source -Backup $Destination
    Write-Host "Backup verified: $Source -> $Destination"
}

function Write-BackupManifest {
    param([Parameter(Mandatory = $true)][string]$ExtensionBackup, [Parameter(Mandatory = $true)][string]$DataBackup, [Parameter(Mandatory = $true)][string]$Path)
    [ordered]@{
        Version = 1
        Snapshots = [ordered]@{
            'Extensions-VNManager' = @(Get-DirectorySnapshot -Path $ExtensionBackup)
            'ExtensionsData' = @(Get-DirectorySnapshot -Path $DataBackup)
        }
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
    Write-Host "Backup manifest written: $Path"
}

function Assert-PublishOutput {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$Description, [Parameter(Mandatory = $true)][string[]]$RequiredFiles)
    Assert-Directory -Path $Path -Description $Description
    foreach ($requiredFile in $RequiredFiles) {
        $requiredPath = Join-Path $Path $requiredFile
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) { throw "Required publish file not found: $requiredPath" }
    }
}

function Copy-Overlay {
    param([Parameter(Mandatory = $true)][string]$Source, [Parameter(Mandatory = $true)][string]$Destination)
    $sourceRoot = (Resolve-Path -LiteralPath $Source).ProviderPath.TrimEnd('\')
    $copied = 0
    $preserved = 0
    foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -File -Recurse -Force) {
        $relativePath = $file.FullName.Substring($sourceRoot.Length + 1)
        $target = Join-Path $Destination $relativePath
        if ($relativePath -like 'Configuration\*.json' -and (Test-Path -LiteralPath $target -PathType Leaf)) {
            Write-Host "Preserved: $relativePath"
            $preserved++
            continue
        }
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
        $copied++
    }
    Write-Host "$Destination : $copied file(s) copied, $preserved configuration file(s) preserved."
}

$runningProcesses = Get-Process -Name 'Playnite.DesktopApp', 'Playnite.FullscreenApp', 'VNInstallOrchestrator' -ErrorAction SilentlyContinue
if ($runningProcesses) { throw 'Close Playnite and VNInstallOrchestrator before publishing: they can lock deployed files.' }

Assert-Directory -Path $ExtensionDirectory -Description 'Installed VNManager extension directory'
Assert-Directory -Path $SupervisorDirectory -Description 'Installed VNManager data directory'
if (Test-Path -LiteralPath $BackupRoot -PathType Leaf) { throw "Backup root is a file, not a directory: $BackupRoot" }

$backupDirectory = Join-Path $BackupRoot $BackupId
$candidateBackupDirectory = Join-Path $BackupRoot "$BackupId-pending-$operationId"
$archivedBackupDirectory = Join-Path $BackupRoot "$BackupId-$operationId"
$candidateExtensionBackupDirectory = Join-Path $candidateBackupDirectory 'Extensions-VNManager'
$candidateDataBackupDirectory = Join-Path $candidateBackupDirectory 'ExtensionsData'
$candidateBackupManifestPath = Join-Path $candidateBackupDirectory $backupManifestName

Write-Host "Plugin output: $PluginOutput"
Write-Host "Supervisor output: $SupervisorOutput"
Write-Host "Backup extension: $(Join-Path $backupDirectory 'Extensions-VNManager')"
Write-Host "Backup data: $(Join-Path $backupDirectory 'ExtensionsData')"
if (Test-Path -LiteralPath $backupDirectory -PathType Container) { Write-Host "Existing backup archive: $archivedBackupDirectory" }
Write-Host "Deploy extension: $ExtensionDirectory"
Write-Host "Deploy data: $SupervisorDirectory"

$action = "Back up '$ExtensionDirectory' and '$SupervisorDirectory', verify both backups, then deploy the extension and supervisor"
if (-not $PSCmdlet.ShouldProcess($ExtensionDirectory, $action)) { return }

if (-not $SkipBuild) {
    if (Test-Path -LiteralPath $SupervisorOutput) { Remove-Item -LiteralPath $SupervisorOutput -Recurse -Force }
    Invoke-DotNet @('publish', $supervisorProject, '-c', 'Release')
    Invoke-DotNet @('build', $pluginProject, '-c', 'Release')
}

Assert-PublishOutput -Path $PluginOutput -Description 'Plugin publish output' -RequiredFiles @('PlaynitePlugin.dll', 'VNInstallation.Protocol.dll', 'System.Text.Json.dll', 'extension.yaml')
Assert-PublishOutput -Path $SupervisorOutput -Description 'Supervisor publish output' -RequiredFiles @('LauncherSupervisor.exe', 'LauncherSupervisor.dll', 'LauncherSupervisor.deps.json', 'LauncherSupervisor.runtimeconfig.json')

if (Test-Path -LiteralPath $backupDirectory) {
    if (-not (Test-Path -LiteralPath $backupDirectory -PathType Container)) { throw "Fixed backup path is not a directory: $backupDirectory" }
}
if (Test-Path -LiteralPath $candidateBackupDirectory) { throw "Candidate backup destination already exists: $candidateBackupDirectory" }

Backup-Directory -Source $ExtensionDirectory -Destination $candidateExtensionBackupDirectory
Backup-Directory -Source $SupervisorDirectory -Destination $candidateDataBackupDirectory
Write-BackupManifest -ExtensionBackup $candidateExtensionBackupDirectory -DataBackup $candidateDataBackupDirectory -Path $candidateBackupManifestPath

if (Test-Path -LiteralPath $backupDirectory) {
    if (Test-Path -LiteralPath $archivedBackupDirectory) { throw "Backup archive destination already exists: $archivedBackupDirectory" }
    Move-Item -LiteralPath $backupDirectory -Destination $archivedBackupDirectory
    try {
        Move-Item -LiteralPath $candidateBackupDirectory -Destination $backupDirectory
    }
    catch {
        $promotionFailure = $_
        try {
            Move-Item -LiteralPath $archivedBackupDirectory -Destination $backupDirectory
        }
        catch { throw "Backup promotion failed and restoring the previous fixed backup also failed. Promotion: $promotionFailure Restore: $_" }
        throw "Backup promotion failed; the previous fixed backup was restored. $promotionFailure"
    }
    Write-Host "Previous backup archived: $archivedBackupDirectory"
}
else {
    Move-Item -LiteralPath $candidateBackupDirectory -Destination $backupDirectory
}

# Deployment is deliberately after both verified backups. Exceptions above leave the installation untouched.
Copy-Overlay -Source $PluginOutput -Destination $ExtensionDirectory
Copy-Overlay -Source $SupervisorOutput -Destination $SupervisorDirectory
Write-Host "Done. Verified backup: $backupDirectory"
