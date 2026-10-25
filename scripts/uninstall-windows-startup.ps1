param([string]$TaskName = 'HourBoostr Remastered by Otter')
$ErrorActionPreference = 'Stop'
if (-not (Get-Command Unregister-ScheduledTask -ErrorAction SilentlyContinue)) {
    Import-Module (Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/Modules/ScheduledTasks/ScheduledTasks.psd1') -ErrorAction Stop
}
Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
Write-Output 'Removed the startup task. Account data and application files remain in place.'
