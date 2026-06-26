# Remove the admin-discord-mcp-server Scheduled Task and stop any running instance.
# Called from the Inno Setup [UninstallRun] section.
$ErrorActionPreference = "Continue"

Stop-ScheduledTask       -TaskName "admin-discord-mcp-server" -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName "admin-discord-mcp-server" -Confirm:$false -ErrorAction SilentlyContinue
Get-Process admin-discord-mcp -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host "Scheduled Task removed (if it existed) and admin-discord-mcp processes stopped."
