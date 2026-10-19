<#
.SYNOPSIS
  Claude in the sound mixer: one permanent "Claude" slider in the Windows Volume
  Mixer that also controls Claude's Terminal panel, plus a chime when a prompt
  finishes.

.DESCRIPTION
  Builds claude-chime.exe from src\claude-chime.cs with the .NET Framework
  compiler that ships with Windows, copies it to <Claude dir>\sounds and adds
  Claude Code hooks to <Claude dir>\settings.json:
    SessionStart  claude-chime.exe --start   starts the mixer watcher (always)
    Stop          claude-chime.exe           chime when a prompt finishes
    Notification  claude-chime.exe           chime when Claude needs you (optional)
  The chime is chime\chime.mp3 or chime\chime.wav in this folder. Without one,
  only the slider is installed.
  settings.json is backed up before it is changed. Running it again updates the
  install; -Uninstall removes it.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File src\patch.ps1
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File src\patch.ps1 -WaitingChime no
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File src\patch.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    # Chime when Claude finishes a prompt (needs chime\chime.mp3 or .wav). no = slider only.
    [ValidateSet('yes', 'no')] [string] $Chime = 'yes',
    # Also chime when Claude asks for permission or is waiting for you.
    [ValidateSet('ask', 'yes', 'no')] [string] $WaitingChime = 'ask',
    # Claude Code's config folder. Default: $env:CLAUDE_CONFIG_DIR, else ~\.claude.
    [string] $ClaudeDir,
    [switch] $Uninstall
)

$ErrorActionPreference = 'Stop'

if ($env:OS -ne 'Windows_NT') { throw 'This patch is for Windows machines.' }

$repo = Split-Path -Parent $PSScriptRoot   # the repository folder (this script is in src)
if (-not $ClaudeDir) {
    if ($env:CLAUDE_CONFIG_DIR) { $ClaudeDir = $env:CLAUDE_CONFIG_DIR }
    else { $ClaudeDir = Join-Path $env:USERPROFILE '.claude' }
}
$installDir = Join-Path $ClaudeDir 'sounds'
$exe = Join-Path $installDir 'claude-chime.exe'
$settingsPath = Join-Path $ClaudeDir 'settings.json'

# Files this patch puts in the install folder (cursor-done.wav and claude-chime.cs
# are from the hand-made version it replaces).
$ownFiles = 'claude-chime.exe', 'chime.wav', 'chime.mp3', 'claude-level.txt', 'watcher.log',
            'cursor-done.wav', 'claude-chime.cs'

# ==== helpers ====================================================================

function Ask-YesNo([string] $question, [bool] $default) {
    if ($default) { $hint = '[Y/n]' } else { $hint = '[y/N]' }
    while ($true) {
        $answer = Read-Host "$question $hint"
        if ([string]::IsNullOrWhiteSpace($answer)) { return $default }
        $answer = $answer.Trim().ToLowerInvariant()
        if ($answer -eq 'y' -or $answer -eq 'yes') { return $true }
        if ($answer -eq 'n' -or $answer -eq 'no') { return $false }
        Write-Host 'Please answer y or n.'
    }
}

function Find-ChimeSound {
    foreach ($name in 'chime.mp3', 'chime.wav') {
        $candidate = Join-Path $repo "chime\$name"
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    return $null
}

function Stop-Watcher {
    foreach ($p in @(Get-Process -Name 'claude-chime' -ErrorAction SilentlyContinue)) {
        $path = $null
        try { $path = $p.Path } catch { }
        if ($path -and ($path -ieq $exe)) {
            Stop-Process -Id $p.Id -Force
            [void] $p.WaitForExit(5000)
        }
    }
}

function Test-Obj($value) { return $value -is [System.Management.Automation.PSCustomObject] }

function Test-Prop($obj, [string] $name) { return $null -ne $obj.PSObject.Properties[$name] }

# --- settings.json ---------------------------------------------------------------
# ConvertTo-Json in Windows PowerShell 5.1 mangles some arrays and escapes < > ' &,
# so settings.json is written by this small serializer instead (2-space indent).

function Quote-Json([string] $s) {
    $e = $s.Replace('\', '\\').Replace('"', '\"').Replace("`n", '\n').Replace("`r", '\r').Replace("`t", '\t')
    return '"' + [regex]::Replace($e, '[\x00-\x1f]', { param($m) '\u{0:x4}' -f [int][char]$m.Value }) + '"'
}

function Format-Json($value, [int] $level = 0) {
    $pad = '  ' * $level
    $inner = '  ' * ($level + 1)
    $inv = [System.Globalization.CultureInfo]::InvariantCulture
    if ($null -eq $value) { return 'null' }
    if ($value -is [bool]) { if ($value) { return 'true' } else { return 'false' } }
    if ($value -is [string] -or $value -is [char]) { return Quote-Json ([string] $value) }
    if ($value -is [double] -or $value -is [single]) { return $value.ToString('R', $inv) }
    if ($value -is [int] -or $value -is [long] -or $value -is [decimal] -or $value -is [int16] -or
        $value -is [byte] -or $value -is [uint32] -or $value -is [uint64]) { return $value.ToString($inv) }
    if (Test-Obj $value) {
        $props = @($value.PSObject.Properties)
        if ($props.Count -eq 0) { return '{}' }
        $parts = foreach ($p in $props) { $inner + (Quote-Json $p.Name) + ': ' + (Format-Json $p.Value ($level + 1)) }
        return "{`n" + ($parts -join ",`n") + "`n$pad}"
    }
    if ($value -is [System.Collections.IDictionary]) {
        if ($value.Count -eq 0) { return '{}' }
        $parts = foreach ($k in $value.Keys) { $inner + (Quote-Json ([string] $k)) + ': ' + (Format-Json $value[$k] ($level + 1)) }
        return "{`n" + ($parts -join ",`n") + "`n$pad}"
    }
    if ($value -is [System.Collections.IEnumerable]) {
        $items = @($value)
        if ($items.Count -eq 0) { return '[]' }
        $parts = foreach ($item in $items) { $inner + (Format-Json $item ($level + 1)) }
        return "[`n" + ($parts -join ",`n") + "`n$pad]"
    }
    return Quote-Json ([string] $value)
}

function Read-Settings {
    if (-not (Test-Path -LiteralPath $settingsPath)) { return [pscustomobject] @{} }
    $text = [System.IO.File]::ReadAllText($settingsPath)
    if ([string]::IsNullOrWhiteSpace($text)) { return [pscustomobject] @{} }
    try { $settings = $text | ConvertFrom-Json }
    catch { throw "$settingsPath is not valid JSON ($($_.Exception.Message)). Nothing was changed." }
    if (-not (Test-Obj $settings)) { throw "$settingsPath does not hold a JSON object. Nothing was changed." }
    return $settings
}

# Removes every hook whose command runs claude-chime.exe, then any hook group or
# event left empty by that.
function Remove-OurHooks($settings) {
    if (-not (Test-Prop $settings 'hooks') -or -not (Test-Obj $settings.hooks)) { return }
    $hooks = $settings.hooks
    foreach ($event in @($hooks.PSObject.Properties)) {
        if ($event.Value -isnot [System.Collections.IEnumerable] -or $event.Value -is [string]) { continue }
        $groups = @()
        foreach ($group in @($event.Value)) {
            if ((Test-Obj $group) -and (Test-Prop $group 'hooks') -and
                $group.hooks -is [System.Collections.IEnumerable] -and $group.hooks -isnot [string]) {
                $kept = @(@($group.hooks) | Where-Object {
                    -not ((Test-Obj $_) -and (Test-Prop $_ 'command') -and ([string] $_.command) -match 'claude-chime\.exe')
                })
                if ($kept.Count -eq 0) { continue }
                $group.hooks = $kept
            }
            $groups += , $group
        }
        if ($groups.Count -eq 0) { $hooks.PSObject.Properties.Remove($event.Name) }
        else { $event.Value = $groups }
    }
    if (@($hooks.PSObject.Properties).Count -eq 0) { $settings.PSObject.Properties.Remove('hooks') }
}

function Add-Hook($settings, [string] $event, [string] $command) {
    if (-not (Test-Prop $settings 'hooks')) {
        $settings | Add-Member -NotePropertyName 'hooks' -NotePropertyValue ([pscustomobject] @{})
    }
    $entry = [pscustomobject] @{ hooks = @([pscustomobject] @{ type = 'command'; command = $command }) }
    if (Test-Prop $settings.hooks $event) { $settings.hooks.$event = @(@($settings.hooks.$event) + $entry) }
    else { $settings.hooks | Add-Member -NotePropertyName $event -NotePropertyValue @($entry) }
}

# Writes settings.json (UTF-8, no BOM) if it changed, after backing up the old one.
function Save-Settings($settings, [string] $before) {
    $after = (Format-Json $settings) + "`n"
    if ($null -ne $before -and $after -ceq $before) { Write-Host "settings.json already up to date."; return }
    if (Test-Path -LiteralPath $settingsPath) {
        $backup = "$settingsPath.bak-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
        Copy-Item -LiteralPath $settingsPath -Destination $backup
        Write-Host "Backed up settings.json to $backup"
    }
    New-Item -ItemType Directory -Force -Path $ClaudeDir | Out-Null
    [System.IO.File]::WriteAllText($settingsPath, $after, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "Updated $settingsPath"
}

function Read-SettingsText {
    if (Test-Path -LiteralPath $settingsPath) { return [System.IO.File]::ReadAllText($settingsPath) }
    return $null
}

# ==== uninstall ==================================================================

if ($Uninstall) {
    $before = Read-SettingsText
    $settings = Read-Settings
    Stop-Watcher
    Remove-OurHooks $settings
    if ($null -ne $before) { Save-Settings $settings $before }
    foreach ($name in $ownFiles) {
        $f = Join-Path $installDir $name
        if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force }
    }
    if ((Test-Path -LiteralPath $installDir) -and -not (Get-ChildItem -LiteralPath $installDir -Force)) {
        Remove-Item -LiteralPath $installDir -Force
    }
    Write-Host ''
    Write-Host 'Uninstalled. The Claude slider goes back to how Windows normally shows Claude.'
    return
}

# ==== install ====================================================================

$csc = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $csc) { throw 'The .NET Framework 4 compiler (csc.exe) was not found. It ships with Windows 10 and 11; on older Windows install .NET Framework 4.8.' }

$source = Join-Path $repo 'src\claude-chime.cs'
if (-not (Test-Path -LiteralPath $source)) { throw "Missing $source. Run the patch from the repository folder." }

# --- choices ---------------------------------------------------------------------
Write-Host 'Claude in the sound mixer'
Write-Host 'This adds one permanent "Claude" slider to the Windows Volume Mixer that also'
Write-Host "controls Claude's Terminal panel."
Write-Host ''

$soundFile = $null
$noSound = $false
if ($Chime -eq 'yes') {
    $soundFile = Find-ChimeSound
    $noSound = -not $soundFile
}
$wantChime = [bool] $soundFile

$wantWaiting = $false
if ($wantChime) {
    if ($WaitingChime -eq 'ask') { $wantWaiting = Ask-YesNo 'Also play the chime when Claude needs your permission or is waiting for you (Cursor does)?' $true }
    else { $wantWaiting = $WaitingChime -eq 'yes' }
}

# Read settings before touching anything, so a broken settings.json stops the patch early.
$before = Read-SettingsText
$settings = Read-Settings

# --- build and copy --------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Stop-Watcher
Write-Host ''
Write-Host "Building $exe"
& $csc /nologo /target:winexe "/out:$exe" $source
if ($LASTEXITCODE -ne 0) { throw 'Building claude-chime.exe failed (compiler output above).' }

foreach ($name in 'chime.wav', 'chime.mp3', 'cursor-done.wav', 'claude-chime.cs') {
    $f = Join-Path $installDir $name
    if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force }
}
if ($soundFile) {
    $target = Join-Path $installDir ('chime' + [System.IO.Path]::GetExtension($soundFile).ToLowerInvariant())
    Copy-Item -LiteralPath $soundFile -Destination $target
    Write-Host "Chime sound: $soundFile"
}

# --- hooks -----------------------------------------------------------------------
$exeForHook = $exe.Replace('\', '/')
if ($exeForHook -match '\s') { $exeForHook = '"' + $exeForHook + '"' }

Remove-OurHooks $settings
Add-Hook $settings 'SessionStart' "$exeForHook --start"
if ($wantChime) { Add-Hook $settings 'Stop' $exeForHook }
if ($wantWaiting) { Add-Hook $settings 'Notification' $exeForHook }
Save-Settings $settings $before

# Start the watcher now if the Claude app is running (it exits by itself otherwise).
& $exe --start

Write-Host ''
Write-Host 'Done.'
Write-Host '  - Volume Mixer: one "Claude" slider while the Claude app runs (after your next'
Write-Host '    Claude Code session starts, or right away if the app is already open).'
if ($wantChime) {
    $when = 'when a prompt finishes'
    if ($wantWaiting) { $when += ' and when Claude needs you' }
    Write-Host "  - Chime $when, at the Claude slider's level, silent when it is muted."
}
elseif ($noSound) {
    Write-Host "  - No chime: there is no chime.mp3 or chime.wav in $(Join-Path $repo 'chime')."
    Write-Host '    Put one there and run install.cmd again to add it.'
}
else { Write-Host '  - No chime.' }
Write-Host '  Restart open Claude Code sessions so they pick up the new hooks.'
Write-Host '  Run uninstall.cmd (or src\patch.ps1 -Uninstall) to remove everything.'
