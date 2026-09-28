<#
.SYNOPSIS
    Measures SysPulse's own overhead (spec §7): average CPU % and private bytes over time.
.DESCRIPTION
    Samples the SysPulse extension process every -IntervalSeconds for -Minutes, writes a CSV and
    prints a summary (avg/max CPU, first/last/max private bytes, private-bytes slope per 10 min).
    The Dock band must be pinned and visible so the monitor loop is running (DECISIONS.md D3).
.EXAMPLE
    .\scripts\measure.ps1 -Minutes 10                  # overhead run at default settings
    .\scripts\measure.ps1 -Minutes 30 -IntervalSeconds 10   # leak run (set ScanInterval = 2 first)
#>
[CmdletBinding()]
param(
    [double] $Minutes = 10,
    [int] $IntervalSeconds = 5,
    [string] $OutFile = (Join-Path $PSScriptRoot "..\measurements\syspulse-$(Get-Date -Format yyyyMMdd-HHmmss).csv")
)

$ErrorActionPreference = 'Stop'
$proc = Get-Process -Name 'SysPulse' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { throw 'SysPulse process not running. Pin the band / open Command Palette first.' }

New-Item -ItemType Directory -Force (Split-Path $OutFile) | Out-Null
$cores = [Environment]::ProcessorCount
$rows = [System.Collections.Generic.List[object]]::new()
$end = (Get-Date).AddMinutes($Minutes)
$prevCpu = $proc.TotalProcessorTime
$prevWall = Get-Date

Write-Host "Measuring PID $($proc.Id) for $Minutes min (every $IntervalSeconds s) -> $OutFile"
while ((Get-Date) -lt $end) {
    Start-Sleep -Seconds $IntervalSeconds
    $proc.Refresh()
    if ($proc.HasExited) { throw 'SysPulse process exited during measurement.' }
    $now = Get-Date
    $cpu = $proc.TotalProcessorTime
    $cpuPct = ($cpu - $prevCpu).TotalMilliseconds / (($now - $prevWall).TotalMilliseconds * $cores) * 100
    $rows.Add([pscustomobject]@{
        Time         = $now.ToString('o')
        CpuPercent   = [math]::Round($cpuPct, 3)
        PrivateMB    = [math]::Round($proc.PrivateMemorySize64 / 1MB, 2)
        WorkingSetMB = [math]::Round($proc.WorkingSet64 / 1MB, 2)
        Handles      = $proc.HandleCount
        Threads      = $proc.Threads.Count
    })
    $prevCpu = $cpu; $prevWall = $now
}

$rows | Export-Csv -NoTypeInformation -Path $OutFile

# Leak check: slope of private MB over the second half (skips warm-up), scaled to MB per 10 min.
$half = $rows | Select-Object -Skip ([int]($rows.Count / 2))
$n = $half.Count
$xs = 0..($n - 1) | ForEach-Object { $_ * $IntervalSeconds / 60.0 }
$ys = $half.PrivateMB
$mx = ($xs | Measure-Object -Average).Average; $my = ($ys | Measure-Object -Average).Average
$num = 0; $den = 0
for ($i = 0; $i -lt $n; $i++) { $num += ($xs[$i] - $mx) * ($ys[$i] - $my); $den += ($xs[$i] - $mx) * ($xs[$i] - $mx) }
$slope = if ($den -ne 0) { $num / $den * 10 } else { 0 }

[pscustomobject]@{
    Samples             = $rows.Count
    AvgCpuPercent       = [math]::Round(($rows.CpuPercent | Measure-Object -Average).Average, 3)
    MaxCpuPercent       = ($rows.CpuPercent | Measure-Object -Maximum).Maximum
    PrivateMBFirst      = $rows[0].PrivateMB
    PrivateMBLast       = $rows[-1].PrivateMB
    PrivateMBMax        = ($rows.PrivateMB | Measure-Object -Maximum).Maximum
    PrivateMBPer10Min   = [math]::Round($slope, 3)
    HandlesFirstLast    = "$($rows[0].Handles) -> $($rows[-1].Handles)"
    Csv                 = (Resolve-Path $OutFile).Path
} | Format-List
