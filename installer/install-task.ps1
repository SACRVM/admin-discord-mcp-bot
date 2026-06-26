# Register the admin-discord-mcp-server Scheduled Task (on logon, auto-restart on crash).
# Called from the Inno Setup [Run] section with -ExePath pointing at the installed exe.
param(
    [Parameter(Mandatory)][string]$ExePath
)
$ErrorActionPreference = "Stop"

if (-not (Test-Path $ExePath)) {
    throw "ExePath not found: $ExePath"
}

$taskName = "admin-discord-mcp-server"

# Drop any prior registration so settings always reflect the installer.
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
Get-Process admin-discord-mcp -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

$action = New-ScheduledTaskAction -Execute $ExePath

# Trigger: at every logon for the installing user.
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME

$settings = New-ScheduledTaskSettingsSet `
    -StartWhenAvailable `
    -RestartCount 999 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero)

# Run under the interactive user (Limited token - no elevation needed).
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited

Register-ScheduledTask `
    -TaskName    $taskName `
    -Description "Hosts admin-discord-mcp.exe so Claude Code clients can connect over HTTP at 127.0.0.1:47823." `
    -Action      $action `
    -Trigger     $trigger `
    -Settings    $settings `
    -Principal   $principal `
    -Force | Out-Null

Start-ScheduledTask -TaskName $taskName

Write-Host "Scheduled Task '$taskName' registered and started."
