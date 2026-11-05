<#
.SYNOPSIS
  Copies the encrypted backups of the TradePulse server to THIS computer and keeps only the newest ones here.

.DESCRIPTION
  Run it on your own machine (Windows PowerShell 5.1 or PowerShell 7, with the OpenSSH client that Windows ships). It
    1. lists the backup files on the server (deploy/backup.sh makes them) over ssh,
    2. downloads the ones this computer does not have yet (to a .part file first, checked, then renamed),
    3. deletes the older local copies so that -Keep of each kind stay (the main database, the key vault, the data protection keys).
  Nothing is ever deleted on the server, and nothing is ever deleted here that does not look like a backup file of this stack.

  Only files ending in .age (encrypted by backup.sh) are taken. A plain .dump holds every secret of the database; use -AllowPlain
  only if you accept that it lands readable on this disk.

  Nothing is written into the script: where the server is and where the files go are settings (parameters, or the environment
  variables below), so every person who runs it on their own machine chooses their own.

    TRADEPULSE_SERVER             user@host of the server (same as -Server)
    TRADEPULSE_REMOTE_BACKUP_DIR  folder of the backups on the server (same as -RemoteDir)
    TRADEPULSE_BACKUP_DIR         folder on this computer (same as -Destination)

.EXAMPLE
  .\pull-backups.ps1 -Server deploy@my.server.example
  Pulls into the folder tradepulse-backups of your user profile.

.EXAMPLE
  .\pull-backups.ps1 -Server deploy@my.server.example -Destination D:\Safe\tp -Keep 12 -Register -At 04:00
  Registers a daily task for the current user that runs the same command (it runs once now, too, unless -NoRun).

.EXAMPLE
  .\pull-backups.ps1 -PruneOnly -Destination D:\Safe\tp -Keep 3
  Only tidies the local folder.
#>
[CmdletBinding()]
param(
    [string]$Server = $env:TRADEPULSE_SERVER,
    # Where backup.sh keeps them on the server (its BACKUP_DIR; /opt/tradepulse-backups unless that setting was changed there).
    [string]$RemoteDir = $(if ($env:TRADEPULSE_REMOTE_BACKUP_DIR) { $env:TRADEPULSE_REMOTE_BACKUP_DIR } else { '/opt/tradepulse-backups' }),
    [string]$Destination = $(if ($env:TRADEPULSE_BACKUP_DIR) { $env:TRADEPULSE_BACKUP_DIR } else { Join-Path $HOME 'tradepulse-backups' }),
    [ValidateRange(1, 1000)][int]$Keep = 8,
    [string]$IdentityFile,
    [ValidateRange(1, 65535)][int]$Port = 22,
    [switch]$AllowPlain,
    [switch]$PruneOnly,
    [switch]$Register,
    [string]$At = '03:30',
    [string]$TaskName = 'TradePulse backup pull',
    [switch]$NoRun
)

$ErrorActionPreference = 'Stop'

# The names backup.sh makes: <kind>-YYYYMMDD-HHMMSS.<dump|tgz>[.age]. Anything else in the folder is left alone.
$kinds = @('tradepulse', 'vaultdb', 'api-keys')
$namePattern = '^(?<kind>tradepulse|vaultdb|api-keys)-\d{8}-\d{6}\.(dump|tgz)(?<age>\.age)?$'

function Write-Log([string]$Message) {
    $line = '{0:u} {1}' -f (Get-Date), $Message
    Write-Host $line
    try { Add-Content -LiteralPath (Join-Path $Destination 'pull-backups.log') -Value $line } catch { }
}

function Invoke-Prune {
    $failed = $false
    foreach ($kind in $kinds) {
        $mine = Get-ChildItem -LiteralPath $Destination -File |
            Where-Object { $_.Name -match $namePattern -and $Matches['kind'] -eq $kind } |
            Sort-Object Name -Descending     # the stamp in the name sorts by time
        foreach ($old in ($mine | Select-Object -Skip $Keep)) {
            try { Remove-Item -LiteralPath $old.FullName -Force; Write-Log "removed old copy $($old.Name)" }
            catch { Write-Log "could not remove $($old.Name)"; $failed = $true }
        }
    }
    return -not $failed
}

# --- register a daily task for the current user -------------------------------------------------------------------------
if ($Register) {
    if (-not $Server -and -not $PruneOnly) { throw 'Give -Server (user@host) so that the task knows where to pull from.' }
    if ($At -notmatch '^\d{1,2}:\d{2}$') { throw "-At must look like 03:30, not '$At'." }
    $taskArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"",
                  '-Server', "`"$Server`"", '-RemoteDir', "`"$RemoteDir`"", '-Destination', "`"$Destination`"", '-Keep', $Keep, '-Port', $Port)
    if ($IdentityFile) { $taskArgs += @('-IdentityFile', "`"$IdentityFile`"") }
    if ($AllowPlain)   { $taskArgs += '-AllowPlain' }
    $shell = if ($PSVersionTable.PSEdition -eq 'Core') { 'pwsh.exe' } else { 'powershell.exe' }
    $action   = New-ScheduledTaskAction -Execute $shell -Argument ($taskArgs -join ' ')
    $trigger  = New-ScheduledTaskTrigger -Daily -At $At
    # StartWhenAvailable: a computer that was off at that time pulls as soon as it is on again.
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 2)
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings `
        -Description 'Copies the encrypted TradePulse backups to this computer (deploy/pull-backups.ps1).' -Force | Out-Null
    Write-Host "Task '$TaskName' registered: every day at $At, as the current user (it needs their ssh key, with no passphrase prompt)."
    if ($NoRun) { return }
}

New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$Destination = (Resolve-Path -LiteralPath $Destination).Path

if ($PruneOnly) {
    if (-not (Invoke-Prune)) { exit 1 }
    return
}

# --- checks of what we were given ---------------------------------------------------------------------------------------
if (-not $Server) { throw 'Give -Server (user@host) or set TRADEPULSE_SERVER.' }
if ($Server -notmatch '^[A-Za-z0-9._-]+@[A-Za-z0-9._:\[\]-]+$') { throw "-Server must look like user@host, not '$Server'." }
if ($RemoteDir -notmatch '^/[A-Za-z0-9_./-]+$') { throw "-RemoteDir must be an absolute folder on the server without spaces, not '$RemoteDir'." }
if (-not (Get-Command ssh -ErrorAction SilentlyContinue) -or -not (Get-Command scp -ErrorAction SilentlyContinue)) {
    throw 'ssh and scp are needed (Windows: Settings > Optional features > OpenSSH Client).'
}

$sshOptions = @('-o', 'BatchMode=yes', '-o', 'ConnectTimeout=20', '-o', 'StrictHostKeyChecking=yes')
if ($IdentityFile) { $sshOptions += @('-i', $IdentityFile) }

# --- what is on the server ----------------------------------------------------------------------------------------------
$globs = foreach ($k in $kinds) { if ($AllowPlain) { "$k-*.dump $k-*.tgz $k-*.dump.age $k-*.tgz.age" } else { "$k-*.dump.age $k-*.tgz.age" } }
$remoteCommand = "cd '$RemoteDir' && stat -c '%s %n' -- $($globs -join ' ') 2>/dev/null"
$listing = & ssh @sshOptions -p $Port $Server $remoteCommand
$files = @()
foreach ($line in @($listing)) {
    if ($line -match '^(?<size>\d+) (?<name>\S+)$') {
        # Read both groups before the second -match, which replaces $Matches.
        $size = [long]$Matches['size']
        $name = $Matches['name']
        if ($name -match $namePattern) { $files += [pscustomobject]@{ Name = $name; Size = $size } }
    }
}
if ($files.Count -eq 0) {
    Write-Log "FAILED: no backup file found in $RemoteDir on the server (wrong -RemoteDir, no encrypted backup yet, or ssh could not log in)."
    exit 1
}

# --- download what is missing -------------------------------------------------------------------------------------------
$failed = $false
$downloaded = 0
Push-Location $Destination      # scp takes the local name relative to here: no drive letter for it to take for a host
try {
    foreach ($file in ($files | Sort-Object Name)) {
        $target = Join-Path $Destination $file.Name
        if ((Test-Path -LiteralPath $target) -and (Get-Item -LiteralPath $target).Length -eq $file.Size) { continue }

        $part = "$($file.Name).part"
        Remove-Item -LiteralPath $part -Force -ErrorAction SilentlyContinue
        & scp -q @sshOptions -P $Port "${Server}:${RemoteDir}/$($file.Name)" $part
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $part)) {
            Write-Log "FAILED: $($file.Name) could not be copied."; $failed = $true; continue
        }
        if ((Get-Item -LiteralPath $part).Length -ne $file.Size) {
            Remove-Item -LiteralPath $part -Force
            Write-Log "FAILED: $($file.Name) arrived with the wrong size."; $failed = $true; continue
        }
        if ($file.Name.EndsWith('.age')) {
            # An age file begins with this line; a half-written or wrong file does not.
            $head = New-Object byte[] 21
            $stream = [System.IO.File]::OpenRead((Join-Path $Destination $part))
            try { [void]$stream.Read($head, 0, 21) } finally { $stream.Dispose() }
            if ([System.Text.Encoding]::ASCII.GetString($head) -ne 'age-encryption.org/v1') {
                Remove-Item -LiteralPath $part -Force
                Write-Log "FAILED: $($file.Name) is not an age file."; $failed = $true; continue
            }
        }
        Move-Item -LiteralPath $part -Destination $file.Name -Force
        $downloaded++
        Write-Log "copied $($file.Name) ($([math]::Round($file.Size / 1MB, 1)) MB)"
    }
}
finally { Pop-Location }

# Old pieces of a copy that was interrupted.
Get-ChildItem -LiteralPath $Destination -File -Filter '*.part' | Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-1) } |
    Remove-Item -Force -ErrorAction SilentlyContinue

# --- tidy ---------------------------------------------------------------------------------------------------------------
# Not after a failed copy: the older files are then the only ones there are.
if ($failed) { exit 1 }
if (-not (Invoke-Prune)) { exit 1 }
$news = if ($downloaded -eq 0) { 'nothing new to copy' } else { "$downloaded new file(s) copied" }
Write-Log "done: $news; $($files.Count) file(s) on the server, newest $Keep of each kind kept in $Destination"
