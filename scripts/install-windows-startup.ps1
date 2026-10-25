param(
    [Parameter(Mandatory)][string]$Executable,
    [string]$DataDirectory = (Join-Path $env:LOCALAPPDATA 'HourBoostrRemastered'),
    [int]$Port = 3241,
    [string]$TaskName = 'HourBoostr Remastered by Otter'
)
$ErrorActionPreference = 'Stop'
if (-not (Get-Command Register-ScheduledTask -ErrorAction SilentlyContinue)) {
    $schedulerModule = Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/Modules/ScheduledTasks/ScheduledTasks.psd1'
    Import-Module $schedulerModule -ErrorAction Stop
}
$resolvedExe = (Resolve-Path -LiteralPath $Executable).Path
$isDesktop = [IO.Path]::GetFileName($resolvedExe) -eq 'HourBoostr.Desktop.exe'
if (-not $isDesktop -and [IO.Path]::GetFileName($resolvedExe) -ne 'HourBoostr.Remastered.exe') { throw 'Select the published remaster or desktop executable.' }
if ($Port -lt 1024 -or $Port -gt 65535) { throw 'Invalid dashboard port.' }
$resolvedData = [IO.Path]::GetFullPath($DataDirectory)
if ($resolvedData.Contains('"')) { throw 'Invalid data directory.' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$startupArguments = if ($isDesktop) { "--data-dir `"$resolvedData`" --port $Port --start-minimized" } else { "serve --data-dir `"$resolvedData`" --port $Port" }
$action = New-ScheduledTaskAction -Execute $resolvedExe -Argument $startupArguments -WorkingDirectory (Split-Path -Parent $resolvedExe)
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $identity
$principal = New-ScheduledTaskPrincipal -UserId $identity -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero)
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Start the local HourBoostr Remastered installation at Windows sign-in.' -Force | Out-Null
Write-Output "Installed current-user sign-in task: $TaskName. No Windows password was stored."
